using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using EML.Core.CurseForge;
using EML.Core.Minecraft.Catalog;
using EML.Core.Modrinth;

namespace EML.Infrastructure.Minecraft.Catalog;

/// <summary>查询原平台整合包与 Mojang 官方原版目录，固定版本身份和下载完整性始终一起返回。</summary>
public sealed class InstanceCatalog : IInstanceCatalog, IDisposable
{
    private const int MinecraftGameId = 432, ModpackClassId = 4471;
    private const int MaximumManifestBytes = 8 * 1024 * 1024;
    private static readonly Uri ManifestUri = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
    private static readonly string[] LoaderNames = ["forge", "fabric", "neoforge", "quilt", "liteloader"];
    private static readonly HttpClient SharedClient = CreateClient(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10) }, true);
    private readonly IModrinthApiClient _modrinth;
    private readonly ICurseForgeApiClient _curseForge;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private volatile ManifestSnapshot? _manifestCache;

    /// <summary>平台 API 复用启动器现有认证；Mojang 目录使用独立的无凭据连接池。</summary>
    public InstanceCatalog(IModrinthApiClient modrinth, ICurseForgeApiClient curseForge)
    { _modrinth = modrinth; _curseForge = curseForge; _client = SharedClient; }

    /// <summary>注入有界官方目录的测试传输，平台客户端与传输的释放仍由调用方管理。</summary>
    public InstanceCatalog(IModrinthApiClient modrinth, ICurseForgeApiClient curseForge, HttpMessageHandler handler)
    {
        _modrinth = modrinth; _curseForge = curseForge;
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is HttpClientHandler http) http.AllowAutoRedirect = false;
        if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
        _client = CreateClient(handler, false); _ownsClient = true;
    }

    /// <summary>只搜索客户端整合包或官方版本，不按名称合并不同平台项目。</summary>
    public async Task<InstanceCatalogPage> SearchAsync(InstanceCatalogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = query with { Query = query.Query.Trim(), GameVersion = query.GameVersion.Trim(), Loader = query.Loader.Trim() };
        ValidatePage(query.Offset, query.Limit); ValidateFilter(query.GameVersion, query.Loader);
        if (!Enum.IsDefined(query.Provider) || query.Sort is < 0 or > 3 || query.Query.Length > 1024)
            throw new ArgumentException("实例搜索条件无效。");
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Provider == InstanceCatalogProvider.Modrinth)
        {
            var page = await _modrinth.SearchAsync(new()
            {
                Query = query.Query.Trim(), ProjectTypes = [ModrinthProjectType.Modpack],
                GameVersions = Values(query.GameVersion), Loaders = Values(query.Loader), Offset = query.Offset, Limit = query.Limit,
                Sort = query.Sort switch { 1 => ModrinthSearchSort.Downloads, 2 => ModrinthSearchSort.Updated, 3 => ModrinthSearchSort.Newest, _ => ModrinthSearchSort.Relevance }
            }, cancellationToken).ConfigureAwait(false);
            var next = checked(query.Offset + page.Hits.Count);
            var projects = page.Hits.Where(hit => hit.ProjectType == "modpack").Select(hit =>
            {
                ValidateModrinthId(hit.ProjectId);
                return new InstanceCatalogProject
                {
                    Provider = query.Provider, Id = hit.ProjectId, Name = hit.Title, Summary = hit.Description,
                    Author = hit.Author, IconUrl = hit.IconUrl, Downloads = hit.Downloads,
                    WebsiteUrl = "https://modrinth.com/modpack/" + Uri.EscapeDataString(hit.ProjectId)
                };
            }).ToArray();
            return new(projects, next, page.Hits.Count > 0 && next < page.TotalHits);
        }
        if (query.Provider == InstanceCatalogProvider.CurseForge)
        {
            RequireCurseForgeVersion(query.GameVersion, query.Loader);
            var page = await _curseForge.SearchAsync(new()
            {
                GameId = MinecraftGameId, ClassId = ModpackClassId, SearchFilter = query.Query.Trim(),
                GameVersion = Optional(query.GameVersion), ModLoaderType = CurseForgeLoader(query.Loader),
                Index = query.Offset, PageSize = query.Limit,
                SortField = query.Sort switch { 1 => CurseForgeSortField.TotalDownloads, 2 => CurseForgeSortField.LastUpdated,
                    3 => CurseForgeSortField.ReleasedDate, _ => CurseForgeSortField.Popularity }
            }, cancellationToken).ConfigureAwait(false);
            var next = NextCurseForgeOffset(query.Offset, page.Data.Count, page.Pagination);
            return new(page.Data.Where(mod => mod.GameId == MinecraftGameId && mod.ClassId == ModpackClassId)
                .Select(CurseForgeProject).ToArray(), next, page.Data.Count > 0 && next < Math.Min(page.Pagination.TotalCount, 10000));
        }
        var versions = await ReadMinecraftVersionsAsync(false, cancellationToken).ConfigureAwait(false);
        var filtered = versions.Where(version => version.Type == "release" || query.IncludeSnapshots && version.Type == "snapshot")
            .Where(version => version.Id.Contains(query.Query.Trim(), StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(query.GameVersion) || version.Id.Equals(query.GameVersion.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(version => version.Released).ToArray();
        var items = filtered.Skip(query.Offset).Take(query.Limit).Select(version => new InstanceCatalogProject
        {
            Provider = query.Provider, Id = version.Id, Name = "Minecraft " + version.Id, Author = "Mojang Studios",
            Summary = $"{ReleaseLabel(version.Type)} · {version.Released:yyyy-MM-dd} · Java 版官方原版",
            WebsiteUrl = "https://www.minecraft.net/download"
        }).ToArray();
        return new(items, query.Offset + items.Length, query.Offset + items.Length < filtered.Length);
    }

    /// <summary>只为选中的固定项目获取平台原文；正文中的中文与其他语言一起保留，不套用本地翻译。</summary>
    public async Task<InstanceCatalogProject> GetProjectAsync(InstanceCatalogProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateIdentity(project.Provider, project.Id);
        cancellationToken.ThrowIfCancellationRequested();
        if (project.Provider == InstanceCatalogProvider.Modrinth)
        {
            var details = await _modrinth.GetProjectAsync(project.Id, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateModrinthProject(details, project.Id);
            return project with
            {
                Name = details.Title, Summary = details.Description,
                Description = PlatformProjectDescription.FromMarkdown(details.Body),
                IconUrl = details.IconUrl, Downloads = details.Downloads,
                WebsiteUrl = "https://modrinth.com/modpack/" + Uri.EscapeDataString(details.Id)
            };
        }
        if (project.Provider == InstanceCatalogProvider.CurseForge)
        {
            var id = NumericId(project.Id);
            var details = await _curseForge.GetModAsync(id, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCurseForgeProject(details, id);
            var body = await _curseForge.GetModDescriptionAsync(id, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return CurseForgeProject(details) with { Description = PlatformProjectDescription.FromHtml(body) };
        }
        // 原版条目已经包含官方版本说明，不额外请求 Mojang 目录或整合包平台。
        return project;
    }

    /// <summary>读取完整发布列表并核对类型；服务端包不会混入客户端版本选择。</summary>
    public async Task<InstanceVersionPage> GetVersionsAsync(InstanceCatalogProject project, string gameVersion = "", string loader = "",
        int offset = 0, int limit = 20, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project); ValidateIdentity(project.Provider, project.Id);
        gameVersion = gameVersion.Trim(); loader = loader.Trim();
        ValidatePage(offset, limit); ValidateFilter(gameVersion, loader);
        cancellationToken.ThrowIfCancellationRequested();
        if (project.Provider == InstanceCatalogProvider.Modrinth)
        {
            var details = await _modrinth.GetProjectAsync(project.Id, cancellationToken).ConfigureAwait(false);
            ValidateModrinthProject(details, project.Id);
            var values = await _modrinth.GetVersionsAsync(project.Id, new()
            { GameVersions = Values(gameVersion), Loaders = Values(loader) }, cancellationToken).ConfigureAwait(false);
            if (values.Any(version => version.ProjectId != project.Id)) throw new InvalidDataException("Modrinth 发布列表包含其他项目。");
            var filtered = values.Where(version => Compatible(version.GameVersions, version.Loaders, gameVersion, loader))
                .OrderByDescending(version => version.DatePublished).ToArray();
            var items = filtered.Skip(offset).Take(limit).Select(ModrinthRelease).ToArray();
            return new(items, offset + items.Length, offset + items.Length < filtered.Length);
        }
        if (project.Provider == InstanceCatalogProvider.CurseForge)
        {
            RequireCurseForgeVersion(gameVersion, loader);
            var id = NumericId(project.Id);
            var details = await _curseForge.GetModAsync(id, cancellationToken).ConfigureAwait(false);
            ValidateCurseForgeProject(details, id);
            var page = await _curseForge.GetModFilesAsync(id, new()
            { GameVersion = Optional(gameVersion), ModLoaderType = CurseForgeLoader(loader), Index = offset, PageSize = limit }, cancellationToken).ConfigureAwait(false);
            foreach (var file in page.Data) ValidateCurseForgeFile(file, id);
            var items = page.Data.Where(file => !file.IsServerPack && SafeFilename(file.FileName, ".zip"))
                .Where(file => gameVersion.Length == 0 || file.GameVersions.Contains(gameVersion.Trim(), StringComparer.OrdinalIgnoreCase))
                .Where(file => loader.Length == 0 || !file.GameVersions.Any(IsLoader) || file.GameVersions.Contains(loader.Trim(), StringComparer.OrdinalIgnoreCase))
                .Select(file => CurseForgeRelease(details, file)).ToArray();
            var next = NextCurseForgeOffset(offset, page.Data.Count, page.Pagination);
            return new(items, next, page.Data.Count > 0 && next < Math.Min(page.Pagination.TotalCount, 10000));
        }
        if (offset > 0) return new([], offset, false);
        var versions = await ReadMinecraftVersionsAsync(false, cancellationToken).ConfigureAwait(false);
        var selected = FindMinecraftVersion(versions, project.Id);
        return new([MinecraftRelease(selected)], 1, false);
    }

    /// <summary>确认下载时重新请求固定身份与官方摘要，不接受搜索结果缓存中的旧地址。</summary>
    public async Task<InstanceCatalogRelease> GetReleaseAsync(InstanceCatalogProvider provider, string projectId, string releaseId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(provider, projectId); ValidateIdentity(provider, releaseId);
        cancellationToken.ThrowIfCancellationRequested();
        if (provider == InstanceCatalogProvider.Modrinth)
        {
            var project = await _modrinth.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            ValidateModrinthProject(project, projectId);
            var version = await _modrinth.GetVersionAsync(releaseId, cancellationToken).ConfigureAwait(false);
            if (version.ProjectId != projectId || version.Id != releaseId) throw new InvalidDataException("Modrinth 版本不属于选中的整合包。");
            return ModrinthRelease(version);
        }
        if (provider == InstanceCatalogProvider.CurseForge)
        {
            var project = await _curseForge.GetModAsync(NumericId(projectId), cancellationToken).ConfigureAwait(false);
            ValidateCurseForgeProject(project, NumericId(projectId));
            var file = await _curseForge.GetFileAsync(project.Id, NumericId(releaseId), cancellationToken).ConfigureAwait(false);
            ValidateCurseForgeFile(file, project.Id);
            if (file.Id != NumericId(releaseId)) throw new InvalidDataException("CurseForge 返回了不同的固定文件。");
            return CurseForgeRelease(project, file);
        }
        if (projectId != releaseId) throw new InvalidDataException("原版版本身份不一致。");
        var versions = await ReadMinecraftVersionsAsync(true, cancellationToken).ConfigureAwait(false);
        return MinecraftRelease(FindMinecraftVersion(versions, releaseId));
    }

    /// <summary>Modrinth 多文件仅使用唯一主 mrpack 或唯一 mrpack，绝不猜测同名附件。</summary>
    private static InstanceCatalogRelease ModrinthRelease(ModrinthVersion version)
    {
        ValidateModrinthId(version.Id); ValidateModrinthId(version.ProjectId);
        var candidates = version.Files.Where(file => SafeFilename(file.Filename, ".mrpack")).ToArray();
        var primary = candidates.Where(file => file.Primary).ToArray();
        var file = primary.Length == 1 ? primary[0] : primary.Length == 0 && candidates.Length == 1 ? candidates[0] : null;
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? problem = null;
        if (file is null) problem = "此版本没有唯一的客户端 mrpack 文件，请从项目页面确认。";
        else
        {
            foreach (var pair in file.Hashes.Where(pair => pair.Key is "sha1" or "sha512"))
            {
                if (!ValidHash(pair.Value, pair.Key == "sha1" ? 40 : 128)) throw new InvalidDataException("Modrinth 整合包摘要格式无效。");
                hashes.Add(pair.Key, pair.Value.ToLowerInvariant());
            }
            if (hashes.Count == 0 || file.Size <= 0) problem = "此整合包缺少可信的官方大小或摘要。";
            else if (!DownloadUrl(file.Url)) problem = "此整合包没有有效的 HTTPS 下载地址。";
        }
        return new()
        {
            Provider = InstanceCatalogProvider.Modrinth, ProjectId = version.ProjectId, Id = version.Id,
            Name = version.Name, VersionNumber = version.VersionNumber, GameVersions = version.GameVersions,
            Loaders = version.Loaders, Published = version.DatePublished, ReleaseType = ReleaseLabel(version.VersionType),
            FileName = file?.Filename ?? "", Size = file?.Size, Hashes = hashes, Problem = problem,
            DownloadUrls = problem is null && file is not null ? [file.Url] : [],
            WebsiteUrl = $"https://modrinth.com/modpack/{version.ProjectId}/version/{version.Id}"
        };
    }

    /// <summary>CurseForge 保留官方 SHA-1；禁止分发、服务端包或缺失摘要均不能自动下载。</summary>
    private static InstanceCatalogRelease CurseForgeRelease(CurseForgeMod project, CurseForgeFile file)
    {
        ValidateCurseForgeFile(file, project.Id);
        var official = file.Hashes.Where(hash => hash.Algo == 1).Select(hash => hash.Value.ToLowerInvariant()).Distinct().ToArray();
        var hashes = new Dictionary<string, string>();
        if (official.Length == 1 && ValidHash(official[0], 40)) hashes["sha1"] = official[0];
        var problem = file.IsServerPack ? "此文件是服务端包，请选择客户端版本。"
            : !SafeFilename(file.FileName, ".zip") ? "此文件不是可导入的客户端 ZIP。"
            : file.FileLength <= 0 || hashes.Count == 0 ? "此文件缺少可信的官方大小或 SHA-1。"
            : !file.IsAvailable || !project.IsAvailable || project.AllowModDistribution == false || !DownloadUrl(file.DownloadUrl)
                ? "CurseForge 未提供此文件的第三方下载；请从官方页面下载客户端包后导入。" : null;
        return new()
        {
            Provider = InstanceCatalogProvider.CurseForge, ProjectId = Number(project.Id), Id = Number(file.Id),
            Name = file.DisplayName, VersionNumber = file.DisplayName, FileName = file.FileName, Size = file.FileLength,
            GameVersions = file.GameVersions.Where(version => !IsLoader(version)).ToArray(),
            Loaders = file.GameVersions.Where(IsLoader).ToArray(), Published = file.FileDate,
            ReleaseType = file.ReleaseType switch { 1 => "正式版", 2 => "测试版", 3 => "开发版", _ => "未声明" },
            Hashes = hashes, Problem = problem, DownloadUrls = problem is null ? [file.DownloadUrl!] : [],
            WebsiteUrl = $"https://www.curseforge.com/projects/{project.Id}/files/{file.Id}"
        };
    }

    /// <summary>官方目录中的版本 JSON 是后续本体、类库和资源下载的可信起点。</summary>
    private static InstanceCatalogRelease MinecraftRelease(MinecraftVersion version) => new()
    {
        Provider = InstanceCatalogProvider.Minecraft, ProjectId = version.Id, Id = version.Id, Name = "Minecraft " + version.Id,
        VersionNumber = version.Id, GameVersions = [version.Id], ReleaseType = ReleaseLabel(version.Type), Published = version.Released,
        FileName = version.Id + ".json", DownloadUrls = [version.Url], Hashes = new Dictionary<string, string> { ["sha1"] = version.Sha1 },
        WebsiteUrl = "https://www.minecraft.net/download"
    };

    /// <summary>目录按时间短暂缓存；确认安装强制重新读取，并限制响应、深度和条目总量。</summary>
    private async Task<MinecraftVersion[]> ReadMinecraftVersionsAsync(bool refresh, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!refresh && _manifestCache is { } cache && cache.Expires > DateTimeOffset.UtcNow) return cache.Values;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUri);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != ManifestUri) throw new InvalidDataException("官方目录不允许重定向。");
        if (response.Content.Headers.ContentLength > MaximumManifestBytes) throw new InvalidDataException("官方目录超过大小限制。");
        using var buffer = new MemoryStream();
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var bytes = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaximumManifestBytes) throw new InvalidDataException("官方目录超过大小限制。");
            await buffer.WriteAsync(bytes.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
        }
        using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), new() { MaxDepth = 16 });
        if (!document.RootElement.TryGetProperty("versions", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 10000)
            throw new InvalidDataException("官方版本目录格式无效。");
        var values = new List<MinecraftVersion>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in array.EnumerateArray())
        {
            var type = value.GetProperty("type").GetString();
            if (type is not ("release" or "snapshot")) continue;
            var id = value.GetProperty("id").GetString() ?? "";
            var url = value.GetProperty("url").GetString() ?? "";
            var hash = value.GetProperty("sha1").GetString() ?? "";
            ValidateMinecraftId(id);
            if (!identities.Add(id) || !ValidHash(hash, 40) || !OfficialMetadataUrl(url)) throw new InvalidDataException("官方版本身份、地址或摘要无效。");
            values.Add(new(id, type, url, hash.ToLowerInvariant(), value.GetProperty("releaseTime").GetDateTimeOffset()));
        }
        timeout.Token.ThrowIfCancellationRequested();
        var result = values.ToArray(); _manifestCache = new(DateTimeOffset.UtcNow.AddMinutes(2), result);
        return result;
    }

    /// <summary>只接受目录中精确命中的可支持版本，不能把未知版本回退到最新版。</summary>
    private static MinecraftVersion FindMinecraftVersion(IEnumerable<MinecraftVersion> versions, string id) =>
        versions.SingleOrDefault(version => version.Id == id) ?? throw new InvalidDataException("官方目录中未找到所选正式版或快照版本。");

    /// <summary>将官方整合包搜索条目转换为纯展示信息。</summary>
    private static InstanceCatalogProject CurseForgeProject(CurseForgeMod mod)
    {
        ValidateCurseForgeProject(mod, mod.Id);
        return new()
        {
            Provider = InstanceCatalogProvider.CurseForge, Id = Number(mod.Id), Name = mod.Name, Summary = mod.Summary,
            Author = string.Join("、", mod.Authors.Select(author => author.Name)), IconUrl = mod.Logo?.ThumbnailUrl ?? mod.Logo?.Url,
            Downloads = double.IsFinite(mod.DownloadCount) ? (long)Math.Clamp(mod.DownloadCount, 0, long.MaxValue) : 0,
            WebsiteUrl = "https://www.curseforge.com/projects/" + Number(mod.Id)
        };
    }

    /// <summary>公共搜索和分页值有限，避免溢出或请求平台不支持的窗口。</summary>
    private static void ValidatePage(int offset, int limit)
    { if (limit is < 1 or > 50 || offset < 0 || offset > 10000 - limit) throw new ArgumentOutOfRangeException(nameof(offset), "目录分页范围无效。"); }
    /// <summary>版本与加载器只接受有界文字及已知类型。</summary>
    private static void ValidateFilter(string version, string loader)
    { if (version.Length > 128 || loader.Length > 0 && !IsLoader(loader.Trim())) throw new ArgumentException("版本或加载器筛选无效。"); }
    /// <summary>CurseForge 的加载器筛选必须配合游戏版本，错误条件不能悄悄放宽。</summary>
    private static void RequireCurseForgeVersion(string version, string loader)
    { if (loader.Length > 0 && string.IsNullOrWhiteSpace(version)) throw new ArgumentException("筛选 CurseForge 加载器时，请同时填写 Minecraft 版本。"); }
    /// <summary>固定身份允许的字符因来源不同而分别校验。</summary>
    private static void ValidateIdentity(InstanceCatalogProvider provider, string id)
    {
        if (!Enum.IsDefined(provider)) throw new ArgumentException("未知实例来源。");
        if (provider == InstanceCatalogProvider.Modrinth) ValidateModrinthId(id);
        else if (provider == InstanceCatalogProvider.CurseForge) _ = NumericId(id);
        else ValidateMinecraftId(id);
    }
    /// <summary>Modrinth 只存储固定字母数字 ID，不使用可变 slug。</summary>
    private static void ValidateModrinthId(string id)
    { if (id is not { Length: > 0 and <= 128 } || !id.All(char.IsAsciiLetterOrDigit)) throw new InvalidDataException("Modrinth 固定 ID 无效。"); }
    /// <summary>官方版本 ID 必须可安全作为单个文件名使用。</summary>
    private static void ValidateMinecraftId(string id)
    { if (id is not { Length: > 0 and <= 128 } || !id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or ' ') || id != id.Trim() || id is "." or "..") throw new InvalidDataException("Minecraft 版本 ID 无效。"); }
    /// <summary>再次确认项目为客户端整合包，不把普通模组作为实例下载。</summary>
    private static void ValidateModrinthProject(ModrinthProject project, string id)
    { if (project.Id != id || project.ProjectType != "modpack") throw new InvalidDataException("Modrinth 返回的项目不是所选整合包。"); }
    /// <summary>CurseForge 固定项目必须属于 Minecraft 整合包类别。</summary>
    private static void ValidateCurseForgeProject(CurseForgeMod project, long id)
    { if (id <= 0 || project.Id != id || project.GameId != MinecraftGameId || project.ClassId != ModpackClassId) throw new InvalidDataException("CurseForge 返回的项目不是所选 Minecraft 整合包。"); }
    /// <summary>文件归属和大小来自官方响应，不从展示名称推测。</summary>
    private static void ValidateCurseForgeFile(CurseForgeFile file, long id)
    { if (file.Id <= 0 || file.ModId != id || file.GameId != MinecraftGameId || file.FileLength < 0) throw new InvalidDataException("CurseForge 文件身份或大小无效。"); }
    /// <summary>下一页按原始响应数量推进，过滤服务端包不会导致反复请求同一页。</summary>
    private static int NextCurseForgeOffset(int offset, int count, CurseForgePagination pagination)
    { if (pagination.Index != offset || pagination.ResultCount != count || pagination.TotalCount < 0) throw new InvalidDataException("CurseForge 分页响应不一致。"); return checked(offset + count); }
    /// <summary>文件名必须是单个安全名称并符合目标扩展名。</summary>
    private static bool SafeFilename(string name, string extension) => !string.IsNullOrWhiteSpace(name) && name.Length <= 255 &&
        name.IndexOfAny(['/', '\\', ':', '\0']) < 0 && !name.Any(char.IsControl) && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    /// <summary>官方下载信息至少必须是无凭据 HTTPS URL，下载器仍会检查重定向和目标地址。</summary>
    private static bool DownloadUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    /// <summary>版本元数据只允许 Mojang 元数据域名，不跟随目录外的主机。</summary>
    private static bool OfficialMetadataUrl(string url) => DownloadUrl(url) && new Uri(url).Host is "piston-meta.mojang.com" or "launchermeta.mojang.com";
    /// <summary>哈希必须满足算法规定的十六进制长度。</summary>
    private static bool ValidHash(string hash, int length) => hash is not null && hash.Length == length && hash.All(Uri.IsHexDigit);
    /// <summary>本地再次筛选平台明确声明的 Minecraft 和加载器版本。</summary>
    private static bool Compatible(IReadOnlyList<string> versions, IReadOnlyList<string> loaders, string version, string loader) =>
        (version.Length == 0 || versions.Contains(version.Trim(), StringComparer.OrdinalIgnoreCase)) && (loader.Length == 0 || loaders.Contains(loader.Trim(), StringComparer.OrdinalIgnoreCase));
    /// <summary>平台加载器标签按不区分大小写匹配。</summary>
    private static bool IsLoader(string loader) => LoaderNames.Contains(loader, StringComparer.OrdinalIgnoreCase);
    /// <summary>将加载器名称转换为 CurseForge 的官方筛选值。</summary>
    private static CurseForgeModLoaderType? CurseForgeLoader(string value) => value.Trim().ToLowerInvariant() switch
    { "" => null, "forge" => CurseForgeModLoaderType.Forge, "fabric" => CurseForgeModLoaderType.Fabric, "neoforge" => CurseForgeModLoaderType.NeoForge,
        "quilt" => CurseForgeModLoaderType.Quilt, "liteloader" => CurseForgeModLoaderType.LiteLoader, _ => throw new ArgumentException("未知加载器。") };
    /// <summary>正整数 ID 使用固定文化解析，不接受符号或小数。</summary>
    private static long NumericId(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw new ArgumentException("CurseForge 固定 ID 无效。");
    /// <summary>空筛选不向平台发送空数组元素。</summary>
    private static string[] Values(string value) => string.IsNullOrWhiteSpace(value) ? [] : [value.Trim()];
    /// <summary>空的可选条件转换为省略值。</summary>
    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    /// <summary>固定 ID 的数字格式不受系统语言影响。</summary>
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    /// <summary>发布类型转换为中文状态，保留未知信息而不猜测稳定性。</summary>
    private static string ReleaseLabel(string value) => value switch { "release" => "正式版", "snapshot" => "快照", "beta" => "测试版", "alpha" => "开发版", _ => "未声明" };
    /// <summary>独立 HTTP 客户端不携带平台令牌，并设置可识别的产品名称。</summary>
    private static HttpClient CreateClient(HttpMessageHandler handler, bool disposeHandler)
    { var client = new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan }; client.DefaultRequestHeaders.UserAgent.ParseAdd("EML/1.0"); return client; }
    /// <summary>仅释放本对象拥有的目录客户端，平台 API 生命周期由宿主负责。</summary>
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
    /// <summary>官方目录内部记录不包含运行参数，由既有运行解析器处理版本 JSON。</summary>
    private sealed record MinecraftVersion(string Id, string Type, string Url, string Sha1, DateTimeOffset Released);
    /// <summary>以单一引用发布目录快照，避免并行搜索读取到一半更新的缓存。</summary>
    private sealed record ManifestSnapshot(DateTimeOffset Expires, MinecraftVersion[] Values);
}
