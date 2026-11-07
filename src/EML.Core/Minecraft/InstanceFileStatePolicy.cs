namespace EML.Core.Minecraft;

/// <summary>检查器、安装器和界面使用相同的阻断判断；可选内容与必要依赖分开处理。</summary>
public static class InstanceFileStatePolicy
{
    /// <summary>只对必要文件阻断；已允许的内容变化仍需实际存在且被安全读取。</summary>
    public static bool IsBlocking(InstanceFileCheckResult file) => file.Requirement.Required &&
        file.State is not (InstanceFileState.Verified or InstanceFileState.PresentUnverified) &&
        !(file.State == InstanceFileState.ChangedSinceImport && file.IsAllowedConfigurationChange && !file.IsRuntimeFile &&
            file.Requirement.Source == InstanceFileSource.Embedded && file.Requirement.Origin is null &&
            file.Requirement.Role != InstanceFileRole.Content) &&
        !(file.State == InstanceFileState.Consumed && file.IsAllowedMissing && !file.IsRuntimeFile &&
            file.Requirement.Source == InstanceFileSource.Embedded && file.Requirement.Origin is null &&
            file.Requirement.Role is InstanceFileRole.ConsumableMarker or InstanceFileRole.UserConfiguration);

    /// <summary>识别未取得或校验失败的必要下载文件，不把来源无哈希等同下载失败。</summary>
    public static bool IsFailure(InstanceDownloadFileResult file) => file.Requirement.Required &&
        file.State is InstanceDownloadState.Failed or InstanceDownloadState.Unavailable or InstanceDownloadState.MissingEmbeddedFile;
}
