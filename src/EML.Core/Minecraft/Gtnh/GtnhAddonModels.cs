using EML.Core.Downloads;

namespace EML.Core.Minecraft.Gtnh;

/// <summary>推荐条目使用固定的发行平台，作者页面不会被当作可直接下载的文件。</summary>
public enum GtnhAddonProvider { Github, CurseForge, External, Modrinth }

/// <summary>模组与资源包分开登记，资源包不能进入 mods 目录。</summary>
public enum GtnhAddonKind { Mod, ResourcePack, Shader, ShaderLoader }

/// <summary>人工整理的 GTNH 适配项目；仓库固定，不按普通 Minecraft 版本猜测兼容性。</summary>
public sealed record GtnhAddon(string Id, string Name, string Summary, string Category, string Repository, string Notes)
{
    public GtnhAddonProvider Provider { get; init; } = GtnhAddonProvider.Github;
    public GtnhAddonKind Kind { get; init; } = GtnhAddonKind.Mod;
    public long? CurseForgeProjectId { get; init; }
    public long? CurseForgeFileId { get; init; }
    public string? ModrinthProjectId { get; init; }
    public string? ReleaseTag { get; init; }
    public string? WebsiteUrl { get; init; }
    public bool IsNonStargate { get; init; }
    public string? ManualReason { get; init; }
    public string? AssetNamePrefix { get; init; }
    /// <summary>人工目录中的文件名白名单，用于区分同一发行里的主包、界面包和其他附件。</summary>
    public string? AssetNamePattern { get; init; }
    public string ProjectUrl => WebsiteUrl ?? (Provider == GtnhAddonProvider.Github ? "https://github.com/" + Repository : "");
    public string SourceDisplay => Provider switch
    {
        GtnhAddonProvider.Github => "GitHub", GtnhAddonProvider.CurseForge => "CurseForge",
        GtnhAddonProvider.Modrinth => "Modrinth", _ => "作者页面"
    };
    public string RuleDescription => IsNonStargate
        ? "非星门规则模组：会修改流程、平衡性或难度，不用于星门规则进度。"
        : "分类依据 Wiki；不代表所有发行版本均符合星门规则，请另行核对规则和服务端要求。";
    public bool CanQueryReleases => ManualReason is null &&
        (Provider == GtnhAddonProvider.Github && Repository.Length > 0 ||
         Provider == GtnhAddonProvider.CurseForge && CurseForgeProjectId is > 0 ||
         Provider == GtnhAddonProvider.Modrinth && ModrinthProjectId is { Length: > 0 });
}

/// <summary>用户可选的固定发行文件；缺少官方摘要时仅展示说明，不能自动安装。</summary>
public sealed record GtnhAddonRelease
{
    public required string AddonId { get; init; }
    public GtnhAddonProvider Provider { get; init; } = GtnhAddonProvider.Github;
    public GtnhAddonKind Kind { get; init; } = GtnhAddonKind.Mod;
    public required string Repository { get; init; }
    public long ReleaseId { get; init; }
    public long AssetId { get; init; }
    public string? PlatformVersionId { get; init; }
    public required string Tag { get; init; }
    public required string FileName { get; init; }
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
    public required string DownloadUrl { get; init; }
    public required string ReleaseUrl { get; init; }
    public DateTimeOffset Published { get; init; }
    public bool Prerelease { get; init; }
    public string Notes { get; init; } = "";
    public string? Problem { get; init; }
    /// <summary>平台校验要求只属于实际采用的内容来源，安装服务还会核对签发快照。</summary>
    public InstanceFileRequirement? DownloadRequirement { get; init; }
    public bool CanInstall => Enum.IsDefined(Kind) && Problem is null && Size > 0 &&
        (Provider == GtnhAddonProvider.Github && Sha256.Length == 64 && Sha256.All(Uri.IsHexDigit) ||
         Provider == GtnhAddonProvider.CurseForge && DownloadRequirement is { Downloads.Count: > 0, ProjectId: > 0, FileId: > 0 } requirement &&
         requirement.Size == Size && requirement.Hashes.TryGetValue("sha1", out var hash) && hash.Length == 40 && hash.All(Uri.IsHexDigit) ||
         Provider == GtnhAddonProvider.Modrinth && DownloadRequirement is { Downloads.Count: > 0, ModrinthOrigin: not null } modrinth &&
         modrinth.Size == Size && modrinth.Hashes.Any(pair => (pair.Key is "sha1" or "sha512") &&
             pair.Value.Length == (pair.Key == "sha1" ? 40 : 128) && pair.Value.All(Uri.IsHexDigit)));
    public string DisplayName => $"{Tag} · {FileName}" + (Prerelease ? "（预发布）" : "");
}

/// <summary>额外模组安装结果独立于整个实例的启动就绪状态。</summary>
public sealed record GtnhAddonInstallResult(bool Succeeded, bool Cancelled, string Message)
{
    /// <summary>实际安装目标，供结果通知复制路径；本地文件采用别名时不能由界面按官方文件名推算。</summary>
    public string? DestinationPath { get; init; }
}

/// <summary>资源与光影使用独立登记流程，不进入模组管理目录或改变原整合包文件来源。</summary>
public interface IGtnhContentRegistrar
{
    /// <summary>提交已经下载并校验的固定来源文件，注册器负责目标目录、冲突保护及来源持久化。</summary>
    Task RegisterDownloadedAsync(string instancePath, string stagedFile, InstanceFileRequirement requirement,
        GtnhAddonKind kind, CancellationToken token = default);
}

/// <summary>读取 GTNH 专用发行文件，并在明确确认兼容性后安装到用户选定的 GTNH 实例。</summary>
public interface IGtnhAddonService
{
    IReadOnlyList<GtnhAddon> Addons { get; }
    Task<IReadOnlyList<GtnhAddonRelease>> GetReleasesAsync(string instancePath, string addonId, CancellationToken token = default);
    Task<GtnhAddonInstallResult> InstallAsync(string instancePath, GtnhAddonRelease release, bool compatibilityConfirmed,
        IProgress<FileDownloadProgress>? progress = null, CancellationToken token = default);
}
