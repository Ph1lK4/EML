namespace EML.Core.Minecraft.Catalog;

/// <summary>新实例目录的数据来源，官方原版与整合包平台使用相同的选择流程。</summary>
public enum InstanceCatalogProvider { Modrinth, CurseForge, Minecraft }

/// <summary>一页搜索请求；偏移量以平台返回的原始条目数计算。</summary>
public sealed record InstanceCatalogQuery
{
    public InstanceCatalogProvider Provider { get; init; }
    public string Query { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public int Sort { get; init; }
    public bool IncludeSnapshots { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 20;
}

/// <summary>搜索结果保持固定项目身份；图标和描述只用于显示。</summary>
public sealed record InstanceCatalogProject
{
    public required InstanceCatalogProvider Provider { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Summary { get; init; } = "";
    /// <summary>选中项目后读取的平台完整介绍；仅保存可显示的纯文本，不自行翻译。</summary>
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public string? IconUrl { get; init; }
    public string? WebsiteUrl { get; init; }
    public long Downloads { get; init; }
}

/// <summary>目录分页与后续偏移，不以筛掉后的展示数量推算下一页。</summary>
public sealed record InstanceCatalogPage(IReadOnlyList<InstanceCatalogProject> Items, int NextOffset, bool HasMore);

/// <summary>精确发布版本及其原平台完整性要求；原版下载对象为官方版本 JSON。</summary>
public sealed record InstanceCatalogRelease
{
    public required InstanceCatalogProvider Provider { get; init; }
    public required string ProjectId { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string VersionNumber { get; init; } = "";
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public IReadOnlyList<string> Loaders { get; init; } = [];
    public DateTimeOffset Published { get; init; }
    public string ReleaseType { get; init; } = "";
    public string FileName { get; init; } = "";
    public long? Size { get; init; }
    public IReadOnlyDictionary<string, string> Hashes { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> DownloadUrls { get; init; } = [];
    public string? Problem { get; init; }
    public string? WebsiteUrl { get; init; }
    public bool CanDownload => Problem is null && DownloadUrls.Count > 0 && Hashes.Count > 0;
    public string DisplayName => $"{Name} · {string.Join(" / ", GameVersions)} · {ReleaseType}";
}

/// <summary>发布版本的独立分页，避免把项目摘要当作完整版本列表。</summary>
public sealed record InstanceVersionPage(IReadOnlyList<InstanceCatalogRelease> Items, int NextOffset, bool HasMore);

/// <summary>只查询目录和固定版本；确认下载时重新查询，不能信任界面保留的旧 URL。</summary>
public interface IInstanceCatalog
{
    /// <summary>查询一个来源的整合包或官方版本目录。</summary>
    Task<InstanceCatalogPage> SearchAsync(InstanceCatalogQuery query, CancellationToken cancellationToken = default);
    /// <summary>按固定身份读取作者原始名称、简介和正文；未提供详情的目录保留原摘要。</summary>
    Task<InstanceCatalogProject> GetProjectAsync(InstanceCatalogProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(project);
    }
    /// <summary>读取当前项目的精确版本列表，使用独立分页与兼容性筛选。</summary>
    Task<InstanceVersionPage> GetVersionsAsync(InstanceCatalogProject project, string gameVersion = "", string loader = "",
        int offset = 0, int limit = 20, CancellationToken cancellationToken = default);
    /// <summary>下载前重新核对固定身份和该来源的可信摘要。</summary>
    Task<InstanceCatalogRelease> GetReleaseAsync(InstanceCatalogProvider provider, string projectId, string releaseId,
        CancellationToken cancellationToken = default);
}

/// <summary>已验证并保留的导入源；缓存原包用于日后恢复 Embedded 文件。</summary>
public sealed record PreparedInstanceArchive(string ArchivePath, string SuggestedName, string ExpectedFormat);

/// <summary>下载并准备可导入原包；不自行覆盖或修改既有实例。</summary>
public interface IInstanceArchiveService
{
    /// <summary>返回已校验的持久导入源，不改写现有实例或声明运行就绪。</summary>
    Task<PreparedInstanceArchive> PrepareAsync(InstanceCatalogProvider provider, string projectId, string releaseId,
        IProgress<Downloads.FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}
