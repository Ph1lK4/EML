using System.Net;
using System.Text;
using System.Text.Json;
using EML.Core.CurseForge;
using EML.Core.Minecraft.Catalog;
using EML.Core.Modrinth;
using EML.Infrastructure.Minecraft.Catalog;
using EML.Infrastructure.Minecraft.CurseForge.Api;

namespace EML.Tests;

/// <summary>目录测试使用平台假对象和内存 HTTP，验证身份、分页、完整性及官方版本边界。</summary>
public sealed class InstanceCatalogTests
{
    private const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha512 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>详情直接使用作者中文名称、摘要和正文，不改变固定项目身份或搜索结果对象。</summary>
    [Fact]
    public async Task ModrinthDetailsKeepPlatformChineseTextAndIdentity()
    {
        var mr = new MrApi { ProjectDetails = new() { Id = "Project", ProjectType = "modpack", Title = "作者中文整合包",
            Description = "作者提供的中文简介", Body = "# 中文介绍\n\n保留**中文**内容。\n\nEnglish paragraph.", Downloads = 123 } };
        using var catalog = new InstanceCatalog(mr, new CfApi());
        var original = Project(InstanceCatalogProvider.Modrinth, "Project") with { Author = "Author", Summary = "Search summary" };
        var details = await catalog.GetProjectAsync(original);
        Assert.Equal(original.Provider, details.Provider); Assert.Equal(original.Id, details.Id);
        Assert.Equal("作者中文整合包", details.Name); Assert.Equal("作者提供的中文简介", details.Summary);
        Assert.Equal("中文介绍\n\n保留中文内容。\n\nEnglish paragraph.", details.Description);
        Assert.Equal("Author", details.Author); Assert.Equal(123, details.Downloads);
        Assert.Equal("Search summary", original.Summary); Assert.Equal("", original.Description);
        Assert.Equal(1, mr.ProjectCalls); Assert.Equal(0, mr.VersionCalls);
    }

    /// <summary>英文简介不遮蔽完整正文中的中文段落，也不生成平台没有的中文摘要。</summary>
    [Fact]
    public async Task CurseForgeDetailsKeepChineseBodyWithEnglishSummary()
    {
        var cf = new CfApi { ProjectDetails = new() { Id = 12, GameId = 432, ClassId = 4471, Name = "Pack",
            Summary = "Original English summary" }, Description = "<h2>English</h2><p>English body.</p><h2>中文</h2><p>作者中文介绍 &amp; 特点。</p>" };
        using var catalog = new InstanceCatalog(new MrApi(), cf);
        var details = await catalog.GetProjectAsync(Project(InstanceCatalogProvider.CurseForge, "12"));
        Assert.Equal("Pack", details.Name); Assert.Equal("Original English summary", details.Summary);
        Assert.Contains("English body.", details.Description); Assert.Contains("作者中文介绍 & 特点。", details.Description);
        Assert.DoesNotContain("<p>", details.Description); Assert.Equal("12", details.Id);
        Assert.Equal(1, cf.DescriptionCalls);
    }

    /// <summary>全英文正文保持平台原文，空正文不会由本地中文目录补写。</summary>
    [Theory]
    [InlineData("Original English body.")]
    [InlineData("")]
    public async Task DetailsDoNotInventTranslationOrFallbackBody(string body)
    {
        var mr = new MrApi { ProjectDetails = new() { Id = "Project", ProjectType = "modpack", Title = "Pack",
            Description = "English summary", Body = body } };
        using var catalog = new InstanceCatalog(mr, new CfApi());
        var details = await catalog.GetProjectAsync(Project(InstanceCatalogProvider.Modrinth, "Project"));
        Assert.Equal("Pack", details.Name); Assert.Equal("English summary", details.Summary); Assert.Equal(body, details.Description);
    }

