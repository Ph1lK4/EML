using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.External;

/// <summary>以独立副本接管其他启动器的实例；不创建中间 ZIP，不移动或改写源目录。</summary>
public sealed class MinecraftFolderImporter : IMinecraftFolderImporter
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly IReadOnlyDictionary<string, string> SizingHashes =
        new Dictionary<string, string> { ["sha256"] = new('0', 64) };
    private static readonly HashSet<string> PrivateNames = new(Paths)
    {
        "accounts.json", "account.json", "launcher_accounts.json", "launcher_profiles.json",
        "launcher_profiles_microsoft_store.json", "launcher_msa_credentials.bin", "auth.json", "auth.db",
        "tokens.json", "credentials.json", "credentials.bin", "msa.json", "app.db", "app.db-wal", "app.db-shm",
        ".eml-runtime", ".eml-imports", ".eml-build", ".git", ".vs"
    };
    private readonly string _launcherRoot;
    private readonly string _instancesRoot;
    private readonly InstanceArchiveLimits _limits;
    private readonly Func<ExternalMinecraftInstance, CancellationToken, ExternalInstanceDefinition> _read;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>把新实例存入指定启动器的 instances，来源结构由独立适配器重新读取。</summary>
    public MinecraftFolderImporter(string launcherRoot) : this(launcherRoot, ExternalInstanceReader.Read,
        InstanceArchiveLimits.Default) { }

    /// <summary>测试可替换只读适配器和额度，不向生产界面提供放宽安全限制的选项。</summary>
    internal MinecraftFolderImporter(string launcherRoot,
        Func<ExternalMinecraftInstance, CancellationToken, ExternalInstanceDefinition> read, InstanceArchiveLimits limits)
    {
        _launcherRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(launcherRoot));
        _instancesRoot = Path.Combine(_launcherRoot, "instances");
        _read = read;
        _limits = limits;
    }

    /// <summary>只统计将被复制的文件并校验布局与空间；预览不会生成暂存目录。</summary>
    public Task<FolderInstanceImportPreview> InspectAsync(ExternalMinecraftInstance instance,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var plan = CreatePlan(instance, null, cancellationToken);
        CheckSpace(plan.TotalBytes);
        return new FolderInstanceImportPreview(plan.Definition.Instance,
            new(plan.Manifest.Name, plan.Manifest.SourceFormat, plan.Manifest.MinecraftVersion,
                plan.Manifest.Components, plan.Entries.Count(entry => !entry.Directory), 0, 0, plan.Manifest.Notes),
            plan.TotalBytes, _instancesRoot, plan.SkippedItems);
    }, cancellationToken);

    /// <summary>串行执行复制；预览后再次读取源数据，支持取消而不留下可被扫描的半成品。</summary>
    public async Task<InstanceImportResult> ImportAsync(ExternalMinecraftInstance instance, string instanceName,
        IProgress<InstanceImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => CopyAsync(instance, instanceName, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _operationLock.Release(); }
    }

    /// <summary>在私有暂存目录边复制边计算 SHA-256，确认源快照未变化后原子提交新实例。</summary>
    private async Task<InstanceImportResult> CopyAsync(ExternalMinecraftInstance instance, string name,
        IProgress<InstanceImportProgress>? progress, CancellationToken token)
    {
        name = InstanceImportPaths.InstanceName(name);
        var destination = InstanceImportPaths.Within(_instancesRoot, name);
        InstanceImportPaths.EnsureNoLinks(destination);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("同名实例或文件已经存在，请使用其他名称：" + destination);
        var plan = CreatePlan(instance, name, token);
        CheckSpace(plan.TotalBytes);
        var stagingParent = Path.Combine(_launcherRoot, ".eml-imports");
        var staging = Path.Combine(stagingParent, Guid.NewGuid().ToString("N"));
        InstanceImportPaths.EnsureNoLinks(staging);
        var committed = false;
        var locks = new List<FileStream>();
        try
        {
            // 在副本准备期间阻止已知世界锁文件被写入；不能替代用户关闭所有外部游戏。
            AcquireWorldLocks(plan, locks);
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(staging);
            long copied = 0;
            // 已有来源的副本沿用原始及用户明确选择的要求；复制成功不等于重新认证当前内容。
            var requirements = plan.Definition.PreservedSource is null
                ? new List<InstanceFileRequirement>() : plan.Manifest.Files.ToList();
            var watch = Stopwatch.StartNew();
            var buffer = new byte[128 * 1024];
            progress?.Report(new("准备复制文件夹（不会修改原实例）", 0, plan.TotalBytes));
            foreach (var entry in plan.Entries)
            {
                token.ThrowIfCancellationRequested();
                var target = InstanceImportPaths.Within(staging, entry.TargetPath);
                InstanceImportPaths.EnsureNoLinks(target);
                if (entry.Directory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (entry.Generated is null) EnsureEntryUnchanged(entry);
                using var source = entry.Generated is { } content ? (Stream)new MemoryStream(content, writable: false) :
                    new FileStream(entry.SourcePath!, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long length = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    if (count > entry.Length - length)
                        throw new IOException("源文件在复制期间变大，请关闭游戏后重试：" + entry.SourcePath);
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    length += count;
                    copied += count;
                    if (watch.ElapsedMilliseconds >= 100)
                    {
                        progress?.Report(new(entry.TargetPath, copied, plan.TotalBytes));
                        watch.Restart();
                    }
                }
                if (length != entry.Length) throw new IOException("源文件未完整读取：" + entry.SourcePath);
                if (entry.Generated is null) EnsureEntryUnchanged(entry);
                if (plan.Definition.PreservedSource is null)
                    requirements.Add(Requirement(entry.TargetPath, length,
                        new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(hash.GetHashAndReset()) },
                        plan.Manifest.GameDirectory, plan.Manifest.SourceFormat));
            }
            var manifest = plan.Manifest with { Files = requirements };
            var encoded = InstanceManifestStore.Serialize(manifest);
            if (encoded.Length != plan.ManifestBytes.Length)
                throw new InvalidDataException("生成的实例清单长度与预检不符，未提交副本。");
            await File.WriteAllBytesAsync(Path.Combine(staging, InstanceImportManifest.FileName), encoded, token).ConfigureAwait(false);
            progress?.Report(new("正在确认源文件未变化", plan.TotalBytes, plan.TotalBytes));
            var current = CreatePlan(instance, name, token);
            EnsurePlanUnchanged(plan, current);
            if (plan.Definition.PreservedSource is { } preserved)
                InstanceBackupManifest.EnsureUnchanged(plan.Definition.Instance.SourcePath, preserved);
            token.ThrowIfCancellationRequested();
            InstanceImportPaths.EnsureNoLinks(destination);
            Directory.CreateDirectory(_instancesRoot);
            Directory.Move(staging, destination);
            committed = true;
            // 提交后不再回调或响应取消，防止已经成功的导入被界面报告为失败。
            return new(destination, manifest.GameDirectory == "." ? destination :
                InstanceImportPaths.Within(destination, manifest.GameDirectory), manifest);
        }
        finally
        {
            foreach (var handle in locks) handle.Dispose();
            if (!committed) Cleanup(staging);
        }
    }

    /// <summary>重建可信复制计划；已有 EML 来源只通过经验证的快照保留，仍拒绝目录交叠、凭证和链接。</summary>
    private CopyPlan CreatePlan(ExternalMinecraftInstance instance, string? name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        InstanceImportPaths.EnsureNoLinks(_instancesRoot);
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.SourcePath));
        InstanceImportPaths.EnsureNoLinks(sourceRoot);
        ValidateSourceBoundary(sourceRoot);
        var definition = _read(instance, token);
        if (definition.PreservedSource is null && (File.Exists(Path.Combine(sourceRoot, InstanceImportManifest.FileName)) ||
            definition.Manifest.Files.Count != 0 || definition.Manifest.MigratedPlayerFiles is not null ||
            definition.Manifest.ManagedMods is not null || definition.Manifest.AddedContentFiles is not null))
            throw new InvalidDataException("文件夹含已有来源或管理记录，不能重新赋予本地来源；请使用完整备份导入。");
        if (definition.PreservedSource is { } preserved)
        {
            InstanceBackupManifest.EnsureUnchanged(sourceRoot, preserved);
            // 私有状态已折叠入可移植清单；拒绝把凭证或启动脚本作为待补全内容带到新实例。
            foreach (var file in preserved.Manifest.Files.Concat(preserved.Requirements.Requirements))
                if (file.Path is { } path && ShouldSkipPreserved(path, preserved.Manifest.GameDirectory))
                    throw new InvalidDataException("已有来源清单包含不迁移的启动器私有文件或启动脚本，请先核对：" + path);
        }
        name = InstanceImportPaths.InstanceName(name ?? InstanceImportPaths.SuggestName(definition.Instance.Name));
        var entries = new Dictionary<string, CopyEntry>(Paths);
        var skipped = new HashSet<string>(Paths);
        var visited = 0;
        long total = 0;
        foreach (var root in definition.Roots)
        {
            token.ThrowIfCancellationRequested();
            _limits.ValidateEntryCount(++visited);
            var source = Path.GetFullPath(root.SourcePath);
            ValidateSourceBoundary(source);
            InstanceImportPaths.EnsureNoLinks(source);
            var target = root.TargetPath == "." ? "" : InstanceImportPaths.Relative(root.TargetPath);
            var attributes = File.GetAttributes(source);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                if (target.Length == 0) throw new InvalidDataException("文件不能映射为实例根目录。");
                if (ShouldSkip(Path.GetFileName(source), true)) { skipped.Add(source); continue; }
                AddEntry(source, target, false, null);
                continue;
            }
            if (target.Length > 0) AddEntry(source, target, true, null);
            var pending = new Stack<(string Directory, string Relative)>();
            pending.Push((source, ""));
            var exclusions = root.ExcludedTopLevelNames.ToHashSet(Paths);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var (directory, relativeDirectory) = pending.Pop();
                InstanceImportPaths.EnsureNoLinks(directory);
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    _limits.ValidateEntryCount(++visited);
                    var leaf = Path.GetFileName(path);
                    if (relativeDirectory.Length == 0 && exclusions.Contains(leaf)) { skipped.Add(path); continue; }
                    var relative = relativeDirectory.Length == 0 ? leaf : relativeDirectory + "/" + leaf;
                    if (ShouldSkip(relative, relativeDirectory.Length == 0) || definition.PreservedSource is not null &&
                        ShouldSkipPreserved(relative, definition.Manifest.GameDirectory)) { skipped.Add(path); continue; }
                    var itemAttributes = File.GetAttributes(path);
                    if ((itemAttributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("源实例含符号链接或目录联接，请先处理后重试：" + path);
                    var isDirectory = (itemAttributes & FileAttributes.Directory) != 0;
                    var mapped = target.Length == 0 ? relative : target + "/" + relative;
                    AddEntry(path, mapped, isDirectory, null);
                    if (isDirectory) pending.Push((path, relative));
                }
            }
        }
        foreach (var (path, value) in definition.GeneratedFiles.OrderBy(pair => pair.Key, Paths))
        {
            token.ThrowIfCancellationRequested();
            var text = path.Equals("instance.cfg", StringComparison.OrdinalIgnoreCase) && definition.Manifest.SourceFormat == "Prism"
                ? MinecraftInstanceManager.UpdatePrismName(value, name) : value;
            AddEntry(null, path, false, Encoding.UTF8.GetBytes(text));
        }
        if (definition.Manifest.GameDirectory != ".") AddParents(definition.Manifest.GameDirectory + "/placeholder");
        var ordered = entries.Values.OrderBy(entry => entry.TargetPath, Paths).ToArray();
        var manifest = definition.Manifest with
        {
            Name = name, SourceArchive = definition.PreservedSource is null ? "" : definition.Manifest.SourceArchive,
            IntegrityStatus = "NotChecked",
            Files = definition.PreservedSource is null
                ? ordered.Where(entry => !entry.Directory).Select(entry => Requirement(entry.TargetPath, entry.Length, SizingHashes,
                    definition.Manifest.GameDirectory, definition.Manifest.SourceFormat)).ToArray()
                : definition.Manifest.Files,
            Notes = definition.Manifest.Notes.Concat(definition.PreservedSource is null ? new[] {
                "从其他启动器的文件夹复制导入，原实例保持不变；当前内容按本地文件的大小和 SHA-256 建立基线，不等同于平台官方认证。",
                "未迁移启动器账号凭证和本机启动钩子。请在 EML 单独选择账号、Java 和启动设置。",
                "原目录不是自动恢复来源；缺失的本地文件需要用户重新提供，不能按文件名自动替换。"
            } : new[] {
                "从文件夹复制并保留已有 EML 来源、官方完整性要求、明确用户替换及模组管理记录；未重新信任当前磁盘内容。",
                "原清单未登记的新文件仍原样复制，但不会伪装成原包文件；缺失或变化的已登记内容交由后续检查与补全报告。",
                "未迁移启动器账号、Java 路径、启动钩子、派生运行状态及回收载荷、迁移撤销备份；这些内容仍保留在源实例。",
                "保留原包文件名和恢复条目，但不复制本机原包定位记录；原目录不是自动恢复来源。"
            }).Distinct().ToArray()
        };
        var manifestBytes = InstanceManifestStore.Serialize(manifest);
        _limits.ValidateEntryCount(entries.Count + 1);
        total = _limits.AddLength(total, manifestBytes.LongLength, false, InstanceImportManifest.FileName);
        InstanceImportPaths.ValidateLayout(ordered.Select(entry => (entry.TargetPath, entry.Directory))
            .Append((InstanceImportManifest.FileName, false)));
        return new(definition, manifest, ordered, total, manifestBytes, skipped.Order(Paths).ToArray());

        /// <summary>收录一个目标并核算实际数据量，重复目录只允许作共同父目录。</summary>
        void AddEntry(string? source, string targetPath, bool directory, byte[]? generated)
        {
            targetPath = InstanceImportPaths.Relative(targetPath);
            if (targetPath.Equals(InstanceImportManifest.FileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("不能覆盖已有 EML 来源清单，请使用完整备份导入。");
            AddParents(targetPath);
            if (entries.TryGetValue(targetPath, out var existing))
            {
                if (directory && existing.Directory)
                {
                    if (existing.SourcePath is null) entries[targetPath] = existing with { SourcePath = source };
                    else if (source is not null && !Paths.Equals(existing.SourcePath, source))
                        throw new InvalidDataException("多个来源目录映射到同一路径：" + targetPath);
                    return;
                }
                throw new InvalidDataException("目录导入有重复目标路径：" + targetPath);
            }
            var info = source is not null && !directory ? new FileInfo(source) : null;
            var length = generated?.LongLength ?? info?.Length ?? 0;
            total = _limits.AddLength(total, length, directory, targetPath);
            entries.Add(targetPath, new(source, targetPath, directory, length, info?.LastWriteTimeUtc ?? default, generated));
            _limits.ValidateEntryCount(entries.Count + 1);
        }

        /// <summary>显式计入目标父目录，确保日后导出 ZIP 的条目数不会超出相同限额。</summary>
        void AddParents(string targetPath)
        {
            var safe = InstanceImportPaths.Relative(targetPath);
            var parts = safe.Split('/');
            var parent = "";
            for (var i = 0; i < parts.Length - 1; i++)
            {
                parent = parent.Length == 0 ? parts[i] : parent + "/" + parts[i];
                if (entries.TryGetValue(parent, out var existing))
                {
                    if (!existing.Directory) throw new InvalidDataException("文件被用作父目录：" + parent);
                    continue;
                }
                entries.Add(parent, new(null, parent, true, 0, default, null));
                _limits.ValidateEntryCount(entries.Count + 1);
            }
        }
    }

    /// <summary>排除已知账号文件及启动器私有状态；根脚本不继承，游戏内脚本仍按原目录复制。</summary>
    private static bool ShouldSkip(string relative, bool topLevel)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(PrivateNames.Contains)) return true;
        return topLevel && Path.GetExtension(parts[^1]).ToLowerInvariant() is ".exe" or ".bat" or ".cmd" or ".ps1" or ".sh";
    }

    /// <summary>保留来源时仍排除根目录和游戏根目录中的启动脚本、凭证及本机派生状态。</summary>
    private static bool ShouldSkipPreserved(string relative, string gameDirectory)
    {
        var path = relative.Replace('\\', '/');
        if (ShouldSkip(path, !path.Contains('/'))) return true;
        var prefix = gameDirectory == "." ? "" : gameDirectory + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var gameRelative = path[prefix.Length..];
        return ShouldSkip(gameRelative, !gameRelative.Contains('/'));
    }

    /// <summary>不允许源数据与 EML 的实例容器或暂存容器互相包含，防止递归复制或误接管。</summary>
    private void ValidateSourceBoundary(string source)
    {
        foreach (var owned in new[] { _instancesRoot, Path.Combine(_launcherRoot, ".eml-imports") })
            if (Contains(source, owned) || Contains(owned, source))
                throw new InvalidDataException("源目录与 EML 实例或暂存目录重叠，请选择其他启动器中的独立实例：" + source);
    }

    /// <summary>使用目录分隔符比较绝对路径，避免把同名前缀的兄弟目录判断为包含。</summary>
    private static bool Contains(string parent, string child)
    {
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        return Paths.Equals(parent, child) || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>预检目标卷容量，保留少量余量；复制期间仍依赖操作系统报告真实写入错误。</summary>
    private void CheckSpace(long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(_launcherRoot)!);
        if (!drive.IsReady) throw new IOException("目标磁盘未就绪：" + _launcherRoot);
        const long reserve = 16L * 1024 * 1024;
        if (drive.AvailableFreeSpace < bytes || drive.AvailableFreeSpace - bytes < reserve)
            throw new IOException($"目标磁盘空间不足，需要至少 {bytes:N0} 字节并保留 16 MiB 余量：{_launcherRoot}");
    }

    /// <summary>已有世界锁只以只读方式持有；无法读取或仍被游戏写入时要求关闭游戏。</summary>
    private static void AcquireWorldLocks(CopyPlan plan, List<FileStream> handles)
    {
        foreach (var path in plan.Entries.Where(entry => !entry.Directory && entry.Generated is null &&
                     string.Equals(Path.GetFileName(entry.SourcePath), "session.lock", StringComparison.OrdinalIgnoreCase))
                 .Select(entry => entry.SourcePath!).Distinct(Paths))
        {
            InstanceImportPaths.EnsureNoLinks(path);
            try
            {
                var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                handles.Add(handle);
                _ = handle.ReadByte();
            }
            catch (IOException ex) { throw new IOException("世界正在使用或无法取得只读快照，请先正常关闭游戏：" + path, ex); }
        }
    }

    /// <summary>本地文件只采用本次复制记录；可变玩家数据不是启动必需项，模组、数据包和配置仍保留必要性。</summary>
    private static InstanceFileRequirement Requirement(string path, long size, IReadOnlyDictionary<string, string> hashes,
        string gameDirectory, string sourceFormat)
    {
        var requirement = new InstanceFileRequirement
        {
            Path = path, Source = InstanceFileSource.Embedded, Origin = InstanceFileOrigin.LocalFolderImport,
            Size = size, Hashes = hashes, ArchiveEntry = null
        };
        // 用户可以删除截图、旧世界和日志；不能把同目录里的模组、脚本或数据包也降为可选。
        var optionalPlayerData = InstanceFileChangePolicy.GetRole(requirement, gameDirectory, sourceFormat) == InstanceFileRole.UserData &&
            InstanceFileChangePolicy.IsMutableFile(requirement, gameDirectory, sourceFormat);
        return requirement with { Required = !optionalPlayerData };
    }

    /// <summary>逐次读取前后检查源元信息，拒绝文件被替换为链接、移除或明显改写。</summary>
    private static void EnsureEntryUnchanged(CopyEntry entry)
    {
        InstanceImportPaths.EnsureNoLinks(entry.SourcePath!);
        var info = new FileInfo(entry.SourcePath!);
        if (!info.Exists || info.Length != entry.Length || info.LastWriteTimeUtc != entry.LastWriteTimeUtc)
            throw new IOException("源文件在复制期间发生变化，请关闭游戏后重试：" + entry.SourcePath);
    }

    /// <summary>最后重新枚举，拒绝复制期间的增删文件、元数据变化或来源布局变化。</summary>
    private static void EnsurePlanUnchanged(CopyPlan expected, CopyPlan current)
    {
        if (!expected.ManifestBytes.AsSpan().SequenceEqual(current.ManifestBytes) || expected.Entries.Count != current.Entries.Count)
            throw new IOException("源实例在复制期间发生变化，未提交副本。请关闭游戏和其他启动器后重试。");
        for (var i = 0; i < expected.Entries.Count; i++)
        {
            var left = expected.Entries[i];
            var right = current.Entries[i];
            if (!Paths.Equals(left.SourcePath, right.SourcePath) || left.TargetPath != right.TargetPath ||
                left.Directory != right.Directory || left.Length != right.Length || left.LastWriteTimeUtc != right.LastWriteTimeUtc ||
                !(left.Generated ?? []).AsSpan().SequenceEqual(right.Generated ?? []))
                throw new IOException("源实例在复制期间发生变化，未提交副本：" + (right.SourcePath ?? right.TargetPath));
        }
    }

    /// <summary>只清理本次随机生成的暂存目录，失败时保留原错误并记录清理位置。</summary>
    private static void Cleanup(string staging)
    {
        try
        {
            InstanceImportPaths.EnsureNoLinks(staging);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("无法清理目录导入暂存区 {0}：{1}", staging, ex.Message); }
    }

    /// <summary>固定源文件状态和生成内容，以便复制前后复查。</summary>
    private sealed record CopyEntry(string? SourcePath, string TargetPath, bool Directory, long Length,
        DateTime LastWriteTimeUtc, byte[]? Generated);

    /// <summary>保存一次完整的只读计划；清单长度也计入未来可恢复备份的额度。</summary>
    private sealed record CopyPlan(ExternalInstanceDefinition Definition, InstanceImportManifest Manifest,
        IReadOnlyList<CopyEntry> Entries, long TotalBytes, byte[] ManifestBytes, IReadOnlyList<string> SkippedItems);
}
