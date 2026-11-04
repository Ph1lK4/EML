namespace EML.Core.Minecraft;

/// <summary>按 CurseForge 整合包固定项目与文件 ID 补全内容，再准备游戏运行依赖。</summary>
public interface ICurseForgeInstanceInstaller
{
    /// <summary>校验并下载必需文件；保留已有冲突文件和取消前完成的下载，不自动启动游戏。</summary>
    Task<PrismInstallResult> InstallAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}
