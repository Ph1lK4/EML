namespace EML.Core.Modrinth;

/// <summary>Modrinth 公开查询接口，为后续模组和整合包搜索、筛选及下载提供统一入口。</summary>
public interface IModrinthApiClient
{
    ModrinthRateLimit? LastRateLimit { get; }

    /// <summary>按关键词与分组筛选条件搜索项目，返回分页结果。</summary>
    Task<ModrinthSearchResult> SearchAsync(ModrinthSearchOptions options, CancellationToken cancellationToken = default);

    /// <summary>使用固定项目 ID 或当前 slug 查询项目信息；持久记录应保存返回的固定 ID。</summary>
    Task<ModrinthProject> GetProjectAsync(string projectIdOrSlug, CancellationToken cancellationToken = default);

    /// <summary>使用固定版本 ID 获取精确版本、依赖和可下载文件。</summary>
    Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken = default);

    /// <summary>按文件 SHA-1 或 SHA-512 精确查询版本；结果仅包含找到的哈希，不选择更新版本。</summary>
    Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(IReadOnlyList<string> hashes,
        string algorithm = "sha1", CancellationToken cancellationToken = default);

    /// <summary>查询项目的兼容版本，可同时限制 Minecraft 版本和加载器。</summary>
    Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectIdOrSlug,
        ModrinthVersionFilter? filter = null, CancellationToken cancellationToken = default);
}

/// <summary>公开 API 响应中的速率限制快照；缺少响应头时对应值为空。</summary>
public sealed record ModrinthRateLimit(int? Limit, int? Remaining, DateTimeOffset? ResetsAt);

/// <summary>支持搜索的内容类型；整合包与普通模组使用相同的查询和下载基础。</summary>
public enum ModrinthProjectType { Mod, Modpack, ResourcePack, Shader }

/// <summary>Modrinth 搜索支持的排序方式。</summary>
public enum ModrinthSearchSort { Relevance, Downloads, Follows, Newest, Updated }

/// <summary>可组合的相等筛选字段，不接受任意查询表达式。</summary>
public enum ModrinthFacetField { ProjectType, Version, Category, License, Author, Environment, ClientSide, ServerSide }

/// <summary>一个筛选条件；AdditionalFacetGroups 的组内为 OR，组与组之间为 AND。</summary>
public sealed record ModrinthSearchFacet(ModrinthFacetField Field, string Value);

/// <summary>搜索条件；同一快捷列表内任一值匹配即可，不同列表同时满足。</summary>
public sealed record ModrinthSearchOptions
{
    public string Query { get; init; } = "";
    public IReadOnlyList<ModrinthProjectType> ProjectTypes { get; init; } = [];
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public IReadOnlyList<string> Loaders { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Environments { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<ModrinthSearchFacet>> AdditionalFacetGroups { get; init; } = [];
    public ModrinthSearchSort Sort { get; init; } = ModrinthSearchSort.Relevance;
    public int Offset { get; init; }
    public int Limit { get; init; } = 20;
}

/// <summary>版本列表的兼容性筛选；各列表为空时不限制该条件。</summary>
public sealed record ModrinthVersionFilter
{
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public IReadOnlyList<string> Loaders { get; init; } = [];
    public bool? Featured { get; init; }
    /// <summary>列表默认不请求所有版本的长更新说明；查看详情时可按版本单独获取。</summary>
    public bool IncludeChangelog { get; init; }
}
