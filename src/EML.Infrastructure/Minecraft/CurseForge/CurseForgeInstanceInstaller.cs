using System.Text.Json;
using EML.Core.CurseForge;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.CurseForge.Api;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Prism;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Infrastructure.Minecraft.CurseForge;

/// <summary>解析 CurseForge 固定文件引用，补齐必需内容后再交给共享游戏运行安装流程。</summary>
public sealed class CurseForgeInstanceInstaller : ICurseForgeInstanceInstaller
{
    private readonly string _instancesRoot;
    private readonly IFileDownloader _downloader;
    private readonly CurseForgeFileResolver _resolver;
    private readonly IPrismInstanceInstaller _runtimeInstaller;
    private readonly int _maxConcurrentFiles;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>复用通用下载器和官方 API 客户端；允许测试注入 API 和运行阶段。</summary>
    public CurseForgeInstanceInstaller(string launcherRoot, IFileDownloader downloader,
        ICurseForgeApiClient? apiClient = null, IPrismInstanceInstaller? runtimeInstaller = null, int maxConcurrentFiles = 4)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        if (maxConcurrentFiles is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maxConcurrentFiles));
        _instancesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(launcherRoot, "instances")));
        _downloader = downloader;
        _resolver = new CurseForgeFileResolver(apiClient ?? new CurseForgeApiClient());
        _runtimeInstaller = runtimeInstaller ?? new InstanceRuntimeInstaller(launcherRoot, downloader,
            new CurseForgeRuntimeResolver(), "CurseForge");
        _maxConcurrentFiles = maxConcurrentFiles;
    }

    /// <summary>串行管理实例安装，等待期间和工作期间取消都返回可处理的状态。</summary>
    public async Task<PrismInstallResult> InstallAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var acquired = false;
        try
        {
            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            return await Task.Run(() => InstallCoreAsync(instancePath, progress, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PrismInstallResult { SourceFormat = "CurseForge", IsCanceled = true };
        }
        finally { if (acquired) _operationLock.Release(); }
    }

    /// <summary>解析并校验必需文件，保留所有成功记录；仅在整合包内容完整时继续游戏运行准备。</summary>
    private async Task<PrismInstallResult> InstallCoreAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress, CancellationToken token)
    {
        var records = new List<InstanceDownloadFileResult>();
        var issues = new List<string>();
        var notes = new List<string>();
        string? launchPath = null;
        var canceled = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var root = ValidateInstance(instancePath);
            var manifest = InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName));
            if (!manifest.SourceFormat.Equals("CurseForge", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("此安装器只处理 CurseForge 实例。");
            var fingerprint = CurseForgeFileStateStore.Fingerprint(manifest);
            progress?.Report(new("解析 CurseForge 文件列表", 0, 0, ""));
            var resolution = await _resolver.ResolveAsync(root, manifest, token).ConfigureAwait(false);
            var resolvedById = resolution.Files.ToDictionary(file => (file.Requirement.ProjectId, file.Requirement.FileId));
            var required = CurseForgeFileStateStore.EffectiveRequirements(manifest, resolution).Where(file => file.Required).ToArray();
            var optionalCount = manifest.Files.Count(file => !file.Required);
            if (optionalCount > 0) notes.Add($"已跳过 {optionalCount} 个可选文件；可选模组选择功能尚未启用。");
            var completed = 0;
            var gate = new object();
            await Parallel.ForEachAsync(required, new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxConcurrentFiles, CancellationToken = token
            }, async (file, fileToken) =>
            {
                var resolved = file.Source == InstanceFileSource.CurseForge
                    ? resolvedById[(file.ProjectId, file.FileId)] : null;
                var effective = resolved?.Requirement ?? file;
                InstanceDownloadFileResult? result = null;
                try
                {
                    result = await EnsureFileAsync(root, manifest.GameDirectory, effective, resolved,
                        new InlineProgress<FileDownloadProgress>(value => progress?.Report(new(
                            "下载 CurseForge 整合包文件", Volatile.Read(ref completed), required.Length,
                            effective.Path ?? $"{effective.ProjectId}/{effective.FileId}", value))),
                        note => { lock (gate) notes.Add(note); }, fileToken).ConfigureAwait(false);
                }
                finally
                {
                    lock (gate)
                    {
                        if (result is not null)
                        {
                            records.Add(result);
                            if (result.State is InstanceDownloadState.Failed or InstanceDownloadState.Unavailable)
                                issues.Add(result.PathDisplay + "：" + result.Message);
                        }
                        completed++;
                        progress?.Report(new("检查 CurseForge 整合包文件", completed, required.Length, effective.Path ?? ""));
                    }
                }
            }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (issues.Count > 0) return Result();
            ValidateInstance(root);
            CurseForgeFileStateStore.EnsureManifestUnchanged(root, fingerprint);
            var runtime = await _runtimeInstaller.InstallAsync(root, progress, token).ConfigureAwait(false);
            records.AddRange(runtime.Files);
            issues.AddRange(runtime.Issues);
            notes.AddRange(runtime.Notes);
            canceled = runtime.IsCanceled;
            if (canceled) return Result();
            token.ThrowIfCancellationRequested();
            CurseForgeFileStateStore.EnsureManifestUnchanged(root, fingerprint);
            launchPath = runtime.LaunchProfilePath;
            if (launchPath is null && issues.Count == 0) issues.Add("运行依赖安装未生成启动配置，请查看组件解析结果。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; }
        catch (Exception ex) when (IsExpectedFailure(ex)) { issues.Add(ex.Message); }
        return Result();

        /// <summary>汇总结果，必需模组缺失、运行失败或取消均不会被标记为完整可运行。</summary>
        PrismInstallResult Result() => new()
        {
            SourceFormat = "CurseForge", Files = records.ToArray(), Issues = issues.Distinct().ToArray(),
            Notes = notes.Distinct().ToArray(), LaunchProfilePath = launchPath, IsCanceled = canceled
        };
    }

    /// <summary>先验证现有文件，允许人工放入被限制分发的文件；只下载 API 明确授权且有 SHA-1 的内容。</summary>
    private async Task<InstanceDownloadFileResult?> EnsureFileAsync(string root, string gameDirectory,
        InstanceFileRequirement file, CurseForgeResolvedFile? resolved, IProgress<FileDownloadProgress>? progress,
        Action<string> addNote, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            ValidateInstance(root);
            if (resolved?.BlockReason is { } reason)
                return new(file, InstanceDownloadState.Unavailable, reason + " 官方文件页面：" + resolved.ManualDownloadUrl);
            var path = InstanceImportPaths.Within(root, file.Path ?? throw new InvalidDataException("文件缺少目标路径。"));
            InstanceImportPaths.EnsureNoLinks(path);
            try
            {
                if (await PrismInstanceInstaller.ExistingFileMatchesAsync(path, file, token).ConfigureAwait(false)) return null;
            }
            catch (InvalidDataException) when (InstanceFileChangePolicy.IsMutableConfiguration(file, gameDirectory))
            {
                addNote("已保留导入后修改的配置：" + file.Path);
                return null;
            }
            if (file.Source == InstanceFileSource.Embedded)
                return new(file, InstanceDownloadState.Unavailable, "缺少包内文件，请从原 CurseForge 压缩包恢复。");
            if (file.Source != InstanceFileSource.CurseForge || resolved is null)
                return new(file, InstanceDownloadState.Unavailable, "CurseForge 导入清单包含不支持的文件来源。");
            if (file.Downloads.Count == 0)
                return new(file, InstanceDownloadState.Unavailable,
                    $"作者限制第三方分发或平台未提供下载地址。请从官方文件页面 {resolved.ManualDownloadUrl} 下载指定版本并放入 {file.Path}，再执行补全。");
            if (!file.Hashes.ContainsKey("sha1"))
                return new(file, InstanceDownloadState.Unavailable, "CurseForge 文件缺少 SHA-1，无法校验指定版本。");
            var download = await _downloader.DownloadAsync(new FileDownloadRequest
            {
                DestinationPath = path, Urls = file.Downloads, ExpectedSize = file.Size, Hashes = file.Hashes
            }, progress, token).ConfigureAwait(false);
            if (!download.HashVerified) throw new InvalidDataException("下载器未确认 CurseForge 文件 SHA-1。");
            return new(file, InstanceDownloadState.Downloaded, "已下载并校验 CurseForge 固定版本文件。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsExpectedFailure(ex)) { return new(file, InstanceDownloadState.Failed, ex.Message); }
    }

    /// <summary>仅允许处理 launcherRoot/instances 的直接子目录并拒绝所有链接。</summary>
    private string ValidateInstance(string instancePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instancePath));
        if (!string.Equals(Path.GetDirectoryName(root), _instancesRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能补全 instances 的直接子实例。");
        InstanceImportPaths.EnsureNoLinks(root);
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) throw new IOException("实例路径不是目录。");
        return root;
    }

    /// <summary>只将预期的网络、磁盘和格式错误显示为安装问题，取消单独处理。</summary>
    private static bool IsExpectedFailure(Exception ex) => ex is IOException or InvalidDataException or
        HttpRequestException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
        TimeoutException or JsonException;

    /// <summary>同步转发单文件进度，由上层界面负责线程切换。</summary>
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        /// <summary>转发一个下载状态，不捕获后台同步上下文。</summary>
        public void Report(T value) => callback(value);
    }
}
