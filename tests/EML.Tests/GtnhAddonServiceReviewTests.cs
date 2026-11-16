using System.Net;
using System.Text.Json;
using EML.Core.Downloads;
using EML.Core.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Mods;

namespace EML.Tests;

public sealed partial class GtnhAddonServiceTests
{
    /// <summary>仓库跳转不能继续解析为原作者文件，即使返回的 JSON 看起来仍然有效。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GithubRepositoryRedirectIsRejected(bool alreadyFollowed)
    {
        await InitializeAsync(); using var http = new HttpClient(new ReviewHandler(request => new HttpResponseMessage(
            alreadyFollowed ? HttpStatusCode.OK : HttpStatusCode.MovedPermanently)
        {
            RequestMessage = alreadyFollowed ? new(HttpMethod.Get, "https://api.github.com/repos/other/owner/releases") : request,
            Content = new StringContent(Payload())
        }));
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http);
        if (alreadyFollowed) await Assert.ThrowsAsync<InvalidDataException>(() => service.GetReleasesAsync(Instance, service.Addons[0].Id));
        else await Assert.ThrowsAsync<HttpRequestException>(() => service.GetReleasesAsync(Instance, service.Addons[0].Id));
    }

    /// <summary>固定标签响应不能被平台列表中其他发行替换。</summary>
    [Fact]
    public async Task GithubFixedTagCannotReturnAnotherRelease()
    {
        await InitializeAsync(); var addon = GtnhAddonCatalog.Items.First(item => item.Provider == GtnhAddonProvider.Github && item.ReleaseTag is not null && item.CanQueryReleases);
        using var http = new HttpClient(new ReviewHandler(request =>
        {
            Assert.Contains("/releases/tags/", request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id = 101, tag_name = "different", assets = Array.Empty<object>() })) };
        }));
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetReleasesAsync(Instance, addon.Id));
    }

    /// <summary>模组条目不会因共享 GitHub 来源已支持 ZIP 就把资源文件展示成可安装模组。</summary>
    [Fact]
    public async Task GithubModEntryNeverSelectsZipAsset()
    {
        await InitializeAsync(); using var http = new HttpClient(new Handler(() => Payload().Replace("addon.jar", "addon.zip", StringComparison.Ordinal)));
        using var service = new GtnhAddonService(_root, new Downloader(_bytes), new LocalModManager(_root), http);
        Assert.Empty(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
    }

    /// <summary>展示后的平台校验集合不能替换成另一份同名文件要求。</summary>
    [Fact]
    public async Task IssuedCurseForgeRequirementCannotBeReplaced()
    {
        await InitializeAsync(); using var http = Client(); var downloader = new Downloader(_bytes);
        using var service = new GtnhAddonService(_root, downloader, new LocalModManager(_root), http, new AddonCatalog(CurseForgeVersion()));
        var release = Assert.Single(await service.GetReleasesAsync(Instance, CurseForgeAddon.Id));
        var modified = release with { DownloadRequirement = release.DownloadRequirement! with
        { Hashes = new Dictionary<string, string> { ["sha1"] = new('0', 40) } } };
        Assert.False((await service.InstallAsync(Instance, modified, true)).Succeeded); Assert.Equal(0, downloader.Calls);
    }

    /// <summary>传给下载器的 GitHub 摘要同样不可变，不能在传输期间改写服务随后核验的依据。</summary>
    [Fact]
    public async Task GithubDownloaderCannotRewriteVerificationHash()
    {
        await InitializeAsync(); using var http = Client(); var downloader = new ReadOnlyHashDownloader(_bytes);
        using var service = new GtnhAddonService(_root, downloader, new LocalModManager(_root), http);
        var release = Assert.Single(await service.GetReleasesAsync(Instance, service.Addons[0].Id));
        Assert.True((await service.InstallAsync(Instance, release, true)).Succeeded); Assert.True(downloader.MutationRejected);
    }

    /// <summary>显式模拟响应 URL，覆盖 HttpClient 已经跟随重定向的防御检查。</summary>
    private sealed class ReviewHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        /// <summary>仅返回受控内存响应，不进行实际网络访问。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    /// <summary>模拟下载器尝试更改校验集合，确认该操作不能改变最终登记依据。</summary>
    private sealed class ReadOnlyHashDownloader(byte[] bytes) : IFileDownloader
    {
        public bool MutationRejected { get; private set; }
        /// <summary>记录字典写入被拒绝后写入正确测试内容，交给真实登记流程。</summary>
        public async Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            try { ((IDictionary<string, string>)request.Hashes)["sha256"] = new('0', 64); }
            catch (NotSupportedException) { MutationRejected = true; }
            await File.WriteAllBytesAsync(request.DestinationPath, bytes, cancellationToken);
            return new(request.DestinationPath, bytes.Length, true);
        }
    }
}
