namespace EML.Core.Modrinth;

/// <summary>搜索分页结果，保留总数以便界面实现翻页和加载更多。</summary>
public sealed record ModrinthSearchResult
{
    public IReadOnlyList<ModrinthSearchHit> Hits { get; init; } = [];
    public int Offset { get; init; }
    public int Limit { get; init; }
    public int TotalHits { get; init; }
}

/// <summary>搜索条目；ProjectId 稳定，Slug 和展示名称可能随作者修改。</summary>
public sealed record ModrinthSearchHit
{
    public string ProjectId { get; init; } = "";
    public string Slug { get; init; } = "";
    public string ProjectType { get; init; } = "";
    public IReadOnlyList<string> AllProjectTypes { get; init; } = [];
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> DisplayCategories { get; init; } = [];
    public IReadOnlyList<string> Versions { get; init; } = [];
    public string? LatestVersion { get; init; }
    public string? IconUrl { get; init; }
    public string ClientSide { get; init; } = "";
    public string ServerSide { get; init; } = "";
    public IReadOnlyList<string> Environment { get; init; } = [];
    public long Downloads { get; init; }
    public int Follows { get; init; }
    public DateTimeOffset DateCreated { get; init; }
    public DateTimeOffset DateModified { get; init; }
}

/// <summary>项目详情，用于展示简介、兼容信息和固定版本列表。</summary>
public sealed record ModrinthProject
{
    public string Id { get; init; } = "";
    public string Slug { get; init; } = "";
    public string ProjectType { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Body { get; init; } = "";
    public string ClientSide { get; init; } = "";
    public string ServerSide { get; init; } = "";
    public IReadOnlyList<string> Environment { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> AdditionalCategories { get; init; } = [];
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public IReadOnlyList<string> Loaders { get; init; } = [];
    public IReadOnlyList<string> Versions { get; init; } = [];
    public string? IconUrl { get; init; }
    public string? IssuesUrl { get; init; }
    public string? SourceUrl { get; init; }
    public string? WikiUrl { get; init; }
    public ModrinthLicense? License { get; init; }
    public long Downloads { get; init; }
    public int Followers { get; init; }
}

/// <summary>项目声明的许可证元数据，不能据此推断额外的转载授权。</summary>
public sealed record ModrinthLicense
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Url { get; init; }
}

/// <summary>固定版本详情，包含下载文件和 required、optional 等依赖类型。</summary>
public sealed record ModrinthVersion
{
    public string Id { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string Name { get; init; } = "";
    public string VersionNumber { get; init; } = "";
    public string VersionType { get; init; } = "";
    public string Environment { get; init; } = "unknown";
    public string? Changelog { get; init; }
    public DateTimeOffset DatePublished { get; init; }
    public bool Featured { get; init; }
    public long Downloads { get; init; }
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public IReadOnlyList<string> Loaders { get; init; } = [];
    public IReadOnlyList<ModrinthVersionFile> Files { get; init; } = [];
    public IReadOnlyList<ModrinthVersionDependency> Dependencies { get; init; } = [];
}

/// <summary>一个精确文件的下载地址、哈希及大小；Primary 表示作者指定的默认文件。</summary>
public sealed record ModrinthVersionFile
{
    public string Filename { get; init; } = "";
    public string Url { get; init; } = "";
    public long Size { get; init; }
    public bool Primary { get; init; }
    public string? FileType { get; init; }
    public IReadOnlyDictionary<string, string> Hashes { get; init; } = new Dictionary<string, string>();
}

/// <summary>版本依赖；项目 ID 和版本 ID 均可为空，调用方必须保留依赖类型后再决定安装。</summary>
public sealed record ModrinthVersionDependency
{
    public string? ProjectId { get; init; }
    public string? VersionId { get; init; }
    public string? FileName { get; init; }
    public string DependencyType { get; init; } = "";
}