    /// <summary>详情拒绝不同项目或非整合包响应，不能把他人的中文介绍展示到当前项目。</summary>
    [Theory]
    [InlineData("Other", "modpack")]
    [InlineData("Project", "mod")]
    public async Task ModrinthDetailsRejectWrongIdentityOrType(string id, string type)
    {
        var mr = new MrApi { ProjectDetails = new() { Id = id, ProjectType = type, Body = "其他项目介绍" } };
        using var catalog = new InstanceCatalog(mr, new CfApi());
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.GetProjectAsync(Project(InstanceCatalogProvider.Modrinth, "Project")));
    }

    /// <summary>CurseForge 身份、游戏或项目类别错误时不会继续获取该正文。</summary>
    [Theory]
    [InlineData(13, 432, 4471)]
    [InlineData(12, 99, 4471)]
    [InlineData(12, 432, 6)]
    public async Task CurseForgeDetailsRejectWrongIdentityBeforeReadingBody(long id, int gameId, int classId)
    {
        var cf = new CfApi { ProjectDetails = new() { Id = id, GameId = gameId, ClassId = classId } };
        using var catalog = new InstanceCatalog(new MrApi(), cf);
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.GetProjectAsync(Project(InstanceCatalogProvider.CurseForge, "12")));
        Assert.Equal(0, cf.DescriptionCalls);
    }

    /// <summary>搜索只读取目录摘要，不为每个结果附加正文请求。</summary>
    [Fact]
    public async Task SearchDoesNotFetchProjectDescriptions()
    {
        var mr = new MrApi(); var cf = new CfApi(); using var catalog = new InstanceCatalog(mr, cf);
        await catalog.SearchAsync(new() { Provider = InstanceCatalogProvider.Modrinth });
        await catalog.SearchAsync(new() { Provider = InstanceCatalogProvider.CurseForge });
        Assert.Equal(0, mr.ProjectCalls); Assert.Equal(0, cf.DescriptionCalls);
    }

    /// <summary>取消请求不会继续调用平台；原版详情直接使用现有条目，不新增任何网络读取。</summary>
    [Fact]
    public async Task DetailsHonorCancellationAndMinecraftNeedsNoNetwork()
    {
        var mr = new MrApi(); var cf = new CfApi(); using var handler = new JsonHandler(_ => throw new InvalidOperationException("Unexpected request"));
        using var catalog = new InstanceCatalog(mr, cf, handler);
        var vanilla = Project(InstanceCatalogProvider.Minecraft, "1.20.1") with { Summary = "正式版" };
        Assert.Same(vanilla, await catalog.GetProjectAsync(vanilla));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.GetProjectAsync(Project(InstanceCatalogProvider.Modrinth, "Project"), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.GetProjectAsync(Project(InstanceCatalogProvider.CurseForge, "12"), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.GetProjectAsync(vanilla, cancellation.Token));
        Assert.Equal(0, handler.Calls); Assert.Equal(0, mr.ProjectCalls); Assert.Equal(0, cf.DescriptionCalls); Assert.Equal(0, cf.ProjectCalls);
    }

    /// <summary>正在读取正文时取消会传递给现有 API，不能把取消后的正文作为成功结果。</summary>
    [Fact]
    public async Task DescriptionRequestReceivesCancellation()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cf = new CfApi { ReadDescription = async token => { reached.SetResult(); await Task.Delay(Timeout.Infinite, token); return "unexpected"; } };
        using var catalog = new InstanceCatalog(new MrApi(), cf);
        using var cancellation = new CancellationTokenSource();
        var pending = catalog.GetProjectAsync(Project(InstanceCatalogProvider.CurseForge, "12"), cancellation.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    /// <summary>详情转换失败只使本次说明请求失败，既有版本接口仍能独立提供可下载版本。</summary>
    [Fact]
    public async Task OversizedDescriptionDoesNotBreakVersionQuery()
    {
        var cf = new CfApi { Description = new string('x', PlatformProjectDescription.MaximumInputLength + 1) };
        using var catalog = new InstanceCatalog(new MrApi(), cf);
        var project = Project(InstanceCatalogProvider.CurseForge, "12");
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.GetProjectAsync(project));
        Assert.True(Assert.Single((await catalog.GetVersionsAsync(project)).Items).CanDownload);
    }

    /// <summary>搜索强制 modpack 类型，同时传递筛选、分页及排序。</summary>
    [Fact]
    public async Task ModrinthSearchUsesPackTypeAndFilters()
    {
        var mr = new MrApi(); using var catalog = new InstanceCatalog(mr, new CfApi());
        var page = await catalog.SearchAsync(new() { Provider = InstanceCatalogProvider.Modrinth,
            Query = " tech ", GameVersion = "1.20.1", Loader = "fabric", Offset = 20, Sort = 2 });
        Assert.Equal("tech", mr.Search!.Query); Assert.Equal([ModrinthProjectType.Modpack], mr.Search.ProjectTypes);
        Assert.Equal(["1.20.1"], mr.Search.GameVersions); Assert.Equal(["fabric"], mr.Search.Loaders);
        Assert.Equal(ModrinthSearchSort.Updated, mr.Search.Sort); Assert.Equal(20, mr.Search.Offset);
        Assert.Equal("Pack", Assert.Single(page.Items).Name); Assert.True(page.HasMore); Assert.Equal(21, page.NextOffset);
    }

    /// <summary>CurseForge 搜索限定 Minecraft 整合包类别而非普通模组类别。</summary>
    [Fact]
    public async Task CurseForgeSearchUsesPackClassAndFilters()
    {
        var cf = new CfApi(); using var catalog = new InstanceCatalog(new MrApi(), cf);
        var page = await catalog.SearchAsync(new() { Provider = InstanceCatalogProvider.CurseForge,
            GameVersion = "1.20.1", Loader = "fabric", Sort = 1 });
        Assert.Equal(432, cf.Search!.GameId); Assert.Equal(4471, cf.Search.ClassId);
        Assert.Equal(CurseForgeModLoaderType.Fabric, cf.Search.ModLoaderType);
        Assert.Equal(CurseForgeSortField.TotalDownloads, cf.Search.SortField);
        Assert.Equal("Author", Assert.Single(page.Items).Author);
    }

    /// <summary>加载器筛选缺少必要 Minecraft 版本时明确报错，不能偷偷放宽。</summary>
    [Fact]
    public async Task CurseForgeLoaderRequiresGameVersion()
    {
        using var catalog = new InstanceCatalog(new MrApi(), new CfApi());
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.SearchAsync(new() { Provider = InstanceCatalogProvider.CurseForge, Loader = "fabric" }));
    }

    /// <summary>版本列表核对兼容性并使用发布列表排序分页，不使用项目 latestFiles。</summary>
    [Fact]
    public async Task ModrinthVersionsAreFilteredAndPaged()
    {
        var mr = new MrApi { Versions = Enumerable.Range(1, 24).Select(index => MrVersion() with
        { Id = "V" + index, DatePublished = DateTimeOffset.UnixEpoch.AddDays(index) }).Append(MrVersion() with { Loaders = ["forge"] }).ToArray() };
        using var catalog = new InstanceCatalog(mr, new CfApi());
        var first = await catalog.GetVersionsAsync(Project(InstanceCatalogProvider.Modrinth, "Project"), "1.20.1", "fabric");
        var second = await catalog.GetVersionsAsync(Project(InstanceCatalogProvider.Modrinth, "Project"), "1.20.1", "fabric", first.NextOffset);
        Assert.Equal(20, first.Items.Count); Assert.Equal("V24", first.Items[0].Id); Assert.True(first.HasMore);
        Assert.Equal(4, second.Items.Count); Assert.False(second.HasMore);
    }

    /// <summary>同版本多附件只使用唯一主 mrpack，不下载普通 ZIP 或其他附件。</summary>
    [Fact]
    public async Task ModrinthSelectsUniquePrimaryMrpack()
    {
        var mr = new MrApi { Version = MrVersion() with { Files = [MrFile("first.mrpack") with { Primary = false },
            MrFile("client.mrpack"), MrFile("server.zip")] } };
        using var catalog = new InstanceCatalog(mr, new CfApi());
        var release = await catalog.GetReleaseAsync(InstanceCatalogProvider.Modrinth, "Project", "Version");
        Assert.Equal("client.mrpack", release.FileName); Assert.True(release.CanDownload);
        Assert.Equal(Sha1, release.Hashes["sha1"]); Assert.Equal(Sha512, release.Hashes["sha512"]);
    }

    /// <summary>附件选择有歧义时禁止自动下载，不能任意挑选其中一个文件。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModrinthAmbiguousMrpacksAreBlocked(bool primary)
    {
        var mr = new MrApi { Version = MrVersion() with { Files = [MrFile("one.mrpack") with { Primary = primary }, MrFile("two.mrpack") with { Primary = primary }] } };
        using var catalog = new InstanceCatalog(mr, new CfApi());
        var release = await catalog.GetReleaseAsync(InstanceCatalogProvider.Modrinth, "Project", "Version");
        Assert.False(release.CanDownload); Assert.NotNull(release.Problem); Assert.Empty(release.DownloadUrls);
    }

    /// <summary>普通模组、错误项目或错误版本身份都不能安装为所选整合包。</summary>
    [Theory]
    [InlineData("project")]
    [InlineData("version")]
    [InlineData("type")]
    public async Task ModrinthExactReleaseRejectsIdentityMismatch(string kind)
    {
        var mr = new MrApi();
        if (kind == "project") mr.Version = mr.Version with { ProjectId = "Other" };
        if (kind == "version") mr.Version = mr.Version with { Id = "Other" };
        if (kind == "type") mr.ProjectType = "mod";
        using var catalog = new InstanceCatalog(mr, new CfApi());
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.GetReleaseAsync(InstanceCatalogProvider.Modrinth, "Project", "Version"));
    }

    /// <summary>下载前重新查询后端，搜索期间返回的旧下载地址不作为权威依据。</summary>
    [Fact]
    public async Task ReleaseRefreshesPlatformMetadata()
    {
        var mr = new MrApi(); using var catalog = new InstanceCatalog(mr, new CfApi());
        await catalog.GetVersionsAsync(Project(InstanceCatalogProvider.Modrinth, "Project"));
        mr.Version = MrVersion() with { Files = [MrFile() with { Url = "https://cdn.modrinth.com/changed.mrpack" }] };
        var result = await catalog.GetReleaseAsync(InstanceCatalogProvider.Modrinth, "Project", "Version");
        Assert.Equal("https://cdn.modrinth.com/changed.mrpack", Assert.Single(result.DownloadUrls));
        Assert.Equal(1, mr.VersionCalls);
    }

    /// <summary>没有官方 sha1/sha512 的整合包不能得到可下载状态。</summary>
    [Fact]
    public async Task ModrinthMissingOfficialHashesBlocksDownload()
    {
        var mr = new MrApi { Version = MrVersion() with { Files = [MrFile() with { Hashes = new Dictionary<string, string>() }] } };
        using var catalog = new InstanceCatalog(mr, new CfApi());
        Assert.False((await catalog.GetReleaseAsync(InstanceCatalogProvider.Modrinth, "Project", "Version")).CanDownload);
    }

    /// <summary>过滤服务端包后仍按响应原始条目数量推进分页。</summary>
    [Fact]
    public async Task CurseForgeServerPacksAreFilteredWithoutRepeatingPage()
    {
        var cf = new CfApi { Files = [CfFile() with { IsServerPack = true }, CfFile() with { Id = 35 }] };
        using var catalog = new InstanceCatalog(new MrApi(), cf);
        var page = await catalog.GetVersionsAsync(Project(InstanceCatalogProvider.CurseForge, "12"));
        Assert.Equal("35", Assert.Single(page.Items).Id); Assert.Equal(2, page.NextOffset); Assert.True(page.HasMore);
    }

    /// <summary>明确服务端、禁分发、无地址和无 SHA-1 都保持不可下载，不拼 CDN。</summary>
    [Theory]
    [InlineData("server")]
    [InlineData("distribution")]
    [InlineData("url")]
    [InlineData("hash")]
    [InlineData("size")]
    public async Task CurseForgeUnsupportedFilesAreBlocked(string reason)
    {
        var cf = new CfApi();
        if (reason == "server") cf.File = cf.File with { IsServerPack = true };
        if (reason == "distribution") cf.AllowDistribution = false;
        if (reason == "url") cf.File = cf.File with { DownloadUrl = null };
        if (reason == "hash") cf.File = cf.File with { Hashes = [] };
        if (reason == "size") cf.File = cf.File with { FileLength = 0 };
        using var catalog = new InstanceCatalog(new MrApi(), cf);
        var release = await catalog.GetReleaseAsync(InstanceCatalogProvider.CurseForge, "12", "34");
        Assert.False(release.CanDownload); Assert.Empty(release.DownloadUrls); Assert.Contains("curseforge.com", release.WebsiteUrl);
    }

    /// <summary>CurseForge 文件归属不一致必须拒绝，不能只依赖相同文件名。</summary>
    [Fact]
    public async Task CurseForgeWrongProjectIsRejected()
    {
        var cf = new CfApi { File = CfFile() with { ModId = 99 } };
        using var catalog = new InstanceCatalog(new MrApi(), cf);
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.GetReleaseAsync(InstanceCatalogProvider.CurseForge, "12", "34"));
    }

    /// <summary>现有 API 的命名策略正确读取官方 isServerPack 字段。</summary>
    [Fact]
    public async Task CurseForgeApiReadsServerPackFlag()
    {
        using var handler = new JsonHandler(_ => """{"data":{"id":34,"modId":12,"gameId":432,"isServerPack":true}}""");
        using var api = new CurseForgeApiClient(handler, new TestKey());
        Assert.True((await api.GetFileAsync(12, 34)).IsServerPack);
    }

    /// <summary>原版默认只展示正式版；勾选快照后含带空格的官方历史快照。</summary>
    [Fact]
    public async Task MinecraftSearchDefaultsToReleasesAndSupportsSnapshots()
    {
        using var handler = new JsonHandler(_ => Manifest());
        using var catalog = new InstanceCatalog(new MrApi(), new CfApi(), handler);
        var stable = await catalog.SearchAsync(new() { Provider = InstanceCatalogProvider.Minecraft });
        Assert.Equal("1.20.1", Assert.Single(stable.Items).Id);
        var all = await catalog.SearchAsync(new() { Provider = InstanceCatalogProvider.Minecraft, IncludeSnapshots = true });
        Assert.Equal(3, all.Items.Count); Assert.Contains(all.Items, item => item.Id == "3D Shareware v1.34");
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>确认安装强制重新读取官方目录，返回对应 JSON 的 SHA-1 而非 client.jar 的猜测地址。</summary>
    [Fact]
    public async Task MinecraftReleaseRefreshesOfficialManifest()
    {
        using var handler = new JsonHandler(_ => Manifest());
        using var catalog = new InstanceCatalog(new MrApi(), new CfApi(), handler);
        await catalog.SearchAsync(new() { Provider = InstanceCatalogProvider.Minecraft });
        var release = await catalog.GetReleaseAsync(InstanceCatalogProvider.Minecraft, "1.20.1", "1.20.1");
        Assert.Equal(2, handler.Calls); Assert.Equal("1.20.1.json", release.FileName);
        Assert.Equal(Sha1, release.Hashes["sha1"]); Assert.True(release.CanDownload);
        Assert.Contains("piston-meta.mojang.com", Assert.Single(release.DownloadUrls));
    }

    /// <summary>未知版本、改动身份或路径型 ID 不会回退为当前最新版。</summary>
    [Theory]
    [InlineData("unknown", "unknown")]
    [InlineData("1.20.1", "24w01a")]
    [InlineData("../1.20.1", "../1.20.1")]
    public async Task MinecraftRejectsUnknownOrMismatchedIds(string project, string version)
    {
        using var handler = new JsonHandler(_ => Manifest());
        using var catalog = new InstanceCatalog(new MrApi(), new CfApi(), handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.GetReleaseAsync(InstanceCatalogProvider.Minecraft, project, version));
    }

    /// <summary>官方目录的外部域名、摘要损坏或重复身份都不能进入下载计划。</summary>
    [Theory]
    [InlineData("host")]
    [InlineData("hash")]
    [InlineData("duplicate")]
    public async Task MinecraftRejectsInvalidManifestEntries(string mode)
    {
        var entry = VersionEntry("1.20.1", "release");
        if (mode == "host") entry["url"] = "https://example.com/version.json";
        if (mode == "hash") entry["sha1"] = "bad";
        var json = JsonSerializer.Serialize(new { versions = mode == "duplicate" ? new[] { entry, entry } : new[] { entry } });
        using var handler = new JsonHandler(_ => json); using var catalog = new InstanceCatalog(new MrApi(), new CfApi(), handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.SearchAsync(new() { Provider = InstanceCatalogProvider.Minecraft }));
    }

    /// <summary>过大的目录响应在解析前拒绝，防止无界缓冲。</summary>
    [Fact]
    public async Task MinecraftRejectsOversizedManifest()
    {
        using var handler = new JsonHandler(_ => new string(' ', 8 * 1024 * 1024 + 1));
        using var catalog = new InstanceCatalog(new MrApi(), new CfApi(), handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.SearchAsync(new() { Provider = InstanceCatalogProvider.Minecraft }));
    }

    /// <summary>取消必须在读取缓存前检查，不能因缓存存在返回已取消请求的结果。</summary>
    [Fact]
    public async Task CanceledSearchNeverUsesCache()
    {
        using var handler = new JsonHandler(_ => Manifest()); using var catalog = new InstanceCatalog(new MrApi(), new CfApi(), handler);
        await catalog.SearchAsync(new() { Provider = InstanceCatalogProvider.Minecraft });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.SearchAsync(new() { Provider = InstanceCatalogProvider.Minecraft }, cancellation.Token));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>为接口生成固定身份的测试项目。</summary>
    private static InstanceCatalogProject Project(InstanceCatalogProvider provider, string id) => new() { Provider = provider, Id = id, Name = "Pack" };
    /// <summary>生成有官方摘要及唯一主附件的 Modrinth 整合包。</summary>
    private static ModrinthVersion MrVersion() => new() { Id = "Version", ProjectId = "Project", Name = "Pack 1.0", VersionNumber = "1.0",
        GameVersions = ["1.20.1"], Loaders = ["fabric"], VersionType = "release", Files = [MrFile()] };
    /// <summary>测试文件只引用假的 URL，不联网读取内容。</summary>
    private static ModrinthVersionFile MrFile(string name = "pack.mrpack") => new() { Filename = name, Size = 42, Primary = true,
        Url = "https://cdn.modrinth.com/pack.mrpack", Hashes = new Dictionary<string, string> { ["sha1"] = Sha1, ["sha512"] = Sha512 } };
    /// <summary>生成可下载的官方客户端 ZIP 元数据。</summary>
    private static CurseForgeFile CfFile() => new() { Id = 34, ModId = 12, GameId = 432, FileName = "pack.zip", DisplayName = "Pack 1.0",
        FileLength = 42, IsAvailable = true, DownloadUrl = "https://edge.forgecdn.net/files/1/2/pack.zip",
        GameVersions = ["1.20.1", "Fabric"], Hashes = [new() { Algo = 1, Value = Sha1 }] };
    /// <summary>构造一个正式版、两个快照与不展示的历史类型。</summary>
    private static string Manifest() => JsonSerializer.Serialize(new { versions = new[]
    { VersionEntry("1.20.1", "release"), VersionEntry("24w01a", "snapshot"), VersionEntry("3D Shareware v1.34", "snapshot"), VersionEntry("a1.0", "old_alpha") } });
    /// <summary>目录记录包含可信域名、SHA-1 与发布时间。</summary>
    private static Dictionary<string, string> VersionEntry(string id, string type) => new()
    { ["id"] = id, ["type"] = type, ["url"] = "https://piston-meta.mojang.com/v1/packages/" + Sha1 + "/" + Uri.EscapeDataString(id) + ".json",
        ["sha1"] = Sha1, ["releaseTime"] = "2024-01-01T00:00:00Z" };

    /// <summary>记录 Modrinth 查询并返回可调整的内存项目。</summary>
    private sealed class MrApi : IModrinthApiClient
    {
        public ModrinthRateLimit? LastRateLimit => null;
        public ModrinthSearchOptions? Search { get; private set; }
        public string ProjectType { get; set; } = "modpack";
        public ModrinthProject? ProjectDetails { get; set; }
        public int ProjectCalls { get; private set; }
        public ModrinthVersion Version { get; set; } = MrVersion();
        public IReadOnlyList<ModrinthVersion> Versions { get; set; } = [MrVersion()];
        public int VersionCalls { get; private set; }
        /// <summary>返回一项可继续翻页的目录。</summary>
        public Task<ModrinthSearchResult> SearchAsync(ModrinthSearchOptions options, CancellationToken cancellationToken = default)
        { Search = options; return Task.FromResult(new ModrinthSearchResult { Hits = [new() { ProjectId = "Project", ProjectType = "modpack", Title = "Pack" }], TotalHits = 100 }); }
        /// <summary>返回类型可调整的固定项目。</summary>
        public Task<ModrinthProject> GetProjectAsync(string id, CancellationToken cancellationToken = default)
        { ProjectCalls++; return Task.FromResult(ProjectDetails ?? new ModrinthProject { Id = id, ProjectType = ProjectType }); }
        /// <summary>记录精确版本请求，以检查确认下载时的重新查询。</summary>
        public Task<ModrinthVersion> GetVersionAsync(string id, CancellationToken cancellationToken = default) { VersionCalls++; return Task.FromResult(Version); }
        /// <summary>返回完整内存版本列表。</summary>
        public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string id, ModrinthVersionFilter? filter = null, CancellationToken cancellationToken = default) => Task.FromResult(Versions);
        /// <summary>整合包目录不使用模组哈希备用查询。</summary>
        public Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(IReadOnlyList<string> hashes, string algorithm = "sha1", CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>提供客户端与服务端文件可混排的 CurseForge 目录。</summary>
    private sealed class CfApi : ICurseForgeApiClient
    {
        public CurseForgeRateLimit? LastRateLimit => null;
        public CurseForgeSearchOptions? Search { get; private set; }
        public CurseForgeFile File { get; set; } = CfFile();
        public IReadOnlyList<CurseForgeFile> Files { get; set; } = [CfFile()];
        public bool AllowDistribution { get; set; } = true;
        public CurseForgeMod? ProjectDetails { get; set; }
        public string Description { get; set; } = "";
        public int DescriptionCalls { get; private set; }
        public int ProjectCalls { get; private set; }
        public Func<CancellationToken, Task<string>>? ReadDescription { get; set; }
        /// <summary>返回固定整合包项目与分发偏好。</summary>
        public Task<CurseForgeMod> GetModAsync(long id, CancellationToken cancellationToken = default)
        {
            ProjectCalls++;
            return Task.FromResult(ProjectDetails ?? new CurseForgeMod
            { Id = id, GameId = 432, ClassId = 4471, Name = "Pack", IsAvailable = true, AllowModDistribution = AllowDistribution, Authors = [new() { Name = "Author" }] });
        }
        /// <summary>返回可控制的正文请求，便于验证独立加载、失败和取消。</summary>
        public Task<string> GetModDescriptionAsync(long id, CancellationToken cancellationToken = default)
        { DescriptionCalls++; return ReadDescription?.Invoke(cancellationToken) ?? Task.FromResult(Description); }
        /// <summary>返回可人为设置身份错误的固定文件。</summary>
        public Task<CurseForgeFile> GetFileAsync(long id, long fileId, CancellationToken cancellationToken = default) => Task.FromResult(File);
        /// <summary>记录官方搜索条件。</summary>
        public async Task<CurseForgeSearchResult> SearchAsync(CurseForgeSearchOptions options, CancellationToken cancellationToken = default)
        { Search = options; return new() { Data = [await GetModAsync(12, cancellationToken)], Pagination = new() { Index = options.Index, ResultCount = 1, TotalCount = 100 } }; }
        /// <summary>返回完整文件接口的一页及未过滤计数。</summary>
        public Task<CurseForgeFilePage> GetModFilesAsync(long id, CurseForgeFileFilter filter, CancellationToken cancellationToken = default) => Task.FromResult(new CurseForgeFilePage
        { Data = Files, Pagination = new() { Index = filter.Index, ResultCount = Files.Count, TotalCount = 100 } });
        /// <summary>此目录不依赖批量文件接口。</summary>
        public Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>此目录不依赖批量项目接口。</summary>
        public Task<IReadOnlyList<CurseForgeMod>> GetModsAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>测试凭据仅供内存传输使用，不是真实授权。</summary>
    private sealed class TestKey : ICurseForgeApiKeyProvider { public string GetApiKey() => "test-not-real"; }
    /// <summary>内存响应记录官方请求地址，不接触外网。</summary>
    private sealed class JsonHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        /// <summary>每次请求同步获得一个测试 JSON 响应。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request,
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json") });
        }
    }
}
