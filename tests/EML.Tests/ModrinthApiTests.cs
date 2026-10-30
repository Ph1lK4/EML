using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EML.Core.Downloads;
using EML.Core.Modrinth;
using EML.Infrastructure.Minecraft.Modrinth.Api;

namespace EML.Tests;

/// <summary>用内存 HTTP 替身验证公开 API、筛选语义、限流和精确版本文件下载，不访问真实网络。</summary>
public sealed class ModrinthApiTests
{
    /// <summary>关键词独立编码，快捷筛选与额外条件保持组内 OR、组间 AND。</summary>
    [Fact]
    public async Task SearchEncodesQueryFacetsAndPagination()
    {
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("/v2/search", request.RequestUri.AbsolutePath);
            Assert.Contains("EML/", request.Headers.UserAgent.ToString());
            Assert.Null(request.Headers.Authorization);
            var query = Query(request.RequestUri);
            Assert.Equal("科技 & 魔法", query["query"]);
            Assert.Equal("downloads", query["index"]);
            Assert.Equal("40", query["offset"]);
            Assert.Equal("20", query["limit"]);
            var facets = JsonSerializer.Deserialize<string[][]>(query["facets"])!;
            Assert.Equal(new[] { "project_type:mod", "project_type:modpack" }, facets[0]);
            Assert.Equal(new[] { "versions:1.20.1", "versions:1.21.1" }, facets[1]);
            Assert.Equal(new[] { "categories:fabric", "categories:quilt" }, facets[2]);
            Assert.Equal(new[] { "categories:technology" }, facets[3]);
            Assert.Equal(new[] { "license:MIT", "license:Apache-2.0" }, facets[4]);
            return Task.FromResult(Json("""{"hits":[{"project_id":"fixed-id","slug":"mutable-slug","project_type":"modpack","title":"Pack"}],"offset":40,"limit":20,"total_hits":41}"""));
        });
        using var client = new ModrinthApiClient(handler);

        var result = await client.SearchAsync(new ModrinthSearchOptions
        {
            Query = "科技 & 魔法", ProjectTypes = [ModrinthProjectType.Mod, ModrinthProjectType.Modpack],
            GameVersions = ["1.20.1", "1.21.1"], Loaders = ["fabric", "quilt"], Categories = ["technology"],
            AdditionalFacetGroups = [new ModrinthSearchFacet[]
            {
                new(ModrinthFacetField.License, "MIT"), new(ModrinthFacetField.License, "Apache-2.0")
            }], Sort = ModrinthSearchSort.Downloads, Offset = 40, Limit = 20
        });

        Assert.Equal("fixed-id", Assert.Single(result.Hits).ProjectId);
        Assert.Equal(41, result.TotalHits);
    }

    /// <summary>版本查询保留加载器和游戏版本筛选，以及固定版本、依赖和文件字段。</summary>
    [Fact]
    public async Task VersionListPreservesFiltersFilesAndDependencies()
    {
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal("/v2/project/example/version", request.RequestUri!.AbsolutePath);
            var query = Query(request.RequestUri);
            Assert.Equal(new[] { "fabric" }, JsonSerializer.Deserialize<string[]>(query["loaders"]));
            Assert.Equal(new[] { "1.21.1" }, JsonSerializer.Deserialize<string[]>(query["game_versions"]));
            Assert.Equal("false", query["featured"]);
            Assert.Equal("false", query["include_changelog"]);
            return Task.FromResult(Json("""
                [{"id":"v1","project_id":"p1","version_number":"1.0","version_type":"release",
                  "files":[{"filename":"pack.mrpack","url":"https://cdn.modrinth.com/data/p1/pack.mrpack","size":12,"primary":true,"hashes":{"sha512":"abc"}}],
                  "dependencies":[{"project_id":"dependency-project","version_id":"dependency-version","dependency_type":"required"}]}]
                """));
        });
        using var client = new ModrinthApiClient(handler);

        var versions = await client.GetVersionsAsync("example", new ModrinthVersionFilter
        {
            Loaders = ["fabric"], GameVersions = ["1.21.1"], Featured = false
        });

        var version = Assert.Single(versions);
        Assert.Equal("p1", version.ProjectId);
        Assert.Equal("1.0", version.VersionNumber);
        Assert.Equal("pack.mrpack", Assert.Single(version.Files).Filename);
        Assert.Equal("required", Assert.Single(version.Dependencies).DependencyType);
        Assert.Equal("dependency-version", version.Dependencies[0].VersionId);
    }

    /// <summary>项目和版本均通过固定 ID 查询，项目详情保留筛选与链接字段。</summary>
    [Fact]
    public async Task ProjectAndSingleVersionUseDistinctEndpoints()
    {
        using var handler = new FakeHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/v2/project/example"
                ? Json("""{"id":"project-id","slug":"example","project_type":"modpack","loaders":["fabric"],"license":{"id":"MIT","name":"MIT"}}""")
                : Json("""{"id":"version-id","project_id":"project-id","files":[]}""")));
        using var client = new ModrinthApiClient(handler);

        var project = await client.GetProjectAsync("example");
        var version = await client.GetVersionAsync("version-id");

        Assert.Equal("project-id", project.Id);
        Assert.Equal("MIT", project.License!.Id);
        Assert.Equal("fabric", Assert.Single(project.Loaders));
        Assert.Equal("version-id", version.Id);
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>限流错误提供等待秒数和余额，不暗中重试或显示响应正文。</summary>
    [Fact]
    public async Task RateLimitReturnsRetryInformationWithoutRetrying()
    {
        using var handler = new FakeHandler((_, _) =>
        {
            var response = Json("private server diagnostic", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
            response.Headers.Add("X-Ratelimit-Limit", "300");
            response.Headers.Add("X-Ratelimit-Remaining", "0");
            response.Headers.Add("X-Ratelimit-Reset", "20");
            return Task.FromResult(response);
        });
        using var client = new ModrinthApiClient(handler);

        var error = await Assert.ThrowsAsync<ModrinthApiException>(() => client.GetProjectAsync("example"));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(12), error.RetryAfter);
        Assert.Equal(0, client.LastRateLimit!.Remaining);
        Assert.Equal(300, error.RateLimit!.Limit);
        Assert.NotNull(error.RateLimit.ResetsAt);
        Assert.DoesNotContain("private", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>未提供 Retry-After 时使用 Modrinth 重置响应头供调用方等待。</summary>
    [Fact]
    public async Task RateLimitCanUseResetHeader()
    {
        using var handler = new FakeHandler((_, _) =>
        {
            var response = Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.Add("X-Ratelimit-Reset", "3");
            return Task.FromResult(response);
        });
        using var client = new ModrinthApiClient(handler);

        var error = await Assert.ThrowsAsync<ModrinthApiException>(() => client.GetVersionAsync("example"));

        Assert.Equal(TimeSpan.FromSeconds(3), error.RetryAfter);
    }

    /// <summary>未知 Content-Length 的响应也受实际读取字节数限制。</summary>
    [Fact]
    public async Task BoundsUnknownLengthJsonResponse()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes("{\"id\":\"too-long\"}")))
        }));
        using var client = new ModrinthApiClient(handler, new ModrinthApiOptions { MaximumJsonBytes = 4 });

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetProjectAsync("example"));
    }

    /// <summary>响应声明过大时立即拒绝，防止缓冲任意大 JSON。</summary>
    [Fact]
    public async Task BoundsAdvertisedJsonResponse()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json("{\"id\":\"too-long\"}")));
        using var client = new ModrinthApiClient(handler, new ModrinthApiOptions { MaximumJsonBytes = 4 });

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetProjectAsync("example"));
    }

    /// <summary>无效 JSON 与缺少固定 ID 的响应提供明确的数据错误。</summary>
    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task RejectsInvalidProjectResponse(string payload)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json(payload)));
        using var client = new ModrinthApiClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetProjectAsync("example"));
    }

    /// <summary>网络超时与用户主动取消分别报告，调用方可以保留正确的界面状态。</summary>
    [Fact]
    public async Task RequestTimeoutIsReportedSeparatelyFromCancellation()
    {
        using var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        });
        using var client = new ModrinthApiClient(handler, new ModrinthApiOptions { RequestTimeout = TimeSpan.FromMilliseconds(30) });

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetProjectAsync("example"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetProjectAsync("example", cancellation.Token));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>重定向不会自动请求不可信的目标地址。</summary>
    [Fact]
    public async Task DoesNotFollowRedirects()
    {
        using var handler = new FakeHandler((_, _) =>
        {
            var response = Json("{}", HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://unsafe.test/project");
            return Task.FromResult(response);
        });
        using var client = new ModrinthApiClient(handler);

        var error = await Assert.ThrowsAsync<ModrinthApiException>(() => client.GetProjectAsync("example"));

        Assert.Equal(HttpStatusCode.Redirect, error.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>不安全的基础地址在发起网络请求前被拒绝。</summary>
    [Theory]
    [InlineData("http://api.modrinth.com/v2/")]
    [InlineData("https://name:secret@api.modrinth.com/v2/")]
    [InlineData("https://api.modrinth.com/v2")]
    public void RejectsUnsafeApiAddress(string url)
    {
        Assert.Throws<ArgumentException>(() => new ModrinthApiClient(new ModrinthApiOptions { BaseAddress = new Uri(url) }));
    }

    /// <summary>项目标识不能用路径分隔符跳到另一个 API 端点。</summary>
    [Theory]
    [InlineData("../version/other")]
    [InlineData("..")]
    [InlineData("bad\\name")]
    public async Task RejectsPathLikeIdentifiers(string id)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json("{}")));
        using var client = new ModrinthApiClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetProjectAsync(id));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>下载时使用唯一 primary 的精确 URL、大小和哈希，而不是重新拼接 CDN 地址。</summary>
    [Fact]
    public async Task DownloadsPrimaryFileThroughSharedDownloader()
    {
        var downloader = new RecordingDownloader();
        var service = new ModrinthVersionDownloadService(downloader);
        var version = new ModrinthVersion
        {
            Id = "fixed-version", ProjectId = "fixed-project", Files =
            [File("alternate.jar", false), File("pack.mrpack", true)]
        };
        var directory = Path.Combine(Path.GetTempPath(), "eml-api-test-download");

        var result = await service.DownloadAsync(version, directory);

        Assert.Equal(Path.Combine(directory, "pack.mrpack"), result.DestinationPath);
        Assert.Equal("https://cdn.modrinth.com/data/example/pack.mrpack", Assert.Single(downloader.Request!.Urls));
        Assert.Equal(123, downloader.Request.ExpectedSize);
        Assert.Equal(new string('a', 128), downloader.Request.Hashes["sha512"]);
        Assert.Equal(new string('b', 40), downloader.Request.Hashes["sha1"]);
    }

    /// <summary>多个无默认项文件要求用户明确选择，选择后不会误下载其他变体。</summary>
    [Fact]
    public void RequiresSelectionForAmbiguousFiles()
    {
        var version = new ModrinthVersion { Files = [File("client.jar", false), File("server.jar", false)] };

        Assert.Throws<InvalidDataException>(() => ModrinthVersionDownloadService.SelectFile(version));
        Assert.Equal("client.jar", ModrinthVersionDownloadService.SelectFile(version, "client.jar").Filename);
        Assert.Throws<ArgumentException>(() => ModrinthVersionDownloadService.SelectFile(version, "missing.jar"));
    }

    /// <summary>没有 primary 的单文件版本仍能选择。</summary>
    [Fact]
    public void SelectsOnlyFileWithoutPrimaryMarker()
    {
        var version = new ModrinthVersion { Files = [File("client.jar", false)] };

        Assert.Equal("client.jar", ModrinthVersionDownloadService.SelectFile(version).Filename);
    }

    /// <summary>阻止 API 文件名通过路径穿越、设备名或备用数据流写到目录外。</summary>
    [Theory]
    [InlineData("../escape.jar")]
    [InlineData("folder\\escape.jar")]
    [InlineData("file.jar:stream")]
    [InlineData("CON.jar")]
    [InlineData("LPT1.jar")]
    [InlineData("trailing.jar.")]
    public void RejectsUnsafeDownloadFilename(string filename)
    {
        var version = new ModrinthVersion { Files = [File(filename, true)] };

        Assert.Throws<InvalidDataException>(() => ModrinthVersionDownloadService.CreateDownloadRequest(version, Path.GetTempPath()));
    }

    /// <summary>HTTP 地址和无哈希文件不能进入 Modrinth 的已校验下载流程。</summary>
    [Fact]
    public void RejectsUnverifiedOrInsecureFileDownloads()
    {
        var file = File("client.jar", true);
        var insecure = new ModrinthVersion { Files = [file with { Url = "http://cdn.modrinth.com/client.jar" }] };
        var unverified = new ModrinthVersion { Files = [file with { Hashes = new Dictionary<string, string>() }] };
        var malformed = new ModrinthVersion { Files = [file with { Hashes = new Dictionary<string, string> { ["sha512"] = "invalid" } }] };

        Assert.Throws<InvalidDataException>(() => ModrinthVersionDownloadService.CreateDownloadRequest(insecure, Path.GetTempPath()));
        Assert.Throws<InvalidDataException>(() => ModrinthVersionDownloadService.CreateDownloadRequest(unverified, Path.GetTempPath()));
        Assert.Throws<InvalidDataException>(() => ModrinthVersionDownloadService.CreateDownloadRequest(malformed, Path.GetTempPath()));
    }

    /// <summary>生成带有效长度哈希的内存版本文件，不进行真实下载。</summary>
    private static ModrinthVersionFile File(string filename, bool primary) => new()
    {
        Filename = filename, Url = "https://cdn.modrinth.com/data/example/" + filename, Size = 123, Primary = primary,
        Hashes = new Dictionary<string, string> { ["sha512"] = new string('a', 128), ["sha1"] = new string('b', 40) }
    };

    /// <summary>生成一次内存 JSON 响应。</summary>
    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    /// <summary>拆解 URL 查询参数供断言检查编码后的实际语义。</summary>
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    /// <summary>记录网络次数并将每个请求交给当前测试提供的响应函数。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public int Calls { get; private set; }

        /// <summary>安装不访问真实网络的响应函数。</summary>
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;

        /// <summary>把请求和取消令牌转发给测试，并记录调用次数。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return _send(request, cancellationToken);
        }
    }

    /// <summary>模拟无法提前得知长度的 HTTP 响应流。</summary>
    private sealed class NonSeekableStream : MemoryStream
    {
        /// <summary>用固定字节构造不支持定位的读取流。</summary>
        public NonSeekableStream(byte[] content) : base(content) { }
        public override bool CanSeek => false;
    }

    /// <summary>记录交给公共下载器的请求，避免测试产生实际文件。</summary>
    private sealed class RecordingDownloader : IFileDownloader
    {
        public FileDownloadRequest? Request { get; private set; }

        /// <summary>捕获请求并模拟成功校验，供测试确认文件选择和参数传递。</summary>
        public Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(new FileDownloadResult(request.DestinationPath, request.ExpectedSize ?? 0, true));
        }
    }
}
