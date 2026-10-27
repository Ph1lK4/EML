namespace EML.Core.Minecraft;

/// <summary>只处理本地整合包；下载和完整性检查后续通过统一清单接入。</summary>
public interface IMinecraftInstanceImporter
{
    /// <summary>只读分析压缩包，供界面显示格式、名称和待补全的依赖。</summary>
    Task<InstanceImportPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default);

    /// <summary>重新验证压缩包，导入到新的实例目录；不会覆盖已有实例。</summary>
    Task<InstanceImportResult> ImportAsync(string archivePath, string instanceName,
        IProgress<InstanceImportProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>保存格式分析结果；包内条目数和依赖声明数不代表完整性检查结果。</summary>
/// <param name="SuggestedName">可在确认窗口修改的建议名称。</param>
/// <param name="Format">识别出的整合包来源格式。</param>
/// <param name="MinecraftVersion">声明的 Minecraft 版本；无法判断时为 null。</param>
/// <param name="Components">Minecraft、加载器等组件的安装需求。</param>
/// <param name="EmbeddedFileCount">本次计划从压缩包提取的文件数。</param>
/// <param name="DownloadFileCount">带下载地址的文件声明数。</param>
/// <param name="UnresolvedFileCount">仍需解析的 CurseForge 文件声明数。</param>
/// <param name="Notes">导入范围及待补全内容的提示。</param>
public sealed record InstanceImportPreview(string SuggestedName, string Format, string? MinecraftVersion,
    IReadOnlyList<InstanceComponentRequirement> Components, int EmbeddedFileCount,
    int DownloadFileCount, int UnresolvedFileCount, IReadOnlyList<string> Notes);

/// <summary>提交成功后返回路径和持久化清单，界面据此刷新并选中新实例。</summary>
public sealed record InstanceImportResult(string InstancePath, string GameDirectory, InstanceImportManifest Manifest);

/// <summary>按解压后的字节报告进度，百分比不包含后续尚未实现的下载。</summary>
public sealed record InstanceImportProgress(string FileName, long BytesCopied, long TotalBytes)
{
    /// <summary>按解压后的字节计算进度；总大小为零时暂记为 0。</summary>
    public double Percent => TotalBytes == 0 ? 0 : Math.Clamp(100.0 * BytesCopied / TotalBytes, 0, 100);
}
