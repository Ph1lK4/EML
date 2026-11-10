namespace EML.Core.Minecraft.Mods.Online;

/// <summary>平台与固定项目编号共同组成身份，不能按显示名称合并项目。</summary>
public enum ModProvider { Modrinth, CurseForge }
public sealed record OnlineProjectId(ModProvider Provider, string ProjectId);
public enum OnlineModSort { Relevance, Downloads, Updated, Newest }
public enum OnlineDependencyKind { Required, Optional, Incompatible, Embedded }

/// <summary>实例兼容条件与分页快照；Provider 为空时分别查询两个平台。</summary>
public sealed record OnlineModQuery
{
    public string Text { get; init; } = "";
    public ModProvider? Provider { get; init; }
    public string GameVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string Category { get; init; } = "";
    public string Environment { get; init; } = "";
    public OnlineModSort Sort { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; } = 20;
}

/// <summary>搜索与详情共用展示信息；远程正文只作为文本，不能执行脚本。</summary>
public sealed record OnlineModProject
{
    public required OnlineProjectId Id { get; init; }
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Description { get; init; } = "";
    public string? IconUrl { get; init; }
    public string? WebsiteUrl { get; init; }
    public string? SourceUrl { get; init; }
    public string? IssuesUrl { get; init; }
    public string License { get; init; } = "未提供";
    public string Environment { get; init; } = "未提供";
    public long Downloads { get; init; }
    public DateTimeOffset Updated { get; init; }
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public IReadOnlyList<string> Loaders { get; init; } = [];
}

public sealed record OnlineModSearchPage(IReadOnlyList<OnlineModProject> Items, bool HasMore, IReadOnlyList<string> Notes);
public sealed record OnlineModVersionPage(IReadOnlyList<OnlineModVersion> Items, bool HasMore);

/// <summary>一个固定版本包含的具体文件；Requirement 的摘要仅属于实际下载来源。</summary>
public sealed record OnlineModFile
{
    public required string FileName { get; init; }
    public required InstanceFileRequirement Requirement { get; init; }
    public bool Primary { get; init; }
    public string? ManualDownloadUrl { get; init; }
    public string? Problem { get; init; }
}

/// <summary>依赖可以指向项目或固定版本，Key 用于可选项选择，不依赖显示名称。</summary>
public sealed record OnlineModDependency
{
    public required string Key { get; init; }
    public OnlineProjectId? Project { get; init; }
    public string? VersionId { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public OnlineDependencyKind Kind { get; init; }
}

public sealed record OnlineModVersion
{
    public required OnlineProjectId Project { get; init; }
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public string VersionNumber { get; init; } = "";
    public string Channel { get; init; } = "";
    public string Changelog { get; init; } = "";
    public DateTimeOffset Published { get; init; }
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public IReadOnlyList<string> Loaders { get; init; } = [];
    public IReadOnlyList<OnlineModFile> Files { get; init; } = [];
    public IReadOnlyList<OnlineModDependency> Dependencies { get; init; } = [];
}

/// <summary>预览锁定实例、版本、文件和用户依赖选择；安装不会临时换成更新版本。</summary>
public sealed record OnlineModPlanRequest
{
    public required string InstancePath { get; init; }
    public required OnlineProjectId Project { get; init; }
    public required string VersionId { get; init; }
    public required string FileName { get; init; }
    public bool IncludeRequired { get; init; } = true;
    public IReadOnlyList<string> OptionalDependencyKeys { get; init; } = [];
}

public enum OnlineModPlanStatus { Download, AlreadyInstalled, Blocked }

/// <summary>下载条目保留最初选择的平台身份和实际文件来源，备用下载不会丢失原始依据。</summary>
public sealed record OnlineModPlanItem(OnlineModVersion Version, OnlineModFile File, string Reason,
    OnlineModPlanStatus Status, string? Message = null);

/// <summary>OptionalDependencies 包含解析过程中发现的可选依赖，可勾选后重新生成计划。</summary>
public sealed record OnlineModInstallPlan
{
    public required string Id { get; init; }
    public required OnlineModPlanRequest Request { get; init; }
    public IReadOnlyList<OnlineModPlanItem> Items { get; init; } = [];
    public IReadOnlyList<OnlineModDependency> OptionalDependencies { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool CanInstall => Items.Count > 0 && Items.All(item => item.Status != OnlineModPlanStatus.Blocked);
    public long TotalBytes => Items.Where(item => item.Status == OnlineModPlanStatus.Download).Sum(item => item.File.Requirement.Size ?? 0);
}

public sealed record OnlineModInstallProgress(string Stage, string FileName, int CompletedFiles, int TotalFiles, long ReceivedBytes, long? TotalBytes);
/// <summary>安装状态与启动就绪分开；失败或取消时已经提交的文件保留并逐项报告。</summary>
public sealed record OnlineModInstallResult(int Installed, int Skipped, bool Cancelled, IReadOnlyList<string> Issues)
{
    public bool Succeeded => !Cancelled && Issues.Count == 0;
}

/// <summary>只查询远程目录，不修改实例；精确版本接口验证项目归属。</summary>
public interface IOnlineModCatalog
{
    Task<OnlineModSearchPage> SearchAsync(OnlineModQuery query, CancellationToken token = default);
    Task<OnlineModProject> GetProjectAsync(OnlineProjectId project, CancellationToken token = default);
    Task<OnlineModVersionPage> GetVersionsAsync(OnlineProjectId project, string gameVersion, string loader, int page = 0, CancellationToken token = default);
    Task<OnlineModVersion> GetVersionAsync(OnlineProjectId project, string versionId, CancellationToken token = default);
    /// <summary>官方无法直接获取时仅尝试 Modrinth 同大小同哈希文件，不按名称替代。</summary>
    Task<OnlineModFile> ResolveDownloadAsync(OnlineModVersion version, OnlineModFile file, CancellationToken token = default);
}

/// <summary>依赖解析和实际安装分开，只有用户确认的计划可以修改实例。</summary>
public interface IOnlineModInstaller
{
    Task<OnlineModInstallPlan> PlanAsync(OnlineModPlanRequest request, CancellationToken token = default);
    Task<OnlineModInstallResult> InstallAsync(OnlineModInstallPlan plan,
        IProgress<OnlineModInstallProgress>? progress = null, CancellationToken token = default);
}
