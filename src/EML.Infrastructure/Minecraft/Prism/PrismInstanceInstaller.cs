using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Infrastructure.Minecraft.Prism;

/// <summary>Prism 的安装入口；固定组件由 Prism 解析，共用运行文件下载和整理流程。</summary>
public sealed class PrismInstanceInstaller : IPrismInstanceInstaller
{
    private readonly InstanceRuntimeInstaller _runtime;
    private readonly string _launcherRoot;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>保留原调用方式，允许测试注入 Prism 解析器。</summary>
    public PrismInstanceInstaller(string launcherRoot, IFileDownloader downloader, IPrismRuntimeResolver? resolver = null)
    {
        _launcherRoot = Path.GetFullPath(launcherRoot);
        _runtime = new InstanceRuntimeInstaller(launcherRoot, downloader, resolver ?? new PrismRuntimeResolver(), "Prism");
    }

    /// <summary>串行准备运行依赖并复核内容，取消时保留已完成下载和独立阶段结果。</summary>
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
        { return new() { SourceFormat = "Prism", IsCanceled = true }; }
        finally { if (acquired) _operationLock.Release(); }
    }

    /// <summary>内容问题不阻止运行文件下载，但来源快照和全部必要内容通过后才发布最终启动路径。</summary>
    private async Task<PrismInstallResult> InstallCoreAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress, CancellationToken token)
    {
        var issues = new List<string>();
        var notes = new List<string>();
        InstanceImportManifest? manifest = null;
        InstanceEffectiveRequirements.Snapshot? snapshot = null;
        var root = instancePath;
        var runtime = new PrismInstallResult { SourceFormat = "Prism" };
        var canceled = false;
        try
        {
            token.ThrowIfCancellationRequested();
            root = EmbeddedFileBaseline.ValidateInstance(_launcherRoot, instancePath);
            Migration.MigrationJournalStore.EnsureSettled(root);
            try
            {
                manifest = InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName));
                if (!manifest.SourceFormat.Equals("Prism", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("此安装器只处理 Prism 实例。");
                snapshot = InstanceEffectiveRequirements.Build(root, manifest);
                issues.AddRange(snapshot.ContentIssues);
                notes.AddRange(snapshot.Notes);
            }
            catch (Exception ex) when (IsContentFailure(ex)) { issues.Add("实例内容来源无法验证：" + ex.Message); }

            runtime = await _runtime.InstallAsync(root, progress, token).ConfigureAwait(false);
            if (runtime.IsCanceled) return Result();
            token.ThrowIfCancellationRequested();
            if (snapshot is not null && manifest is not null)
            {
                issues.AddRange(await InstanceContentVerifier.VerifyRequiredAsync(root, manifest, snapshot.Requirements, token).ConfigureAwait(false));
                InstanceEffectiveRequirements.EnsureUnchanged(root, manifest, snapshot);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; }
        catch (Exception ex) when (IsContentFailure(ex)) { issues.Add(ex.Message); }
        return Result();

        /// <summary>合并完整内容检查与独立运行阶段，保留准备路径并正确限制最终就绪状态。</summary>
        PrismInstallResult Result() => InstanceInstallResultPolicy.Merge(new()
        {
            SourceFormat = "Prism", ContentIssues = issues.Distinct().ToArray(), Notes = notes.Distinct().ToArray(),
            IsCanceled = canceled
        }, runtime);
    }

    /// <summary>只把可预期的文件、来源和参数错误转为内容问题。</summary>
    private static bool IsContentFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or System.Text.Json.JsonException;

    /// <summary>供安装及检查模块复用已有文件校验，不覆盖不匹配的原文件。</summary>
    internal static Task<bool> ExistingFileMatchesAsync(string path, InstanceFileRequirement file, CancellationToken token) =>
        InstanceRuntimeInstaller.ExistingFileMatchesAsync(path, file, token);
}
