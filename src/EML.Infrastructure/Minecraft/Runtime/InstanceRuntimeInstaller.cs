using System.Buffers;
using System.Security.Cryptography;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Prism;

namespace EML.Infrastructure.Minecraft.Runtime;

/// <summary>按固定组件和平台元数据安装运行依赖、资源与启动配置，不执行包内程序。</summary>
internal sealed class InstanceRuntimeInstaller : IPrismInstanceInstaller
{
    private readonly string _instancesRoot;
    private readonly string _sourceFormat;
    private readonly IFileDownloader _downloader;
    private readonly IPrismRuntimeResolver _resolver;
    private readonly PrismRuntimeMaterializer _materializer = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>复用通用下载器；允许测试注入解析器，生产默认根据本机平台解析。</summary>
    public InstanceRuntimeInstaller(string launcherRoot, IFileDownloader downloader, IPrismRuntimeResolver resolver, string sourceFormat)
    {
        _instancesRoot = Path.GetFullPath(Path.Combine(launcherRoot, "instances"));
        _downloader = downloader;
        _resolver = resolver;
        _sourceFormat = sourceFormat;
    }

    /// <summary>串行处理同一安装服务的请求，将哈希与文件整理放到后台执行。</summary>
    public async Task<PrismInstallResult> InstallAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => InstallCoreAsync(instancePath, progress, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _operationLock.Release(); }
    }

    /// <summary>先解析组件，再下载类库和索引、展开资源，最后生成启动配置并发布准备状态。</summary>
    private async Task<PrismInstallResult> InstallCoreAsync(string instancePath, IProgress<InstanceDownloadProgress>? progress, CancellationToken token)
    {
        var records = new List<InstanceDownloadFileResult>();
        var issues = new List<string>();
        var contentIssues = new List<string>();
        var notes = new List<string>();
        string? launchPath = null;
        try
        {
            var root = ValidateInstance(instancePath);
            Migration.MigrationJournalStore.EnsureSettled(root);
            var manifest = InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName));
            if (!manifest.SourceFormat.Equals(_sourceFormat, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"此安装器只处理 {_sourceFormat} 实例。");
            var plan = _resolver.Resolve(root, manifest);
            var attemptedMetadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 组件依赖可以引入其他固定组件，但必须有界，且同一未解决请求不无限重试。
            for (var round = 0; plan.MetadataRequests.Count > 0 && round < 32; round++)
            {
                foreach (var request in plan.MetadataRequests)
                {
                    token.ThrowIfCancellationRequested();
                    if (!attemptedMetadata.Add(request.RelativePath))
                    {
                        issues.Add($"无法解析组件 {request.Id} {request.Version}，请检查元数据文件。");
                        continue;
                    }
                    progress?.Report(new("组件元数据", 0, plan.MetadataRequests.Count, request.Id + " " + request.Version));
                    var requirement = new InstanceFileRequirement
                    {
                        Path = request.RelativePath, Source = InstanceFileSource.DirectDownload, Downloads = [request.Url],
                        Hashes = request.RequirementHashes, Size = request.ExpectedSize
                    };
                    await EnsureFileAsync(root, requirement, records, issues, progress, 0, 1, token, 8 * 1024 * 1024).ConfigureAwait(false);
                }
                if (issues.Count > 0) break;
                plan = _resolver.Resolve(root, manifest);
            }
            notes.AddRange(plan.Notes);
            issues.AddRange(plan.BlockingIssues);
            if (plan.MetadataRequests.Count > 0) issues.Add("仍有组件元数据尚未取得，不能生成完整运行配置。");
            if (issues.Count > 0) return Result();

            // 与检查器及平台安装器共享来源选择和用户替换；运行依赖仍按选定内容严格校验。
            InstanceEffectiveRequirements.Snapshot? sourceSnapshot = null;
            IReadOnlyList<InstanceFileRequirement> effective = manifest.Files;
            var usesEmbeddedRuntime = plan.Files.Any(file => file.Requirement.Source == InstanceFileSource.Embedded);
            try
            {
                sourceSnapshot = InstanceEffectiveRequirements.Build(root, manifest);
                effective = sourceSnapshot.Requirements;
                contentIssues.AddRange(sourceSnapshot.ContentIssues);
                notes.AddRange(sourceSnapshot.Notes);
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
                effective = [];
                contentIssues.Add("文件来源无法验证：" + ex.Message);
                if (usesEmbeddedRuntime) issues.Add("本地运行文件的来源或用户替换记录无法验证，不能发布运行配置。");
            }
            var baseline = effective.Where(f => f.Path is not null).ToDictionary(f => f.Path!, StringComparer.OrdinalIgnoreCase);
            plan = plan with
            {
                Files = plan.Files.Select(file => file.Requirement.Source == InstanceFileSource.Embedded &&
                    baseline.TryGetValue(file.Requirement.Path!, out var imported)
                    ? file with { Requirement = imported } : file).ToArray()
            };
            for (var index = 0; index < plan.Files.Count; index++)
            {
                var file = plan.Files[index];
                // 来源快照失败时继续下载可信远端依赖，但不回退到失效的旧包内基线。
                if (sourceSnapshot is null && file.Requirement.Source == InstanceFileSource.Embedded) continue;
                progress?.Report(new("运行依赖", index, plan.Files.Count, file.Requirement.Path ?? ""));
                await EnsureFileAsync(root, file.Requirement, records, issues, progress, index, plan.Files.Count, token,
                    file.Role == PrismRuntimeFileRole.AssetIndex ? 8 * 1024 * 1024 : null).ConfigureAwait(false);
            }
            if (issues.Count > 0) return Result();

            var assets = _materializer.ExpandAssets(plan);
            var completedAssets = 0;
            var assetGate = new object();
            // 资源往往有数百个小文件，限制为四路并发减少网络等待；每个任务独立收集结果。
            await Parallel.ForEachAsync(assets, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                async (asset, assetToken) =>
                {
                    var fileRecords = new List<InstanceDownloadFileResult>();
                    var fileIssues = new List<string>();
                    try
                    {
                        await EnsureFileAsync(root, asset.Requirement, fileRecords, fileIssues, null, 0, 1, assetToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (assetGate)
                        {
                            records.AddRange(fileRecords);
                            issues.AddRange(fileIssues);
                            completedAssets++;
                            progress?.Report(new("游戏资源", completedAssets, assets.Count, asset.Requirement.Path ?? ""));
                        }
                    }
                }).ConfigureAwait(false);
            if (issues.Count > 0) return Result();

            token.ThrowIfCancellationRequested();
            ValidateInstance(root);
            // 生成阶段也重新验证文件，防止下载期间实例内容被其他程序改写。
            progress?.Report(new("生成启动配置", 0, 1, plan.MainClass));
            var prepared = await _materializer.PrepareAsync(plan with { Files = plan.Files.Concat(assets).ToArray() }, token).ConfigureAwait(false);
            var current = _resolver.Resolve(root, InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName)));
            if (current.Fingerprint != plan.Fingerprint) throw new IOException("准备过程中组件配置发生变化，请重新补全。");
            if (sourceSnapshot is not null)
            {
                try { InstanceEffectiveRequirements.EnsureUnchanged(root, manifest, sourceSnapshot); }
                catch (Exception ex) when (IsExpectedFailure(ex))
                {
                    contentIssues.Add("文件来源在运行依赖准备期间发生变化：" + ex.Message);
                    // 本地类库也采用该快照时，运行阶段本身必须重新确认。
                    if (usesEmbeddedRuntime) throw new IOException("本地运行文件依据已变化，请重新补全。", ex);
                }
            }
            await PrismRuntimeStateStore.WriteAsync(plan, new PrismRuntimeState
            {
                Fingerprint = plan.Fingerprint, LaunchProfilePath = prepared.LaunchProfilePath,
                GeneratedFiles = prepared.GeneratedFiles
            }, token).ConfigureAwait(false);
            launchPath = InstanceImportPaths.Within(root, prepared.LaunchProfilePath);
            progress?.Report(new("运行依赖已准备", 1, 1, prepared.LaunchProfilePath));
            notes.Add("已生成启动模块可用的版本配置；仍需选择匹配的 Java 并由启动模块验证进入游戏。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return Result() with { IsCanceled = true }; }
        catch (Exception ex) when (IsExpectedFailure(ex)) { issues.Add(ex.Message); }
        return Result();

        /// <summary>保留当前成功下载记录，并分别返回阻断原因和启动配置位置。</summary>
        PrismInstallResult Result() => new()
        {
            SourceFormat = _sourceFormat, Files = records.Select(file => file with { IsRuntimeFile = true }).ToArray(),
            Issues = issues.Concat(contentIssues).Distinct().ToArray(), ContentIssues = contentIssues.Distinct().ToArray(),
            RuntimeIssues = issues.Distinct().ToArray(), Notes = notes.Distinct().ToArray(),
            PreparedLaunchProfilePath = launchPath, LaunchProfilePath = contentIssues.Count == 0 ? launchPath : null
        };
    }

    /// <summary>已有文件校验后复用；缺失文件按明确来源下载，不覆盖用户文件或从名称猜测来源。</summary>
    private async Task EnsureFileAsync(string root, InstanceFileRequirement file, List<InstanceDownloadFileResult> records,
        List<string> issues, IProgress<InstanceDownloadProgress>? progress, int completed, int total, CancellationToken token,
        long? maximumBytes = null)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            ValidateInstance(root);
            var path = InstanceImportPaths.Within(root, file.Path ?? throw new InvalidDataException("运行依赖缺少目标路径。"));
            InstanceImportPaths.EnsureNoLinks(path);
            if (maximumBytes is { } cap)
            {
                try
                {
                    if (new FileInfo(path).Length > cap) throw new InvalidDataException("已有元数据超过允许的大小。");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            if (await ExistingFileMatchesAsync(path, file, token).ConfigureAwait(false)) return;
            if (file.Source == InstanceFileSource.Embedded || file.Downloads.Count == 0)
                throw new FileNotFoundException("缺少包内本地组件，请从原压缩包恢复：" + file.Path);
            var result = await _downloader.DownloadAsync(new FileDownloadRequest
            {
                DestinationPath = path, Urls = file.Downloads, Hashes = file.Hashes,
                ExpectedSize = file.Size, MaximumBytes = maximumBytes
            }, new InlineProgress<FileDownloadProgress>(value => progress?.Report(
                new("下载运行依赖", completed, total, file.Path!, value))), token).ConfigureAwait(false);
            if (file.Hashes.Count > 0 && !result.HashVerified)
                throw new InvalidDataException("下载器没有确认预期哈希：" + file.Path);
            records.Add(new(file, result.HashVerified ? InstanceDownloadState.Downloaded : InstanceDownloadState.DownloadedUnverified,
                result.HashVerified ? "已下载并校验运行文件。" : "按组件声明的 HTTPS 地址下载；上游未提供哈希，内容未获得来源哈希验证。"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            issues.Add(file.Path + "：" + ex.Message);
            records.Add(new(file, InstanceDownloadState.Failed, ex.Message));
        }
    }

    /// <summary>只把明确不存在视作待下载；已有不匹配文件需用户处理，不能静默覆盖。</summary>
    internal static async Task<bool> ExistingFileMatchesAsync(string path, InstanceFileRequirement file, CancellationToken token)
    {
        var integrityHashes = InstanceFileIntegrity.HashesFor(file);
        FileStream input;
        try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        await using (input)
        {
            if (file.Size is { } size && input.Length != size) throw new InvalidDataException("已有文件大小不符，已保留原文件。");
            if (integrityHashes.Count == 0) return true;
            var hashes = integrityHashes.Select(pair => (Expected: pair.Value, Hash: IncrementalHash.CreateHash(pair.Key switch
            {
                "sha1" => HashAlgorithmName.SHA1, "sha256" => HashAlgorithmName.SHA256,
                "sha512" => HashAlgorithmName.SHA512, _ => throw new InvalidDataException("不支持的文件哈希。")
            }))).ToArray();
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    foreach (var item in hashes) item.Hash.AppendData(buffer, 0, count);
                foreach (var item in hashes)
                    if (!Convert.ToHexString(item.Hash.GetHashAndReset()).Equals(item.Expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("已有文件哈希不符，已保留原文件。");
            }
            finally
            {
                foreach (var item in hashes) item.Hash.Dispose();
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return true;
        }
    }

    /// <summary>每次写入前确认选中实例仍属于启动器且没有链接祖先。</summary>
    private string ValidateInstance(string instancePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instancePath));
        if (!string.Equals(Path.GetDirectoryName(root), _instancesRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能补全 instances 的直接子实例。");
        InstanceImportPaths.EnsureNoLinks(root);
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) throw new IOException("实例路径不是目录。");
        return root;
    }

    /// <summary>筛选可显示给用户的文件、网络及格式错误，保留取消的独立处理。</summary>
    private static bool IsExpectedFailure(Exception ex) => ex is IOException or InvalidDataException or HttpRequestException or
        UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException;

    /// <summary>同步转发阶段进度，由界面最外层 Progress 切回 UI 线程。</summary>
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        /// <summary>直接把当前进度交给接收者。</summary>
        public void Report(T value) => callback(value);
    }
}
