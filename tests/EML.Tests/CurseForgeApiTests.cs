using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EML.Core.CurseForge;
using EML.Infrastructure.Minecraft.CurseForge.Api;

namespace EML.Tests;

/// <summary>使用内存传输验证直连官方 API 和凭证边界，不访问网络或真实凭据。</summary>
public sealed class CurseForgeApiTests
{
    private const string TestSecret = "unit-test-only-not-a-real-key";

    /// <summary>批量文件查询分为 50 条一批，并核实每次都只向官方主机发送请求头密钥。</summary>
    [Fact]
    public async Task BatchFilesUsesOfficialHostAndBatchesFiftyIds()
    {
        var seen = new List<long[]>();
        using var handler = new FakeHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.curseforge.com/v1/mods/files", request.RequestUri!.AbsoluteUri);
            Assert.Equal(TestSecret, Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Null(request.Headers.Authorization);
            Assert.DoesNotContain(TestSecret, request.RequestUri.AbsoluteUri);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var ids = json.RootElement.GetProperty("fileIds").EnumerateArray().Select(item => item.GetInt64()).ToArray();
            seen.Add(ids);
            return Json(new { data = ids.Select(id => new { id, gameId = 432, modId = 99, isAvailable = true, fileName = "test.jar" }) });
        });
        using var api = Client(handler);

        var files = await api.GetFilesAsync(Enumerable.Range(1, 101).Select(value => (long)value).ToArray());

