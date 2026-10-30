using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Infrastructure.Minecraft.Prism;

/// <summary>Prism 的安装入口；固定组件由 Prism 解析，共用运行文件下载和整理流程。</summary>
public sealed class PrismInstanceInstaller : IPrismInstanceInstaller
{
    private readonly InstanceRuntimeInstaller _runtime;

    /// <summary>保留原调用方式，允许测试注入 Prism 解析器。</summary>
    public PrismInstanceInstaller(string launcherRoot, IFileDownloader downloader, IPrismRuntimeResolver? resolver = null)
    {
        _runtime = new InstanceRuntimeInstaller(launcherRoot, downloader, resolver ?? new PrismRuntimeResolver(), "Prism");
    }

    /// <summary>下载运行依赖并生成配置，取消时保留已完成的下载。</summary>
    public Task<PrismInstallResult> InstallAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
        _runtime.InstallAsync(instancePath, progress, cancellationToken);

    /// <summary>供安装及检查模块复用已有文件校验，不覆盖不匹配的原文件。</summary>
    internal static Task<bool> ExistingFileMatchesAsync(string path, InstanceFileRequirement file, CancellationToken token) =>
        InstanceRuntimeInstaller.ExistingFileMatchesAsync(path, file, token);
}
