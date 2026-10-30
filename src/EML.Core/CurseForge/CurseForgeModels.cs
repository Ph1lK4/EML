namespace EML.Core.CurseForge;

/// <summary>固定文件元数据；DownloadUrl 为空时不能自行构造 CDN 地址绕过分发限制。</summary>
public sealed record CurseForgeFile
{
    public long Id { get; init; }
    public int GameId { get; init; }
    public long ModId { get; init; }
    public bool IsAvailable { get; init; }
    public string DisplayName { get; init; } = "";
    public string FileName { get; init; } = "";
    public long FileLength { get; init; }
    public string? DownloadUrl { get; init; }
    public IReadOnlyList<CurseForgeFileHash> Hashes { get; init; } = [];
    public IReadOnlyList<CurseForgeFileDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public int ReleaseType { get; init; }
    public int FileStatus { get; init; }
    public DateTimeOffset FileDate { get; init; }
    public long DownloadCount { get; init; }
}

/// <summary>官方文件哈希：Algo 1 为 SHA1，2 为 MD5，未知算法由下载规划层明确处理。</summary>
public sealed record CurseForgeFileHash
{
    public string Value { get; init; } = "";
    public int Algo { get; init; }
}

/// <summary>模组依赖及关联类型；不得据此自动把整合包固定版本替换成依赖最新版。</summary>
public sealed record CurseForgeFileDependency
{
    public long ModId { get; init; }
    public int RelationType { get; init; }
}

/// <summary>项目基本信息；可空的分发偏好保持三态，不把未知擅自解释为允许。</summary>
public sealed record CurseForgeMod
{
    public long Id { get; init; }
    public int GameId { get; init; }
    public int? ClassId { get; init; }
    public string Name { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Summary { get; init; } = "";
    public bool? AllowModDistribution { get; init; }
    public bool IsAvailable { get; init; }
    public CurseForgeModLinks Links { get; init; } = new();
    public CurseForgeModAsset? Logo { get; init; }
    public IReadOnlyList<CurseForgeCategory> Categories { get; init; } = [];
    public IReadOnlyList<CurseForgeFile> LatestFiles { get; init; } = [];
    public long MainFileId { get; init; }
    public double DownloadCount { get; init; }
    public DateTimeOffset DateModified { get; init; }
}

/// <summary>作者项目网页及帮助链接，供无法自动获取文件时指向合法来源。</summary>
public sealed record CurseForgeModLinks
{
    public string? WebsiteUrl { get; init; }
    public string? WikiUrl { get; init; }
    public string? IssuesUrl { get; init; }
    public string? SourceUrl { get; init; }
}

/// <summary>项目图标或截图，当前只保存来源信息，不在 API 层自动下载。</summary>
public sealed record CurseForgeModAsset
{
    public long Id { get; init; }
    public string? Title { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? Url { get; init; }
}

/// <summary>分类信息，为将来的分类和筛选界面保留固定编号。</summary>
public sealed record CurseForgeCategory
{
    public int Id { get; init; }
    public int GameId { get; init; }
    public int? ClassId { get; init; }
    public string Name { get; init; } = "";
    public string Slug { get; init; } = "";
}

/// <summary>搜索结果及分页信息。</summary>
public sealed record CurseForgeSearchResult
{
    public IReadOnlyList<CurseForgeMod> Data { get; init; } = [];
    public CurseForgePagination Pagination { get; init; } = new();
}

/// <summary>官方分页统计，保留实际返回数量供继续加载判断。</summary>
public sealed record CurseForgePagination
{
    public int Index { get; init; }
    public int PageSize { get; init; }
    public int ResultCount { get; init; }
    public int TotalCount { get; init; }
}
