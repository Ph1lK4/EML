namespace EML.Core.Minecraft;

/// <summary>其他启动器中的候选实例；仅供选择，导入时重新读取磁盘，不信任预览中的版本或路径派生值。</summary>
public sealed record ExternalMinecraftInstance(string Name, string Launcher, string SourcePath,
    string GameDirectory, string? MinecraftVersion, string Loader, string? SelectionId = null);

/// <summary>一次有边界的目录发现结果；单个位置失败不会隐藏其他可用实例。</summary>
public sealed record ExternalInstanceDiscoveryResult(IReadOnlyList<ExternalMinecraftInstance> Instances,
    IReadOnlyList<string> Warnings);

/// <summary>只读发现常见位置或用户选择的目录，不全盘搜索，也不访问账号凭证。</summary>
public interface IExternalInstanceDiscovery
{
    /// <summary>未指定目录时检查常见位置；指定时分析该目录及已知实例容器。</summary>
    Task<ExternalInstanceDiscoveryResult> DiscoverAsync(string? directory = null,
        CancellationToken cancellationToken = default);
}

/// <summary>文件夹复制预览；目标始终是 EML 新实例，现有源目录不会被移动。</summary>
public sealed record FolderInstanceImportPreview(ExternalMinecraftInstance Instance,
    InstanceImportPreview Import, long TotalBytes, string DestinationRoot,
    IReadOnlyList<string> SkippedItems);

/// <summary>无需中间 ZIP 的目录复制导入；预览与执行都重新校验源布局。</summary>
public interface IMinecraftFolderImporter
{
    /// <summary>只读统计文件及空间，给出来源、版本和被排除的启动器私有文件。</summary>
    Task<FolderInstanceImportPreview> InspectAsync(ExternalMinecraftInstance instance,
        CancellationToken cancellationToken = default);

    /// <summary>流式复制到暂存目录，成功后提交；取消或失败不修改原实例。</summary>
    Task<InstanceImportResult> ImportAsync(ExternalMinecraftInstance instance, string instanceName,
        IProgress<InstanceImportProgress>? progress = null, CancellationToken cancellationToken = default);
}
