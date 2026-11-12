using System.Security.Cryptography;
using System.Text.Json;
using EML.Core.CurseForge;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;
using EML.Core.Modrinth;
using EML.Infrastructure.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Mods;

namespace EML.Tests;

public sealed partial class GtnhAddonServiceTests
{
    /// <summary>GitHub 指定标签只查该发行，ZIP 校验后送独立资源注册器，不能写入 mods。</summary>
    [Fact]
    public async Task FixedGithubResourceTagUsesResourceRegistrar()
    {
        await InitializeAsync(); var addon = GtnhAddonCatalog.Items.First(item => item.Provider == GtnhAddonProvider.Github &&
            item.Kind == GtnhAddonKind.ResourcePack && item.ReleaseTag is not null && item.CanQueryReleases);
        var fileName = (addon.AssetNamePrefix ?? "resource-") + "test.zip";
        var handler = new Handler(() => JsonSerializer.Serialize(new
        {
            id = 101, tag_name = addon.ReleaseTag, draft = false, body = "原作者说明", assets = new[] { new
            {
                id = 202, name = fileName, size = _bytes.Length, digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(_bytes)),
                browser_download_url = $"https://github.com/{addon.Repository}/releases/download/{Uri.EscapeDataString(addon.ReleaseTag!)}/{fileName}"
            } }
        }));
        using var http = new HttpClient(handler); var registrar = new ContentRegistrar();
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http, contentRegistrar: registrar);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, addon.Id));
        Assert.Equal(GtnhAddonKind.ResourcePack, release.Kind); Assert.Equal(addon.ReleaseTag, release.Tag);
        Assert.True((await service.InstallAsync(Instance, release, true)).Succeeded);
        Assert.Equal(GtnhAddonKind.ResourcePack, registrar.Kind); Assert.Equal(".minecraft/resourcepacks/" + fileName, registrar.Requirement!.Path);
        Assert.Equal(InstanceFileOrigin.GithubReleaseDownload, registrar.Requirement.Origin);
        Assert.Empty(ModManagementStore.Read(Instance, Manifest()).Files); Assert.Equal(1, handler.Calls);
    }

    /// <summary>Modrinth 光影完整展示平台标签，不因未声明 1.7.10 猜测不兼容或自动选取版本。</summary>
    [Fact]
    public async Task ModrinthShaderPreservesTagsAndUsesShaderDirectory()
    {
        await InitializeAsync(); var addon = GtnhAddonCatalog.Items.First(item => item.Provider == GtnhAddonProvider.Modrinth && item.Kind == GtnhAddonKind.Shader && item.CanQueryReleases);
        var api = new ContentModrinth(addon, _bytes); var registrar = new ContentRegistrar(); using var http = Client();
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http, modrinth: api, contentRegistrar: registrar);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, addon.Id));
        Assert.True(release.CanInstall); Assert.Empty(api.Filter!.GameVersions); Assert.Empty(api.Filter.Loaders);
        Assert.Contains("1.21.1", release.Notes); Assert.Contains("iris", release.Notes); Assert.Contains("不证明适配 GTNH", release.Notes);
        Assert.Equal("fixedversion", release.PlatformVersionId); Assert.Equal(1, api.DetailCalls);
        Assert.True((await service.InstallAsync(Instance, release, true)).Succeeded);
        Assert.Equal(GtnhAddonKind.Shader, registrar.Kind); Assert.Equal(".minecraft/shaderpacks/content.zip", registrar.Requirement!.Path);
        Assert.Equal(InstanceFileOrigin.ModrinthDownload, registrar.Requirement.Origin);
        Assert.Equal("fixedversion", registrar.Requirement.ModrinthOrigin!.VersionId);
        Assert.Empty(ModManagementStore.Read(Instance, Manifest()).Files);
    }

    /// <summary>Modrinth 项目或精确版本不能换成其他类型、项目、文件身份或非官方地址。</summary>
    [Theory]
    [InlineData("type")]
    [InlineData("project")]
    [InlineData("version")]
    [InlineData("url")]
    public async Task ModrinthResourceRejectsChangedIdentity(string change)
    {
        await InitializeAsync(); var addon = GtnhAddonCatalog.Items.First(item => item.Provider == GtnhAddonProvider.Modrinth && item.Kind == GtnhAddonKind.Shader && item.CanQueryReleases);
        var api = new ContentModrinth(addon, _bytes) { Change = change }; using var http = Client();
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http, modrinth: api, contentRegistrar: new ContentRegistrar());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetReleasesAsync(Instance, addon.Id));
    }

    /// <summary>资源下载需独立注册器；缺失服务时在下载前明确失败，绝不退回 mods 路径。</summary>
    [Fact]
    public async Task MissingContentRegistrarDoesNotFallBackToModInstallation()
    {
        await InitializeAsync(); var addon = GtnhAddonCatalog.Items.First(item => item.Provider == GtnhAddonProvider.Modrinth && item.Kind == GtnhAddonKind.Shader && item.CanQueryReleases);
        var api = new ContentModrinth(addon, _bytes); var downloader = new Downloader(_bytes); using var http = Client();
        using var service = new GtnhAddonService(_root, downloader, new LocalModManager(_root), http, modrinth: api);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, addon.Id));
        var result = await service.InstallAsync(Instance, release, true);
        Assert.False(result.Succeeded); Assert.Contains("安装服务尚未配置", result.Message); Assert.Equal(0, downloader.Calls);
    }

    /// <summary>CurseForge 资源必须匹配资源类别和固定项目文件，保留官方 SHA-1 并写入资源目录。</summary>
    [Fact]
    public async Task CurseForgeResourceKeepsFixedFileAndOfficialHash()
    {
        await InitializeAsync(); var addon = GtnhAddonCatalog.Items.First(item => item.Provider == GtnhAddonProvider.CurseForge && item.Kind == GtnhAddonKind.ResourcePack && item.CanQueryReleases);
        var api = new ContentCurseForge(addon, _bytes); var registrar = new ContentRegistrar(); using var http = Client();
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http, curseForge: api, contentRegistrar: registrar);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, addon.Id));
        Assert.True((await service.InstallAsync(Instance, release, true)).Succeeded);
        Assert.Equal(".minecraft/resourcepacks/content.zip", registrar.Requirement!.Path);
        Assert.Equal(InstanceFileOrigin.CurseForgeDownload, registrar.Requirement.Origin);
        Assert.Equal(addon.CurseForgeFileId ?? 456, registrar.Requirement.FileId);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(_bytes)), registrar.Requirement.Hashes["sha1"]);
        if (addon.CurseForgeFileId is not null) { Assert.Equal(1, api.FixedCalls); Assert.Equal(0, api.ListCalls); }
        else { Assert.Equal(0, api.FixedCalls); Assert.Equal(1, api.ListCalls); }
    }

    /// <summary>其他类型的 CurseForge 项目不能以资源包入口进入实例。</summary>
    [Fact]
    public async Task CurseForgeResourceRejectsModProjectType()
    {
        await InitializeAsync(); var addon = GtnhAddonCatalog.Items.First(item => item.Provider == GtnhAddonProvider.CurseForge && item.Kind == GtnhAddonKind.ResourcePack && item.CanQueryReleases);
        var api = new ContentCurseForge(addon, _bytes) { ClassId = 6 }; using var http = Client();
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http, curseForge: api, contentRegistrar: new ContentRegistrar());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetReleasesAsync(Instance, addon.Id));
        Assert.Equal(0, api.FixedCalls + api.ListCalls);
    }

    /// <summary>项目详情或文件元数据不能返回另一个同名资源项目。</summary>
    [Theory]
    [InlineData("project")]
    [InlineData("file")]
    public async Task CurseForgeResourceRejectsDifferentProject(string changed)
    {
        await InitializeAsync(); var addon = GtnhAddonCatalog.Items.First(item => item.Provider == GtnhAddonProvider.CurseForge && item.Kind == GtnhAddonKind.ResourcePack && item.CanQueryReleases);
        var api = new ContentCurseForge(addon, _bytes) { IdentityChange = changed }; using var http = Client();
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http, curseForge: api, contentRegistrar: new ContentRegistrar());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetReleasesAsync(Instance, addon.Id));
    }

    /// <summary>作者禁止分发的资源仅提供官方入口，不能以猜测 URL 或跨平台名称匹配自动替换。</summary>
    [Fact]
    public async Task RestrictedCurseForgeResourceStaysManual()
    {
        await InitializeAsync(); var addon = GtnhAddonCatalog.Items.First(item => item.Provider == GtnhAddonProvider.CurseForge && item.Kind == GtnhAddonKind.ResourcePack && item.CanQueryReleases);
        var api = new ContentCurseForge(addon, _bytes) { AllowDistribution = false }; var downloader = new Downloader(_bytes); using var http = Client();
        using var service = new GtnhAddonService(_root, downloader, new LocalModManager(_root), http, curseForge: api, contentRegistrar: new ContentRegistrar());
        var release = Assert.Single(await service.GetReleasesAsync(Instance, addon.Id));
        Assert.False(release.CanInstall); Assert.Empty(release.DownloadRequirement!.Downloads);
        Assert.False((await service.InstallAsync(Instance, release, true)).Succeeded); Assert.Equal(0, downloader.Calls);
    }

    /// <summary>资源文件未声明 1.7.10 时显示具体原因，不把无匹配或有摘要误报成可安装。</summary>
    [Fact]
    public async Task CurseForgeResourceWithoutLegacyVersionShowsBlockedReason()
    {
        await InitializeAsync(); var addon = GtnhAddonCatalog.Items.First(item => item.Provider == GtnhAddonProvider.CurseForge && item.Kind == GtnhAddonKind.ResourcePack && item.CanQueryReleases);
        var api = new ContentCurseForge(addon, _bytes) { GameVersions = ["1.20.1"] }; var downloader = new Downloader(_bytes); using var http = Client();
        using var service = new GtnhAddonService(_root, downloader, new LocalModManager(_root), http, curseForge: api, contentRegistrar: new ContentRegistrar());
        var release = Assert.Single(await service.GetReleasesAsync(Instance, addon.Id));
        Assert.False(release.CanInstall); Assert.Contains("未声明支持 Minecraft 1.7.10", release.Problem);
        Assert.False((await service.InstallAsync(Instance, release, true)).Succeeded); Assert.Equal(0, downloader.Calls);
    }

    /// <summary>已签发资源不能靠改名和改类型转入模组登记流程。</summary>
    [Fact]
    public async Task IssuedResourceCannotBeReclassifiedAsMod()
    {
        await InitializeAsync(); var addon = GtnhAddonCatalog.Items.First(item => item.Provider == GtnhAddonProvider.CurseForge && item.Kind == GtnhAddonKind.ResourcePack && item.CanQueryReleases);
        var api = new ContentCurseForge(addon, _bytes); var downloader = new Downloader(_bytes); using var http = Client();
        using var service = new GtnhAddonService(_root, downloader, new LocalModManager(_root), http, curseForge: api, contentRegistrar: new ContentRegistrar());
        var release = Assert.Single(await service.GetReleasesAsync(Instance, addon.Id));
        Assert.False((await service.InstallAsync(Instance, release with { Kind = GtnhAddonKind.Mod, FileName = "content.jar" }, true)).Succeeded);
        Assert.Equal(0, downloader.Calls); Assert.Empty(ModManagementStore.Read(Instance, Manifest()).Files);
    }

    /// <summary>记录独立资源安装参数，实际归档检查与持久化由资源注册器测试覆盖。</summary>
    private sealed class ContentRegistrar : IGtnhContentRegistrar
    {
        public InstanceFileRequirement? Requirement { get; private set; }
        public GtnhAddonKind Kind { get; private set; }
        /// <summary>确保服务交付已存在的暂存文件及明确资源目录。</summary>
        public Task RegisterDownloadedAsync(string instancePath, string stagedFile, InstanceFileRequirement requirement, GtnhAddonKind kind, CancellationToken token = default)
        { Assert.True(File.Exists(stagedFile)); Requirement = requirement; Kind = kind; return Task.CompletedTask; }
    }

    /// <summary>提供固定项目、版本与文件，测试类型过滤、完整性和查询边界。</summary>
    private sealed class ContentModrinth(GtnhAddon addon, byte[] bytes) : IModrinthApiClient
    {
        public string? Change { get; init; }
        public ModrinthVersionFilter? Filter { get; private set; }
        public int DetailCalls { get; private set; }
        public ModrinthRateLimit? LastRateLimit => null;
        /// <summary>返回与目录条目 slug 对应的固定平台项目。</summary>
        public Task<ModrinthProject> GetProjectAsync(string projectIdOrSlug, CancellationToken cancellationToken = default) => Task.FromResult(new ModrinthProject
        { Id = "fixedproject", Slug = addon.ModrinthProjectId!, ProjectType = Change == "type" ? "resourcepack" : "shader" });
        /// <summary>保留调用方筛选条件，让测试确认光影没有错误按旧 Minecraft 版本过滤。</summary>
        public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectIdOrSlug, ModrinthVersionFilter? filter = null, CancellationToken cancellationToken = default)
        { Filter = filter; return Task.FromResult<IReadOnlyList<ModrinthVersion>>([Version()]); }
        /// <summary>精确详情允许模拟项目、版本或下载地址被更换。</summary>
        public Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken = default)
        { ++DetailCalls; return Task.FromResult(Version() with { Id = Change == "version" ? "changed" : "fixedversion", ProjectId = Change == "project" ? "otherproject" : "fixedproject" }); }
        /// <summary>构造带官方 SHA-512 的资源 ZIP，不要求其平台游戏版本标签包含 1.7.10。</summary>
        private ModrinthVersion Version() => new()
        {
            Id = "fixedversion", ProjectId = "fixedproject", VersionNumber = "1", VersionType = "release", GameVersions = ["1.21.1"], Loaders = ["iris"],
            Files = [new() { Filename = "content.zip", Size = bytes.Length, Primary = true,
                Url = Change == "url" ? "https://example.com/content.zip" : "https://cdn.modrinth.com/data/fixedproject/versions/fixedversion/content.zip",
                Hashes = new Dictionary<string, string> { ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(bytes)) } }]
        };
        /// <summary>推荐资源读取不会执行模糊搜索。</summary>
        public Task<ModrinthSearchResult> SearchAsync(ModrinthSearchOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>资源不会通过其他平台文件的哈希自动替换来源。</summary>
        public Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(IReadOnlyList<string> hashes, string algorithm = "sha1", CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>模拟官方资源项目，保留固定文件查询与分页调用次数。</summary>
    private sealed class ContentCurseForge(GtnhAddon addon, byte[] bytes) : ICurseForgeApiClient
    {
        public int ClassId { get; init; } = 12;
        public string? IdentityChange { get; init; }
        public bool AllowDistribution { get; init; } = true;
        public IReadOnlyList<string> GameVersions { get; init; } = ["1.7.10"];
        public int FixedCalls { get; private set; }
        public int ListCalls { get; private set; }
        public CurseForgeRateLimit? LastRateLimit => null;
        /// <summary>提供固定资源分类和作者分发选择。</summary>
        public Task<CurseForgeMod> GetModAsync(long projectId, CancellationToken cancellationToken = default) => Task.FromResult(new CurseForgeMod
        { Id = IdentityChange == "project" ? addon.CurseForgeProjectId!.Value + 1 : addon.CurseForgeProjectId!.Value, ClassId = ClassId, GameId = 432, IsAvailable = true, AllowModDistribution = AllowDistribution });
        /// <summary>记录精确文件查询，不能换成项目最新文件。</summary>
        public Task<CurseForgeFile> GetFileAsync(long projectId, long fileId, CancellationToken cancellationToken = default)
        { ++FixedCalls; Assert.Equal(addon.CurseForgeFileId, fileId); return Task.FromResult(File()); }
        /// <summary>资源分页限定 1.7.10，不使用 Forge 模组加载器筛选。</summary>
        public Task<CurseForgeFilePage> GetModFilesAsync(long projectId, CurseForgeFileFilter filter, CancellationToken cancellationToken = default)
        { ++ListCalls; Assert.Equal("1.7.10", filter.GameVersion); Assert.Null(filter.ModLoaderType); return Task.FromResult(new CurseForgeFilePage { Data = [File()], Pagination = new() { ResultCount = 1, TotalCount = 1 } }); }
        /// <summary>构造官方 SHA-1 对应的唯一资源文件。</summary>
        private CurseForgeFile File() => new()
        {
            Id = addon.CurseForgeFileId ?? 456, ModId = IdentityChange == "file" ? addon.CurseForgeProjectId!.Value + 1 : addon.CurseForgeProjectId!.Value, GameId = 432, IsAvailable = true,
            FileName = "content.zip", FileLength = bytes.Length, DownloadUrl = "https://edge.forgecdn.net/files/0/456/content.zip", ReleaseType = 1,
            GameVersions = GameVersions, Hashes = [new() { Algo = 1, Value = Convert.ToHexStringLower(SHA1.HashData(bytes)) }]
        };
        /// <summary>固定资源读取不使用批量文件查询。</summary>
        public Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(IReadOnlyList<long> fileIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>固定资源读取不使用批量项目查询。</summary>
        public Task<IReadOnlyList<CurseForgeMod>> GetModsAsync(IReadOnlyList<long> projectIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>固定资源读取不执行名称搜索。</summary>
        public Task<CurseForgeSearchResult> SearchAsync(CurseForgeSearchOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
