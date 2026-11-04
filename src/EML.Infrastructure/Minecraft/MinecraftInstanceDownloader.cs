using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Modrinth;
using EML.Infrastructure.Minecraft.CurseForge;
using EML.Infrastructure.Minecraft.Prism;

namespace EML.Infrastructure.Minecraft;

/// <summary>连接实例清单检查与通用下载器；仅补齐缺失的必需文件，不猜测平台下载地址。</summary>
public sealed class MinecraftInstanceDownloader : IMinecraftInstanceDownloader
{
    private readonly string _instancesRoot;
    private readonly IMinecraftInstanceChecker _checker;
    private readonly IFileDownloader _downloader;
    private readonly IPrismInstanceInstaller _prismInstaller;
    private readonly IModrinthInstanceInstaller _modrinthInstaller;
    private readonly ICurseForgeInstanceInstaller _curseForgeInstaller;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>复用页面的检查器和下载器，所有写入限制在启动器的 instances 直接子目录。</summary>
    public MinecraftInstanceDownloader(string launcherRoot, IMinecraftInstanceChecker checker, IFileDownloader downloader,
        IPrismInstanceInstaller? prismInstaller = null, IModrinthInstanceInstaller? modrinthInstaller = null,
        ICurseForgeInstanceInstaller? curseForgeInstaller = null)
    {
        _instancesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(launcherRoot, "instances")));
        _checker = checker;
        _downloader = downloader;
        _prismInstaller = prismInstaller ?? new PrismInstanceInstaller(launcherRoot, downloader);
        _modrinthInstaller = modrinthInstaller ?? new ModrinthInstanceInstaller(launcherRoot, downloader);
        _curseForgeInstaller = curseForgeInstaller ?? new CurseForgeInstanceInstaller(launcherRoot, downloader);
    }

    /// <summary>同一服务的下载依次执行；失败文件单独记录，取消则停止余下任务并保留已完成文件。</summary>
    public async Task<InstanceDownloadReport> DownloadMissingAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await DownloadCoreAsync(instancePath, progress, cancellationToken).ConfigureAwait(false); }
        finally { _operationLock.Release(); }
    }

    /// <summary>重新读取检查依据、筛选可下载文件，完成后完整复查；不采用界面缓存的旧检查结果。</summary>
    private async Task<InstanceDownloadReport> DownloadCoreAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = ValidateInstance(instancePath);
        PrismInstallResult? runtime = null;
        // 平台安装器负责各自模组清单；通用阶段只复查，避免重复下载或绕过分发限制。
        var manifestPath = Path.Combine(root, InstanceImportManifest.FileName);
        var sourceFormat = File.Exists(manifestPath) ? InstanceManifestStore.Read(manifestPath).SourceFormat : null;
        var isModrinth = string.Equals(sourceFormat, "Modrinth", StringComparison.OrdinalIgnoreCase);
        var isCurseForge = string.Equals(sourceFormat, "CurseForge", StringComparison.OrdinalIgnoreCase);
        if (isModrinth || isCurseForge || string.Equals(sourceFormat, "Prism", StringComparison.OrdinalIgnoreCase))
        {
            runtime = isCurseForge
                ? await _curseForgeInstaller.InstallAsync(root, progress, token).ConfigureAwait(false)
                : isModrinth
                ? await _modrinthInstaller.InstallAsync(root, progress, token).ConfigureAwait(false)
                : await _prismInstaller.InstallAsync(root, progress, token).ConfigureAwait(false);
            if (runtime.IsCanceled) return new(runtime.Files, null, IsCanceled: true) { Runtime = runtime };
        }
        var results = new List<InstanceDownloadFileResult>(runtime?.Files ?? []);
        try
        {
            var before = await _checker.CheckAsync(root, InstanceCheckMode.Quick,
                new ForwardProgress<InstanceCheckProgress>(p => progress?.Report(new("准备", p.CompletedFiles, p.TotalFiles, p.FileName, Check: p))), token)
                .ConfigureAwait(false);
            var pending = new List<InstanceFileRequirement>();
            foreach (var file in before.Files.Where(f => !isModrinth && !isCurseForge && f.Requirement.Required && !f.IsRuntimeFile))
            {
                token.ThrowIfCancellationRequested();
                var requirement = file.Requirement;
                if (file.State == InstanceFileState.Missing)
                {
                    if (requirement.Source == InstanceFileSource.DirectDownload && requirement.Path is not null &&
                        requirement.Downloads.Count > 0 && requirement.Hashes.Count > 0)
                        pending.Add(requirement);
                    else results.Add(new(requirement, InstanceDownloadState.Unavailable, MissingSourceReason(requirement)));
                }
                else if (InstanceFileStatePolicy.IsBlocking(file))
                    results.Add(new(requirement, InstanceDownloadState.Unavailable,
                        file.State == InstanceFileState.Unresolved ? "平台文件引用尚未解析，不能确定下载目标。" :
                        "保留现有内容，不自动替换：" + file.Message));
            }

            for (var index = 0; index < pending.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                // 固定当前序号，避免进度回调读取到下一次循环的值。
                var completed = index;
                var file = pending[index];
                progress?.Report(new("下载", completed, pending.Count, file.Path!));
                try
                {
                    ValidateInstance(root);
                    var destination = InstanceImportPaths.Within(root, file.Path!);
                    InstanceImportPaths.EnsureNoLinks(destination);
                    var downloaded = await _downloader.DownloadAsync(new FileDownloadRequest
                    {
                        DestinationPath = destination, Urls = file.Downloads,
                        ExpectedSize = file.Size, Hashes = file.Hashes
                    }, new ForwardProgress<FileDownloadProgress>(p => progress?.Report(
                        new("下载", completed, pending.Count, file.Path!, p))), token).ConfigureAwait(false);
                    if (!downloaded.HashVerified || file.Size is { } expected && downloaded.BytesWritten != expected)
                        throw new InvalidDataException("下载器未确认文件校验结果，请执行完整检查。");
                    results.Add(new(file, InstanceDownloadState.Downloaded,
                        file.Size.HasValue ? "下载完成，文件大小和声明的哈希已校验。" : "下载完成，声明的哈希已校验；清单未声明大小。"));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or
                    ArgumentException or InvalidOperationException or OperationCanceledException or InvalidDataException or TimeoutException)
                {
                    results.Add(new(file, InstanceDownloadState.Failed, ex.Message));
                }
                progress?.Report(new("下载", index + 1, pending.Count, file.Path!));
            }

            token.ThrowIfCancellationRequested();
            var after = await _checker.CheckAsync(root, InstanceCheckMode.Full,
                new ForwardProgress<InstanceCheckProgress>(p => progress?.Report(new("复查", p.CompletedFiles, p.TotalFiles, p.FileName, Check: p))), token)
                .ConfigureAwait(false);
            return new(results, after) { Runtime = runtime };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 已提交的文件不会回滚；没有完成复查就不返回旧快照冒充当前状态。
            return new(results, null, IsCanceled: true) { Runtime = runtime };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        {
            return new(results, null, CheckError: ex.Message) { Runtime = runtime };
        }
    }

    /// <summary>给无法自动补全的文件说明真实原因，避免把 Prism 或 CurseForge 引用伪装成可用 URL。</summary>
    private static string MissingSourceReason(InstanceFileRequirement file) => file.Source switch
    {
        InstanceFileSource.Embedded => "文件来自原整合包，需要从原压缩包恢复；当前清单没有下载来源。",
        InstanceFileSource.CurseForge => "需要先通过 CurseForge 项目和文件 ID 解析下载信息。",
        _ when file.Hashes.Count == 0 => "没有校验值，本次实例补全不自动下载。",
        _ => "没有可用的下载地址或目标路径。"
    };

    /// <summary>每轮写入前验证实例仍存在、属于指定根目录且没有链接祖先，防止用旧选择重建已移走的实例。</summary>
    private string ValidateInstance(string instancePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instancePath));
        if (!string.Equals(Path.GetDirectoryName(root), _instancesRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能补全 instances 中的直接子实例。");
        InstanceImportPaths.EnsureNoLinks(root);
        var attributes = File.GetAttributes(root);
        if ((attributes & FileAttributes.Directory) == 0) throw new IOException("实例路径不是文件夹。");
        return root;
    }

    /// <summary>同步转发内部进度；由最外层 UI Progress 负责切回界面线程，避免内部回调排队乱序。</summary>
    private sealed class ForwardProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        /// <summary>保存进度接收方法。</summary>
        public ForwardProgress(Action<T> report) => _report = report;
        /// <summary>直接转发当前阶段的进度。</summary>
        public void Report(T value) => _report(value);
    }
}
