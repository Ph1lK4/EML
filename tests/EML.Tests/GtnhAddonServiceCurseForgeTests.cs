using System.Security.Cryptography;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;
using EML.Core.Minecraft.Mods.Online;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Tests;

public sealed partial class GtnhAddonServiceTests
{
    private static GtnhAddon CurseForgeAddon => GtnhAddonCatalog.Items.Single(addon => addon.CurseForgeProjectId == 280294);

    /// <summary>固定项目按 1.7.10 Forge 筛选，精确文件下载后保留官方 SHA-1 和管理来源。</summary>
    [Fact]
    public async Task CurseForgeReleaseUsesExactLegacyForgeFileAndOfficialSha1()
    {
        await InitializeAsync(); using var http = Client(); var catalog = new AddonCatalog(CurseForgeVersion());
        var manager = new LocalModManager(_root); var downloader = new Downloader(_bytes);
        using var service = new GtnhAddonService(_root, downloader, manager, http, catalog);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, CurseForgeAddon.Id));
        Assert.True(release.CanInstall); Assert.Equal(GtnhAddonProvider.CurseForge, release.Provider);
        var query = Assert.Single(catalog.Queries); Assert.Equal("1.7.10", query.GameVersion); Assert.Equal("forge", query.Loader);
        Assert.Equal("280294", query.Project.ProjectId); Assert.Equal("12345", Assert.Single(catalog.Details));
        Assert.Equal(1, catalog.Resolutions); Assert.Contains("必需依赖：测试前置", release.Notes);
        var result = await service.InstallAsync(Instance, release, true); Assert.True(result.Succeeded, result.Message);
        var baseline = Assert.Single(InstanceEffectiveRequirements.Build(Instance, Manifest()).Requirements);
        Assert.Equal(InstanceFileOrigin.CurseForgeDownload, baseline.Origin); Assert.Equal(InstanceFileSource.CurseForge, baseline.Source);
        Assert.Equal(280294, baseline.ProjectId); Assert.Equal(12345, baseline.FileId);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(_bytes)), Assert.Single(baseline.Hashes).Value);
        Assert.True(await InstanceRuntimeInstaller.ExistingFileMatchesAsync(Path.Combine(Instance, baseline.Path!), baseline, default));
        Assert.True(Assert.Single((await manager.ListAsync(Instance)).Files).CanManage);
    }

    /// <summary>同内容 Modrinth 备用下载保留实际来源和原 CurseForge 固定身份，不混入其他文件摘要。</summary>
    [Fact]
    public async Task CurseForgeFallbackKeepsBothSourceIdentitiesAndContentHashes()
    {
        await InitializeAsync(); using var http = Client(); var original = CurseForgeVersion(download: false);
        var catalog = new AddonCatalog(original) { Resolve = (version, file) => file with
        {
            Problem = null, Requirement = file.Requirement with
            {
                Source = InstanceFileSource.DirectDownload, Origin = InstanceFileOrigin.ModrinthDownload,
                Downloads = ["https://cdn.modrinth.com/data/alternate/versions/fixed/other-name.jar"],
                Hashes = new Dictionary<string, string>
                {
                    ["sha1"] = file.Requirement.Hashes["sha1"], ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(_bytes))
                },
                ModrinthOrigin = new() { ProjectId = "alternate", VersionId = "fixed", FileName = "other-name.jar" },
                CurseForgeOrigin = new()
                {
                    ProjectId = 280294, FileId = 12345, Size = _bytes.Length, Hashes = file.Requirement.Hashes
                }
            }
        }};
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http, catalog);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, CurseForgeAddon.Id));
        Assert.Contains("Modrinth", release.Notes); Assert.True((await service.InstallAsync(Instance, release, true)).Succeeded);
        var baseline = Assert.Single(InstanceEffectiveRequirements.Build(Instance, Manifest()).Requirements);
        Assert.Equal(InstanceFileOrigin.ModrinthDownload, baseline.Origin); Assert.Equal(2, baseline.Hashes.Count);
        Assert.Equal("fixed", baseline.ModrinthOrigin!.VersionId); Assert.Equal("other-name.jar", baseline.ModrinthOrigin.FileName);
        Assert.Equal(12345, baseline.CurseForgeOrigin!.FileId);
        Assert.Equal(baseline.Hashes["sha1"], Assert.Single(baseline.CurseForgeOrigin.Hashes).Value);
    }

    /// <summary>CurseForge 下载即使宣称成功，错误大小或错误 SHA-1 仍被登记前校验拒绝。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurseForgeCorruptOrTruncatedFileNeverBecomesManaged(bool truncate)
    {
        await InitializeAsync(); using var http = Client(); var bytes = _bytes.ToArray();
        if (truncate) bytes = bytes[..^1]; else bytes[^1] ^= 1;
        using var service = new GtnhAddonService(_root, new Downloader(bytes), new LocalModManager(_root), http, new AddonCatalog(CurseForgeVersion()));
        var release = Assert.Single(await service.GetReleasesAsync(Instance, CurseForgeAddon.Id));
        Assert.False((await service.InstallAsync(Instance, release, true)).Succeeded);
        Assert.Empty(ModManagementStore.Read(Instance, Manifest()).Files);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Instance, ".minecraft", "mods")));
    }

    /// <summary>缺少官方 SHA-1 或可验证地址的文件只展示原因，用户确认不能绕过校验。</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UnverifiableCurseForgeFileCannotInstall(bool digest, bool download)
    {
        await InitializeAsync(); using var http = Client(); var downloader = new Downloader(_bytes);
        using var service = new GtnhAddonService(_root, downloader, new LocalModManager(_root), http, new AddonCatalog(CurseForgeVersion(digest, download)));
        var release = Assert.Single(await service.GetReleasesAsync(Instance, CurseForgeAddon.Id));
        Assert.False(release.CanInstall); Assert.NotNull(release.Problem);
        Assert.False((await service.InstallAsync(Instance, release, true)).Succeeded); Assert.Equal(0, downloader.Calls);
    }

    /// <summary>列表和详情各自检查版本与加载器；不能把新版或其他加载器模组当作 GTNH 可用版本。</summary>
    [Theory]
    [InlineData(false, "1.20.1", "forge")]
    [InlineData(false, "1.7.10", "fabric")]
    [InlineData(true, "1.20.1", "forge")]
    [InlineData(true, "1.7.10", "fabric")]
    public async Task OtherMinecraftVersionsAndLoadersAreExcluded(bool detailOnly, string gameVersion, string loader)
    {
        await InitializeAsync(); using var http = Client(); var valid = CurseForgeVersion();
        var invalid = valid with { GameVersions = [gameVersion], Loaders = [loader] };
        var catalog = new AddonCatalog(detailOnly ? valid : invalid) { Detail = detailOnly ? invalid : null };
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http, catalog);
        Assert.Empty(await service.GetReleasesAsync(Instance, CurseForgeAddon.Id)); Assert.Equal(0, catalog.Resolutions);
    }

    /// <summary>详情或下载解析不能偷偷更换固定平台项目、文件编号或官方 SHA-1。</summary>
    [Theory]
    [InlineData("project")]
    [InlineData("version")]
    [InlineData("file")]
    [InlineData("hash")]
    public async Task ChangedCurseForgeIdentityIsRejected(string changed)
    {
        await InitializeAsync(); using var http = Client(); var version = CurseForgeVersion();
        var catalog = new AddonCatalog(version);
        if (changed == "project") catalog.Detail = version with { Project = new(ModProvider.CurseForge, "99999") };
        else if (changed == "version") catalog.Detail = version with { Id = "99999" };
        else catalog.Resolve = (_, file) => file with { Requirement = changed == "file" ? file.Requirement with { FileId = 99999 } :
            file.Requirement with { Hashes = new Dictionary<string, string> { ["sha1"] = new('0', 40) } } };
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http, catalog);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetReleasesAsync(Instance, CurseForgeAddon.Id));
    }

    /// <summary>目录来源集合后来改变不会改写已签发的摘要，界面拿到的快照也不可原地编辑。</summary>
    [Fact]
    public async Task CurseForgeSelectionHasImmutableIntegritySnapshot()
    {
        await InitializeAsync(); using var http = Client(); var version = CurseForgeVersion();
        var catalog = new AddonCatalog(version); using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http, catalog);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, CurseForgeAddon.Id));
        var originalHash = release.DownloadRequirement!.Hashes["sha1"];
        ((Dictionary<string, string>)version.Files[0].Requirement.Hashes)["sha1"] = new('0', 40);
        Assert.Equal(originalHash, release.DownloadRequirement.Hashes["sha1"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)release.DownloadRequirement.Hashes)["sha1"] = new('0', 40));
        Assert.True((await service.InstallAsync(Instance, release, true)).Succeeded);
    }

    /// <summary>重复分页最多查询四页，重复文件只解析一次，避免旧项目发行历史造成无限请求。</summary>
    [Fact]
    public async Task CurseForgePaginationIsBoundedAndDeduplicated()
    {
        await InitializeAsync(); using var http = Client(); var catalog = new AddonCatalog(CurseForgeVersion()) { HasMore = true };
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http, catalog);
        Assert.Single(await service.GetReleasesAsync(Instance, CurseForgeAddon.Id));
        Assert.Equal(new[] { 0, 1, 2, 3 }, catalog.Queries.Select(query => query.Page)); Assert.Single(catalog.Details);
    }

    /// <summary>外部作者页面保留人工操作入口，不会对空 GitHub 仓库发请求或伪造发行文件。</summary>
    [Fact]
    public async Task ExternalAuthorPageHasNoSyntheticReleasesOrNetwork()
    {
        await InitializeAsync(); var handler = new Handler(() => throw new InvalidOperationException("不能请求 GitHub"));
        using var http = new HttpClient(handler); var catalog = new AddonCatalog(CurseForgeVersion());
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http, catalog);
        var external = service.Addons.First(addon => addon.Provider == GtnhAddonProvider.External);
        Assert.Empty(await service.GetReleasesAsync(Instance, external.Id)); Assert.Equal(0, handler.Calls); Assert.Empty(catalog.Queries);
    }

    /// <summary>资源包即使有下载身份也不能被 GTNH 模组服务当作 JAR 安装。</summary>
    [Fact]
    public async Task ResourcePackReleaseCannotInstallIntoMods()
    {
        await InitializeAsync(); using var http = Client(); var downloader = new Downloader(_bytes);
        using var service = new GtnhAddonService(_root, downloader, new LocalModManager(_root), http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        var pack = release with { Kind = GtnhAddonKind.ResourcePack };
        Assert.False((await service.InstallAsync(Instance, pack, true)).Succeeded); Assert.Equal(0, downloader.Calls);
    }

    /// <summary>构造具有固定 CurseForge 项目、文件、官方摘要和依赖的受控版本。</summary>
    private OnlineModVersion CurseForgeVersion(bool digest = true, bool download = true) => new()
    {
        Project = new(ModProvider.CurseForge, "280294"), Id = "12345", Name = "Legacy Forge", VersionNumber = "legacy-1",
        GameVersions = ["1.7.10"], Loaders = ["forge"], Channel = "release", Published = DateTimeOffset.UtcNow,
        Files = [new()
        {
            FileName = "addon.jar", Primary = true,
            Problem = !digest ? "缺少 SHA-1" : !download ? "等待人工下载" : null,
            Requirement = new()
            {
                Source = InstanceFileSource.CurseForge, Origin = InstanceFileOrigin.CurseForgeDownload, Role = InstanceFileRole.Content,
                Size = _bytes.Length, ProjectId = 280294, FileId = 12345,
                Hashes = digest ? new Dictionary<string, string> { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(_bytes)) } : new Dictionary<string, string>(),
                Downloads = download ? ["https://edge.forgecdn.net/files/12/345/addon.jar"] : []
            }
        }],
        Dependencies = [new() { Key = "CurseForge:111", Project = new(ModProvider.CurseForge, "111"), Kind = OnlineDependencyKind.Required, Name = "测试前置" }]
    };

    /// <summary>模拟共用平台目录，记录筛选与固定版本请求，不接触外网或真实授权。</summary>
    private sealed class AddonCatalog(OnlineModVersion version) : IOnlineModCatalog
    {
        public List<(OnlineProjectId Project, string GameVersion, string Loader, int Page)> Queries { get; } = [];
        public List<string> Details { get; } = [];
        public int Resolutions { get; private set; }
        public bool HasMore { get; init; }
        public OnlineModVersion? Detail { get; set; }
        public Func<OnlineModVersion, OnlineModFile, OnlineModFile>? Resolve { get; set; }
        public Task<OnlineModSearchPage> SearchAsync(OnlineModQuery query, CancellationToken token = default) => throw new NotSupportedException();
        public Task<OnlineModProject> GetProjectAsync(OnlineProjectId project, CancellationToken token = default) => throw new NotSupportedException();
        public Task<OnlineModVersionPage> GetVersionsAsync(OnlineProjectId project, string gameVersion, string loader, int page = 0, CancellationToken token = default)
        { Queries.Add((project, gameVersion, loader, page)); return Task.FromResult(new OnlineModVersionPage([version], HasMore)); }
        public Task<OnlineModVersion> GetVersionAsync(OnlineProjectId project, string versionId, CancellationToken token = default)
        { Details.Add(versionId); return Task.FromResult(Detail ?? version); }
        public Task<OnlineModFile> ResolveDownloadAsync(OnlineModVersion current, OnlineModFile file, CancellationToken token = default)
        { ++Resolutions; return Task.FromResult(Resolve?.Invoke(current, file) ?? file); }
    }
}
