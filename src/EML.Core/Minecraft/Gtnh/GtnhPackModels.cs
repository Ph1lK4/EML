using EML.Core.Downloads;
using EML.Core.Minecraft.Catalog;

namespace EML.Core.Minecraft.Gtnh;

/// <summary>镜像目录中的固定 Prism 客户端包；目录大小不代表作者签发的内容摘要。</summary>
public sealed record GtnhPackRelease
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string JavaVersion { get; init; }
    public required string FileName { get; init; }
    public required long Size { get; init; }
    public bool IsPrerelease { get; init; }
    public string DisplayName => $"GTNH {Version} · Java {JavaVersion}" + (IsPrerelease ? "（预发布）" : "");
}

/// <summary>查询国内镜像并准备 Prism 导入源，实例导入与依赖补全仍由既有流程负责。</summary>
public interface IGtnhPackService
{
    /// <summary>只返回支持的数字版本客户端包，可显式包含测试版目录。</summary>
    Task<IReadOnlyList<GtnhPackRelease>> GetReleasesAsync(bool includePrereleases = false,
        CancellationToken cancellationToken = default);

    /// <summary>重新确认固定目录项，下载并验证 Prism 包结构，保留原包供后续恢复内嵌文件。</summary>
    Task<PreparedInstanceArchive> PrepareAsync(string releaseId, IProgress<FileDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
