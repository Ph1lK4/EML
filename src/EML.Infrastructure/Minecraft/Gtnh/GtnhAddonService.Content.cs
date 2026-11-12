using System.Globalization;
using EML.Core.CurseForge;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;
using EML.Core.Modrinth;
using EML.Infrastructure.Minecraft.CurseForge.Api;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Modrinth.Api;

namespace EML.Infrastructure.Minecraft.Gtnh;

public sealed partial class GtnhAddonService
{
    /// <summary>资源包与光影核对官方项目类型和文件身份，不复用仅限模组的目录，也不构造受限文件地址。</summary>
    private async Task<IReadOnlyList<GtnhAddonRelease>> ReadCurseForgeContentReleasesAsync(GtnhAddon addon, CancellationToken token)
    {
        var api = _curseForge ?? throw new InvalidOperationException("尚未连接 CurseForge 资源目录，请检查平台授权配置。");
        var projectId = addon.CurseForgeProjectId!.Value;
        var project = await api.GetModAsync(projectId, token).ConfigureAwait(false);
        var expectedClass = addon.Kind switch { GtnhAddonKind.ResourcePack => 12, GtnhAddonKind.Shader => 6552, _ => 0 };
        if (expectedClass == 0 || project.Id != projectId || project.GameId != 432 || project.ClassId != expectedClass)
            throw new InvalidDataException("CurseForge 项目类型或固定身份与所选资源不一致。");
        var files = new List<CurseForgeFile>();
        if (addon.CurseForgeFileId is { } fixedId && fixedId > 0)
        {
            var file = await api.GetFileAsync(projectId, fixedId, token).ConfigureAwait(false);
            if (file.Id != fixedId) throw new InvalidDataException("CurseForge 没有返回指定文件。");
            files.Add(file);
        }
        else for (var page = 0; page < 4; page++)
        {
            var response = await api.GetModFilesAsync(projectId, new()
            {
                GameVersion = addon.Kind == GtnhAddonKind.Shader ? null : "1.7.10", Index = page * 20, PageSize = 20
            }, token).ConfigureAwait(false);
            if (response.Data.Count > 100) throw new InvalidDataException("CurseForge 文件分页超过允许大小。");
            files.AddRange(response.Data.Take(Math.Max(0, 80 - files.Count)));
            if (response.Data.Count == 0 || response.Pagination.Index + response.Pagination.ResultCount >= response.Pagination.TotalCount || files.Count >= 80) break;
        }
        var releases = new List<GtnhAddonRelease>();
        foreach (var file in files.DistinctBy(file => file.Id))
        {
            if (file.Id <= 0 || file.ModId != projectId || file.GameId != 432)
                throw new InvalidDataException("CurseForge 文件不属于所选资源项目。");
            if (!HasExpectedExtension(file.FileName, addon.Kind) || file.IsServerPack) continue;
            var compatibilityProblem = addon.Kind == GtnhAddonKind.ResourcePack && !file.GameVersions.Contains("1.7.10", StringComparer.Ordinal)
                ? "官方文件未声明支持 Minecraft 1.7.10，无法确认资源包适用于 GTNH；请通过作者页面核对，本页不自动安装。" : null;
            var safeName = InstanceImportPaths.Relative(file.FileName);
            if (safeName.Contains('/')) throw new InvalidDataException("资源文件名称不能包含路径。");
            var hashes = file.Hashes.Where(hash => hash.Algo == 1).ToArray();
            if (hashes.Length > 1) throw new InvalidDataException("CurseForge 官方 SHA-1 重复。");
            var sha1 = hashes.SingleOrDefault()?.Value;
            var validHash = sha1 is { Length: 40 } && sha1.All(Uri.IsHexDigit);
            var allowed = project.AllowModDistribution != false && project.IsAvailable && file.IsAvailable;
            var url = allowed ? file.DownloadUrl : null;
            if (url is { Length: > 0 } && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !CurseForgeRequestHeaders.IsCdnUri(uri)))
                throw new InvalidDataException("CurseForge 文件下载地址不是受信任的官方 CDN。");
            var requirement = SnapshotRequirement(new()
            {
                Source = InstanceFileSource.CurseForge, Origin = InstanceFileOrigin.CurseForgeDownload, Role = InstanceFileRole.Content,
                ProjectId = projectId, FileId = file.Id, Size = file.FileLength,
                Hashes = validHash ? new Dictionary<string, string> { ["sha1"] = sha1!.ToLowerInvariant() } : new Dictionary<string, string>(),
                Downloads = validHash && url is { Length: > 0 } ? [url] : []
            });
            var problem = compatibilityProblem ?? (!validHash ? "CurseForge 未提供有效官方 SHA-1，不能自动下载并校验。" :
                file.FileLength is <= 0 or > MaximumAssetBytes ? "发行文件大小无效或超过 256 MiB，本页不自动安装。" :
                requirement.Downloads.Count == 0 ? "作者分发限制或平台暂无下载地址，请从官方文件页面获取；本页不自动替换为其他内容。" : null);
            releases.Add(new()
            {
                AddonId = addon.Id, Provider = GtnhAddonProvider.CurseForge, Kind = addon.Kind, Repository = "",
                ReleaseId = file.Id, AssetId = file.Id, Tag = string.IsNullOrWhiteSpace(file.DisplayName) ? file.Id.ToString(CultureInfo.InvariantCulture) : file.DisplayName,
                FileName = file.FileName, Size = file.FileLength, DownloadUrl = requirement.Downloads.FirstOrDefault() ?? "", DownloadRequirement = requirement,
                ReleaseUrl = $"https://www.curseforge.com/projects/{projectId}/files/{file.Id}", Published = file.FileDate,
                Prerelease = file.ReleaseType != 1, Problem = problem,
                Notes = ContentNotes(addon.Kind, file.GameVersions, [], "") +
                    (file.Dependencies.Count > 0 ? "\n平台声明了关联或依赖项目，请查看官方文件页；本页不自动安装依赖。" : "")
            });
        }
        if (releases.Count == 0 && addon.Kind == GtnhAddonKind.ResourcePack)
            throw new InvalidOperationException("平台未返回可核对的 Minecraft 1.7.10 资源文件，请查看作者页面；不会自动安装其他版本或替代文件。");
        return releases;
    }

    /// <summary>Modrinth 项目类型与固定版本逐一核对；光影保留全部官方兼容标签让用户选择，不推断跨版本兼容。</summary>
    private async Task<IReadOnlyList<GtnhAddonRelease>> ReadModrinthReleasesAsync(GtnhAddon addon, CancellationToken token)
    {
        var api = _modrinth ?? throw new InvalidOperationException("尚未连接 Modrinth 资源目录。");
        var project = await api.GetProjectAsync(addon.ModrinthProjectId!, token).ConfigureAwait(false);
        var expectedType = addon.Kind switch { GtnhAddonKind.ResourcePack => "resourcepack", GtnhAddonKind.Shader => "shader", _ => "mod" };
        if (!SafePlatformId(project.Id) || project.ProjectType != expectedType ||
            project.Id != addon.ModrinthProjectId && project.Slug != addon.ModrinthProjectId)
            throw new InvalidDataException("Modrinth 项目类型或身份与所选内容不一致。");
        var versions = await api.GetVersionsAsync(project.Id, new()
        {
            GameVersions = addon.Kind == GtnhAddonKind.Shader ? [] : ["1.7.10"],
            Loaders = IsModKind(addon.Kind) ? ["forge"] : [], IncludeChangelog = false
        }, token).ConfigureAwait(false);
        if (versions.Count > 2000) throw new InvalidDataException("Modrinth 发行列表超过允许大小。");
        var releases = new List<GtnhAddonRelease>();
        foreach (var batch in versions.DistinctBy(version => version.Id).OrderByDescending(version => version.DatePublished).Take(80).Chunk(4))
        {
            var details = await Task.WhenAll(batch.Select(async listed =>
            {
                if (!SafePlatformId(listed.Id) || listed.ProjectId != project.Id)
                    throw new InvalidDataException("Modrinth 版本不属于所选项目。");
                var version = await api.GetVersionAsync(listed.Id, token).ConfigureAwait(false);
                if (version.Id != listed.Id || version.ProjectId != project.Id)
                    throw new InvalidDataException("Modrinth 固定版本身份已变化。");
                return version;
            })).ConfigureAwait(false);
            foreach (var version in details)
            {
                if (IsModKind(addon.Kind) && (!version.GameVersions.Contains("1.7.10", StringComparer.Ordinal) ||
                    !version.Loaders.Contains("forge", StringComparer.OrdinalIgnoreCase))) continue;
                var compatibilityProblem = addon.Kind == GtnhAddonKind.ResourcePack && !version.GameVersions.Contains("1.7.10", StringComparer.Ordinal)
                    ? "官方版本未声明支持 Minecraft 1.7.10，无法确认资源包适用于 GTNH；请通过作者页面核对，本页不自动安装。" : null;
                if (version.Files.Count > 30) throw new InvalidDataException("Modrinth 固定版本返回过多文件。");
                foreach (var file in version.Files.Where(file => HasExpectedExtension(file.Filename, addon.Kind)))
                {
                    var safeName = InstanceImportPaths.Relative(file.Filename);
                    if (safeName.Contains('/')) throw new InvalidDataException("资源文件名称不能包含路径。");
                    if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "cdn.modrinth.com" ||
                        !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                        Uri.UnescapeDataString(uri.AbsolutePath) != $"/data/{project.Id}/versions/{version.Id}/{file.Filename}")
                        throw new InvalidDataException("Modrinth 地址与固定项目、版本或文件名不一致。");
                    var request = ModrinthVersionDownloadService.CreateDownloadRequest(version, Path.GetTempPath(), file.Filename);
                    var requirement = SnapshotRequirement(new()
                    {
                        Source = InstanceFileSource.DirectDownload, Origin = InstanceFileOrigin.ModrinthDownload, Role = InstanceFileRole.Content,
                        Size = file.Size, Hashes = request.Hashes, Downloads = request.Urls,
                        ModrinthOrigin = new() { ProjectId = project.Id, VersionId = version.Id, FileName = file.Filename }
                    });
                    var problem = compatibilityProblem ?? (!requirement.Hashes.ContainsKey("sha1") && !requirement.Hashes.ContainsKey("sha512") ? "Modrinth 未提供官方 SHA-1 或 SHA-512，不能自动安装。" :
                        file.Size is <= 0 or > MaximumAssetBytes ? "发行文件大小无效或超过 256 MiB，本页不自动安装。" : null);
                    releases.Add(new()
                    {
                        AddonId = addon.Id, Provider = GtnhAddonProvider.Modrinth, Kind = addon.Kind, Repository = "", PlatformVersionId = version.Id,
                        Tag = string.IsNullOrWhiteSpace(version.VersionNumber) ? version.Id : version.VersionNumber,
                        FileName = file.Filename, Size = file.Size, DownloadUrl = file.Url, DownloadRequirement = requirement,
                        ReleaseUrl = $"https://modrinth.com/{expectedType}/{project.Id}/version/{version.Id}", Published = version.DatePublished,
                        Prerelease = !version.VersionType.Equals("release", StringComparison.OrdinalIgnoreCase), Problem = problem,
                        Notes = ContentNotes(addon.Kind, version.GameVersions, version.Loaders, version.Changelog ?? "") +
                            (version.Dependencies.Count > 0 ? "\n平台声明了关联或依赖项目，请查看官方版本页；本页不自动安装依赖。" : "")
                    });
                }
            }
        }
        if (releases.Count == 0 && addon.Kind == GtnhAddonKind.ResourcePack)
            throw new InvalidOperationException("平台未返回可核对的 Minecraft 1.7.10 资源文件，请查看作者页面；不会自动安装其他版本或替代文件。");
        return releases;
    }

    /// <summary>普通模组和光影加载器属于 JAR 管理流程，其余内容使用独立 ZIP 目录。</summary>
    private static bool IsModKind(GtnhAddonKind kind) => kind is GtnhAddonKind.Mod or GtnhAddonKind.ShaderLoader;

    /// <summary>每种推荐内容只接受预期归档后缀，不按下载地址猜测安装目录。</summary>
    private static bool HasExpectedExtension(string name, GtnhAddonKind kind) =>
        name.EndsWith(IsModKind(kind) ? ".jar" : ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>目标由明确类型和实例游戏目录构造，资源与光影不会写入 mods。</summary>
    private static string DestinationPath(InstanceImportManifest manifest, GtnhAddonRelease release)
    {
        if (!HasExpectedExtension(release.FileName, release.Kind)) throw new InvalidDataException("发行文件后缀与所选内容类型不一致。");
        if (IsModKind(release.Kind)) return LocalModPaths.NewPath(manifest, release.FileName);
        var localName = release.Provider == GtnhAddonProvider.Github
            ? GithubResourceFileName.ForInstall(release.Kind, Origin(release)) : release.FileName;
        var name = InstanceImportPaths.Relative(localName);
        if (name.Contains('/')) throw new InvalidDataException("新增资源名称不能包含目录。");
        var folder = release.Kind switch { GtnhAddonKind.ResourcePack => "resourcepacks", GtnhAddonKind.Shader => "shaderpacks", _ => throw new InvalidDataException("未知资源类型。") };
        return InstanceImportPaths.Relative((manifest.GameDirectory == "." ? "" : manifest.GameDirectory + "/") + folder + "/" + name);
    }

    /// <summary>资源下载沿用实际来源的完整性规则；类型、身份和地址在目录解析时已固定。</summary>
    private static void ValidateContentRequirement(InstanceFileRequirement requirement)
    {
        if (requirement.Size is null or <= 0 or > MaximumAssetBytes || !requirement.Required || requirement.Role != InstanceFileRole.Content ||
            requirement.ArchiveEntry is not null || requirement.Downloads.Count == 0 || requirement.Path is not { Length: > 0 })
            throw new InvalidDataException("资源下载缺少目标或官方完整性依据。");
        InstanceImportPaths.Relative(requirement.Path);
        foreach (var (algorithm, hash) in InstanceFileIntegrity.HashesFor(requirement))
        {
            var length = algorithm switch { "sha1" => 40, "sha256" => 64, "sha512" => 128, _ => 0 };
            if (length == 0 || hash.Length != length || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("资源包含无效官方摘要。");
        }
        foreach (var url in requirement.Downloads)
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0)
                throw new InvalidDataException("资源下载地址必须使用 HTTPS。");
    }

    /// <summary>只接受无路径和请求表达式的固定平台标识。</summary>
    private static bool SafePlatformId(string value) => value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    /// <summary>展示平台原始版本与加载器标签，光影跨版本兼容性需要用户依据作者说明确认。</summary>
    private static string ContentNotes(GtnhAddonKind kind, IReadOnlyList<string> gameVersions, IReadOnlyList<string> loaders, string changelog)
    {
        var notes = "官方游戏版本：" + (gameVersions.Count == 0 ? "未声明" : string.Join("、", gameVersions)) +
            "\n官方加载器：" + (loaders.Count == 0 ? "未声明" : string.Join("、", loaders));
        notes += kind == GtnhAddonKind.Shader ? "\n光影跨版本兼容取决于加载器；仅有平台标签或目录收录不证明适配 GTNH，请核对作者兼容说明。" :
            "\n请核对当前 GTNH 版本和作者依赖要求；本页不自动安装前置依赖。";
        if (changelog.Length > 12000) changelog = changelog[..12000] + "\n（更多内容请查看官方发行说明）";
        return string.IsNullOrWhiteSpace(changelog) ? notes : notes + "\n\n" + changelog;
    }
}
