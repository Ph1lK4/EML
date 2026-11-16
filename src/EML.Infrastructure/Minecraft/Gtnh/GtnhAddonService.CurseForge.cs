using System.Collections.ObjectModel;
using System.Globalization;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;
using EML.Core.Minecraft.Mods.Online;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Mods;

namespace EML.Infrastructure.Minecraft.Gtnh;

public sealed partial class GtnhAddonService
{
    /// <summary>只读取固定项目的 1.7.10 Forge 文件，限制分页和并发后逐个核对精确版本。</summary>
    private async Task<IReadOnlyList<GtnhAddonRelease>> ReadCurseForgeReleasesAsync(GtnhAddon addon, CancellationToken token)
    {
        var catalog = _onlineCatalog ?? throw new InvalidOperationException("尚未连接 CurseForge 模组目录，请检查平台授权与下载服务配置。");
        var project = new OnlineProjectId(ModProvider.CurseForge, addon.CurseForgeProjectId!.Value.ToString(CultureInfo.InvariantCulture));
        var candidates = new Dictionary<string, OnlineModVersion>(StringComparer.Ordinal);
        if (addon.CurseForgeFileId is { } fixedId && fixedId > 0)
        {
            var fixedVersion = await catalog.GetVersionAsync(project, fixedId.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
            RequireCurseForgeIdentity(fixedVersion, project, fixedId.ToString(CultureInfo.InvariantCulture));
            if (IsGtnhPlatformVersion(fixedVersion)) candidates.Add(fixedVersion.Id, fixedVersion);
        }
        else for (var page = 0; page < 4; page++)
        {
            var response = await catalog.GetVersionsAsync(project, "1.7.10", "forge", page, token).ConfigureAwait(false);
            if (response.Items.Count > 100) throw new InvalidDataException("平台发行分页超过允许大小。");
            foreach (var version in response.Items)
            {
                RequireCurseForgeIdentity(version, project, version.Id);
                if (IsGtnhPlatformVersion(version) && candidates.Count < 80) candidates.TryAdd(version.Id, version);
            }
            if (!response.HasMore || candidates.Count >= 80) break;
        }
        var result = new List<GtnhAddonRelease>();
        // 共用目录负责平台授权与同内容备用下载；本服务只在固定版本中选择，不推断 GTNH 兼容性。
        foreach (var batch in candidates.Values.Chunk(4))
        {
            var groups = await Task.WhenAll(batch.Select(async candidate =>
            {
                var version = await catalog.GetVersionAsync(project, candidate.Id, token).ConfigureAwait(false);
                RequireCurseForgeIdentity(version, project, candidate.Id);
                if (!IsGtnhPlatformVersion(version)) return Array.Empty<GtnhAddonRelease>();
                if (version.Files.Count > 10) throw new InvalidDataException("固定版本返回过多文件，请通过项目页面核对。");
                var releases = new List<GtnhAddonRelease>();
                foreach (var file in version.Files)
                {
                    if (!IsJarFile(file.FileName)) continue;
                    var official = SnapshotRequirement(file.Requirement);
                    RequireCurseForgeFileIdentity(official, addon.CurseForgeProjectId.Value, long.Parse(candidate.Id, CultureInfo.InvariantCulture));
                    if (official.Source != InstanceFileSource.CurseForge || official.Origin != InstanceFileOrigin.CurseForgeDownload)
                        throw new InvalidDataException("CurseForge 文件没有保留原平台完整性来源。");
                    var resolved = await catalog.ResolveDownloadAsync(version, file, token).ConfigureAwait(false);
                    var requirement = SnapshotRequirement(resolved.Requirement);
                    RequireCurseForgeFileIdentity(requirement, addon.CurseForgeProjectId.Value, long.Parse(candidate.Id, CultureInfo.InvariantCulture));
                    if (resolved.FileName != file.FileName || requirement.Size != official.Size)
                        throw new InvalidDataException("备用文件与所选官方文件身份或大小不一致。");
                    if (official.Hashes.TryGetValue("sha1", out var originalSha1) &&
                        (!requirement.Hashes.TryGetValue("sha1", out var resolvedSha1) || !originalSha1.Equals(resolvedSha1, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("备用文件与所选官方 SHA-1 不一致。");
                    var problem = !ValidSha1(official) ? "CurseForge 未提供可验证的官方 SHA-1，请通过官方页面核对并手动处理。" :
                        requirement.Size is null or <= 0 or > MaximumAssetBytes ? "发行文件大小无效或超过 256 MiB，本页不自动安装。" :
                        resolved.Problem ?? (requirement.Downloads.Count == 0 ? "没有可验证的下载地址，请从官方页面获取所选文件。" : null);
                    if (problem is null)
                        ModManagementStore.ValidateOnlineBaseline(requirement with { Path = "mods/" + file.FileName });
                    releases.Add(new()
                    {
                        AddonId = addon.Id, Provider = GtnhAddonProvider.CurseForge, Kind = addon.Kind, Repository = "",
                        ReleaseId = requirement.FileId!.Value, AssetId = requirement.FileId.Value,
                        Tag = string.IsNullOrWhiteSpace(version.VersionNumber) ? version.Id : version.VersionNumber,
                        FileName = file.FileName, Size = requirement.Size ?? 0,
                        DownloadUrl = requirement.Downloads.FirstOrDefault() ?? "", DownloadRequirement = requirement,
                        ReleaseUrl = $"https://www.curseforge.com/projects/{addon.CurseForgeProjectId.Value}/files/{requirement.FileId.Value}",
                        Published = version.Published, Prerelease = !version.Channel.Equals("release", StringComparison.OrdinalIgnoreCase),
                        Notes = CurseForgeNotes(version, requirement), Problem = problem
                    });
                }
                return releases.ToArray();
            })).ConfigureAwait(false);
            result.AddRange(groups.SelectMany(group => group));
        }
        return result;
    }

    /// <summary>精确版本必须属于原定项目和文件 ID，不能让列表或详情切换到其他模组。</summary>
    private static void RequireCurseForgeIdentity(OnlineModVersion version, OnlineProjectId project, string expectedVersion)
    {
        if (version.Project != project || version.Id != expectedVersion ||
            !long.TryParse(version.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new InvalidDataException("CurseForge 返回的发行文件不属于所选固定项目或版本。");
    }

    /// <summary>目录筛选还需由返回元数据佐证；未知加载器或其他游戏版本不视为 GTNH 候选。</summary>
    private static bool IsGtnhPlatformVersion(OnlineModVersion version) =>
        version.GameVersions.Contains("1.7.10", StringComparer.Ordinal) && version.Loaders.Contains("forge", StringComparer.OrdinalIgnoreCase);

    /// <summary>下载前后必须保留相同的 CurseForge 项目与文件身份。</summary>
    private static void RequireCurseForgeFileIdentity(InstanceFileRequirement requirement, long projectId, long fileId)
    {
        if (requirement.ProjectId != projectId || requirement.FileId != fileId)
            throw new InvalidDataException("下载要求与所选 CurseForge 文件身份不一致。");
    }

    /// <summary>仅处理单个模组 JAR 文件名，不接受路径、源码包或资源包归档。</summary>
    private static bool IsJarFile(string name)
    {
        var relative = InstanceImportPaths.Relative(name);
        return !relative.Contains('/') && relative.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
            !new[] { "-sources", "-javadoc", "-dev", "-deobf" }.Any(suffix => relative.Contains(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>官方 SHA-1 缺失或格式错误时不能以另一平台的摘要代替原始要求。</summary>
    private static bool ValidSha1(InstanceFileRequirement requirement) => requirement.Hashes.TryGetValue("sha1", out var hash) &&
        hash.Length == 40 && hash.All(Uri.IsHexDigit);

    /// <summary>提示玩家自行核对 GTNH 版本、两端安装及依赖，不把平台筛选当作兼容保证。</summary>
    private static string CurseForgeNotes(OnlineModVersion version, InstanceFileRequirement requirement)
    {
        var notes = "平台已筛选 Minecraft 1.7.10 / Forge；仍需核对当前 GTNH 版本、客户端/服务端和已有模组。";
        var required = version.Dependencies.Where(dependency => dependency.Kind == OnlineDependencyKind.Required)
            .Select(dependency => string.IsNullOrWhiteSpace(dependency.Name) ? dependency.Key : dependency.Name).Distinct().ToArray();
        notes += required.Length > 0 ? "\n平台声明的必需依赖：" + string.Join("、", required) + "。本页不自动安装依赖，请先确认实例中已满足；也可通过在线模组页面处理依赖。" :
            "\n本页不自动安装依赖，请同时核对作者说明与当前实例。";
        if (requirement.Origin == InstanceFileOrigin.ModrinthDownload)
            notes += "\n使用 Modrinth 同大小、同 SHA-1 的文件作为备用下载，保留原 CurseForge 文件身份。";
        var changelog = version.Changelog.Length > 12000 ? version.Changelog[..12000] + "\n（更多内容请查看官方发行说明）" : version.Changelog;
        return string.IsNullOrWhiteSpace(changelog) ? notes : notes + "\n\n" + changelog;
    }

    /// <summary>复制为不可变快照，避免目录缓存或界面修改集合后改变已经展示的官方校验依据。</summary>
    private static InstanceFileRequirement SnapshotRequirement(InstanceFileRequirement requirement) => requirement with
    {
        Hashes = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(requirement.Hashes, StringComparer.Ordinal)),
        Downloads = Array.AsReadOnly(requirement.Downloads.ToArray()),
        CurseForgeOrigin = requirement.CurseForgeOrigin is { } origin ? origin with
        { Hashes = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(origin.Hashes, StringComparer.Ordinal)) } : null
    };
}
