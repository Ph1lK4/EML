namespace EML.Core.Minecraft;

/// <summary>补全 Modrinth 导入清单中的文件，再准备 Minecraft 与加载器运行依赖。</summary>
public interface IModrinthInstanceInstaller
{
    /// <summary>按整合包固定版本下载并校验必需文件；取消时保留已完成文件，不自动启动游戏。</summary>
    Task<PrismInstallResult> InstallAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}
