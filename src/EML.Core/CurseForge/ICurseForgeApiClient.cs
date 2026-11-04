namespace EML.Core.CurseForge;

/// <summary>CurseForge 官方 API 入口，保留固定项目与文件 ID，供实例补全和后续搜索页面复用。</summary>
public interface ICurseForgeApiClient
{
    CurseForgeRateLimit? LastRateLimit { get; }

    /// <summary>按固定文件 ID 批量查询；未返回的 ID 由调用方作为不可获取项处理。</summary>
    Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(IReadOnlyList<long> fileIds, CancellationToken cancellationToken = default);

    /// <summary>批量读取项目及作者分发偏好，用于核实清单中的项目与文件归属。</summary>
    Task<IReadOnlyList<CurseForgeMod>> GetModsAsync(IReadOnlyList<long> projectIds, CancellationToken cancellationToken = default);

    /// <summary>读取一个项目的固定文件版本，不以最新版替代清单版本。</summary>
    Task<CurseForgeFile> GetFileAsync(long projectId, long fileId, CancellationToken cancellationToken = default);

    /// <summary>读取一个项目的分类、网页链接和分发偏好。</summary>
    Task<CurseForgeMod> GetModAsync(long projectId, CancellationToken cancellationToken = default);

    /// <summary>按游戏版本、类型、分类和加载器等条件搜索项目。</summary>
    Task<CurseForgeSearchResult> SearchAsync(CurseForgeSearchOptions options, CancellationToken cancellationToken = default);
}

/// <summary>服务器实际返回的限流信息；未声明的字段保持为空。</summary>
public sealed record CurseForgeRateLimit(int? Limit, int? Remaining, TimeSpan? RetryAfter);

/// <summary>官方搜索排序字段值。</summary>
public enum CurseForgeSortField
{
    Featured = 1, Popularity = 2, LastUpdated = 3, Name = 4, Author = 5,
    TotalDownloads = 6, Category = 7, GameVersion = 8, EarlyAccess = 9, FeaturedReleased = 10, ReleasedDate = 11, Rating = 12
}

/// <summary>官方 Minecraft 加载器编号，查询时应使用包声明的加载器。</summary>
public enum CurseForgeModLoaderType { Any = 0, Forge = 1, Cauldron = 2, LiteLoader = 3, Fabric = 4, Quilt = 5, NeoForge = 6 }

/// <summary>未来模组、整合包与资源搜索共用的筛选值；Minecraft 默认 gameId 为 432。</summary>
public sealed record CurseForgeSearchOptions
{
    public int GameId { get; init; } = 432;
    public int? ClassId { get; init; }
    public int? CategoryId { get; init; }
    public string SearchFilter { get; init; } = "";
    public string? GameVersion { get; init; }
    public CurseForgeModLoaderType? ModLoaderType { get; init; }
    public CurseForgeSortField SortField { get; init; } = CurseForgeSortField.Popularity;
    public bool SortDescending { get; init; } = true;
    public string? Slug { get; init; }
    public int Index { get; init; }
    public int PageSize { get; init; } = 20;
}
