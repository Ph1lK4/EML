using System.Text.Json;
using EML.Core.CurseForge;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Core.Modrinth;
using EML.Infrastructure.Minecraft.CurseForge.Api;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Modrinth.Api;
using EML.Infrastructure.Minecraft.Prism;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Infrastructure.Minecraft.CurseForge;

/// <summary>按内容来源补全实例，独立准备运行依赖，最终就绪要求必需内容和运行文件同时通过。</summary>
public sealed class CurseForgeInstanceInstaller : ICurseForgeInstanceInstaller
{
    private readonly string _instancesRoot;
    private readonly IFileDownloader _downloader;
    private readonly CurseForgeFileResolver _resolver;
    private readonly IPrismInstanceInstaller _runtimeInstaller;
    private readonly EmbeddedFileRestorer _embeddedRestorer;
    private readonly int _maxConcurrentFiles;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>复用通用下载器和官方 API 客户端；允许测试注入 API 和运行阶段。</summary>
    public CurseForgeInstanceInstaller(string launcherRoot, IFileDownloader downloader,
        ICurseForgeApiClient? apiClient = null, IPrismInstanceInstaller? runtimeInstaller = null, int maxConcurrentFiles = 4,
        IModrinthApiClient? modrinthApiClient = null)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        if (maxConcurrentFiles is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maxConcurrentFiles));
        _instancesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(launcherRoot, "instances")));
        _downloader = downloader;
        _embeddedRestorer = new EmbeddedFileRestorer(launcherRoot);
        _resolver = new CurseForgeFileResolver(apiClient ?? new CurseForgeApiClient(), modrinthApiClient ?? new ModrinthApiClient());
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

    /// <summary>分别处理内容与运行阶段；内容失败不阻止准备运行依赖，但不能发布最终就绪结果。</summary>
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
            Migration.MigrationJournalStore.EnsureSettled(root);
            EML.Infrastructure.Minecraft.Mods.ModOperationJournal.EnsureSettled(root);
            var manifest = InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName));
            if (!manifest.SourceFormat.Equals("CurseForge", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("此安装器只处理 CurseForge 实例。");
            progress?.Report(new("解析 CurseForge 文件列表", 0, 0, ""));
            CurseForgeFileResolution? resolution = null;
            InstanceEffectiveRequirements.Snapshot? snapshot = null;
            try
            {
                resolution = await _resolver.ResolveAsync(root, manifest, token).ConfigureAwait(false);
                snapshot = InstanceEffectiveRequirements.Build(root, manifest, resolution);
                contentIssues.AddRange(snapshot.ContentIssues);
                notes.AddRange(snapshot.Notes);
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
                // 平台或本机来源记录失效时不尝试内容下载；可信运行依赖仍可独立准备。
                contentIssues.Add("CurseForge 内容来源解析失败：" + ex.Message);
                notes.Add("本轮跳过内容文件处理，保留现有文件；来源解析恢复前不会标记为可启动。");
            }
            var fallbackCount = resolution?.Files.Count(file => file.ModrinthFallback is not null) ?? 0;
            if (fallbackCount > 0) notes.Add($"{fallbackCount} 个文件已匹配 Modrinth 同内容来源，保留原版本、文件名和安装目录。");
            var resolvedById = (resolution?.Files ?? []).ToDictionary(file => (file.Requirement.ProjectId, file.Requirement.FileId));
            var effectiveRequirements = snapshot?.Requirements ?? [];
            var onlinePaths = snapshot is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) :
                Mods.ModManagementStore.OnlineDownloadPaths(snapshot.Mods, snapshot.Migration);
            var required = effectiveRequirements.Where(file => file.Required).ToArray();
            var overrideCount = required.Count(file => file.Origin == InstanceFileOrigin.EmbeddedOverride);
            if (overrideCount > 0) notes.Add($"{overrideCount} 个包内覆盖文件使用原包大小与 SHA-256；官方文件信息仅保留为来源记录。");
            var userCount = required.Count(file => file.Origin == InstanceFileOrigin.UserLocalImport);
            if (userCount > 0) notes.Add($"{userCount} 个文件使用用户明确接受的本地替代基线。");
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
                    ? resolvedById.GetValueOrDefault((file.ProjectId, file.FileId)) : null;
                var effective = file; // 已由来源优先级选择，不能再换回同路径官方需求。
                InstanceDownloadFileResult? result = null;
                try
                {
                    result = await EnsureFileAsync(root, manifest, effective, resolved,
                        file.Path is not null && onlinePaths.Contains(file.Path),
                        new InlineProgress<FileDownloadProgress>(value => progress?.Report(new(
                            resolved?.ModrinthFallback is not null ? "从 Modrinth 补全同内容文件" : "下载 CurseForge 整合包文件",
                            Volatile.Read(ref completed), required.Length,
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
                            if (InstanceFileStatePolicy.IsFailure(result))
                            {
                                var issue = result.PathDisplay + "：" + result.Message;
                                contentIssues.Add(issue);
                            }
                        }
                        completed++;
                        progress?.Report(new("检查 CurseForge 整合包文件", completed, required.Length, effective.Path ?? ""));
                    }
                }
            }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ValidateInstance(root);
            EnsureContentSnapshotUnchanged();
            if (contentIssues.Count > 0)
                notes.Add("内容文件仍待修复；继续准备 Minecraft/加载器依赖，必需内容通过前不会标记为可启动。");
            try
            {
                runtime = await _runtimeInstaller.InstallAsync(root, progress, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
                runtime = new PrismInstallResult { SourceFormat = "CurseForge", RuntimeIssues = [ex.Message] };
            }
            canceled = runtime.IsCanceled;
            if (canceled) return Result();
            token.ThrowIfCancellationRequested();
            EnsureContentSnapshotUnchanged();
            // 运行阶段期间内容可能被外部修改；发布就绪前重新确认所有必需内容。
            contentIssues.AddRange(await InstanceContentVerifier.VerifyRequiredAsync(root, manifest, required, token).ConfigureAwait(false));
            EnsureContentSnapshotUnchanged();

            /// <summary>内容来源解析失败时仅保留清单一致性检查；未验证的内容始终保持失败状态。</summary>
            void EnsureContentSnapshotUnchanged()
            {
                if (snapshot is not null) InstanceEffectiveRequirements.EnsureUnchanged(root, manifest, snapshot);
                else EmbeddedFileBaseline.EnsureManifestUnchanged(root, manifest);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; }
        catch (Exception ex) when (IsExpectedFailure(ex)) { contentIssues.Add(ex.Message); }
        return Result();

        /// <summary>汇总结果，必需模组缺失、运行失败或取消均不会被标记为完整可运行。</summary>
        PrismInstallResult Result() => InstanceInstallResultPolicy.Merge(new PrismInstallResult
        {
            SourceFormat = "CurseForge", Files = records.ToArray(), ContentIssues = contentIssues.Distinct().ToArray(),
            Notes = notes.Distinct().ToArray(), IsCanceled = canceled
        }, runtime ?? new PrismInstallResult { SourceFormat = "CurseForge" });
    }

    /// <summary>先验证现有文件，允许人工放入被限制分发的文件；只下载 API 明确授权且有 SHA-1 的内容。</summary>
    private async Task<InstanceDownloadFileResult?> EnsureFileAsync(string root, InstanceImportManifest manifest,
        InstanceFileRequirement file, CurseForgeResolvedFile? resolved, bool managedOnline, IProgress<FileDownloadProgress>? progress,
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
                    return new(file, InstanceDownloadState.MissingEmbeddedFile, "用户明确选择的本地替代文件缺失，请重新提供该文件；不会改用官方版本。");
                if (await _embeddedRestorer.RestoreAsync(root, manifest, file, token).ConfigureAwait(false))
                    return new(file, InstanceDownloadState.RestoredEmbeddedFile, "已从原始整合包条目恢复，并验证导入时大小及 SHA-256。");
                return new(file, InstanceDownloadState.MissingEmbeddedFile, "找不到可恢复的原始整合包或条目，请提供原包；不会使用不同内容的官方文件替代。");
            }
            if (!managedOnline && (file.Source != InstanceFileSource.CurseForge || resolved is null))
                return new(file, InstanceDownloadState.Unavailable, "CurseForge 导入清单包含不支持的文件来源。");
            if (managedOnline) Mods.ModManagementStore.ValidateOnlineBaseline(file);
            if (file.Downloads.Count == 0)
                return new(file, InstanceDownloadState.Unavailable,
                    $"作者限制第三方分发或平台未提供下载地址。{resolved?.FallbackMessage}请从官方文件页面 {resolved?.ManualDownloadUrl} 下载指定版本并放入 {file.Path}，再执行补全。");
            if (!managedOnline && !hashes.ContainsKey("sha1"))
                return new(file, InstanceDownloadState.Unavailable, "CurseForge 文件缺少 SHA-1，无法校验指定版本。");
            var download = await _downloader.DownloadAsync(new FileDownloadRequest
            {
                DestinationPath = path, Urls = file.Downloads, ExpectedSize = file.Size, Hashes = hashes,
                DisplayName = file.Path, GroupName = Path.GetFileName(root) + " · CurseForge 补全"
            }, progress, token).ConfigureAwait(false);
            if (!download.HashVerified) throw new InvalidDataException("下载器未确认文件的官方哈希。");
            return new(file, InstanceDownloadState.Downloaded, managedOnline ? "已按登记的平台来源补全并校验在线模组。" : resolved?.ModrinthFallback is null
                ? "已下载并校验 CurseForge 固定版本文件。"
                : "已从 Modrinth 下载同内容文件，并按原 CurseForge 清单哈希校验。");
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
