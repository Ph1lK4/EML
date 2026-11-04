using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft;

/// <summary>离线导入实例：分析压缩包、提取文件、生成清单，再提交到 instances 目录。</summary>
public sealed class MinecraftInstanceImporter : IMinecraftInstanceImporter
{
    private readonly string _launcherRoot;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>指定启动器根目录；未指定时使用程序所在目录，实例存入其 instances 子目录。</summary>
    public MinecraftInstanceImporter(string? launcherRoot = null) =>
        _launcherRoot = Path.GetFullPath(launcherRoot ?? AppContext.BaseDirectory);

    /// <summary>在后台分析压缩包，返回名称、版本和文件需求预览，不解压或下载。</summary>
    public Task<InstanceImportPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var input = OpenArchive(archivePath);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            var plan = RemoveLocalSourceRecord(InstanceArchiveReader.Read(archive, Path.GetFileName(archivePath), cancellationToken));
            ValidatePlan(plan);
            return new InstanceImportPreview(InstanceImportPaths.SuggestName(plan.Manifest.Name), plan.Manifest.SourceFormat,
                plan.Manifest.MinecraftVersion, plan.Manifest.Components, plan.Copies.Count(c => !c.Directory),
                plan.Manifest.Files.Count(f => f.Source == InstanceFileSource.DirectDownload),
                plan.Manifest.Files.Count(f => f.Source == InstanceFileSource.CurseForge), plan.Manifest.Notes);
        }, cancellationToken);

    /// <summary>等待其他导入结束后执行本次导入，支持进度通知和取消。</summary>
    public async Task<InstanceImportResult> ImportAsync(string archivePath, string instanceName,
        IProgress<InstanceImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => ImportCoreAsync(archivePath, instanceName, progress, cancellationToken), cancellationToken);
        }
        finally { _operationLock.Release(); }
    }

    /// <summary>在临时目录中提取文件并记录哈希，全部完成后返回已提交的实例路径和清单。</summary>
    private async Task<InstanceImportResult> ImportCoreAsync(string archivePath, string instanceName,
        IProgress<InstanceImportProgress>? progress, CancellationToken token)
    {
        var name = InstanceImportPaths.InstanceName(instanceName);
        var instances = Path.Combine(_launcherRoot, "instances");
        var destination = InstanceImportPaths.Within(instances, name);
        InstanceImportPaths.EnsureNoLinks(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("同名实例或文件已经存在，请使用其他名称。");
        using var input = OpenArchive(archivePath);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        // 预览后原包可能变更，因此执行导入时重做所有校验，不信任旧的界面预览。
        var plan = RemoveLocalSourceRecord(InstanceArchiveReader.Read(archive, Path.GetFileName(archivePath), token));
        ValidatePlan(plan);
        var stagingParent = Path.Combine(_launcherRoot, ".eml-imports");
        InstanceImportPaths.EnsureNoLinks(stagingParent);
        var staging = Path.Combine(stagingParent, Guid.NewGuid().ToString("N"));
        var committed = false;
        try
        {
            Directory.CreateDirectory(staging);
            var total = plan.Copies.Where(c => !c.Directory).Sum(c => c.Entry.Length);
            long copied = 0;
            progress?.Report(new("准备导入", 0, total));
            token.ThrowIfCancellationRequested();
            var files = new Dictionary<string, InstanceFileRequirement>(StringComparer.OrdinalIgnoreCase);
            var watch = Stopwatch.StartNew();
            var buffer = new byte[128 * 1024];
            foreach (var copy in plan.Copies)
            {
                token.ThrowIfCancellationRequested();
                var target = InstanceImportPaths.Within(staging, copy.TargetPath);
                InstanceImportPaths.EnsureNoLinks(target);
                if (copy.Directory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = copy.Entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true);
                // 边写入边记录哈希，避免导入时再读一遍文件；后续校验仍需读取当前文件。
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long length = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    length += count;
                    if (length > copy.Entry.Length) throw new InvalidDataException("解压内容超过条目声明的长度：" + copy.TargetPath);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    hash.AppendData(buffer, 0, count);
                    copied += count;
                    if (watch.ElapsedMilliseconds >= 100)
                    {
                        progress?.Report(new(copy.TargetPath, copied, total));
                        watch.Restart();
                    }
                }
                if (length != copy.Entry.Length) throw new InvalidDataException("压缩包文件未完整读取：" + copy.TargetPath);
                files.Add(copy.TargetPath, new InstanceFileRequirement
                {
                    Path = copy.TargetPath, Source = InstanceFileSource.Embedded, Size = length,
                    Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() },
                    ArchiveEntry = copy.Entry.FullName.Replace('\\', '/')
                });
            }
            List<InstanceFileRequirement> remaining;
            if (plan.PreserveRemoteRequirements)
            {
                // 备份是已有来源的搬运：不能重新计算摘要把坏文件或未知 CF JAR 升格为可信覆盖。
                // 未列出的额外文件仍解压保留，但不自动获得完整性依据。
                remaining = plan.Manifest.Files.Select(file => file.Source == InstanceFileSource.Embedded &&
                    file.Origin != InstanceFileOrigin.UserLocalImport && file.ArchiveEntry is not null &&
                    file.Path is { } path && files.TryGetValue(path, out var copiedFile)
                        ? file with { ArchiveEntry = copiedFile.ArchiveEntry } : file).ToList();
            }
            else
            {
                // 首次从平台原包导入时才建立包内基线；与远端同路径的内容保留独立 SHA-256。
                remaining = plan.Manifest.Files.Where(file => file.Path is null || !files.ContainsKey(file.Path)).ToList();
                remaining.AddRange(files.Values);
            }
            var manifest = plan.Manifest with
            {
                Name = name, SourceArchive = Path.GetFileName(archivePath), IntegrityStatus = "NotChecked", Files = remaining
            };
            var game = manifest.GameDirectory == "." ? staging : InstanceImportPaths.Within(staging, manifest.GameDirectory);
            Directory.CreateDirectory(game);
            if (manifest.SourceFormat == "Prism")
                SynchronizePrismName(staging, name, remaining);
            await InstanceManifestStore.WriteAsync(Path.Combine(staging, InstanceImportManifest.FileName), manifest, token);
            await InstanceImportSourceStore.WriteAsync(staging, manifest, archivePath, token);
            token.ThrowIfCancellationRequested();
            InstanceImportPaths.EnsureNoLinks(destination);
            Directory.CreateDirectory(instances);
            Directory.Move(staging, destination); // 不覆盖；发生同名竞争也会失败并清理自己的临时目录。
            committed = true;
            // 提交后不再响应取消，也不调用可能抛异常的外部进度回调，避免成功被报告为失败。
            return new(destination, manifest.GameDirectory == "." ? destination : InstanceImportPaths.Within(destination, manifest.GameDirectory), manifest);
        }
        finally
        {
            if (!committed) Cleanup(staging);
        }
    }

    /// <summary>只打开 ZIP/mrpack 文件；读取期间禁止其他进程写入，保护本次导入的一致性。</summary>
    private static FileStream OpenArchive(string archivePath)
    {
        var extension = Path.GetExtension(archivePath);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .zip 或 .mrpack 整合包。");
        return new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>EML 自有状态只在本机按当前清单重新生成，不能来自压缩包或成为游戏内容基线。</summary>
    private static InstanceArchivePlan RemoveLocalSourceRecord(InstanceArchivePlan plan) => plan with
    {
        Copies = plan.Copies.Where(copy => !InstanceFileChangePolicy.IsDerivedStatePath(copy.TargetPath)).ToArray(),
        Manifest = plan.Manifest with
        {
            Files = plan.Manifest.Files.Where(file => file.Path is null ||
                !InstanceFileChangePolicy.IsDerivedStatePath(file.Path)).ToArray()
        }
    };

    /// <summary>校验导入计划中的文件布局和需求清单，拒绝重复路径及清单自身被覆盖。</summary>
    private static void ValidatePlan(InstanceArchivePlan plan)
    {
        InstanceManifestStore.Validate(plan.Manifest);
        var copiedPaths = plan.Copies.Select(c => c.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        InstanceImportPaths.ValidateLayout(plan.Copies.Select(c => (c.TargetPath, c.Directory))
            .Concat(plan.Manifest.Files.Where(f => f.Path is not null && !copiedPaths.Contains(f.Path)).Select(f => (f.Path!, false)))
            .Append((InstanceImportManifest.FileName, false)));
    }

    /// <summary>只同步策略允许变化的 Prism 名称；已有来源与摘要保持不变，严格来源的配置原样保留。</summary>
    private static void SynchronizePrismName(string staging, string name, List<InstanceFileRequirement> files)
    {
        var requirement = files.SingleOrDefault(file => string.Equals(file.Path, "instance.cfg", StringComparison.OrdinalIgnoreCase));
        if (requirement is not null && !InstanceFileChangePolicy.IsMutableConfiguration(requirement, ".minecraft", "Prism"))
            return;
        var path = Path.Combine(staging, "instance.cfg");
        if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Prism 配置过大。");
        var original = File.Exists(path) ? File.ReadAllText(path) : "";
        File.WriteAllText(path, MinecraftInstanceManager.UpdatePrismName(original, name));
        // 名称属于允许的元数据变化，不重建已有基线；仅为原包未携带的配置建立首次声明。
        if (requirement is not null) return;
        using var stream = File.OpenRead(path);
        files.Add(new InstanceFileRequirement
        {
            Path = "instance.cfg", Source = InstanceFileSource.Embedded, Size = stream.Length,
            Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() },
            // 由 EML 新建的配置没有原压缩条目。
            ArchiveEntry = null
        });
    }

    /// <summary>清理本次导入的临时目录；若清理失败，记录路径并保留原始导入错误。</summary>
    private static void Cleanup(string staging)
    {
        try
        {
            InstanceImportPaths.EnsureNoLinks(staging);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不掩盖导入错误；保留的目录不在 instances 下，不会被误识别成完整实例。
            Trace.TraceWarning("无法清理导入临时目录 {0}：{1}", staging, ex.Message);
        }
    }
}
