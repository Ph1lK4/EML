using System.Buffers;
using System.Security.Cryptography;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Modrinth;
using EML.Infrastructure.Minecraft.CurseForge;
using EML.Infrastructure.Minecraft.Prism;

namespace EML.Infrastructure.Minecraft;

/// <summary>仅检查启动器 instances 目录下的一个实例，依据 EML 清单读取文件，不修改用户数据。</summary>
public sealed class MinecraftInstanceChecker : IMinecraftInstanceChecker
{
    private readonly string _instancesRoot;

    /// <summary>固定检查范围；所有实例都必须是此启动器 instances 目录的直接子目录。</summary>
    public MinecraftInstanceChecker(string launcherRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherRoot);
        _instancesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(launcherRoot, "instances")));
    }

    /// <summary>将文件系统和哈希工作放到后台执行，避免大量小文件阻塞 WPF 界面。</summary>
    public Task<InstanceCheckReport> CheckAsync(string instancePath, InstanceCheckMode mode = InstanceCheckMode.Full,
        IProgress<InstanceCheckProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancePath);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return Task.Run(() => CheckCoreAsync(instancePath, mode, progress, cancellationToken), cancellationToken);
    }

    /// <summary>验证检查范围，读取统一清单，然后逐个生成文件结果与检查范围说明。</summary>
    private async Task<InstanceCheckReport> CheckCoreAsync(string instancePath, InstanceCheckMode mode,
        IProgress<InstanceCheckProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = ValidateInstance(instancePath);
        var manifestPath = Path.Combine(root, InstanceImportManifest.FileName);
        InstanceImportManifest manifest;
        try
        {
            var attributes = File.GetAttributes(manifestPath);
            if ((attributes & FileAttributes.Directory) != 0)
                throw new IOException("EML 清单路径实际是一个文件夹。");
            manifest = InstanceManifestStore.Read(manifestPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 只有明确的不存在才属于缺少清单；拒绝访问等错误必须上报给调用方。
            token.ThrowIfCancellationRequested();
            progress?.Report(new InstanceCheckProgress(0, 0, "没有检查清单"));
            return new InstanceCheckReport
            {
                InstancePath = root, Mode = mode, CheckedAt = DateTimeOffset.Now, HasManifest = false,
                Notes = ["实例没有 eml-instance.json，缺少预期文件清单，无法判断哪些文件缺失或内容是否正确。"]
            };
        }

        var runtimeIssues = new List<string>();
        var runtimeNotes = new List<string>();
        var requirements = ReadEffectiveRequirements(root, manifest, runtimeIssues, runtimeNotes)
            .Select(file => (File: file, Runtime: false)).ToList();
        var isPrism = manifest.SourceFormat.Equals("Prism", StringComparison.OrdinalIgnoreCase);
        var isModrinth = manifest.SourceFormat.Equals("Modrinth", StringComparison.OrdinalIgnoreCase);
        var isCurseForge = manifest.SourceFormat.Equals("CurseForge", StringComparison.OrdinalIgnoreCase);
        var runtimeFormat = isPrism ? "Prism" : isModrinth ? "Modrinth" : isCurseForge ? "CurseForge" : null;
        string? launchProfile = null;
        if (runtimeFormat is not null)
            launchProfile = await AddRuntimeRequirementsAsync(root, manifest, requirements, runtimeIssues, runtimeNotes, token).ConfigureAwait(false);
        var results = new List<InstanceFileCheckResult>(requirements.Count);
        var reporter = new CheckProgressReporter(progress, requirements.Count);
        reporter.Report(0, "读取检查清单", force: true);
        var lastFile = "检查完成";
        foreach (var (requirement, isRuntime) in requirements)
        {
            token.ThrowIfCancellationRequested();
            var fileName = requirement.Path ?? $"CurseForge {requirement.ProjectId}/{requirement.FileId}";
            reporter.Report(results.Count, fileName);
            results.Add((await CheckFileAsync(root, requirement, mode, reporter, results.Count, token).ConfigureAwait(false))
                with { IsRuntimeFile = isRuntime,
                    IsAllowedConfigurationChange = InstanceFileChangePolicy.IsMutableConfiguration(requirement, manifest.GameDirectory) });
            reporter.Report(results.Count, fileName);
            lastFile = fileName;
        }
        token.ThrowIfCancellationRequested();
        // 再次验证整条祖先链，避免检查期间实例目录被换成链接后仍返回可信报告。
        // 路径检查无法消除任意外部进程反复替换目录的竞态；调用方也应避免同时移动实例。
        ValidateInstance(root);
        reporter.Report(results.Count, lastFile, force: true);
        var notes = new List<string>
        {
            "检查导入清单及已解析的运行依赖；未声明的额外文件不会被删除或判断为错误。",
            "包内文件的哈希是导入时快照；配置、存档等导入后变化不等同于损坏。",
            runtimeFormat is not null ? $"{runtimeFormat} 已接入运行依赖解析；未取得的元数据和不支持的组件见下方，Java 和真实启动结果仍需启动模块验证。" :
                "此格式的 Minecraft 本体、加载器和 Java 运行依赖尚未展开，文件校验通过不代表实例一定能启动。"
        };
        notes.AddRange(runtimeNotes);
        notes.AddRange(runtimeIssues);
        if (mode == InstanceCheckMode.Quick) notes.Add("本次为快速检查，只确认路径和声明的大小，没有校验文件内容。");
        if (manifest.Files.Count == 0) notes.Add("清单没有具体文件需求，当前没有可校验的文件依据。");
        return new InstanceCheckReport
        {
            InstancePath = root, Mode = mode, CheckedAt = DateTimeOffset.Now, HasManifest = true,
            Files = results, Components = manifest.Components, Notes = notes, IsPrismRuntime = isPrism,
            RuntimeSourceFormat = runtimeFormat, RuntimeIssues = runtimeIssues, LaunchProfilePath = launchProfile
        };
    }

    /// <summary>按来源格式只读展开本地元数据、资源索引及独立状态，不在检查按钮中访问网络。</summary>
    private static async Task<string?> AddRuntimeRequirementsAsync(string root, InstanceImportManifest manifest,
        List<(InstanceFileRequirement File, bool Runtime)> requirements, List<string> issues, List<string> notes, CancellationToken token)
    {
        try
        {
            IPrismRuntimeResolver resolver = manifest.SourceFormat.Equals("CurseForge", StringComparison.OrdinalIgnoreCase)
                ? new CurseForgeRuntimeResolver()
                : manifest.SourceFormat.Equals("Modrinth", StringComparison.OrdinalIgnoreCase)
                    ? new ModrinthRuntimeResolver() : new PrismRuntimeResolver();
            var plan = resolver.Resolve(root, manifest);
            issues.AddRange(plan.BlockingIssues);
            notes.AddRange(plan.Notes);
            foreach (var request in plan.MetadataRequests)
                issues.Add($"需要下载组件元数据：{request.Id} {request.Version}。请执行“补全实例依赖”。");
            foreach (var file in plan.Files)
                requirements.Add((file.Requirement, true));
            var index = plan.Files.FirstOrDefault(file => file.Role == PrismRuntimeFileRole.AssetIndex);
            if (index is not null)
            {
                var indexPath = InstanceImportPaths.Within(root, index.Requirement.Path!);
                InstanceImportPaths.EnsureNoLinks(indexPath);
                if (await PrismInstanceInstaller.ExistingFileMatchesAsync(indexPath, index.Requirement, token).ConfigureAwait(false))
                    foreach (var file in new PrismRuntimeMaterializer().ExpandAssets(plan))
                        requirements.Add((file.Requirement, true));
                else issues.Add("资源索引尚未下载，无法列出全部资源对象；补全时会下载索引并继续展开。");
            }
            var state = PrismRuntimeStateStore.Read(plan);
            if (state is null)
            {
                issues.Add("当前组件尚未完成启动配置准备，请执行“补全实例依赖”。");
                return null;
            }
            foreach (var file in state.GeneratedFiles) requirements.Add((file, true));
            return InstanceImportPaths.Within(root, state.LaunchProfilePath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            issues.Add(manifest.SourceFormat + " 运行依赖未能完整解析：" + ex.Message);
            return null;
        }
    }

    /// <summary>只读合并 CurseForge 的已解析缓存；文件检查不需要授权，也不会发出网络请求。</summary>
    private static IReadOnlyList<InstanceFileRequirement> ReadEffectiveRequirements(string root,
        InstanceImportManifest manifest, List<string> issues, List<string> notes)
    {
        if (!manifest.SourceFormat.Equals("CurseForge", StringComparison.OrdinalIgnoreCase)) return manifest.Files;
        try
        {
            var cached = CurseForgeFileStateStore.Read(root, manifest);
            if (cached is null)
            {
                notes.Add("CurseForge 文件引用尚未解析，请执行“补全实例依赖”，获取清单指定版本的文件信息。");
                return manifest.Files;
            }
            var entries = cached.Files.ToDictionary(f => (f.Requirement.ProjectId, f.Requirement.FileId));
            var result = new List<InstanceFileRequirement>(manifest.Files.Count);
            foreach (var file in CurseForgeFileStateStore.EffectiveRequirements(manifest, cached))
            {
                if (file.Source != InstanceFileSource.CurseForge || !entries.TryGetValue((file.ProjectId, file.FileId), out var resolved))
                { result.Add(file); continue; }
                result.Add(resolved.Requirement);
                if (!string.IsNullOrEmpty(resolved.BlockReason) && file.Required)
                    issues.Add($"CurseForge {file.ProjectId}/{file.FileId}：{resolved.BlockReason}");
                if (resolved.Requirement.Downloads.Count == 0 && !string.IsNullOrEmpty(resolved.ManualDownloadUrl))
                    notes.Add($"需要手动提供时请将文件放到 {resolved.Requirement.Path ?? "待确认目录"}：{resolved.ManualDownloadUrl}");
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            issues.Add("CurseForge 文件缓存无法读取：" + ex.Message);
            return manifest.Files;
        }
    }

    /// <summary>验证实例层级和所有已有祖先，拒绝越界目录、链接目录以及不存在的实例。</summary>
    private string ValidateInstance(string instancePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instancePath));
        if (!string.Equals(Path.GetDirectoryName(root), _instancesRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("只能检查启动器 instances 目录中的直接子实例。", nameof(instancePath));
        InstanceImportPaths.EnsureNoLinks(root);
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0)
            throw new IOException("实例路径不是文件夹。");
        return root;
    }

    /// <summary>分别报告缺失、无法读取和内容变化；可选属性始终保留在原文件需求中。</summary>
    private static async Task<InstanceFileCheckResult> CheckFileAsync(string root, InstanceFileRequirement requirement,
        InstanceCheckMode mode, CheckProgressReporter progress, int completed, CancellationToken token)
    {
        if (requirement.Path is null)
            return new(requirement, InstanceFileState.Unresolved, "尚未解析平台文件信息，不能确认目标路径或下载来源。");
        var opened = false;
        try
        {
            var path = InstanceImportPaths.Within(root, requirement.Path);
            // 从已有实例目录向下检查；上层缺失时立即结束，避免对每个缺失祖先反复抛异常。
            ValidateFilePath(root, requirement.Path);
            var before = Snapshot(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            opened = true;
            if (input.Length != before.Length)
                return new(requirement, InstanceFileState.ChangedDuringCheck, "文件在打开时发生变化，请重新检查。");
            if (requirement.Size is { } expectedSize && input.Length != expectedSize)
                return Difference(requirement, InstanceFileState.SizeMismatch, $"预期 {expectedSize} 字节，实际 {input.Length} 字节。");
            if (mode == InstanceCheckMode.Quick || requirement.Hashes.Count == 0)
            {
                if (before != Snapshot(path))
                    return new(requirement, InstanceFileState.ChangedDuringCheck, "文件在检查时发生变化，请重新检查。");
                return new(requirement, InstanceFileState.PresentUnverified,
                    mode == InstanceCheckMode.Quick
                        ? requirement.Size.HasValue ? "文件存在，大小符合声明；快速检查未读取内容哈希。" : "文件存在；清单没有声明大小，快速检查未验证内容。"
                        : "文件存在，但清单没有哈希，无法确认内容正确。");
            }

            var mismatches = await HashAsync(input, requirement, progress, completed, token).ConfigureAwait(false);
            ValidateFilePath(root, requirement.Path);
            var after = Snapshot(path);
            if (before != after || input.Length != before.Length)
                return new(requirement, InstanceFileState.ChangedDuringCheck, "检查过程中大小或修改时间发生变化，请在游戏关闭后重新检查。");
            if (mismatches.Count > 0)
                return Difference(requirement, InstanceFileState.HashMismatch, "以下内容哈希与清单不符：" + string.Join("、", mismatches) + "。");
            return new(requirement, InstanceFileState.Verified, "所有已声明哈希均与文件内容一致。");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(requirement, opened ? InstanceFileState.ChangedDuringCheck : InstanceFileState.Missing,
                opened ? "文件在检查过程中消失，请重新检查。" : "清单声明的文件不存在。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(requirement, InstanceFileState.Unreadable, "无法安全读取文件：" + ex.Message);
        }
    }

    /// <summary>逐层拒绝符号链接、目录联接和把目录当文件的情况；权限错误不被当作缺失。</summary>
    private static void ValidateFilePath(string root, string relative)
    {
        var current = root;
        var rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0 || (rootAttributes & FileAttributes.Directory) == 0)
            throw new IOException("实例目录已变化或包含链接。");
        var segments = relative.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("路径含符号链接或目录联接。");
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (i < segments.Length - 1 && !isDirectory) throw new IOException("文件路径中的父目录实际是一个文件。");
            if (i == segments.Length - 1 && isDirectory) throw new IOException("预期文件的位置实际是一个文件夹。");
        }
    }

    /// <summary>获取长度与修改时间快照；显式读取属性以保留拒绝访问等真实错误。</summary>
    private static (long Length, DateTime Modified) Snapshot(string path)
    {
        _ = File.GetAttributes(path);
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>一次流式读取同时计算全部声明哈希，复用缓冲区并及时响应取消。</summary>
    private static async Task<IReadOnlyList<string>> HashAsync(FileStream input, InstanceFileRequirement requirement,
        CheckProgressReporter progress, int completed, CancellationToken token)
    {
        var hashes = new List<(string Name, IncrementalHash Hasher)>();
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            foreach (var name in requirement.Hashes.Keys)
                hashes.Add((name, IncrementalHash.CreateHash(name switch
                {
                    "sha1" => HashAlgorithmName.SHA1, "sha256" => HashAlgorithmName.SHA256,
                    "sha512" => HashAlgorithmName.SHA512, _ => throw new InvalidDataException("不支持的哈希算法。")
                })));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                foreach (var (_, hasher) in hashes) hasher.AppendData(buffer, 0, read);
                progress.Report(completed, requirement.Path!);
            }
            token.ThrowIfCancellationRequested();
            return hashes.Where(pair => !Convert.ToHexString(pair.Hasher.GetHashAndReset())
                    .Equals(requirement.Hashes[pair.Name], StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Name).ToArray();
        }
        finally
        {
            foreach (var (_, hasher) in hashes) hasher.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>包内文件差异仅表示导入后变化，避免把用户配置或存档修改误报为损坏。</summary>
    private static InstanceFileCheckResult Difference(InstanceFileRequirement requirement, InstanceFileState state, string message) =>
        requirement.Source == InstanceFileSource.Embedded
            ? new(requirement, InstanceFileState.ChangedSinceImport, "与导入时快照不同；配置或存档的正常修改也会产生此结果。" + message)
            : new(requirement, state, message);

    /// <summary>文件和哈希读取共用进度节流，避免大量小文件把 WPF 消息队列塞满。</summary>
    private sealed class CheckProgressReporter(IProgress<InstanceCheckProgress>? progress, int total)
    {
        private long _lastReported;

        /// <summary>普通进度最多每 100 毫秒发送一次，开始与结束强制发送保证界面状态准确。</summary>
        public void Report(int completed, string fileName, bool force = false)
        {
            if (progress is null) return;
            var now = Environment.TickCount64;
            if (!force && now - _lastReported < 100) return;
            _lastReported = now;
            progress.Report(new InstanceCheckProgress(completed, total, fileName));
        }
    }
}