        Assert.Equal(new[] { 50, 50, 1 }, seen.Select(batch => batch.Length));
        Assert.Equal(101, files.Count);
        Assert.Equal(Enumerable.Range(1, 101).Select(value => (long)value), files.Select(file => file.Id));
    }

    /// <summary>重复输入只查询一次，空输入不读取密钥或发起 HTTP。</summary>
    [Fact]
    public async Task BatchDeduplicatesInputsAndSkipsEmptyRequests()
    {
        using var handler = new FakeHandler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(new long[] { 1, 2 }, json.RootElement.GetProperty("fileIds").EnumerateArray().Select(value => value.GetInt64()));
            return Json(new { data = new[] { new { id = 1 }, new { id = 2 } } });
        });
        using var api = Client(handler);

        Assert.Empty(await api.GetFilesAsync([]));
        Assert.Empty(await api.GetModsAsync([]));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(2, (await api.GetFilesAsync([1, 1, 2])).Count);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>项目批量返回保留分类、网页链接和可空的分发偏好，不推断未知许可。</summary>
    [Fact]
    public async Task BatchModsPreservesDistributionPreferencesAndLinks()
    {
        using var handler = new FakeHandler(async (request, token) =>
        {
            Assert.Equal("/v1/mods", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(new long[] { 1, 2, 3 }, json.RootElement.GetProperty("modIds").EnumerateArray().Select(value => value.GetInt64()));
            Assert.True(json.RootElement.GetProperty("filterPcOnly").GetBoolean());
            return JsonText("""
                {"data":[
                  {"id":1,"gameId":432,"classId":6,"allowModDistribution":true,"links":{"websiteUrl":"https://www.curseforge.com/minecraft/mc-mods/example"}},
                  {"id":2,"gameId":432,"classId":4471,"allowModDistribution":false},
                  {"id":3,"gameId":432,"classId":12,"allowModDistribution":null}
                ]}
                """);
        });
        using var api = Client(handler);

        var mods = await api.GetModsAsync([1, 2, 3]);

        Assert.True(mods[0].AllowModDistribution);
        Assert.False(mods[1].AllowModDistribution);
        Assert.Null(mods[2].AllowModDistribution);
        Assert.Equal(6, mods[0].ClassId);
        Assert.Equal("https://www.curseforge.com/minecraft/mc-mods/example", mods[0].Links.WebsiteUrl);
    }

    /// <summary>单文件请求保留 nullable 下载地址、官方哈希算法编号和依赖关系。</summary>
    [Fact]
    public async Task GetFilePreservesHashAlgorithmsAndMissingDownloadUrl()
    {
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal("/v1/mods/12/files/34", request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonText("""
                {"data":{"id":34,"gameId":432,"modId":12,"fileName":"test.jar","fileLength":123,"isAvailable":true,
                "downloadUrl":null,"hashes":[{"algo":1,"value":"sha1-value"},{"algo":2,"value":"md5-value"}],
                "dependencies":[{"modId":55,"relationType":3}]}}
                """));
        });
        using var api = Client(handler);

        var file = await api.GetFileAsync(12, 34);

        Assert.Null(file.DownloadUrl);
        Assert.Equal(123, file.FileLength);
        Assert.True(file.IsAvailable);
        Assert.Equal(new[] { 1, 2 }, file.Hashes.Select(hash => hash.Algo));
        Assert.Equal(55, Assert.Single(file.Dependencies).ModId);
    }

    /// <summary>单项目请求解析 data 包装并核对固定编号。</summary>
    [Fact]
    public async Task GetModReadsDataEnvelope()
    {
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal("/v1/mods/12", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(new { data = new { id = 12, gameId = 432, name = "Pack", classId = 4471 } }));
        });
        using var api = Client(handler);

        var mod = await api.GetModAsync(12);

        Assert.Equal("Pack", mod.Name);
        Assert.Equal(4471, mod.ClassId);
    }

    /// <summary>搜索关键词、版本、分类、加载器和分页被独立编码，特殊字符不会改变查询结构。</summary>
    [Fact]
    public async Task SearchEncodesFiltersAndPreservesPagination()
    {
        using var handler = new FakeHandler((request, _) =>
        {
            var query = Query(request.RequestUri!);
            Assert.Equal("432", query["gameId"]);
            Assert.Equal("4471", query["classId"]);
            Assert.Equal("4482", query["categoryId"]);
            Assert.Equal("科技 & 冒险", query["searchFilter"]);
            Assert.Equal("1.20.1", query["gameVersion"]);
            Assert.Equal("4", query["modLoaderType"]);
            Assert.Equal("6", query["sortField"]);
            Assert.Equal("desc", query["sortOrder"]);
            Assert.Equal("50", query["index"]);
            Assert.Equal("25", query["pageSize"]);
            return Task.FromResult(Json(new { data = new[] { new { id = 12, name = "Pack" } },
                pagination = new { index = 50, pageSize = 25, resultCount = 1, totalCount = 51 } }));
        });
        using var api = Client(handler);

        var result = await api.SearchAsync(new CurseForgeSearchOptions
        {
            ClassId = 4471, CategoryId = 4482, SearchFilter = "科技 & 冒险", GameVersion = "1.20.1",
            ModLoaderType = CurseForgeModLoaderType.Fabric, SortField = CurseForgeSortField.TotalDownloads, Index = 50, PageSize = 25
        });

        Assert.Equal(12, Assert.Single(result.Data).Id);
        Assert.Equal(51, result.Pagination.TotalCount);
    }

    /// <summary>身份错误只展示固定提示，正文即使回显密钥也不会进入异常或其内部异常。</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "未通过验证")]
    [InlineData(HttpStatusCode.Forbidden, "拒绝访问")]
    public async Task AuthenticationErrorsNeverExposeResponseBody(HttpStatusCode status, string message)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonText(TestSecret, status)));
        using var api = Client(handler);

        var error = await Assert.ThrowsAsync<CurseForgeApiException>(() => api.GetModAsync(1));

        Assert.Contains(message, error.Message);
        Assert.DoesNotContain(TestSecret, error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(status, error.StatusCode);
    }

    /// <summary>429 保留 Retry-After 和实际响应的余额，但不自动发起重试。</summary>
    [Fact]
    public async Task RateLimitReportsWaitTimeWithoutRetrying()
    {
        using var handler = new FakeHandler((_, _) =>
        {
            var response = JsonText(TestSecret, HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(8));
            response.Headers.Add("X-RateLimit-Limit", "100");
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        });
        using var api = Client(handler);

        var error = await Assert.ThrowsAsync<CurseForgeApiException>(() => api.GetModAsync(1));

        Assert.Equal(TimeSpan.FromSeconds(8), error.RetryAfter);
        Assert.Equal(0, api.LastRateLimit!.Remaining);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(TestSecret, error.ToString());
    }

    /// <summary>不会跟随 302 把密钥带到第三方下载主机。</summary>
    [Fact]
    public async Task RedirectsAreRejectedWithoutSecondRequest()
    {
        using var handler = new FakeHandler((_, _) =>
        {
            var response = JsonText("{}", HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://third-party.invalid/collect");
            return Task.FromResult(response);
        });
        using var api = Client(handler);

        var error = await Assert.ThrowsAsync<CurseForgeApiException>(() => api.GetModAsync(1));

        Assert.Equal(HttpStatusCode.Redirect, error.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>密钥未配置时本地失败，不能发送空的 x-api-key 请求。</summary>
    [Fact]
    public async Task MissingKeyFailsBeforeNetwork()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonText("{}")));
        using var api = new CurseForgeApiClient(handler, new KeyProvider(null));

        var error = await Assert.ThrowsAsync<CurseForgeCredentialException>(() => api.GetModAsync(1));

        Assert.Contains("尚未配置", error.Message);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>密钥内有控制字符时固定报错，不把非法输入放到参数异常里。</summary>
    [Fact]
    public async Task InvalidKeyNeverAppearsInValidationException()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonText("{}")));
        using var api = new CurseForgeApiClient(handler, new KeyProvider(TestSecret + "\r\nInjected: value"));

        var error = await Assert.ThrowsAsync<CurseForgeCredentialException>(() => api.GetModAsync(1));

        Assert.DoesNotContain(TestSecret, error.ToString());
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>传输错误即使错误消息包含完整请求头也会被换成无密钥的固定异常。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransportExceptionsDoNotLeakApiKey(bool httpException)
    {
        using var handler = new FakeHandler((_, _) => Task.FromException<HttpResponseMessage>(httpException
            ? new HttpRequestException("transport echoed " + TestSecret)
            : new IOException("stream echoed " + TestSecret)));
        using var api = Client(handler);

        var error = await Record.ExceptionAsync(() => api.GetModAsync(1));

        Assert.NotNull(error);
        Assert.DoesNotContain(TestSecret, error.ToString());
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(TestSecret, api.ToString());
        Assert.DoesNotContain(TestSecret, new CurseForgeApiOptions().ToString());
    }

    /// <summary>凭证提供者抛出的错误也不能通过异常链暴露密钥。</summary>
    [Fact]
    public async Task ProviderFailureIsSanitizedBeforeNetwork()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonText("{}")));
        using var api = new CurseForgeApiClient(handler, new ThrowingProvider());

        var error = await Assert.ThrowsAsync<CurseForgeCredentialException>(() => api.GetModAsync(1));

        Assert.DoesNotContain(TestSecret, error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>有无长度响应头都必须按上限拒绝过大的 JSON。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JsonResponseIsBounded(bool knownLength)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = knownLength ? new StringContent("{\"data\":{\"id\":1}}") :
                new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes("{\"data\":{\"id\":1}}")))
        }));
        using var api = new CurseForgeApiClient(handler, new KeyProvider(TestSecret), new CurseForgeApiOptions { MaximumJsonBytes = 4 });

        await Assert.ThrowsAsync<InvalidDataException>(() => api.GetModAsync(1));
    }

    /// <summary>未解析正文和错误 data 包装均是数据错误，不能返回伪造的默认对象。</summary>
    [Theory]
    [InlineData("{invalid")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    public async Task InvalidEnvelopeIsRejected(string payload)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonText(payload)));
        using var api = Client(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => api.GetModAsync(1));
    }

    /// <summary>单文件 ID 或项目归属不一致时，不允许继续作为清单文件下载。</summary>
    [Theory]
    [InlineData(4, 12)]
    [InlineData(34, 99)]
    public async Task FileIdentityMismatchIsRejected(long id, long modId)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json(new { data = new { id, modId } })));
        using var api = Client(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => api.GetFileAsync(12, 34));
    }

    /// <summary>批量响应中的重复 ID 或额外 ID 属于来源异常，但合法的部分结果保持原样。</summary>
    [Theory]
    [InlineData("{\"data\":[{\"id\":1},{\"id\":1}]}")]
    [InlineData("{\"data\":[{\"id\":3}]}")]
    public async Task InvalidBatchIdentityIsRejected(string payload)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonText(payload)));
        using var api = Client(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => api.GetFilesAsync([1, 2]));
    }

    /// <summary>服务器少返回文件时保留缺失，不能自动查询或下载替代版本。</summary>
    [Fact]
    public async Task MissingBatchEntriesRemainMissing()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonText("{\"data\":[{\"id\":1}]}")));
        using var api = Client(handler);

        var files = await api.GetFilesAsync([1, 2]);

        Assert.Equal(1, Assert.Single(files).Id);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>超时和用户取消分别返回固定说明，保留取消语义并避免泄漏内部请求。</summary>
    [Fact]
    public async Task TimeoutAndCancellationAreDistinct()
    {
        using var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonText("{}");
        });
        using var api = new CurseForgeApiClient(handler, new KeyProvider(TestSecret),
            new CurseForgeApiOptions { RequestTimeout = TimeSpan.FromMilliseconds(30) });

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => api.GetModAsync(1));
        Assert.DoesNotContain(TestSecret, timeout.ToString());
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetFilesAsync([1], source.Token));
    }

    /// <summary>分页窗口、加载器与版本配对、正数 ID 都在联网前验证。</summary>
    [Fact]
    public async Task InvalidSearchAndIdsDoNotReachNetwork()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonText("{}")));
        using var api = Client(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => api.SearchAsync(new CurseForgeSearchOptions { PageSize = 51 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => api.SearchAsync(new CurseForgeSearchOptions { Index = 9990, PageSize = 20 }));
        await Assert.ThrowsAsync<ArgumentException>(() => api.SearchAsync(new CurseForgeSearchOptions { ModLoaderType = CurseForgeModLoaderType.Fabric }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => api.GetFilesAsync([0]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => api.GetModAsync(-1));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>共享签名只接受精确的官方 HTTPS 主机，普通查询值保持原样。</summary>
    [Theory]
    [InlineData("https://api.curseforge.com/v1/mods/1", true, false)]
    [InlineData("https://edge.forgecdn.net/files/1/2/test.jar", false, true)]
    [InlineData("https://mediafilez.forgecdn.net/files/1/2/test.jar?filename=api-key", false, true)]
    public void SigningAllowsOnlyOfficialApiAndCdnHosts(string address, bool api, bool cdn)
    {
        var uri = new Uri(address);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        Assert.Equal(api, CurseForgeRequestHeaders.IsApiUri(uri));
        Assert.Equal(cdn, CurseForgeRequestHeaders.IsCdnUri(uri));
        CurseForgeRequestHeaders.AddApiKey(request, new KeyProvider(TestSecret));

        Assert.Equal(TestSecret, Assert.Single(request.Headers.GetValues("x-api-key")));
        Assert.Equal(uri, request.RequestUri);
        Assert.Null(request.Headers.Authorization);
        Assert.DoesNotContain(TestSecret, request.RequestUri!.AbsoluteUri);
    }

    /// <summary>不安全目标在读取凭证前被拒绝，防止主机伪装、端口修改与查询签名泄漏。</summary>
    [Theory]
    [InlineData("http://edge.forgecdn.net/files/a.jar")]
    [InlineData("https://edge.forgecdn.net:444/files/a.jar")]
    [InlineData("https://edge.forgecdn.net.evil.invalid/files/a.jar")]
    [InlineData("https://files.forgecdn.net/files/a.jar")]
    [InlineData("https://user@edge.forgecdn.net/files/a.jar")]
    [InlineData("https://edge.forgecdn.net/files/a.jar#fragment")]
    [InlineData("https://edge.forgecdn.net/files/a.jar?x-api-key=secret")]
    [InlineData("https://edge.forgecdn.net/files/a.jar?%2578-api-key=secret")]
    [InlineData("https://edge.forgecdn.net/files/a.jar?X-Amz-Signature=secret")]
    [InlineData("https://edge.forgecdn.net/files/a.jar?access_token=secret")]
    [InlineData("https://api.curseforge.com/v1/mods/1?Authorization=secret")]
    [InlineData("https://api.curseforge.com.evil.invalid/v1/mods/1")]
    public void UnsafeSigningDestinationDoesNotReadKey(string address)
    {
        var uri = new Uri(address);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        Assert.False(CurseForgeRequestHeaders.IsApiUri(uri));
        Assert.False(CurseForgeRequestHeaders.IsCdnUri(uri));
        var error = Assert.Throws<InvalidOperationException>(() =>
            CurseForgeRequestHeaders.AddApiKey(request, new ThrowingProvider()));

        Assert.DoesNotContain(TestSecret, error.ToString());
        Assert.False(request.Headers.Contains("x-api-key"));
        Assert.Null(error.InnerException);
    }

    /// <summary>已有身份信息时禁止覆盖或叠加，且不会再读取内置密钥。</summary>
    [Theory]
    [InlineData("x-api-key")]
    [InlineData("Authorization")]
    public void SigningRejectsExistingAuthentication(string header)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://edge.forgecdn.net/files/a.jar");
        request.Headers.Add(header, "existing-value");

        var error = Assert.Throws<InvalidOperationException>(() =>
            CurseForgeRequestHeaders.AddApiKey(request, new ThrowingProvider()));

        Assert.DoesNotContain(TestSecret, error.ToString());
        Assert.Equal("existing-value", Assert.Single(request.Headers.GetValues(header)));
    }

    /// <summary>使用只存于测试内存的凭证，所有网络请求由替身处理。</summary>
    private static CurseForgeApiClient Client(HttpMessageHandler handler) => new(handler, new KeyProvider(TestSecret));

    /// <summary>将对象序列化为内存 JSON 响应。</summary>
    private static HttpResponseMessage Json(object body) => JsonText(JsonSerializer.Serialize(body));

    /// <summary>创建指定状态码的响应，不连接真实 API。</summary>
    private static HttpResponseMessage JsonText(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>读取独立编码的查询参数，检查特殊字符不会变成额外参数。</summary>
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    /// <summary>仅为测试按需返回固定字符串，不使用真实环境变量或凭据管理器。</summary>
    private sealed class KeyProvider(string? value) : ICurseForgeApiKeyProvider
    {
        /// <summary>交还测试用密钥，不在对象 ToString 中展示它。</summary>
        public string? GetApiKey() => value;
    }

    /// <summary>模拟错误实现意外把秘密写入异常的凭证来源。</summary>
    private sealed class ThrowingProvider : ICurseForgeApiKeyProvider
    {
        /// <summary>抛出包含测试秘密的错误，验证客户端安全替换错误链。</summary>
        public string? GetApiKey() => throw new InvalidOperationException(TestSecret);
    }

    /// <summary>内存请求处理器，统计次数并执行测试回调。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public int Calls { get; private set; }

        /// <summary>保存完全离线的请求回调。</summary>
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;

        /// <summary>把请求交给测试断言并累计次数。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return _send(request, cancellationToken);
        }
    }

    /// <summary>模拟无 Content-Length 的网络响应内容。</summary>
    private sealed class NonSeekableStream : MemoryStream
    {
        /// <summary>以固定内存内容创建流并关闭 Seek 能力。</summary>
        public NonSeekableStream(byte[] bytes) : base(bytes) { }
        public override bool CanSeek => false;
    }
}
