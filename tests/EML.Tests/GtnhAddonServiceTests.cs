using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;
using EML.Core.Minecraft.Migration;
using EML.Core.Minecraft.Mods;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Tests;

/// <summary>用临时 GTNH 实例和可控 GitHub 响应测试真实下载、完整性与管理链，不访问玩家数据。</summary>
public sealed class GtnhAddonServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eml-gtnh-addon-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _bytes = ModBytes();
    private string Instance => Path.Combine(_root, "instances", "GTNH");

    /// <summary>测试拥有独立随机目录，清理范围仅限本测试创建的数据。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    /// <summary>固定发行身份和官方 SHA-256 成为有效来源，下载后不伪装成本地导入。</summary>
    [Fact]
    public async Task InstallsVerifiedReleaseAndKeepsOfficialSource()
    {
        await InitializeAsync(); using var http = Client(); var downloader = new Downloader(_bytes);
        var manager = new LocalModManager(_root); using var service = new GtnhAddonService(_root, downloader, manager, http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        var result = await service.InstallAsync(Instance, release, true);
        Assert.True(result.Succeeded, result.Message); Assert.Equal(1, downloader.Calls);
        var baseline = Assert.Single(InstanceEffectiveRequirements.Build(Instance, Manifest()).Requirements);
        Assert.Equal(InstanceFileOrigin.GithubReleaseDownload, baseline.Origin);
        Assert.Equal(101, baseline.GithubReleaseOrigin!.ReleaseId); Assert.Equal(202, baseline.GithubReleaseOrigin.AssetId);
        Assert.Equal(release.Repository, baseline.GithubReleaseOrigin.Repository);
        Assert.Equal(release.Sha256, Assert.Single(baseline.Hashes).Value);
        Assert.True(await InstanceRuntimeInstaller.ExistingFileMatchesAsync(Path.Combine(Instance, baseline.Path!), baseline, default));
        Assert.True(Assert.Single((await manager.ListAsync(Instance)).Files).CanManage);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, ".eml-runtime", "gtnh-addons"), "*", SearchOption.AllDirectories));
    }

    /// <summary>名字相同的普通 Forge 实例不能请求推荐发行文件或作为安装目标。</summary>
    [Fact]
    public async Task NonGtnhTargetIsRejectedBeforeNetwork()
    {
        await InitializeAsync(); File.Delete(Path.Combine(Instance, ".minecraft", "config", "txloader", "load", "mainmenu", "version.txt"));
        var handler = new Handler(() => Payload()); using var http = new HttpClient(handler);
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetReleasesAsync(Instance, service.Addons[0].Id));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>GitHub 缺少官方摘要时保留说明，但确认按钮不能绕过内容完整性要求。</summary>
    [Fact]
    public async Task MissingOfficialDigestCannotBeInstalled()
    {
        await InitializeAsync(); using var http = Client(digest: false); var downloader = new Downloader(_bytes);
        using var service = new GtnhAddonService(_root, downloader, new LocalModManager(_root), http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        Assert.False(release.CanInstall); Assert.Contains("SHA-256", release.Problem);
        Assert.False((await service.InstallAsync(Instance, release, true)).Succeeded); Assert.Equal(0, downloader.Calls);
    }

    /// <summary>下载器即使谎称成功，登记前仍独立拒绝损坏内容和错误大小。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptOrTruncatedDownloadNeverBecomesManaged(bool truncated)
    {
        await InitializeAsync(); using var http = Client(); var bytes = _bytes.ToArray();
        if (truncated) bytes = bytes[..^1]; else bytes[^1] ^= 1;
        var manager = new LocalModManager(_root); using var service = new GtnhAddonService(_root, new Downloader(bytes), manager, http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        Assert.False((await service.InstallAsync(Instance, release, true)).Succeeded);
        Assert.Empty(ModManagementStore.Read(Instance, Manifest()).Files);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Instance, ".minecraft", "mods")));
    }

    /// <summary>界面持有的发行记录不能被改写 URL、摘要或资产身份后拿去安装。</summary>
    [Theory]
    [InlineData("url")]
    [InlineData("hash")]
    [InlineData("asset")]
    public async Task ModifiedReleaseIsRejectedBeforeDownload(string field)
    {
        await InitializeAsync(); using var http = Client(); var downloader = new Downloader(_bytes);
        using var service = new GtnhAddonService(_root, downloader, new LocalModManager(_root), http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        release = field switch { "url" => release with { DownloadUrl = "https://example.com/file.jar" },
            "hash" => release with { Sha256 = new('0', 64) }, _ => release with { AssetId = 999 } };
        Assert.False((await service.InstallAsync(Instance, release, true)).Succeeded); Assert.Equal(0, downloader.Calls);
    }

    /// <summary>明确兼容确认和固定目标缺一不可，不能把另一实例的选择重放到新实例。</summary>
    [Fact]
    public async Task ConfirmationAndMatchingTargetAreRequired()
    {
        await InitializeAsync(); var other = Path.Combine(_root, "instances", "Other"); await InitializeAsync(other);
        using var http = Client(); var downloader = new Downloader(_bytes);
        using var service = new GtnhAddonService(_root, downloader, new LocalModManager(_root), http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        Assert.False((await service.InstallAsync(Instance, release, false)).Succeeded);
        Assert.False((await service.InstallAsync(other, release, true)).Succeeded);
        Assert.Equal(0, downloader.Calls);
    }

    /// <summary>发行资产过滤拒绝源码包与任意外站文件，缓存只复用短期只读元数据。</summary>
    [Fact]
    public async Task UnsafeAndDeveloperAssetsAreFilteredAndMetadataIsCached()
    {
        await InitializeAsync(); var handler = new Handler(() => Payload(extraAssets: true)); using var http = new HttpClient(handler);
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http);
        Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id)); Assert.Equal(1, handler.Calls);
    }

    /// <summary>预先取消返回明确取消，不开始网络传输或提交管理记录。</summary>
    [Fact]
    public async Task CanceledInstallDoesNotDownload()
    {
        await InitializeAsync(); using var http = Client(); var downloader = new Downloader(_bytes);
        using var service = new GtnhAddonService(_root, downloader, new LocalModManager(_root), http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.True((await service.InstallAsync(Instance, release, true, token: cancellation.Token)).Cancelled);
        Assert.Equal(0, downloader.Calls);
    }

    /// <summary>备份往返与禁用、回收、恢复均保存固定 GitHub 来源；不会被重新归类为用户本地文件。</summary>
    [Theory]
    [InlineData(LocalModState.Enabled)]
    [InlineData(LocalModState.Disabled)]
    [InlineData(LocalModState.Removed)]
    public async Task GithubSourceSurvivesManagementAndBackup(LocalModState state)
    {
        await InitializeAsync(); using var http = Client(); var manager = new LocalModManager(_root);
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), manager, http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        Assert.True((await service.InstallAsync(Instance, release, true)).Succeeded);
        const string relative = ".minecraft/mods/addon.jar";
        if (state == LocalModState.Disabled) await manager.SetEnabledAsync(Instance, relative, false);
        if (state == LocalModState.Removed) await manager.RemoveAsync(Instance, relative);
        var archive = Path.Combine(_root, "backup.zip"); await new MinecraftInstanceManager(_root).ExportAsync(Instance, archive);
        var restored = await new MinecraftInstanceImporter(_root).ImportAsync(archive, "Restored");
        var record = Assert.Single(restored.Manifest.ManagedMods!);
        Assert.Equal(state, record.State); Assert.Equal(InstanceFileOrigin.GithubReleaseDownload, record.Baseline.Origin);
        Assert.Equal(release.Repository, record.Baseline.GithubReleaseOrigin!.Repository);
        Assert.Equal(release.Sha256, record.Baseline.Hashes["sha256"]);
        Assert.Equal(state, Assert.Single((await manager.ListAsync(restored.InstancePath)).Files).State);
        if (state != LocalModState.Removed) await manager.SetEnabledAsync(restored.InstancePath, relative, state != LocalModState.Enabled);
        if (state == LocalModState.Removed) { await manager.RestoreAsync(Instance, relative); Assert.True(File.Exists(Path.Combine(Instance, relative))); }
    }

    /// <summary>恢复下载在模组禁用状态下沿用同一官方摘要和禁用路径，不重新启用模组。</summary>
    [Fact]
    public async Task MissingDisabledGithubModCanBeRepairedWithoutEnabling()
    {
        await InitializeAsync(); using var http = Client(); var manager = new LocalModManager(_root); var downloader = new Downloader(_bytes);
        using var service = new GtnhAddonService(_root, downloader, manager, http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        Assert.True((await service.InstallAsync(Instance, release, true)).Succeeded);
        const string relative = ".minecraft/mods/addon.jar"; await manager.SetEnabledAsync(Instance, relative, false);
        File.Delete(Path.Combine(Instance, relative + ".disabled"));
        var checker = new MinecraftInstanceChecker(_root);
        var result = await new MinecraftInstanceDownloader(_root, checker, downloader, zipInstaller: new EmptyInstaller()).DownloadMissingAsync(Instance);
        Assert.True(File.Exists(Path.Combine(Instance, relative + ".disabled")));
        Assert.False(File.Exists(Path.Combine(Instance, relative)));
        Assert.Equal(LocalModState.Disabled, Assert.Single((await manager.ListAsync(Instance)).Files).State);
    }

    /// <summary>GTNH 更新迁移保存 GitHub 固定来源与禁用状态，补全检查仍使用官方摘要。</summary>
    [Fact]
    public async Task GithubSourceSurvivesGtnhMigration()
    {
        await InitializeAsync(); var target = Path.Combine(_root, "instances", "New"); await InitializeAsync(target);
        using var http = Client(); var manager = new LocalModManager(_root);
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), manager, http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        Assert.True((await service.InstallAsync(Instance, release, true)).Succeeded);
        await manager.SetEnabledAsync(Instance, ".minecraft/mods/addon.jar", false);
        var migration = new GtnhInstanceMigrationService(_root);
        var plan = await migration.PreviewAsync(new(Instance, target, GtnhMigrationMode.FullClient,
            ["mod:mods/addon.jar.disabled"], true, true) { ExtraModsRiskAccepted = true });
        Assert.Equal("Completed", (await migration.MigrateAsync(plan, false)).Status);
        var manifest = InstanceManifestStore.Read(Path.Combine(target, InstanceImportManifest.FileName));
        var baseline = Assert.Single(MigrationDataStore.Read(target, manifest).Files).OnlineModBaseline!;
        Assert.Equal(InstanceFileOrigin.GithubReleaseDownload, baseline.Origin);
        Assert.Equal(release.Sha256, Assert.Single(baseline.Hashes).Value);
        Assert.Equal(release.AssetId, baseline.GithubReleaseOrigin!.AssetId);
        Assert.Equal(LocalModState.Disabled, Assert.Single((await manager.ListAsync(target)).Files).State);
        var entry = Assert.Single(InstanceEffectiveRequirements.Build(target, manifest).Requirements);
        Assert.Equal(InstanceFileState.Verified, await InstanceContentVerifier.CheckExistingAsync(target, manifest, entry, default));
        await manager.SetEnabledAsync(target, ".minecraft/mods/addon.jar.disabled", true);
        Assert.Equal(InstanceFileOrigin.GithubReleaseDownload,
            Assert.Single(InstanceBackupManifest.Create(target, manifest).Manifest.ManagedMods!).Baseline.Origin);
    }

    /// <summary>导入记录在预览后改变时，旧发行选择不再有效，避免下载进已被替换的实例。</summary>
    [Fact]
    public async Task ChangedManifestInvalidatesIssuedRelease()
    {
        await InitializeAsync(); using var http = Client(); var downloader = new Downloader(_bytes);
        using var service = new GtnhAddonService(_root, downloader, new LocalModManager(_root), http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        await InstanceManifestStore.WriteAsync(Path.Combine(Instance, InstanceImportManifest.FileName), Manifest() with { Name = "Changed" }, default);
        Assert.False((await service.InstallAsync(Instance, release, true)).Succeeded);
        Assert.Equal(0, downloader.Calls);
    }

    /// <summary>原包相同文件受到保护，下载器完成也不能覆盖文件或把它改成玩家新增来源。</summary>
    [Fact]
    public async Task ExistingPackModIsPreservedWhenInstallingAddon()
    {
        await InitializeAsync(); var path = Path.Combine(Instance, ".minecraft", "mods", "addon.jar");
        await File.WriteAllBytesAsync(path, _bytes);
        var manifest = Manifest() with { Files = [new()
        {
            Path = ".minecraft/mods/addon.jar", Source = InstanceFileSource.Embedded,
            Origin = InstanceFileOrigin.EmbeddedOverride, ArchiveEntry = ".minecraft/mods/addon.jar",
            Size = _bytes.Length, Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(_bytes)) }
        }] };
        await InstanceManifestStore.WriteAsync(Path.Combine(Instance, InstanceImportManifest.FileName), manifest, default);
        using var http = Client(); using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        Assert.False((await service.InstallAsync(Instance, release, true)).Succeeded);
        Assert.Equal(_bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(ModManagementStore.Read(Instance, Manifest()).Files);
    }

    /// <summary>不能混合其他平台摘要、身份或任意下载地址，备份验证与登记使用同一规则。</summary>
    [Theory]
    [InlineData("url")]
    [InlineData("hash")]
    [InlineData("origin")]
    [InlineData("platform")]
    public async Task GithubBaselineRejectsMixedSources(string mismatch)
    {
        await InitializeAsync(); using var http = Client(); var manager = new LocalModManager(_root);
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), manager, http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        Assert.True((await service.InstallAsync(Instance, release, true)).Succeeded);
        var baseline = Assert.Single(InstanceEffectiveRequirements.Build(Instance, Manifest()).Requirements);
        baseline = mismatch switch
        {
            "url" => baseline with { Downloads = ["https://example.com/addon.jar"] },
            "hash" => baseline with { Hashes = new Dictionary<string, string> { ["sha256"] = release.Sha256, ["sha1"] = new('0', 40) } },
            "origin" => baseline with { Origin = InstanceFileOrigin.ModrinthDownload },
            _ => baseline with { ProjectId = 123, FileId = 456 }
        };
        Assert.Throws<InvalidDataException>(() => InstanceFileIntegrity.HashesFor(baseline));
        Assert.Throws<InvalidDataException>(() => ModManagementStore.ValidateOnlineBaseline(baseline));
    }

    /// <summary>创建带真实 GTNH 识别证据的最小实例，不把实例名称当成依据。</summary>
    private async Task InitializeAsync(string? root = null)
    {
        root ??= Instance; var game = Path.Combine(root, ".minecraft"); Directory.CreateDirectory(Path.Combine(game, "mods"));
        foreach (var (relative, content) in new[] { ("config/txloader/load/mainmenu/version.txt", "GTNH 2.9.0"),
            ("config/GregTech/GT5U.cfg", "test"), ("config/bartworks.cfg", "test") })
        { var path = Path.Combine(game, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
        await InstanceManifestStore.WriteAsync(Path.Combine(root, InstanceImportManifest.FileName), new()
        { Name = "GTNH", SourceFormat = "Zip", MinecraftVersion = "1.7.10", GameDirectory = ".minecraft", Components = [new("forge", "10.13.4.1614")] }, default);
    }
    /// <summary>读取测试当前有效导入记录。</summary>
    private InstanceImportManifest Manifest() => InstanceManifestStore.Read(Path.Combine(Instance, InstanceImportManifest.FileName));
    /// <summary>为测试返回可控 JSON，不接触真实 GitHub 服务。</summary>
    private HttpClient Client(bool digest = true) => new(new Handler(() => Payload(digest)));
    /// <summary>构造平台原始字段，官方摘要来自本测试的确定内容。</summary>
    private string Payload(bool digest = true, bool extraAssets = false)
    {
        var repo = GtnhAddonCatalog.Items[0].Repository;
        object Asset(string name, long id, string? url = null) => new { id, name, size = _bytes.Length,
            browser_download_url = url ?? $"https://github.com/{repo}/releases/download/v1/{name}",
            digest = digest ? "sha256:" + Convert.ToHexStringLower(SHA256.HashData(_bytes)) : null };
        var assets = new List<object> { Asset("addon.jar", 202) };
        if (extraAssets) { assets.Add(Asset("addon-sources.jar", 203)); assets.Add(Asset("other.jar", 204, "https://example.com/other.jar")); }
        return JsonSerializer.Serialize(new[] { new { id = 101, tag_name = "v1", draft = false, prerelease = false,
            published_at = "2026-11-09T00:00:00Z", body = "适配说明", assets } });
    }
    /// <summary>生成可供真实管理器读取的 Forge 模组归档，而不是伪造扩展名的文本。</summary>
    private static byte[] ModBytes()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("mcmod.info").Open(), new UTF8Encoding(false)))
            writer.Write("[{\"modid\":\"testaddon\",\"name\":\"Test addon\",\"version\":\"1\"}]");
        return stream.ToArray();
    }
    /// <summary>受控 HTTP 保留请求次数，以验证短缓存和拒绝目标发生在网络调用前。</summary>
    private sealed class Handler(Func<string> body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { ++Calls; token.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body()) }); }
    }
    /// <summary>测试下载器不替代生产校验，故意让服务和登记层自行核对字节。</summary>
    private sealed class Downloader(byte[] bytes) : IFileDownloader
    {
        public int Calls { get; private set; }
        public async Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        { ++Calls; Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!); await File.WriteAllBytesAsync(request.DestinationPath, bytes, cancellationToken); return new(request.DestinationPath, bytes.Length, true); }
    }
    /// <summary>隔离运行依赖安装，只测试已有通用补全器如何恢复玩家新增的 GitHub 模组。</summary>
    private sealed class EmptyInstaller : IPrismInstanceInstaller
    {
        public Task<PrismInstallResult> InstallAsync(string instancePath, IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PrismInstallResult { SourceFormat = "Zip" });
    }
}
