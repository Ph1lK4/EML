using System.Security.Cryptography;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Mods;

namespace EML.Tests;

public sealed partial class GtnhAddonServiceTests
{
    /// <summary>生产下载入口将 default.zip 放到仓库专属别名，但交给登记层的官方文件身份不变。</summary>
    [Fact]
    public async Task GithubDefaultResourceDownloadUsesAliasWithoutChangingOrigin()
    {
        await InitializeAsync();
        var addon = Assert.Single(GtnhAddonCatalog.Items, item => item.Id == "modernity-dark-ui");
        var url = $"https://github.com/{addon.Repository}/releases/download/v1/default.zip";
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(_bytes));
        var handler = new Handler(() => JsonSerializer.Serialize(new[] { new
        {
            id = 101, tag_name = "v1", draft = false, prerelease = false, body = "适配说明",
            assets = new[] { new { id = 202, name = "default.zip", size = _bytes.Length,
                digest = "sha256:" + sha256, browser_download_url = url } }
        } }));
        using var http = new HttpClient(handler);
        var registrar = new ContentRegistrar();
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root),
            http, contentRegistrar: registrar);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, addon.Id));

        var result = await service.InstallAsync(Instance, release, true);

        Assert.True(result.Succeeded, result.Message);
        var baseline = Assert.IsType<InstanceFileRequirement>(registrar.Requirement);
        Assert.Equal(".minecraft/resourcepacks/eml-modernitygtnh--modernity-gtnh-dark-ui-202.zip", baseline.Path);
        Assert.Equal(Path.GetFullPath(Path.Combine(Instance, baseline.Path!)), result.DestinationPath);
        Assert.Equal("default.zip", release.FileName);
        Assert.Equal("default.zip", baseline.GithubReleaseOrigin!.FileName);
        Assert.Equal(addon.Repository, baseline.GithubReleaseOrigin.Repository);
        Assert.Equal(202, baseline.GithubReleaseOrigin.AssetId);
        Assert.Equal(url, Assert.Single(baseline.Downloads));
        Assert.Equal(sha256, Assert.Single(baseline.Hashes).Value);
        Assert.Equal(GtnhAddonKind.ResourcePack, registrar.Kind);
    }
}
