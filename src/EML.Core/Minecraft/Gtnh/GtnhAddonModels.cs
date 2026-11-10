using EML.Core.Downloads;

namespace EML.Core.Minecraft.Gtnh;

/// <summary>人工整理的 GTNH 适配项目；仓库固定，不按普通 Minecraft 版本猜测兼容性。</summary>
public sealed record GtnhAddon(string Id, string Name, string Summary, string Category, string Repository, string Notes)
{
    public string ProjectUrl => "https://github.com/" + Repository;
}

/// <summary>用户可选的固定发行文件；缺少官方摘要时仅展示说明，不能自动安装。</summary>
public sealed record GtnhAddonRelease
{
    public required string AddonId { get; init; }
    public required string Repository { get; init; }
    public long ReleaseId { get; init; }
    public long AssetId { get; init; }
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
    public bool CanInstall => Problem is null && Size > 0 && Sha256.Length == 64;
    public string DisplayName => $"{Tag} · {FileName}" + (Prerelease ? "（预发布）" : "");
}

/// <summary>额外模组安装结果独立于整个实例的启动就绪状态。</summary>
public sealed record GtnhAddonInstallResult(bool Succeeded, bool Cancelled, string Message);

/// <summary>读取 GTNH 专用发行文件，并在明确确认兼容性后安装到用户选定的 GTNH 实例。</summary>
public interface IGtnhAddonService
{
    IReadOnlyList<GtnhAddon> Addons { get; }
    Task<IReadOnlyList<GtnhAddonRelease>> GetReleasesAsync(string instancePath, string addonId, CancellationToken token = default);
    Task<GtnhAddonInstallResult> InstallAsync(string instancePath, GtnhAddonRelease release, bool compatibilityConfirmed,
        IProgress<FileDownloadProgress>? progress = null, CancellationToken token = default);
}
