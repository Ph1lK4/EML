using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Prism;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Infrastructure.Minecraft.Standard;

/// <summary>补全标准 ZIP 的固定依赖；已含完整运行文件的旧实例可仅在本地生成一次启动配置。</summary>
public sealed class ZipInstanceInstaller : IPrismInstanceInstaller
{
    private readonly string _launcherRoot;
    private readonly InstanceRuntimeInstaller _runtime;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>复用通用下载器与标准版本解析器，不另外实现一套下载及校验规则。</summary>
    public ZipInstanceInstaller(string launcherRoot, IFileDownloader downloader, IPrismRuntimeResolver? resolver = null)
    {
        _launcherRoot = Path.GetFullPath(launcherRoot);
        _runtime = new InstanceRuntimeInstaller(launcherRoot, downloader, resolver ?? new StandardMinecraftRuntimeResolver(), "Zip");
    }

    /// <summary>显式补全或导入阶段下载缺少的运行依赖，并独立复核必要内容。</summary>
    public async Task<PrismInstallResult> InstallAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var acquired = false;
        try
        {
            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            return await Task.Run(() => InstallCoreAsync(instancePath, progress, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new() { SourceFormat = "Zip", IsCanceled = true }; }
        finally { if (acquired) _operationLock.Release(); }
    }

    /// <summary>内容检查与运行准备分别产生结果，运行配置成功不掩盖内容错误。</summary>
    private async Task<PrismInstallResult> InstallCoreAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress, CancellationToken token)
    {
        var issues = new List<string>();
        var notes = new List<string>();
        InstanceImportManifest? manifest = null;
        InstanceEffectiveRequirements.Snapshot? snapshot = null;
        var runtime = new PrismInstallResult { SourceFormat = "Zip" };
        var canceled = false;
        try
        {
            var root = EmbeddedFileBaseline.ValidateInstance(_launcherRoot, instancePath);
            try
            {
                manifest = ReadManifest(root);
                snapshot = InstanceEffectiveRequirements.Build(root, manifest);
                issues.AddRange(snapshot.ContentIssues);
                notes.AddRange(snapshot.Notes);
            }
            catch (Exception ex) when (IsExpectedFailure(ex)) { issues.Add("实例内容来源无法验证：" + ex.Message); }
            runtime = await _runtime.InstallAsync(root, progress, token).ConfigureAwait(false);
            if (runtime.IsCanceled) return Result();
            token.ThrowIfCancellationRequested();
            if (manifest is not null && snapshot is not null)
            {
                issues.AddRange(await InstanceContentVerifier.VerifyRequiredAsync(root, manifest, snapshot.Requirements, token).ConfigureAwait(false));
                InstanceEffectiveRequirements.EnsureUnchanged(root, manifest, snapshot);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; }
        catch (Exception ex) when (IsExpectedFailure(ex)) { issues.Add(ex.Message); }
        return Result();

        /// <summary>合并两个阶段，保留可诊断的准备路径以及最终阻断原因。</summary>
        PrismInstallResult Result() => InstanceInstallResultPolicy.Merge(new()
        {
            SourceFormat = "Zip", ContentIssues = issues.Distinct().ToArray(), Notes = notes.Distinct().ToArray(), IsCanceled = canceled
        }, runtime);
    }

    /// <summary>首次启动已有 ZIP 时仅验证运行文件并生成配置；不联网、不检查模组或玩家配置。</summary>
    public static Task<PrismInstallResult> PrepareLocalAsync(string launcherRoot, string instancePath,
        IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default,
        IPrismRuntimeResolver? resolver = null) =>
        Task.Run(() => PrepareLocalCoreAsync(launcherRoot, instancePath, resolver ?? new StandardMinecraftRuntimeResolver(), progress,
            cancellationToken), cancellationToken);

    /// <summary>复用已导入文件的可信依据，发布状态前再次确认源描述及用户替换没有改变。</summary>
    private static async Task<PrismInstallResult> PrepareLocalCoreAsync(string launcherRoot, string instancePath,
        IPrismRuntimeResolver resolver, IProgress<InstanceDownloadProgress>? progress, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var root = EmbeddedFileBaseline.ValidateInstance(launcherRoot, instancePath);
            var manifest = ReadManifest(root);
            var plan = resolver.Resolve(root, manifest);
            if (plan.BlockingIssues.Count > 0 || plan.MetadataRequests.Count > 0)
                throw new InvalidDataException("ZIP 运行描述尚不完整，请先补全实例依赖。\n" + string.Join('\n', plan.BlockingIssues));
            var snapshot = InstanceEffectiveRequirements.Build(root, manifest);
            var effective = snapshot.Requirements.Where(file => file.Path is not null)
                .ToDictionary(file => file.Path!, StringComparer.OrdinalIgnoreCase);
            plan = plan with
            {
                Files = plan.Files.Select(file => file.Requirement.Source == InstanceFileSource.Embedded &&
                    effective.TryGetValue(file.Requirement.Path!, out var current) ? file with { Requirement = current } : file).ToArray()
            };
            var materializer = new PrismRuntimeMaterializer();
            progress?.Report(new("首次准备 ZIP 启动配置", 0, 1, plan.MainClass));
            var assets = materializer.ExpandAssets(plan);
            var prepared = await materializer.PrepareAsync(plan with { Files = plan.Files.Concat(assets).ToArray() }, token).ConfigureAwait(false);
            InstanceEffectiveRequirements.EnsureUnchanged(root, manifest, snapshot);
            var currentPlan = resolver.Resolve(root, ReadManifest(root));
            if (currentPlan.Fingerprint != plan.Fingerprint) throw new IOException("首次准备期间标准版本描述发生变化，请重试。");
            await PrismRuntimeStateStore.WriteAsync(plan, new()
            {
                Fingerprint = plan.Fingerprint, LaunchProfilePath = prepared.LaunchProfilePath, GeneratedFiles = prepared.GeneratedFiles
            }, token).ConfigureAwait(false);
            var path = InstanceImportPaths.Within(root, prepared.LaunchProfilePath);
            progress?.Report(new("ZIP 启动配置已准备", 1, 1, prepared.LaunchProfilePath));
            return new()
            {
                SourceFormat = "Zip", PreparedLaunchProfilePath = path,
                Notes = ["已验证本地运行依赖并生成启动配置；本次未进行整包内容检查。"]
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new() { SourceFormat = "Zip", IsCanceled = true }; }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            var message = "无法从现有文件准备 ZIP 启动配置，请先点击“补全实例依赖”：" + ex.Message;
            return new() { SourceFormat = "Zip", Issues = [message], RuntimeIssues = [message] };
        }
    }

    /// <summary>读取并确认清单来源，避免把其他平台包按标准 ZIP 解释。</summary>
    private static InstanceImportManifest ReadManifest(string root)
    {
        var manifest = InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName));
        if (!manifest.SourceFormat.Equals("Zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("此安装器只处理 Zip 实例。");
        return manifest;
    }

    /// <summary>将可预期的文件、参数和元数据错误转换为阶段结果。</summary>
    private static bool IsExpectedFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or System.Text.Json.JsonException;
}
