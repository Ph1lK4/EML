using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using EML.Core.CurseForge;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods.Online;
using EML.Core.Modrinth;
using EML.Infrastructure.Minecraft.CurseForge;
using EML.Infrastructure.Minecraft.CurseForge.Api;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Modrinth.Api;

namespace EML.Infrastructure.Minecraft.Mods.Online;

/// <summary>把两套官方目录映射为在线模组视图；查询不修改实例，也不按名称合并平台身份。</summary>
public sealed class OnlineModCatalog(IModrinthApiClient modrinth, ICurseForgeApiClient curseForge) : IOnlineModCatalog
{
    private const int VersionPageSize = 20;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, object Value)> _cache = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, string Loader)> _filteredLoaders = new();
    private readonly ConcurrentDictionary<ModProvider, DateTimeOffset> _retryAfter = new();
    private static readonly string[] Loaders = ["forge", "fabric", "neoforge", "quilt", "liteloader"];
    private static readonly IReadOnlyDictionary<string, string> CurseForgeCategorySlugs = new Dictionary<string, string>
    {
        ["technology"] = "technology", ["magic"] = "magic", ["adventure"] = "adventure-rpg",
        ["worldgen"] = "world-gen", ["storage"] = "storage", ["library"] = "library-api"
    };

    /// <summary>并行查询所选平台；一个平台失败不抹掉另一个平台的结果，取消仍会传播。</summary>
    public async Task<OnlineModSearchPage> SearchAsync(OnlineModQuery query, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Page < 0 || query.PageSize is < 1 or > 50 || query.Page > 10000 / query.PageSize ||
            query.Text.Length > 1024 || !Enum.IsDefined(query.Sort) || query.Provider is { } provider && !Enum.IsDefined(provider))
            throw new ArgumentException("模组搜索条件或分页无效。", nameof(query));
        ValidateEnvironment(query.Environment);
        ValidateLoader(query.Loader);
        var tasks = new List<Task<OnlineModSearchPage>>();
        if (query.Provider is null or ModProvider.Modrinth) tasks.Add(SearchOneAsync(ModProvider.Modrinth, query, token));
        if (query.Provider is null or ModProvider.CurseForge) tasks.Add(SearchOneAsync(ModProvider.CurseForge, query, token));
        var pages = await Task.WhenAll(tasks).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var items = pages.SelectMany(page => page.Items).DistinctBy(item => item.Id).ToArray();
        // 两个平台的相关度分数不能互相比较；全部来源保留平台排序，数字字段可在当前页排序。
        var ordered = query.Sort switch
        {
            OnlineModSort.Downloads => items.OrderByDescending(item => item.Downloads).ToArray(),
            OnlineModSort.Updated => items.OrderByDescending(item => item.Updated).ToArray(),
            _ => items
        };
        return new(ordered, pages.Any(page => page.HasMore), pages.SelectMany(page => page.Notes).ToArray());
    }

    /// <summary>项目详情按固定身份短时缓存；正文只返回纯文本，外部链接仍由界面验证。</summary>
    public async Task<OnlineModProject> GetProjectAsync(OnlineProjectId project, CancellationToken token = default)
    {
        ValidateProject(project);
        return await CachedAsync($"project:{project.Provider}:{project.ProjectId}", async () =>
        {
            if (project.Provider == ModProvider.Modrinth)
            {
                var value = await modrinth.GetProjectAsync(project.ProjectId, token).ConfigureAwait(false);
                if (value.Id != project.ProjectId || value.ProjectType != "mod") throw new InvalidDataException("Modrinth 返回的项目身份或类型不一致。");
                return new OnlineModProject
                {
                    Id = project, Name = value.Title, Summary = value.Description, Description = value.Body,
                    IconUrl = value.IconUrl, WebsiteUrl = "https://modrinth.com/mod/" + Uri.EscapeDataString(value.Slug),
                    SourceUrl = value.SourceUrl, IssuesUrl = value.IssuesUrl, License = value.License?.Name ?? "未提供",
                    Downloads = value.Downloads, GameVersions = value.GameVersions, Loaders = value.Loaders,
                    Environment = EnvironmentText(value.ClientSide, value.ServerSide)
                };
            }
            var id = CurseForgeId(project.ProjectId);
            var modTask = curseForge.GetModAsync(id, token);
            var bodyTask = curseForge.GetModDescriptionAsync(id, token);
            await Task.WhenAll(modTask, bodyTask).ConfigureAwait(false);
            var mod = await modTask.ConfigureAwait(false);
            ValidateCurseForgeProject(mod, id);
            return FromCurseForge(mod) with { Description = PlainText(await bodyTask.ConfigureAwait(false)) };
        }, token).ConfigureAwait(false);
    }

    /// <summary>版本列表按实例条件分页；CurseForge 使用 files 接口而非 latestFiles 摘要。</summary>
    public async Task<OnlineModVersionPage> GetVersionsAsync(OnlineProjectId project, string gameVersion, string loader,
        int page = 0, CancellationToken token = default)
    {
        ValidateProject(project); ValidateLoader(loader);
        if (page < 0 || page > 499) throw new ArgumentOutOfRangeException(nameof(page));
        if (project.Provider == ModProvider.Modrinth)
        {
            var versions = await CachedAsync($"versions:{project.ProjectId}:{gameVersion}:{loader}", async () =>
            {
                var values = await modrinth.GetVersionsAsync(project.ProjectId, new()
                {
                    GameVersions = Values(gameVersion), Loaders = Values(loader)
                }, token).ConfigureAwait(false);
                if (values.Any(value => value.ProjectId != project.ProjectId)) throw new InvalidDataException("版本列表包含其他 Modrinth 项目。");
                return values.Where(value => Compatible(value.GameVersions, value.Loaders, gameVersion, loader))
                    .OrderByDescending(value => value.DatePublished).ToArray();
            }, token).ConfigureAwait(false);
            return new(versions.Skip(page * VersionPageSize).Take(VersionPageSize).Select(FromModrinth).ToArray(),
                (page + 1) * VersionPageSize < versions.Length);
        }
        var projectId = CurseForgeId(project.ProjectId);
        var mod = await curseForge.GetModAsync(projectId, token).ConfigureAwait(false);
        ValidateCurseForgeProject(mod, projectId);
        if (loader.Length > 0 && gameVersion.Length == 0)
            throw new ArgumentException("CurseForge 加载器筛选需要同时指定 Minecraft 版本。");
        var result = await curseForge.GetModFilesAsync(projectId, new()
        {
            GameVersion = EmptyToNull(gameVersion), ModLoaderType = CurseForgeLoader(loader), Index = page * VersionPageSize, PageSize = VersionPageSize
        }, token).ConfigureAwait(false);
        var files = new List<OnlineModVersion>();
        foreach (var file in result.Data)
        {
            ValidateCurseForgeFile(file, projectId);
            if (gameVersion.Length > 0 && !file.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase)) continue;
            var declaredLoaders = file.GameVersions.Where(value => Loaders.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (loader.Length > 0 && declaredLoaders.Length > 0 && !declaredLoaders.Contains(loader, StringComparer.OrdinalIgnoreCase)) continue;
            // 旧版文件没有加载器标签时，只有官方带 modLoaderType 的查询结果能作为兼容证据。
            if (loader.Length > 0)
            {
                if (_filteredLoaders.Count > 2000) _filteredLoaders.Clear();
                _filteredLoaders[file.Id.ToString(CultureInfo.InvariantCulture)] = (DateTimeOffset.UtcNow.AddMinutes(5), loader);
            }
            files.Add(FromCurseForgeFile(file, mod));
        }
        return new(files, result.Pagination.Index + result.Pagination.ResultCount < Math.Min(result.Pagination.TotalCount, 10000) && result.Data.Count > 0);
    }

    /// <summary>重新查询固定版本与分发状态，验证项目归属，供安装计划锁定文件。</summary>
    public async Task<OnlineModVersion> GetVersionAsync(OnlineProjectId project, string versionId, CancellationToken token = default)
    {
        ValidateProject(project);
        if (project.Provider == ModProvider.Modrinth)
        {
            var result = await modrinth.GetVersionAsync(versionId, token).ConfigureAwait(false);
            if (result.Id != versionId || result.ProjectId != project.ProjectId) throw new InvalidDataException("Modrinth 版本不属于选中的项目。");
            var mapped = FromModrinth(result);
            var dependencies = mapped.Dependencies.ToArray();
            for (var i = 0; i < dependencies.Length; i++)
            {
                var dependency = dependencies[i];
                if (dependency.Project is not null || dependency.VersionId is not { Length: > 0 } dependencyVersion) continue;
                // Modrinth 允许只声明固定版本；先由该版本确认所属项目，不能把版本 ID 猜作项目 ID。
                var referenced = await modrinth.GetVersionAsync(dependencyVersion, token).ConfigureAwait(false);
                if (referenced.Id != dependencyVersion || !IsId(referenced.ProjectId))
                    throw new InvalidDataException("Modrinth 依赖版本缺少可信项目身份。");
                dependencies[i] = dependency with { Project = new(ModProvider.Modrinth, referenced.ProjectId), Name = referenced.Name };
            }
            return await DescribeDependenciesAsync(mapped with { Dependencies = dependencies }, token).ConfigureAwait(false);
        }
        var projectId = CurseForgeId(project.ProjectId);
        var fileId = CurseForgeId(versionId);
        var modTask = curseForge.GetModAsync(projectId, token);
        var fileTask = curseForge.GetFileAsync(projectId, fileId, token);
        await Task.WhenAll(modTask, fileTask).ConfigureAwait(false);
        var mod = await modTask.ConfigureAwait(false); var file = await fileTask.ConfigureAwait(false);
        ValidateCurseForgeProject(mod, projectId); ValidateCurseForgeFile(file, projectId);
        if (file.Id != fileId) throw new InvalidDataException("CurseForge 返回的固定文件 ID 不一致。");
        return await DescribeDependenciesAsync(FromCurseForgeFile(file, mod), token).ConfigureAwait(false);
    }

    /// <summary>为依赖选择框读取公开名称与简介，限制并发和数量；网络失败仍保留固定身份。</summary>
    private async Task<OnlineModVersion> DescribeDependenciesAsync(OnlineModVersion version, CancellationToken token)
    {
        var selected = version.Dependencies.Where(dependency => dependency.Project is not null &&
            dependency.Kind is OnlineDependencyKind.Required or OnlineDependencyKind.Optional)
            .Select(dependency => dependency.Project!).Distinct().Take(128).ToArray();
        using var concurrency = new SemaphoreSlim(4, 4);
        var descriptions = new ConcurrentDictionary<OnlineProjectId, OnlineModProject>();
        await Task.WhenAll(selected.Select(async project =>
        {
            await concurrency.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var details = await GetProjectAsync(project, token).ConfigureAwait(false);
                descriptions[project] = details;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not InvalidDataException &&
                ex is HttpRequestException or IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
            {
                // 名称只是展示信息；临时请求失败不能改变依赖编号，也不阻断主文件预览。
                token.ThrowIfCancellationRequested();
            }
            finally { concurrency.Release(); }
        })).ConfigureAwait(false);
        return version with
        {
            Dependencies = version.Dependencies.Select(dependency => dependency.Project is { } project && descriptions.TryGetValue(project, out var details)
                ? dependency with { Name = details.Name, Description = details.Summary } : dependency).ToArray()
        };
    }

    /// <summary>CurseForge 无法直接下载时仅接受同 SHA-1、同大小的唯一 Modrinth 文件，保留双平台来源。</summary>
    public async Task<OnlineModFile> ResolveDownloadAsync(OnlineModVersion version, OnlineModFile file, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!version.Files.Any(candidate => candidate.FileName == file.FileName)) throw new InvalidDataException("所选文件不属于当前版本。");
        if (file.Requirement.Downloads.Count > 0 || version.Project.Provider != ModProvider.CurseForge) return file;
        var original = file.Requirement;
        if (original.Size is null || !original.Hashes.TryGetValue("sha1", out var sha1) || !ValidHash(sha1, 40)) return file;
        try
        {
            var matches = await modrinth.GetVersionsByHashesAsync([sha1], "sha1", token).ConfigureAwait(false);
            var versions = matches.Where(pair => pair.Key.Equals(sha1, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (versions.Length == 0) return file with { Problem = "Modrinth 未找到同哈希文件，请从官方页面获取指定文件后导入。" };
            if (versions.Length != 1) throw new InvalidDataException("Modrinth 哈希查询返回重复结果。");
            var alternateVersion = versions[0].Value;
            var sameFiles = alternateVersion.Files.Where(value => value.Size == original.Size &&
                value.Hashes.TryGetValue("sha1", out var hash) && hash.Equals(sha1, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sameFiles.Length != 1) throw new InvalidDataException("Modrinth 候选未通过唯一性、大小及 SHA-1 校验。");
            var alternate = FromModrinthFile(alternateVersion, sameFiles[0]);
            if (alternate.Problem is not null) throw new InvalidDataException(alternate.Problem);
            return file with
            {
                Requirement = alternate.Requirement with
                {
                    Path = original.Path, ProjectId = original.ProjectId, FileId = original.FileId,
                    CurseForgeOrigin = new()
                    {
                        ProjectId = original.ProjectId!.Value, FileId = original.FileId!.Value,
                        Size = original.Size, Hashes = original.Hashes
                    }
                },
                Problem = null
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or InvalidDataException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return file with { Problem = "Modrinth 备用查询失败或候选未通过验证，可重试或从官方页面手动下载。" };
        }
    }

    /// <summary>单个平台的查询失败返回说明；失败不缓存，用户重试可以重新访问平台。</summary>
    private async Task<OnlineModSearchPage> SearchOneAsync(ModProvider provider, OnlineModQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_retryAfter.TryGetValue(provider, out var resume) && resume > DateTimeOffset.UtcNow)
            return new([], false, [$"{provider}：{RateMessage(resume - DateTimeOffset.UtcNow)}"]);
        try
        {
            return await CachedAsync("search:" + provider + ":" + System.Text.Json.JsonSerializer.Serialize(query),
                () => provider == ModProvider.Modrinth ? SearchModrinthAsync(query, token) : SearchCurseForgeAsync(query, token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or InvalidDataException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            var retry = ex switch
            {
                CurseForgeApiException { StatusCode: HttpStatusCode.TooManyRequests } rate => rate.RetryAfter ?? TimeSpan.FromSeconds(30),
                ModrinthApiException { StatusCode: HttpStatusCode.TooManyRequests } rate => rate.RetryAfter ?? TimeSpan.FromSeconds(30),
                _ => (TimeSpan?)null
            };
            if (retry is { } wait) _retryAfter[provider] = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(wait.TotalSeconds, 1, 86400));
            var detail = ex switch
            {
                CurseForgeCredentialException => "未配置可用的 CurseForge 授权。",
                CurseForgeApiException { StatusCode: HttpStatusCode.TooManyRequests } rate => RateMessage(rate.RetryAfter),
                ModrinthApiException { StatusCode: HttpStatusCode.TooManyRequests } rate => RateMessage(rate.RetryAfter),
                ArgumentException => "当前筛选组合不受此平台支持，请检查版本与加载器。",
                _ => "查询失败，请检查网络或平台授权后重试。"
            };
            return new([], false, [$"{provider}：{detail}"]);
        }
    }

    /// <summary>将跨平台条件映射到 Modrinth v2 的模组类型、版本、加载器与环境 facets。</summary>
    private async Task<OnlineModSearchPage> SearchModrinthAsync(OnlineModQuery query, CancellationToken token)
    {
        var groups = new List<IReadOnlyList<ModrinthSearchFacet>>();
        if (query.Environment is "client" or "both") groups.Add([new(ModrinthFacetField.ClientSide, "required"), new(ModrinthFacetField.ClientSide, "optional")]);
        if (query.Environment is "server" or "both") groups.Add([new(ModrinthFacetField.ServerSide, "required"), new(ModrinthFacetField.ServerSide, "optional")]);
        var result = await modrinth.SearchAsync(new()
        {
            Query = query.Text, ProjectTypes = [ModrinthProjectType.Mod], GameVersions = Values(query.GameVersion), Loaders = Values(query.Loader),
            Categories = Values(query.Category), AdditionalFacetGroups = groups, Offset = checked(query.Page * query.PageSize), Limit = query.PageSize,
            Sort = query.Sort switch
            {
                OnlineModSort.Downloads => ModrinthSearchSort.Downloads, OnlineModSort.Updated => ModrinthSearchSort.Updated,
                OnlineModSort.Newest => ModrinthSearchSort.Newest, _ => ModrinthSearchSort.Relevance
            }
        }, token).ConfigureAwait(false);
        var items = result.Hits.Where(hit => hit.ProjectType == "mod").Select(hit => new OnlineModProject
        {
            Id = new(ModProvider.Modrinth, hit.ProjectId), Name = hit.Title, Author = hit.Author, Summary = hit.Description,
            IconUrl = hit.IconUrl, WebsiteUrl = "https://modrinth.com/mod/" + Uri.EscapeDataString(hit.Slug),
            Downloads = hit.Downloads, Updated = hit.DateModified, GameVersions = hit.Versions,
            Loaders = hit.Categories.Where(value => Loaders.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray(),
            Environment = EnvironmentText(hit.ClientSide, hit.ServerSide)
        }).ToArray();
        return new(items, result.Offset + result.Hits.Count < result.TotalHits && result.Hits.Count > 0, []);
    }

    /// <summary>不伪造 CurseForge 不支持的环境或分类条件，明确说明该平台为何没有结果。</summary>
    private async Task<OnlineModSearchPage> SearchCurseForgeAsync(OnlineModQuery query, CancellationToken token)
    {
        if (query.Environment.Length > 0)
            return new([], false, ["CurseForge 没有对应的客户端/服务端筛选字段；当前环境条件仅显示 Modrinth 结果。"]);
        var index = checked(query.Page * query.PageSize);
        if (index > 10000 - query.PageSize) return new([], false, ["CurseForge 已到达搜索分页上限，请缩小筛选范围。"]);
        int? categoryId = null;
        if (query.Category.Length > 0)
        {
            if (!CurseForgeCategorySlugs.TryGetValue(query.Category, out var slug))
                return new([], false, ["CurseForge 没有当前分类的精确对应项；请选择全部分类或切换到 Modrinth。"]);
            var categories = await CachedAsync("cf:categories", () => curseForge.GetModCategoriesAsync(token), token).ConfigureAwait(false);
            categoryId = categories.SingleOrDefault(value => value.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase))?.Id;
            if (categoryId is null) return new([], false, ["CurseForge 暂未返回此分类，未忽略分类条件进行搜索。"]);
        }
        var result = await curseForge.SearchAsync(new()
        {
            ClassId = 6, SearchFilter = query.Text, CategoryId = categoryId, GameVersion = EmptyToNull(query.GameVersion),
            ModLoaderType = CurseForgeLoader(query.Loader), Index = index, PageSize = query.PageSize,
            SortField = query.Sort switch
            {
                OnlineModSort.Downloads => CurseForgeSortField.TotalDownloads, OnlineModSort.Updated => CurseForgeSortField.LastUpdated,
                OnlineModSort.Newest => CurseForgeSortField.ReleasedDate, _ => CurseForgeSortField.Popularity
            }
        }, token).ConfigureAwait(false);
        return new(result.Data.Where(value => value.GameId == 432 && value.ClassId == 6).Select(FromCurseForge).ToArray(),
            result.Pagination.Index + result.Pagination.ResultCount < Math.Min(result.Pagination.TotalCount, 10000) && result.Data.Count > 0,
            query.Sort == OnlineModSort.Relevance ? ["CurseForge 在关键词结果中使用平台热度排序；两平台相关度不能直接比较。"] : []);
    }

    /// <summary>保留每个固定版本、文件与依赖的身份，缺少平台字段时不猜测版本兼容性。</summary>
    private static OnlineModVersion FromModrinth(ModrinthVersion value) => new()
    {
        Project = new(ModProvider.Modrinth, value.ProjectId), Id = value.Id, Name = value.Name, VersionNumber = value.VersionNumber,
        Channel = value.VersionType, Published = value.DatePublished, Changelog = value.Changelog ?? "",
        GameVersions = value.GameVersions, Loaders = value.Loaders,
        Files = value.Files.Where(file => IsModFile(file.Filename)).Select(file => FromModrinthFile(value, file)).ToArray(),
        Dependencies = value.Dependencies.Select(dependency => new OnlineModDependency
        {
            Key = "Modrinth:" + (dependency.ProjectId ?? "") + ":" + (dependency.VersionId ?? "") + ":" + (dependency.FileName ?? ""),
            Project = dependency.ProjectId is { Length: > 0 } id ? new(ModProvider.Modrinth, id) : null,
            VersionId = dependency.VersionId, Name = dependency.FileName ?? dependency.ProjectId ?? dependency.VersionId ?? "未提供依赖身份",
            Kind = dependency.DependencyType switch
            {
                "required" => OnlineDependencyKind.Required, "optional" => OnlineDependencyKind.Optional,
                "incompatible" => OnlineDependencyKind.Incompatible, "embedded" => OnlineDependencyKind.Embedded,
                _ => throw new InvalidDataException("Modrinth 返回未知依赖类型。")
            }
        }).ToArray()
    };

    /// <summary>使用已有下载器的文件名和哈希验证，再绑定官方 CDN 的固定版本路径。</summary>
    private static OnlineModFile FromModrinthFile(ModrinthVersion version, ModrinthVersionFile file)
    {
        var request = ModrinthVersionDownloadService.CreateDownloadRequest(version, Path.GetTempPath(), file.Filename);
        if (!request.Hashes.ContainsKey("sha1") && !request.Hashes.ContainsKey("sha512"))
            throw new InvalidDataException("Modrinth 模组缺少官方 SHA-1 或 SHA-512，不能作为在线来源安装。");
        if (!IsId(version.ProjectId) || !IsId(version.Id) || !IsModFile(file.Filename) ||
            !Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Host != "cdn.modrinth.com" || uri.Port != 443 ||
            uri.Query.Length != 0 || Uri.UnescapeDataString(uri.AbsolutePath) != $"/data/{version.ProjectId}/versions/{version.Id}/{file.Filename}")
            throw new InvalidDataException("Modrinth 文件地址与固定项目、版本或文件名不一致。");
        return new()
        {
            FileName = file.Filename, Primary = file.Primary,
            Requirement = new()
            {
                Source = InstanceFileSource.DirectDownload, Origin = InstanceFileOrigin.ModrinthDownload, Role = InstanceFileRole.Content,
                Required = true, Size = file.Size, Hashes = request.Hashes, Downloads = request.Urls,
                ModrinthOrigin = new() { ProjectId = version.ProjectId, VersionId = version.Id, FileName = file.Filename }
            }
        };
    }

    /// <summary>映射 CurseForge 项目摘要；latestFiles 仅提供展示信息，不能替代版本列表。</summary>
    private static OnlineModProject FromCurseForge(CurseForgeMod value) => new()
    {
        Id = new(ModProvider.CurseForge, value.Id.ToString(CultureInfo.InvariantCulture)), Name = value.Name,
        Author = string.Join("、", value.Authors.Select(author => author.Name)), Summary = value.Summary,
        IconUrl = value.Logo?.ThumbnailUrl ?? value.Logo?.Url, WebsiteUrl = value.Links.WebsiteUrl,
        SourceUrl = value.Links.SourceUrl, IssuesUrl = value.Links.IssuesUrl, Downloads = (long)Math.Clamp(value.DownloadCount, 0, long.MaxValue),
        Updated = value.DateModified, GameVersions = value.LatestFiles.SelectMany(file => file.GameVersions)
            .Where(version => !Loaders.Contains(version, StringComparer.OrdinalIgnoreCase)).Distinct().ToArray(),
        Loaders = value.LatestFiles.SelectMany(file => file.GameVersions).Where(version => Loaders.Contains(version, StringComparer.OrdinalIgnoreCase))
            .Select(loader => loader.ToLowerInvariant()).Distinct().ToArray()
    };

    /// <summary>CurseForge 下载保留官方 SHA-1；无下载地址和禁止分发均交给精确备用查询。</summary>
    private OnlineModVersion FromCurseForgeFile(CurseForgeFile file, CurseForgeMod mod)
    {
        var sha1 = file.Hashes.Where(hash => hash.Algo == 1).ToArray();
        if (sha1.Length > 1 || sha1.Length == 1 && !ValidHash(sha1[0].Value, 40)) throw new InvalidDataException("CurseForge SHA-1 无效或重复。");
        var hashes = sha1.Length == 1 ? new Dictionary<string, string> { ["sha1"] = sha1[0].Value.ToLowerInvariant() } : [];
        var url = mod.AllowModDistribution != false && file.IsAvailable && mod.IsAvailable ? file.DownloadUrl : null;
        if (!string.IsNullOrWhiteSpace(url) && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !CurseForgeRequestHeaders.IsCdnUri(uri)))
            throw new InvalidDataException("CurseForge 文件下载地址不是受信任的官方 CDN。");
        var loaderValues = file.GameVersions.Where(value => Loaders.Contains(value, StringComparer.OrdinalIgnoreCase)).Select(value => value.ToLowerInvariant()).ToList();
        if (_filteredLoaders.TryGetValue(file.Id.ToString(CultureInfo.InvariantCulture), out var known) && known.Expires > DateTimeOffset.UtcNow &&
            loaderValues.Count == 0) loaderValues.Add(known.Loader);
        var fileName = InstanceImportPaths.Relative(file.FileName);
        if (fileName.Contains('/') || !IsModFile(fileName)) throw new InvalidDataException("所选 CurseForge 文件不是有效的模组归档。");
        return new()
        {
            Project = new(ModProvider.CurseForge, mod.Id.ToString(CultureInfo.InvariantCulture)), Id = file.Id.ToString(CultureInfo.InvariantCulture),
            Name = file.DisplayName, VersionNumber = file.DisplayName, Channel = file.ReleaseType switch { 1 => "release", 2 => "beta", 3 => "alpha", _ => "unknown" },
            Published = file.FileDate, GameVersions = file.GameVersions.Where(value => !Loaders.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray(),
            Loaders = loaderValues,
            Files = [new()
            {
                FileName = fileName, Primary = true, ManualDownloadUrl = FilePage(mod, file.Id),
                Problem = sha1.Length == 0 ? "官方未提供 SHA-1，不能自动下载并校验。" : string.IsNullOrWhiteSpace(url) ? "CurseForge 暂无可用下载地址，将尝试 Modrinth 同内容文件。" : null,
                Requirement = new()
                {
                    Source = InstanceFileSource.CurseForge, Origin = InstanceFileOrigin.CurseForgeDownload, Role = InstanceFileRole.Content,
                    Required = true, Size = file.FileLength, Hashes = hashes, ProjectId = mod.Id, FileId = file.Id,
                    Downloads = sha1.Length == 0 || string.IsNullOrWhiteSpace(url) ? [] : [url]
                }
            }],
            Dependencies = file.Dependencies.Where(dependency => dependency.RelationType is 1 or 2 or 3 or 5 or 6).Select(dependency => new OnlineModDependency
            {
                Key = "CurseForge:" + dependency.ModId.ToString(CultureInfo.InvariantCulture),
                Project = new(ModProvider.CurseForge, dependency.ModId.ToString(CultureInfo.InvariantCulture)), Name = "项目 " + dependency.ModId,
                Kind = dependency.RelationType switch
                {
                    2 => OnlineDependencyKind.Optional, 3 => OnlineDependencyKind.Required,
                    1 or 6 => OnlineDependencyKind.Embedded, _ => OnlineDependencyKind.Incompatible
                }
            }).ToArray()
        };
    }

    /// <summary>缓存只保存公开目录数据，容量有界，取消和失败不会发布为成功缓存。</summary>
    private async Task<T> CachedAsync<T>(string key, Func<Task<T>> fetch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(key, out var value) && value.Expires > DateTimeOffset.UtcNow && value.Value is T typed) return typed;
        var result = await fetch().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (_cache.Count >= 256) _cache.Clear();
        _cache[key] = (DateTimeOffset.UtcNow.AddMinutes(2), result!);
        return result;
    }

    /// <summary>平台编号必须是固定数字或字母数字 ID，不能是路径和显示名称。</summary>
    private static void ValidateProject(OnlineProjectId project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!Enum.IsDefined(project.Provider) || !IsId(project.ProjectId)) throw new ArgumentException("在线项目身份无效。");
        if (project.Provider == ModProvider.CurseForge) _ = CurseForgeId(project.ProjectId);
    }

    /// <summary>只接受 Minecraft 模组项目，不能把整合包或资源包下载到 mods。</summary>
    private static void ValidateCurseForgeProject(CurseForgeMod mod, long id)
    {
        if (mod.Id != id || mod.GameId != 432 || mod.ClassId != 6) throw new InvalidDataException("CurseForge 返回的项目不是所选 Minecraft 模组。");
    }

    /// <summary>文件元数据必须归属所选项目并包含非负大小。</summary>
    private static void ValidateCurseForgeFile(CurseForgeFile file, long projectId)
    {
        if (file.ModId != projectId || file.Id <= 0 || file.GameId != 432 || file.FileLength < 0)
            throw new InvalidDataException("CurseForge 文件身份或大小无效。");
    }

    /// <summary>将官方 HTML 正文转换为普通文本；不执行脚本或创建浏览器控件。</summary>
    private static string PlainText(string value) => WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromMilliseconds(200)));
    /// <summary>生成固定官方文件页面，绝不把未知平台链接当作可下载文件。</summary>
    private static string FilePage(CurseForgeMod mod, long fileId) => CurseForgeFileStateStore.IsOfficialPage(mod.Links.WebsiteUrl)
        ? new Uri(mod.Links.WebsiteUrl!).GetLeftPart(UriPartial.Path).TrimEnd('/') + "/files/" + fileId
        : $"https://www.curseforge.com/projects/{mod.Id}/files/{fileId}";
    /// <summary>校验目录查询实际声明的游戏版本与加载器。</summary>
    private static bool Compatible(IReadOnlyList<string> versions, IReadOnlyList<string> loaders, string version, string loader) =>
        (version.Length == 0 || versions.Contains(version, StringComparer.OrdinalIgnoreCase)) &&
        (loader.Length == 0 || loaders.Contains(loader, StringComparer.OrdinalIgnoreCase));
    /// <summary>加载器名称映射为官方编号，空值表示未筛选。</summary>
    private static CurseForgeModLoaderType? CurseForgeLoader(string value) => value.ToLowerInvariant() switch
    {
        "" => null, "forge" => CurseForgeModLoaderType.Forge, "fabric" => CurseForgeModLoaderType.Fabric,
        "neoforge" => CurseForgeModLoaderType.NeoForge, "quilt" => CurseForgeModLoaderType.Quilt,
        "liteloader" => CurseForgeModLoaderType.LiteLoader, _ => throw new ArgumentException("不支持此加载器。")
    };
    /// <summary>限制加载器值，避免把未知选择悄悄当作全部加载器。</summary>
    private static void ValidateLoader(string value) { if (value.Length > 0 && !Loaders.Contains(value, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("未知加载器。"); }
    /// <summary>环境选择只表示支持端，不据此推断模组间兼容。</summary>
    private static void ValidateEnvironment(string value) { if (value is not ("" or "client" or "server" or "both")) throw new ArgumentException("未知运行环境。"); }
    /// <summary>固定平台数字使用不受地区影响的解析，拒绝正负号和小数。</summary>
    private static long CurseForgeId(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw new ArgumentException("CurseForge ID 无效。");
    /// <summary>目录标识只允许有界字母数字。</summary>
    private static bool IsId(string value) => value is { Length: > 0 and <= 128 } && value.All(char.IsAsciiLetterOrDigit);
    /// <summary>文件摘要必须满足对应算法长度。</summary>
    private static bool ValidHash(string value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit);
    /// <summary>模组首版只接受 JAR 或旧版 ZIP 归档，版本 JSON 等附件不能装进 mods。</summary>
    private static bool IsModFile(string name) => name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    /// <summary>空筛选转为没有条件，避免把空字符串发送给平台。</summary>
    private static string[] Values(string value) => string.IsNullOrWhiteSpace(value) ? [] : [value.Trim()];
    /// <summary>可选查询参数省略空值。</summary>
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    /// <summary>未知端环境保留为未声明，不暗示双端可运行。</summary>
    private static string EnvironmentText(string client, string server) => $"客户端：{Side(client)}；服务端：{Side(server)}";
    /// <summary>将平台端环境标志转换为可读文字。</summary>
    private static string Side(string value) => value switch { "required" => "必需", "optional" => "可选", "unsupported" => "不支持", _ => "未声明" };
    /// <summary>显示平台限流等待时间，不自动产生请求风暴。</summary>
    private static string RateMessage(TimeSpan? retry) => retry is { } value ? $"请求过于频繁，请约 {Math.Max(1, Math.Ceiling(value.TotalSeconds))} 秒后重试。" : "请求过于频繁，请稍后重试。";
}
