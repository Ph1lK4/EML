using System.Net;
using System.Text;
using EML.Core.CurseForge;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods.Online;
using EML.Core.Modrinth;
using EML.Infrastructure.Minecraft.CurseForge.Api;
using EML.Infrastructure.Minecraft.Mods.Online;

namespace EML.Tests;

/// <summary>目录与精确备用下载的隔离测试，不访问网络、不需要真实平台凭据。</summary>
public sealed class OnlineModCatalogTests
{
    private const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha512 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>同名跨平台项目保留独立身份，所有快捷筛选均传入各自平台。</summary>
    [Fact]
    public async Task SearchKeepsProviderIdentityAndFilters()
    {
        var mr = new MrApi(); var cf = new CfApi(); var catalog = new OnlineModCatalog(mr, cf);
        var page = await catalog.SearchAsync(new() { Text = "Tech", GameVersion = "1.20.1", Loader = "fabric", Page = 2, Sort = OnlineModSort.Downloads });
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2, page.Items.Select(project => project.Id.Provider).Distinct().Count());
        Assert.All(page.Items, item => Assert.Equal("Same name", item.Name));
        Assert.Equal(40, mr.LastSearch!.Offset); Assert.Equal(40, cf.LastSearch!.Index);
        Assert.Equal(["1.20.1"], mr.LastSearch.GameVersions); Assert.Equal(["fabric"], mr.LastSearch.Loaders);
        Assert.Equal(6, cf.LastSearch.ClassId); Assert.Equal(CurseForgeModLoaderType.Fabric, cf.LastSearch.ModLoaderType);
        Assert.Equal(ModrinthSearchSort.Downloads, mr.LastSearch.Sort); Assert.Equal(CurseForgeSortField.TotalDownloads, cf.LastSearch.SortField);
    }

    /// <summary>搜索短时缓存命中不重复请求，但改变页数会请求新页。</summary>
    [Fact]
    public async Task SearchCachesSameQueryButSeparatesPages()
    {
        var mr = new MrApi(); var catalog = new OnlineModCatalog(mr, new CfApi());
        var query = new OnlineModQuery { Provider = ModProvider.Modrinth };
        await catalog.SearchAsync(query); await catalog.SearchAsync(query); await catalog.SearchAsync(query with { Page = 1 });
        Assert.Equal(2, mr.SearchCalls);
    }

    /// <summary>CurseForge 不支持的环境条件不会被静默忽略。</summary>
    [Fact]
    public async Task EnvironmentFilterOmitsUnsupportedCurseForgeResults()
    {
        var mr = new MrApi(); var cf = new CfApi(); var catalog = new OnlineModCatalog(mr, cf);
        var result = await catalog.SearchAsync(new() { Environment = "both" });
        Assert.Single(result.Items); Assert.Equal(0, cf.SearchCalls);
        Assert.Equal(2, mr.LastSearch!.AdditionalFacetGroups.Count);
        Assert.Contains(result.Notes, note => note.Contains("环境"));
    }

    /// <summary>平台授权失败不隐藏另一个平台的成功结果，失败本身也不进入成功缓存。</summary>
    [Fact]
    public async Task OneProviderFailureLeavesOtherProviderUsable()
    {
        var cf = new CfApi { SearchError = new HttpRequestException("unit test") };
        var catalog = new OnlineModCatalog(new MrApi(), cf);
        var result = await catalog.SearchAsync(new());
        Assert.Single(result.Items); Assert.Contains(result.Notes, note => note.Contains("CurseForge"));
        cf.SearchError = null;
        Assert.Equal(2, (await catalog.SearchAsync(new())).Items.Count);
    }

    /// <summary>429 后即使用户改变关键词也遵守 Retry-After，不继续请求同一平台。</summary>
    [Fact]
    public async Task SearchHonorsProviderRetryAfter()
    {
        var cf = new CfApi { SearchError = new CurseForgeApiException(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(30)) };
        var catalog = new OnlineModCatalog(new MrApi(), cf);
        await catalog.SearchAsync(new() { Provider = ModProvider.CurseForge, Text = "one" });
        var result = await catalog.SearchAsync(new() { Provider = ModProvider.CurseForge, Text = "two" });
        Assert.Equal(1, cf.SearchCalls);
        Assert.Contains(result.Notes, note => note.Contains("秒后"));
    }

    /// <summary>动态分类使用官方返回编号，不硬编码旧分类 ID。</summary>
    [Fact]
    public async Task CategoriesResolveOfficialReturnedId()
    {
        var cf = new CfApi(); var catalog = new OnlineModCatalog(new MrApi(), cf);
        await catalog.SearchAsync(new() { Provider = ModProvider.CurseForge, Category = "magic" });
        Assert.Equal(12345, cf.LastSearch!.CategoryId);
    }

    /// <summary>Modrinth 版本列表仍核对兼容条件并按时间分页。</summary>
    [Fact]
    public async Task VersionsFilterCompatibilityAndPageWithoutLatestReplacement()
    {
        var mr = new MrApi { Versions = Enumerable.Range(1, 24).Select(index => MrVersion("V" + index) with { DatePublished = DateTimeOffset.UnixEpoch.AddDays(index) }).Append(MrVersion("wrong") with { Loaders = ["forge"] }).ToArray() };
        var catalog = new OnlineModCatalog(mr, new CfApi());
        var first = await catalog.GetVersionsAsync(new(ModProvider.Modrinth, "Project"), "1.20.1", "fabric");
        var second = await catalog.GetVersionsAsync(new(ModProvider.Modrinth, "Project"), "1.20.1", "fabric", 1);
        Assert.Equal(20, first.Items.Count); Assert.True(first.HasMore);
        Assert.Equal(4, second.Items.Count); Assert.False(second.HasMore);
        Assert.Equal("V24", first.Items[0].Id); Assert.DoesNotContain(first.Items.Concat(second.Items), value => value.Id == "wrong");
    }

    /// <summary>带固定版本但不带项目编号的依赖由 API 查明项目，而不是猜测。</summary>
    [Fact]
    public async Task VersionOnlyDependencyResolvesItsProject()
    {
        var mr = new MrApi();
        mr.Version = MrVersion() with { Dependencies = [new() { VersionId = "Dependency", DependencyType = "required" }] };
        var catalog = new OnlineModCatalog(mr, new CfApi());
        var version = await catalog.GetVersionAsync(new(ModProvider.Modrinth, "Project"), "Version");
        Assert.Equal(new(ModProvider.Modrinth, "DependencyProject"), Assert.Single(version.Dependencies).Project);
    }

    /// <summary>可选依赖显示名称和用途，平台编号仍单独保存。</summary>
    [Fact]
    public async Task DependencyCheckboxHasNameAndSummary()
    {
        var mr = new MrApi { Version = MrVersion() with { Dependencies = [new() { ProjectId = "DependencyProject", DependencyType = "optional" }] } };
        var version = await new OnlineModCatalog(mr, new CfApi()).GetVersionAsync(new(ModProvider.Modrinth, "Project"), "Version");
        var dependency = Assert.Single(version.Dependencies);
        Assert.Equal("Dependency title", dependency.Name); Assert.Equal("Dependency summary", dependency.Description);
        Assert.Equal("DependencyProject", dependency.Project!.ProjectId);
    }

    /// <summary>仅依赖展示信息的网络失败保留编号，不阻止获取主模组的固定版本。</summary>
    [Fact]
    public async Task DependencyDescriptionFailureKeepsStableIdentity()
    {
        var mr = new MrApi { ProjectError = new HttpRequestException("test"), Version = MrVersion() with
        { Dependencies = [new() { ProjectId = "DependencyProject", DependencyType = "optional" }] } };
        var version = await new OnlineModCatalog(mr, new CfApi()).GetVersionAsync(new(ModProvider.Modrinth, "Project"), "Version");
        Assert.Equal("DependencyProject", Assert.Single(version.Dependencies).Name);
    }

    /// <summary>接口返回另一个项目的版本时立即拒绝，不能交给下载阶段补救。</summary>
    [Fact]
    public async Task ExactVersionRejectsWrongProject()
    {
        var mr = new MrApi { Version = MrVersion() with { ProjectId = "Other" } };
        await Assert.ThrowsAsync<InvalidDataException>(() => new OnlineModCatalog(mr, new CfApi()).GetVersionAsync(new(ModProvider.Modrinth, "Project"), "Version"));
    }

    /// <summary>在线目录与持久化使用相同的 Modrinth 官方算法要求，下载前拒绝只有本地 SHA-256 的声明。</summary>
    [Fact]
    public async Task ModrinthVersionRequiresSha1OrSha512BeforeDownload()
    {
        var mr = new MrApi { Version = MrVersion() with { Files = [MrFile() with
        { Hashes = new Dictionary<string, string> { ["sha256"] = new('a', 64) } }] } };
        await Assert.ThrowsAsync<InvalidDataException>(() => new OnlineModCatalog(mr, new CfApi()).GetVersionAsync(new(ModProvider.Modrinth, "Project"), "Version"));
    }

    /// <summary>默认文件不是同哈希文件时仍选择真正匹配的附件，并同时保存两个平台身份。</summary>
    [Fact]
    public async Task FallbackUsesMatchingFileNotPrimaryAndKeepsOrigins()
    {
        var mr = new MrApi { HashVersion = MrVersion() with { Files = [MrFile("wrong.jar") with { Primary = true, Hashes = new Dictionary<string, string> { ["sha1"] = new('2', 40) } }, MrFile("alternate.jar") with { Primary = false }] } };
        var catalog = new OnlineModCatalog(mr, new CfApi { AllowDistribution = false });
        var version = await catalog.GetVersionAsync(new(ModProvider.CurseForge, "12"), "34");
        Assert.Empty(Assert.Single(version.Files).Requirement.Downloads);
        var result = await catalog.ResolveDownloadAsync(version, version.Files[0]);
        Assert.Null(result.Problem); Assert.Equal("original.jar", result.FileName);
        Assert.Equal(InstanceFileOrigin.ModrinthDownload, result.Requirement.Origin);
        Assert.Equal(InstanceFileSource.DirectDownload, result.Requirement.Source);
        Assert.Equal("alternate.jar", result.Requirement.ModrinthOrigin!.FileName);
        Assert.Equal(12, result.Requirement.ProjectId); Assert.Equal(34, result.Requirement.FileId);
        Assert.Equal(Sha1, result.Requirement.CurseForgeOrigin!.Hashes["sha1"]);
        Assert.Equal(Sha512, result.Requirement.Hashes["sha512"]);
    }

    /// <summary>名称相同或只有版本号相同不能替代官方文件，大小和 SHA-1 都必须一致。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FallbackRejectsDifferentSizeOrHash(bool wrongSize)
    {
        var file = MrFile("original.jar");
        file = wrongSize ? file with { Size = 99 } : file with { Hashes = new Dictionary<string, string> { ["sha1"] = new('2', 40) } };
        var mr = new MrApi { HashVersion = MrVersion() with { Files = [file] } };
        var catalog = new OnlineModCatalog(mr, new CfApi { AllowDistribution = false });
        var version = await catalog.GetVersionAsync(new(ModProvider.CurseForge, "12"), "34");
        var result = await catalog.ResolveDownloadAsync(version, version.Files[0]);
        Assert.NotNull(result.Problem); Assert.Empty(result.Requirement.Downloads);
        Assert.Equal(InstanceFileOrigin.CurseForgeDownload, result.Requirement.Origin);
    }

    /// <summary>官方可直接下载时不发起备用查询，仍保留官方 SHA-1。</summary>
    [Fact]
    public async Task OfficialDownloadSkipsFallback()
    {
        var mr = new MrApi(); var catalog = new OnlineModCatalog(mr, new CfApi());
        var version = await catalog.GetVersionAsync(new(ModProvider.CurseForge, "12"), "34");
        var file = await catalog.ResolveDownloadAsync(version, version.Files[0]);
        Assert.Equal(0, mr.HashCalls); Assert.Single(file.Requirement.Downloads);
        Assert.Equal(Sha1, file.Requirement.Hashes["sha1"]);
    }

    /// <summary>必要、可选、嵌入及不兼容依赖分别保留，工具关系不会被误安装为依赖。</summary>
    [Fact]
    public async Task CurseForgeDependencyKindsAreNotConflated()
    {
        var cf = new CfApi { File = CfFile() with { Dependencies = Enumerable.Range(1, 6).Select(kind => new CurseForgeFileDependency { ModId = kind + 100, RelationType = kind }).ToArray() } };
        var version = await new OnlineModCatalog(new MrApi(), cf).GetVersionAsync(new(ModProvider.CurseForge, "12"), "34");
        Assert.Equal(5, version.Dependencies.Count);
        Assert.Single(version.Dependencies, item => item.Kind == OnlineDependencyKind.Required);
        Assert.Single(version.Dependencies, item => item.Kind == OnlineDependencyKind.Optional);
        Assert.Single(version.Dependencies, item => item.Kind == OnlineDependencyKind.Incompatible);
        Assert.Equal(2, version.Dependencies.Count(item => item.Kind == OnlineDependencyKind.Embedded));
        Assert.DoesNotContain(version.Dependencies, item => item.Project!.ProjectId == "104");
    }

    /// <summary>完整项目正文与作者通过独立接口读取，HTML 仅转换为普通文字。</summary>
    [Fact]
    public async Task CurseForgeDetailsReadAuthorsAndPlainDescription()
    {
        using var handler = new ApiHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/description", StringComparison.Ordinal)
            ? """{"data":"<p>Hello &amp; world</p>"}"""
            : """{"data":{"id":12,"gameId":432,"classId":6,"name":"Example","authors":[{"id":1,"name":"Author"}]}}""");
        using var api = new CurseForgeApiClient(handler, new TestKey());
        var details = await new OnlineModCatalog(new MrApi(), api).GetProjectAsync(new(ModProvider.CurseForge, "12"));
        Assert.Equal("Author", details.Author); Assert.Contains("Hello & world", details.Description); Assert.DoesNotContain("<p>", details.Description);
    }

    /// <summary>缺失官方 SHA-1 的文件不会借由备用流程变成可信下载。</summary>
    [Fact]
    public async Task MissingOfficialHashCannotUseFallback()
    {
        var mr = new MrApi(); var cf = new CfApi { File = CfFile() with { Hashes = [] } };
        var catalog = new OnlineModCatalog(mr, cf);
        var version = await catalog.GetVersionAsync(new(ModProvider.CurseForge, "12"), "34");
        var file = await catalog.ResolveDownloadAsync(version, version.Files[0]);
        Assert.Equal(0, mr.HashCalls); Assert.Empty(file.Requirement.Downloads); Assert.NotNull(file.Problem);
    }

    /// <summary>官方筛选确认的旧 Forge 文件保持兼容证据，未筛选前不会按年代猜测加载器。</summary>
    [Fact]
    public async Task LegacyCurseForgeLoaderRequiresFilteredEvidence()
    {
        var cf = new CfApi { File = CfFile() with { GameVersions = ["1.7.10"] } };
        var catalog = new OnlineModCatalog(new MrApi(), cf); var project = new OnlineProjectId(ModProvider.CurseForge, "12");
        Assert.Empty((await catalog.GetVersionAsync(project, "34")).Loaders);
        await catalog.GetVersionsAsync(project, "1.7.10", "forge");
        Assert.Equal(["forge"], (await catalog.GetVersionAsync(project, "34")).Loaders);
    }

    /// <summary>取消不会被转换成空结果和网络错误。</summary>
    [Fact]
    public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OnlineModCatalog(new MrApi(), new CfApi()).SearchAsync(new(), cts.Token));
    }

    /// <summary>新的 CurseForge 文件列表接口正确编码参数、解析作者，并拒绝别的项目文件。</summary>
    [Fact]
    public async Task CurseForgeFileEndpointEncodesFiltersAndPreservesPagination()
    {
        using var handler = new ApiHandler(request =>
        {
            Assert.Equal("/v1/mods/12/files", request.RequestUri!.AbsolutePath);
            Assert.Contains("gameVersion=1.20.1", request.RequestUri.Query);
            Assert.Contains("modLoaderType=4", request.RequestUri.Query);
            Assert.Contains("index=20", request.RequestUri.Query);
            Assert.Equal("test-not-real", Assert.Single(request.Headers.GetValues("x-api-key")));
            return """{"data":[{"id":34,"modId":12,"gameId":432}],"pagination":{"index":20,"pageSize":20,"resultCount":1,"totalCount":21}}""";
        });
        using var api = new CurseForgeApiClient(handler, new TestKey());
        var result = await api.GetModFilesAsync(12, new() { GameVersion = "1.20.1", ModLoaderType = CurseForgeModLoaderType.Fabric, Index = 20 });
        Assert.Equal(34, Assert.Single(result.Data).Id); Assert.Equal(21, result.Pagination.TotalCount);
    }

    /// <summary>文件列表不能返回另一个项目的结果。</summary>
    [Fact]
    public async Task CurseForgeFileEndpointRejectsWrongProject()
    {
        using var handler = new ApiHandler(_ => """{"data":[{"id":34,"modId":13}],"pagination":{}}""");
        using var api = new CurseForgeApiClient(handler, new TestKey());
        await Assert.ThrowsAsync<InvalidDataException>(() => api.GetModFilesAsync(12, new()));
    }

    /// <summary>构造合法、固定身份的 Modrinth 文件元数据。</summary>
    private static ModrinthVersionFile MrFile(string name = "example.jar") => new()
    {
        Filename = name, Size = 42, Primary = true, Url = "https://cdn.modrinth.com/data/Project/versions/Version/" + name,
        Hashes = new Dictionary<string, string> { ["sha1"] = Sha1, ["sha512"] = Sha512 }
    };
    /// <summary>构造可按需替换的版本快照，URL 随版本编号改变。</summary>
    private static ModrinthVersion MrVersion(string id = "Version") => new()
    {
        Id = id, ProjectId = "Project", Name = "Example", GameVersions = ["1.20.1"], Loaders = ["fabric"],
        Files = [MrFile() with { Url = "https://cdn.modrinth.com/data/Project/versions/" + id + "/example.jar" }]
    };
    /// <summary>构造官方下载许可和哈希俱全的 CurseForge 模组文件。</summary>
    private static CurseForgeFile CfFile() => new()
    {
        Id = 34, ModId = 12, GameId = 432, IsAvailable = true, FileName = "original.jar", FileLength = 42,
        Hashes = [new() { Algo = 1, Value = Sha1 }], DownloadUrl = "https://edge.forgecdn.net/files/1/2/original.jar",
        GameVersions = ["1.20.1", "Fabric"]
    };

    /// <summary>只返回内存目录，记录筛选与哈希请求以检查是否意外重复查询。</summary>
    private sealed class MrApi : IModrinthApiClient
    {
        public ModrinthRateLimit? LastRateLimit => null;
        public ModrinthSearchOptions? LastSearch { get; private set; }
        public int SearchCalls { get; private set; }
        public int HashCalls { get; private set; }
        public ModrinthVersion Version { get; set; } = MrVersion();
        public Exception? ProjectError { get; set; }
        public ModrinthVersion HashVersion { get; set; } = MrVersion();
        public IReadOnlyList<ModrinthVersion> Versions { get; set; } = [MrVersion()];
        public Task<ModrinthSearchResult> SearchAsync(ModrinthSearchOptions options, CancellationToken cancellationToken = default)
        {
            SearchCalls++; LastSearch = options;
            return Task.FromResult(new ModrinthSearchResult { Hits = [new() { ProjectId = "Project", ProjectType = "mod", Title = "Same name" }], Offset = options.Offset, TotalHits = 100 });
        }
        public Task<ModrinthProject> GetProjectAsync(string projectIdOrSlug, CancellationToken cancellationToken = default) => ProjectError is { } error
            ? Task.FromException<ModrinthProject>(error)
            : Task.FromResult(new ModrinthProject { Id = projectIdOrSlug, ProjectType = "mod", Title = "Dependency title", Description = "Dependency summary" });
        public Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(versionId == "Dependency" ? MrVersion("Dependency") with { ProjectId = "DependencyProject" } : Version);
        public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectIdOrSlug, ModrinthVersionFilter? filter = null, CancellationToken cancellationToken = default) => Task.FromResult(Versions);
        public Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(IReadOnlyList<string> hashes, string algorithm = "sha1", CancellationToken cancellationToken = default)
        { HashCalls++; return Task.FromResult<IReadOnlyDictionary<string, ModrinthVersion>>(new Dictionary<string, ModrinthVersion> { [hashes[0]] = HashVersion }); }
    }

    /// <summary>CurseForge 假服务将分发偏好与下载文件分开，以覆盖无下载地址场景。</summary>
    private sealed class CfApi : ICurseForgeApiClient
    {
        public CurseForgeRateLimit? LastRateLimit => null;
        public CurseForgeSearchOptions? LastSearch { get; private set; }
        public int SearchCalls { get; private set; }
        public Exception? SearchError { get; set; }
        public bool AllowDistribution { get; set; } = true;
        public CurseForgeFile File { get; set; } = CfFile();
        public Task<CurseForgeMod> GetModAsync(long projectId, CancellationToken cancellationToken = default) => Task.FromResult(new CurseForgeMod
        { Id = projectId, GameId = 432, ClassId = 6, Name = "Same name", IsAvailable = true, AllowModDistribution = AllowDistribution });
        public Task<CurseForgeFile> GetFileAsync(long projectId, long fileId, CancellationToken cancellationToken = default) => Task.FromResult(File);
        public async Task<CurseForgeSearchResult> SearchAsync(CurseForgeSearchOptions options, CancellationToken cancellationToken = default)
        {
            SearchCalls++; LastSearch = options;
            if (SearchError is not null) throw SearchError;
            return new() { Data = [await GetModAsync(12, cancellationToken)], Pagination = new() { Index = options.Index, ResultCount = 1, TotalCount = 100 } };
        }
        public Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(IReadOnlyList<long> fileIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CurseForgeFile>>([File]);
        public async Task<IReadOnlyList<CurseForgeMod>> GetModsAsync(IReadOnlyList<long> projectIds, CancellationToken cancellationToken = default) => [await GetModAsync(projectIds[0], cancellationToken)];
        public Task<CurseForgeFilePage> GetModFilesAsync(long projectId, CurseForgeFileFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CurseForgeFilePage { Data = [File], Pagination = new() { ResultCount = 1, TotalCount = 1 } });
        public Task<IReadOnlyList<CurseForgeCategory>> GetModCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CurseForgeCategory>>([new() { Id = 12345, Slug = "magic", ClassId = 6, GameId = 432 }]);
    }

    /// <summary>注入专用于测试的非真实凭证。</summary>
    private sealed class TestKey : ICurseForgeApiKeyProvider { public string GetApiKey() => "test-not-real"; }
    /// <summary>将请求交给断言并返回内存 JSON，避免测试访问外网。</summary>
    private sealed class ApiHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request), Encoding.UTF8, "application/json") });
    }
}
