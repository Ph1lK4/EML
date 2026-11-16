using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Tests;

public sealed partial class GtnhAddonServiceTests
{
    /// <summary>主仓库同时发布主包、界面包、附加包与前置模组，只展示属于主包的历代命名。</summary>
    [Fact]
    public async Task ModernityMainPackDoesNotIncludeUiAddonsOrLibraries()
    {
        await InitializeAsync();
        var addon = GtnhAddonCatalog.Items.Single(item => item.Id == "modernity-gtnh");
        var expected = new[] { "Modernity-29x-1650c31.zip", "Modernity-2.9.x-fa35a3d.zip",
            "Modernity-GTNH-2026-09-21.zip", "Modernity-GTNH-v1.5.9.zip" };
        var names = expected.Concat(["Modernity-GTNH-UI.zip", "Modernity-GTNH-Dark-UI.zip",
            "Modernity-GTNHAddon-OutlinedOres.zip", "MyCTMLib-1.4.4-290.jar",
            "Modernity-29x-sources.zip", "Other.zip"]).ToArray();
        using var http = new HttpClient(new Handler(() => ResourceReleasePayload(addon, names, _bytes)));
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http);
        var releases = await service.GetReleasesAsync(Instance, addon.Id);
        Assert.Equal(expected.Order(), releases.Select(file => file.FileName).Order());
        Assert.All(releases, release => Assert.True(release.CanInstall));
    }

    /// <summary>两个界面入口各自保留具名包和历史 default.zip，不接收另一种界面或无关 ZIP。</summary>
    [Theory]
    [InlineData("modernity-ui", "Modernity-GTNH-UI")]
    [InlineData("modernity-dark-ui", "Modernity-GTNH-Dark-UI")]
    public async Task ModernityUiKeepsOnlyItsOwnAssetsIncludingLegacyNames(string id, string prefix)
    {
        await InitializeAsync();
        var addon = GtnhAddonCatalog.Items.Single(item => item.Id == id);
        var other = id == "modernity-ui" ? "Modernity-GTNH-Dark-UI.zip" : "Modernity-GTNH-UI.zip";
        var expected = new[] { prefix + "-2026-11-10.zip", prefix + ".zip", "default.zip" };
        using var http = new HttpClient(new Handler(() => ResourceReleasePayload(addon,
            [.. expected, other, "Modernity-29x-main.zip", "default-sources.zip", "unrelated.zip"], _bytes)));
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http);
        Assert.Equal(expected.Order(), (await service.GetReleasesAsync(Instance, id)).Select(file => file.FileName).Order());
    }

    /// <summary>三种资源走真实登记器，最终 ZIP 与官方摘要不变，来源能被有效需求和检查器读取。</summary>
    [Theory]
    [InlineData("modernity-gtnh", "Modernity-29x-1650c31.zip")]
    [InlineData("modernity-ui", "Modernity-GTNH-UI-2026-11-10.zip")]
    [InlineData("modernity-dark-ui", "Modernity-GTNH-Dark-UI-2026-11-10.zip")]
    public async Task ModernityPackInstallsWithOfficialIdentityAndIntegrity(string id, string name)
    {
        await InitializeAsync();
        var bytes = ResourcePackBytes();
        var addon = GtnhAddonCatalog.Items.Single(item => item.Id == id);
        using var http = new HttpClient(new Handler(() => ResourceReleasePayload(addon, [name], bytes)));
        using var service = new GtnhAddonService(_root, new Downloader(bytes), new LocalModManager(_root), http,
            contentRegistrar: new GtnhContentRegistrar(_root));
        var release = Assert.Single(await service.GetReleasesAsync(Instance, id));
        var result = await service.InstallAsync(Instance, release, true);
        Assert.True(result.Succeeded, result.Message);
        var baseline = Assert.Single(InstanceEffectiveRequirements.Build(Instance, Manifest()).Requirements);
        Assert.Equal(".minecraft/resourcepacks/" + name, baseline.Path);
        Assert.Equal(InstanceFileOrigin.GithubReleaseDownload, baseline.Origin);
        Assert.Equal(addon.Repository, baseline.GithubReleaseOrigin!.Repository);
        Assert.Equal(name, baseline.GithubReleaseOrigin.FileName);
        Assert.Equal(release.DownloadUrl, Assert.Single(baseline.Downloads));
        Assert.Equal(release.Sha256, Assert.Single(baseline.Hashes).Value);
        var destination = Path.Combine(Instance, baseline.Path!);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.True(await InstanceRuntimeInstaller.ExistingFileMatchesAsync(destination, baseline, default));
        Assert.Empty(ModManagementStore.Read(Instance, Manifest()).Files);
    }

    /// <summary>名称匹配不替代官方摘要；Modernity 也不能绕过无摘要或下载损坏的拒绝逻辑。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModernityPackStillRejectsMissingOrMismatchedDigest(bool corrupted)
    {
        await InitializeAsync(); var bytes = ResourcePackBytes();
        var addon = GtnhAddonCatalog.Items.Single(item => item.Id == "modernity-gtnh");
        using var http = new HttpClient(new Handler(() => ResourceReleasePayload(addon,
            ["Modernity-29x-1650c31.zip"], bytes, includeDigest: corrupted)));
        var downloaded = bytes.ToArray(); downloaded[^1] ^= 1;
        var downloader = new Downloader(downloaded);
        using var service = new GtnhAddonService(_root, downloader, new LocalModManager(_root), http,
            contentRegistrar: new GtnhContentRegistrar(_root));
        var release = Assert.Single(await service.GetReleasesAsync(Instance, addon.Id));
        Assert.False((await service.InstallAsync(Instance, release, true)).Succeeded);
        Assert.Equal(corrupted ? 1 : 0, downloader.Calls);
        Assert.Empty(AddedContentStore.Read(Instance, Manifest()).Files);
    }

    /// <summary>共用仓库的独立纹理不能互串，GTNH 构建中间产物也不能作为成品模组下载。</summary>
    [Theory]
    [InlineData("shadow-ui", "Shadow.UI.v5.48.zip", "Just-InvisHealingAxe.zip")]
    [InlineData("outlined-ores-modern", "GTNH-OutlinedOres.2.1.-.Modernity.version.zip", "GTNH-OutlinedOres.2.1.zip")]
    [InlineData("gtnh-outlined-ores", "GTNH-OutlinedOres.2.1.zip", "GTNH-OutlinedOres.2.1.-.Modernity.version.zip")]
    [InlineData("myctmlib", "MyCTMLib-1.4.4-290.jar", "MyCTMLib-1.4.4-290-predowngrade.jar")]
    [InlineData("better-line-break", "BetterLineBreak-1.7.10.Forge.-1.2.1.jar", "BetterLineBreak-1.12.2.Forge.-1.2.1.jar")]
    public async Task SharedRepositoryVariantsAndBuildArtifactsStaySeparated(string id, string expected, string other)
    {
        await InitializeAsync();
        var addon = GtnhAddonCatalog.Items.Single(item => item.Id == id);
        using var http = new HttpClient(new Handler(() => ResourceReleasePayload(addon, [expected, other], _bytes)));
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, id));
        Assert.Equal(expected, release.FileName);
        Assert.True(release.CanInstall);
    }

    /// <summary>模拟固定仓库的原始发行响应，所有资产都使用自身的大小、摘要和官方 URL。</summary>
    private static string ResourceReleasePayload(GtnhAddon addon, string[] names, byte[] bytes, bool includeDigest = true) =>
        JsonSerializer.Serialize(new[] { new { id = 101, tag_name = "v1", draft = false,
            published_at = "2026-11-10T00:00:00Z", assets = names.Select((name, index) => new
            {
                id = 202 + index, name, size = bytes.Length,
                digest = includeDigest ? "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)) : null,
                browser_download_url = $"https://github.com/{addon.Repository}/releases/download/v1/{name}"
            }).ToArray() } });

    /// <summary>生成 Minecraft 可识别的最小资源 ZIP，实际注册器仍会检查包结构。</summary>
    private static byte[] ResourcePackBytes()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("pack.mcmeta").Open()))
            writer.Write("{\"pack\":{\"pack_format\":1,\"description\":\"Test resource pack\"}}");
        return stream.ToArray();
    }
}
