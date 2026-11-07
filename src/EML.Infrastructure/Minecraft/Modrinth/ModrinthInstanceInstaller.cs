using System.Text.Json;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Prism;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Infrastructure.Minecraft.Modrinth;

/// <summary>按 Modrinth 导入清单补全必需模组和配置，再交给共享安装器准备本体、加载器与资源。</summary>
public sealed class ModrinthInstanceInstaller : IModrinthInstanceInstaller
{
    private readonly string _instancesRoot;
    private readonly IFileDownloader _downloader;
    private readonly IPrismInstanceInstaller _runtimeInstaller;
    private readonly EmbeddedFileRestorer _embeddedRestorer;
    private readonly ModrinthInstallOptions _options;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>复用通用下载器及共享运行安装器，允许测试替换运行阶段并单独调整文件并发数。</summary>
    public ModrinthInstanceInstaller(string launcherRoot, IFileDownloader downloader,
        ModrinthInstallOptions? options = null, IPrismInstanceInstaller? runtimeInstaller = null)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        _instancesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(launcherRoot, "instances")));
        _downloader = downloader;
        _embeddedRestorer = new EmbeddedFileRestorer(launcherRoot);
        _options = options ?? new ModrinthInstallOptions();
        _options.Validate();
        _runtimeInstaller = runtimeInstaller ?? new InstanceRuntimeInstaller(launcherRoot, downloader,
            new ModrinthRuntimeResolver(), "Modrinth");
    }

    /// <summary>串行执行此服务的安装任务，避免同一实例重复下载；等待或工作中取消均返回明确状态。</summary>
    public async Task<PrismInstallResult> InstallAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var acquired = false;
        try
        {
            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            // 将本地清单解析和文件校验安排在后台，避免导入完成后阻塞 WPF 界面。
            return await Task.Run(() => InstallCoreAsync(instancePath, progress, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PrismInstallResult { SourceFormat = "Modrinth", IsCanceled = true };
        }
        finally
        {
            if (acquired) _operationLock.Release();
        }
    }

    /// <summary>独立准备内容和运行依赖，使用同一来源快照重检后才发布整个实例的就绪状态。</summary>
    private async Task<PrismInstallResult> InstallCoreAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress, CancellationToken token)
    {
        var records = new List<InstanceDownloadFileResult>();
        var contentIssues = new List<string>();
        var notes = new List<string>();
        PrismInstallResult? runtime = null;
        var canceled = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var root = ValidateInstance(instancePath);
            var manifestPath = Path.Combine(root, InstanceImportManifest.FileName);
            var manifest = InstanceManifestStore.Read(manifestPath);
            if (!manifest.SourceFormat.Equals("Modrinth", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("此安装器只处理 Modrinth 实例。");
            InstanceEffectiveRequirements.Snapshot? snapshot = null;
            try
            {
                snapshot = InstanceEffectiveRequirements.Build(root, manifest);
                contentIssues.AddRange(snapshot.ContentIssues);
                notes.AddRange(snapshot.Notes);
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
                contentIssues.Add("Modrinth 内容来源解析失败：" + ex.Message);
                notes.Add("本轮跳过内容文件处理，保留现有文件；来源解析恢复前不会标记为可启动。");
            }
            var files = (snapshot?.Requirements ?? []).Where(file => file.Required).ToArray();
            var optionalCount = manifest.Files.Count(file => !file.Required);
            if (optionalCount > 0) notes.Add($"已跳过 {optionalCount} 个可选文件；可选模组选择功能尚未启用。");
            var completed = 0;
            var resultGate = new object();
            await Parallel.ForEachAsync(files, new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxConcurrentFiles, CancellationToken = token
            }, async (file, fileToken) =>
            {
                InstanceDownloadFileResult? result = null;
                try
                {
                    result = await EnsureManifestFileAsync(root, manifest, file,
                        new InlineProgress<FileDownloadProgress>(value => progress?.Report(new(
                            "下载整合包文件", Volatile.Read(ref completed), files.Length, file.Path ?? "", value))),
                        note => { lock (resultGate) notes.Add(note); },
                        fileToken).ConfigureAwait(false);
                }
                finally
                {
                    // 取消会等待各个任务结束；已提交的文件记录必须先汇总，不能随取消一起丢失。
                    lock (resultGate)
                    {
                        if (result is not null)
                        {
                            records.Add(result);
                            if (InstanceFileStatePolicy.IsFailure(result))
                                contentIssues.Add(result.PathDisplay + "：" + result.Message);
                        }
                        completed++;
                        progress?.Report(new("检查整合包文件", completed, files.Length, file.Path ?? ""));
                    }
                }
            }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ValidateInstance(root);
            CheckContentSnapshot();
            if (contentIssues.Count > 0)
                notes.Add("内容文件仍待修复；继续准备 Minecraft/加载器依赖，必需内容通过前不会标记为可启动。");
            try
            {
                runtime = await _runtimeInstaller.InstallAsync(root, progress, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
                runtime = new PrismInstallResult { SourceFormat = "Modrinth", RuntimeIssues = [ex.Message] };
            }
            canceled = runtime.IsCanceled;
            if (canceled) return Result();
            token.ThrowIfCancellationRequested();
            // 运行准备可能耗时较长；不能用阶段开始前的文件或用户选择宣告最终就绪。
            CheckContentSnapshot();
            if (snapshot is not null)
                contentIssues.AddRange(await InstanceContentVerifier.VerifyRequiredAsync(root, manifest, files, token).ConfigureAwait(false));
            CheckContentSnapshot();

            /// <summary>清单变化停止旧计划；仅内容来源或用户选择失效时保留阻断，并允许独立运行准备。</summary>
            void CheckContentSnapshot()
            {
                EmbeddedFileBaseline.EnsureManifestUnchanged(root, manifest);
                if (snapshot is null) return;
                try
                {
                    InstanceEffectiveRequirements.EnsureUnchanged(root, manifest, snapshot);
                }
                catch (Exception ex) when (IsExpectedFailure(ex))
                {
                    EmbeddedFileBaseline.EnsureManifestUnchanged(root, manifest);
                    contentIssues.Add("Modrinth 内容来源在操作期间失效：" + ex.Message);
                    notes.Add("本轮内容快照已失效；保留现有文件并等待重新检查，不沿用旧来源宣告完成。");
                    snapshot = null;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; }
        catch (Exception ex) when (IsExpectedFailure(ex)) { contentIssues.Add(ex.Message); }
        return Result();

        /// <summary>汇总文件、问题及取消状态；存在模组问题时绝不报告整个实例已准备完成。</summary>
        PrismInstallResult Result() => InstanceInstallResultPolicy.Merge(new PrismInstallResult
        {
            SourceFormat = "Modrinth", Files = records.ToArray(), ContentIssues = contentIssues.Distinct().ToArray(),
            Notes = notes.Distinct().ToArray(), IsCanceled = canceled
        }, runtime ?? new PrismInstallResult { SourceFormat = "Modrinth" });
    }

    /// <summary>校验并复用已有文件；仅下载有明确地址和哈希的缺失文件，保留所有不匹配的原文件。</summary>
    private async Task<InstanceDownloadFileResult?> EnsureManifestFileAsync(string root, InstanceImportManifest manifest,
        InstanceFileRequirement file, IProgress<FileDownloadProgress>? progress, Action<string> addNote, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            ValidateInstance(root);
            var path = InstanceImportPaths.Within(root, file.Path ?? throw new InvalidDataException("文件缺少目标路径。"));
            InstanceImportPaths.EnsureNoLinks(path);
            var hashes = InstanceFileIntegrity.HashesFor(file);
            var existingState = await InstanceContentVerifier.CheckExistingAsync(root, manifest, file, token).ConfigureAwait(false);
            if (existingState == InstanceFileState.Verified) return null;
            if (existingState == InstanceFileState.ChangedSinceImport)
            { addNote("已保留导入后修改的配置或用户数据：" + file.Path); return null; }
            if (existingState == InstanceFileState.Consumed)
            { addNote("一次性更新标记已消耗，不自动恢复：" + file.Path); return null; }
            if (file.Source == InstanceFileSource.Embedded)
            {
                if (file.Origin == InstanceFileOrigin.UserLocalImport)
                    return new(file, InstanceDownloadState.MissingEmbeddedFile, "用户明确选择的本地替代文件缺失，请重新提供该文件；不会改用原包或官方版本。");
                if (await _embeddedRestorer.RestoreAsync(root, manifest, file, token).ConfigureAwait(false))
                    return new(file, InstanceDownloadState.RestoredEmbeddedFile, "已从原 mrpack 条目恢复，并验证导入时大小及 SHA-256。");
                return new(file, InstanceDownloadState.MissingEmbeddedFile, "找不到可恢复的原 mrpack 压缩包或条目，请提供原包；不会改用其他来源内容。");
            }
            if (file.Source != InstanceFileSource.DirectDownload || file.Downloads.Count == 0)
                return new(file, InstanceDownloadState.Unavailable, "文件没有可用于 Modrinth 安装的直接下载地址。");
            if (hashes.Count == 0)
                return new(file, InstanceDownloadState.Unavailable, "下载清单缺少文件哈希，无法验证来源内容。");
            var download = await _downloader.DownloadAsync(new FileDownloadRequest
            {
                DestinationPath = path, Urls = file.Downloads, ExpectedSize = file.Size, Hashes = hashes
            }, progress, token).ConfigureAwait(false);
            if (!download.HashVerified) throw new InvalidDataException("下载器未确认清单指定的文件哈希。");
            return new(file, InstanceDownloadState.Downloaded, "已下载并校验整合包文件。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsExpectedFailure(ex)) { return new(file, InstanceDownloadState.Failed, ex.Message); }
    }

    /// <summary>限定为启动器 instances 的直接子实例，并拒绝目录联接和符号链接。</summary>
    private string ValidateInstance(string instancePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instancePath));
        if (!string.Equals(Path.GetDirectoryName(root), _instancesRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能补全 instances 的直接子实例。");
        InstanceImportPaths.EnsureNoLinks(root);
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) throw new IOException("实例路径不是目录。");
        return root;
    }

    /// <summary>只把可预期的文件、网络、格式和参数错误转为安装问题，取消另行处理。</summary>
    private static bool IsExpectedFailure(Exception ex) => ex is IOException or InvalidDataException or
        HttpRequestException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
        TimeoutException or JsonException;

    /// <summary>同步转发下载进度，由最外层界面进度对象切回 UI 线程。</summary>
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        /// <summary>转发单个下载更新，不在后台任务额外捕获同步上下文。</summary>
        public void Report(T value) => callback(value);
    }
}
