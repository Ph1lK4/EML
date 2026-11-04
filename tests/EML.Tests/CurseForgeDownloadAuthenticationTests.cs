using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EML.Core.Downloads;
using EML.Infrastructure.Downloads;
using EML.Infrastructure.Minecraft.CurseForge.Api;

namespace EML.Tests;

/// <summary>验证通用下载器对官方 CDN 的逐跳签名及脱敏，不联网或读取真实编译密钥。</summary>
public sealed class CurseForgeDownloadAuthenticationTests
{
    private const string Secret = "fake-compile-time-test-key";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("fixed CurseForge file content");

    /// <summary>两个批准的官方 CDN 都仅在当前请求头获得凭证，下载结果仍须通过原有哈希校验。</summary>
    [Theory]
    [InlineData("https://edge.forgecdn.net/files/12/mod.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/12/mod.jar")]
    public async Task ApprovedCdnReceivesRequestScopedKey(string url)
    {
        using var folder = new Folder();
        var keys = new Keys();
        using var handler = Handler();
        using var downloader = Downloader(handler, keys);
        var result = await downloader.DownloadAsync(Request(folder, url));
        Assert.True(result.HashVerified);
        Assert.Equal(Payload, File.ReadAllBytes(folder.Target));
        Assert.Equal(Secret, Assert.Single(handler.Requests).Key);
        Assert.Equal(1, keys.Calls);
    }

    /// <summary>普通文件、镜像和 API 地址不会误用 CDN 认证，也不会读取凭证提供者。</summary>
    [Theory]
    [InlineData("https://cdn.modrinth.com/data/test/mod.jar")]
    [InlineData("https://libraries.minecraft.net/test.jar")]
    [InlineData("https://mirror.example.test/mod.jar")]
    [InlineData("https://api.curseforge.com/v1/files/123")]
    [InlineData("https://edge.forgecdn.net.evil.invalid/mod.jar")]
    public async Task OtherDownloadsNeverReadOrSendKey(string url)
    {
        using var folder = new Folder();
        var keys = new Keys { Failure = new InvalidOperationException("credential must not be read") };
        using var handler = Handler();
        using var downloader = Downloader(handler, keys);
        await downloader.DownloadAsync(Request(folder, url));
        Assert.Equal(0, keys.Calls);
        Assert.Null(Assert.Single(handler.Requests).Key);
    }

    /// <summary>同一下载器后续下载其他平台内容时不会残留前一次官方密钥。</summary>
    [Fact]
    public async Task SignedDownloadDoesNotContaminateLaterRequests()
    {
        using var folder = new Folder();
        using var handler = Handler();
        var keys = new Keys();
        using var downloader = Downloader(handler, keys);
        await downloader.DownloadAsync(Request(folder, "https://edge.forgecdn.net/a.jar"));
        await downloader.DownloadAsync(Request(folder, "https://cdn.modrinth.com/b.jar") with
        { DestinationPath = Path.Combine(folder.Path, "second.jar") });
        Assert.Equal(new string?[] { Secret, null }, handler.Requests.Select(request => request.Key));
        Assert.Equal(1, keys.Calls);
    }

    /// <summary>同主机和批准的跨主机跳转都会新建请求，按目标重新读取密钥。</summary>
    [Theory]
    [InlineData("https://edge.forgecdn.net/final.jar")]
    [InlineData("https://mediafilez.forgecdn.net/final.jar")]
    public async Task ApprovedRedirectsAreSignedIndividually(string next)
    {
        using var folder = new Folder();
        var keys = new Keys { Rotate = true };
        using var handler = new CaptureHandler((count, _, _) => Task.FromResult(count == 1 ? Redirect(next) : Response()));
        using var downloader = Downloader(handler, keys);
        await downloader.DownloadAsync(Request(folder, "https://edge.forgecdn.net/start.jar"));
        Assert.Equal(new[] { Secret + "-1", Secret + "-2" }, handler.Requests.Select(request => request.Key));
        Assert.Equal(next, handler.Requests.Last().Url);
    }

    /// <summary>已认证 CDN 跳往任意主机或普通 API 地址时直接拒绝，不向第二个地址发送请求。</summary>
    [Theory]
    [InlineData("https://evil.invalid/file.jar")]
    [InlineData("https://api.curseforge.com/v1/files/123")]
    [InlineData("https://edge.forgecdn.net.evil.invalid/file.jar")]
    [InlineData("http://edge.forgecdn.net/file.jar")]
    public async Task ProtectedRedirectCannotLeaveApprovedCdn(string next)
    {
        using var folder = new Folder();
        using var handler = new CaptureHandler((_, _, _) => Task.FromResult(Redirect(next)));
        using var downloader = Downloader(handler, new Keys());
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Request(folder, "https://edge.forgecdn.net/start.jar")));
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>普通镜像进入官方 CDN 时只签名第二跳，普通镜像之间跳转则维持原有行为。</summary>
    [Theory]
    [InlineData("https://edge.forgecdn.net/final.jar", true)]
    [InlineData("https://another.example.test/final.jar", false)]
    public async Task UnprotectedRedirectEvaluatesDestinationIndependently(string next, bool signed)
    {
        using var folder = new Folder();
        var keys = new Keys();
        using var handler = new CaptureHandler((count, _, _) => Task.FromResult(count == 1 ? Redirect(next) : Response()));
        using var downloader = Downloader(handler, keys);
        await downloader.DownloadAsync(Request(folder, "https://mirror.example.test/start.jar"));
        var requests = handler.Requests.ToArray();
        Assert.Null(requests[0].Key);
        Assert.Equal(signed ? Secret : null, requests[1].Key);
        Assert.Equal(signed ? 1 : 0, keys.Calls);
    }

    /// <summary>官方地址中的明文凭证、协议降级、非标准端口和片段在发出请求前拒绝。</summary>
    [Theory]
    [InlineData("http://edge.forgecdn.net/mod.jar")]
    [InlineData("https://user:pass@edge.forgecdn.net/mod.jar")]
    [InlineData("https://edge.forgecdn.net:444/mod.jar")]
    [InlineData("https://edge.forgecdn.net/mod.jar?x-api-key=secret")]
    [InlineData("https://mediafilez.forgecdn.net/mod.jar?api_key=secret")]
    [InlineData("https://api.curseforge.com/v1/files/1?x-api-key=secret")]
    [InlineData("https://edge.forgecdn.net/mod.jar#fragment")]
    public async Task UnsafeOfficialUrlsAreRejectedBeforeCredentialAccess(string url)
    {
        using var folder = new Folder();
        var keys = new Keys();
        using var handler = Handler();
        using var downloader = Downloader(handler, keys);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Request(folder, url)));
        Assert.Equal(0, keys.Calls);
        Assert.Empty(handler.Requests);
    }

    /// <summary>缺失或非法编译密钥只影响官方 CDN，错误内容不包含传入值。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" invalid secret ")]
    [InlineData("invalid\nsecret")]
    public async Task MissingOrInvalidKeyNeverReachesTransport(string? key)
    {
        using var folder = new Folder();
        using var handler = Handler();
        using var downloader = Downloader(handler, new Keys(key));
        var error = await Assert.ThrowsAsync<CurseForgeCredentialException>(() => downloader.DownloadAsync(Request(folder, "https://edge.forgecdn.net/mod.jar")));
        Assert.Null(error.InnerException);
        if (!string.IsNullOrEmpty(key)) Assert.DoesNotContain(key, error.ToString());
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>凭证提供者抛出的原文和内部异常也不能泄漏进下载错误。</summary>
    [Fact]
    public async Task CredentialProviderFailureIsSanitized()
    {
        using var folder = new Folder();
        using var handler = Handler();
        using var downloader = Downloader(handler, new Keys { Failure = new InvalidOperationException(Secret, new Exception(Secret)) });
        var error = await Assert.ThrowsAsync<CurseForgeCredentialException>(() => downloader.DownloadAsync(Request(folder, "https://edge.forgecdn.net/mod.jar")));
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Null(error.InnerException);
        Assert.Empty(handler.Requests);
    }

    /// <summary>HTTP 传输异常保留状态码但删除正文、内部异常和附加数据中的密钥。</summary>
    [Fact]
    public async Task TransportFailureDoesNotExposeHeaderInException()
    {
        using var folder = new Folder();
        using var handler = new CaptureHandler((_, _, _) =>
        {
            var error = new HttpRequestException(Secret, new Exception(Secret), HttpStatusCode.ServiceUnavailable);
            error.Data["request"] = Secret;
            throw error;
        });
        using var downloader = Downloader(handler, new Keys());
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(Request(folder, "https://edge.forgecdn.net/mod.jar")));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.DoesNotContain(Secret, failure.ToString());
        Assert.Null(failure.InnerException);
        Assert.Empty(failure.Data);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>读取文件流时抛出的敏感异常同样脱敏，且本次临时文件会被清理。</summary>
    [Fact]
    public async Task ResponseStreamFailureIsSanitizedAndPartialFileRemoved()
    {
        using var folder = new Folder();
        using var handler = new CaptureHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new FailingStream()) }));
        using var downloader = Downloader(handler, new Keys());
        var failure = await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(Request(folder, "https://edge.forgecdn.net/mod.jar")));
        Assert.DoesNotContain(Secret, failure.ToString());
        Assert.Null(failure.InnerException);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>上游错误响应不会读取可能回显密钥的正文；状态码仍可用于重试决策。</summary>
    [Fact]
    public async Task ErrorResponseBodyIsNeverIncludedInDiagnostic()
    {
        using var folder = new Folder();
        using var handler = new CaptureHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = new StringContent(Secret) }));
        using var downloader = Downloader(handler, new Keys());
        var failure = await Assert.ThrowsAnyAsync<HttpRequestException>(() => downloader.DownloadAsync(Request(folder, "https://edge.forgecdn.net/mod.jar")));
        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
        Assert.DoesNotContain(Secret, failure.ToString());
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>签名后的主动取消保留取消语义，并删除已经写入的临时文件。</summary>
    [Fact]
    public async Task CancellationKeepsTokenAndRemovesPartialFile()
    {
        using var folder = new Folder();
        using var cancellation = new CancellationTokenSource();
        using var handler = Handler();
        using var downloader = Downloader(handler, new Keys());
        var progress = new InlineProgress<FileDownloadProgress>(value => { if (value.BytesReceived > 0) cancellation.Cancel(); });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadAsync(Request(folder, "https://edge.forgecdn.net/mod.jar"), progress, cancellation.Token));
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Null(error.InnerException);
        Assert.Empty(Directory.GetFiles(folder.Path));
        Assert.Single(handler.Requests);
    }

    /// <summary>超时包装只包含已脱敏的取消异常，不保留传输层带请求头的原始异常。</summary>
    [Fact]
    public async Task TimeoutDoesNotRetainSensitiveInnerException()
    {
        using var folder = new Folder();
        using var handler = new CaptureHandler(async (_, _, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { throw new OperationCanceledException(Secret, new Exception(Secret), token); }
            return Response();
        });
        using var downloader = new HttpFileDownloader(handler,
            new FileDownloadOptions { MaxAttemptsPerUrl = 1, AttemptTimeout = TimeSpan.FromMilliseconds(20) }, new Keys());
        var error = await Assert.ThrowsAsync<TimeoutException>(() => downloader.DownloadAsync(Request(folder, "https://edge.forgecdn.net/mod.jar")));
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.IsType<OperationCanceledException>(error.InnerException);
        Assert.Null(error.InnerException!.InnerException);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>认证失败后显式配置的普通候选镜像仍不会获得官方请求头。</summary>
    [Fact]
    public async Task CandidateMirrorDoesNotInheritPreviousCdnHeaders()
    {
        using var folder = new Folder();
        using var handler = new CaptureHandler((count, _, _) => Task.FromResult(count == 1
            ? new HttpResponseMessage(HttpStatusCode.NotFound) : Response()));
        using var downloader = Downloader(handler, new Keys());
        var request = Request(folder, "https://edge.forgecdn.net/mod.jar") with
        { Urls = ["https://edge.forgecdn.net/mod.jar", "https://mirror.example.test/mod.jar"] };
        await downloader.DownloadAsync(request);
        Assert.Equal(new string?[] { Secret, null }, handler.Requests.Select(sent => sent.Key));
    }

    /// <summary>官方地址缺少有效凭证时仍尝试显式普通候选，不重试签名或向镜像传递凭证。</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("invalid\nsecret", false)]
    [InlineData(null, true)]
    public async Task CredentialFailureAllowsExplicitOrdinaryCandidate(string? key, bool providerFails)
    {
        using var folder = new Folder();
        var keys = new Keys(key)
        {
            Failure = providerFails ? new InvalidOperationException(Secret, new Exception(Secret)) : null
        };
        using var handler = Handler();
        using var downloader = new HttpFileDownloader(handler,
            new FileDownloadOptions { MaxAttemptsPerUrl = 3, RetryDelay = TimeSpan.Zero }, keys);
        var request = Request(folder, "https://edge.forgecdn.net/mod.jar") with
        { Urls = ["https://edge.forgecdn.net/mod.jar", "https://mirror.example.test/mod.jar"] };

        var result = await downloader.DownloadAsync(request);

        Assert.True(result.HashVerified);
        Assert.Equal(Payload, File.ReadAllBytes(folder.Target));
        Assert.Equal(1, keys.Calls);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://mirror.example.test/mod.jar", sent.Url);
        Assert.Null(sent.Key);
    }

    /// <summary>所有候选都需要缺失的官方凭证时，保留固定清晰的授权提示且不发出任何网络请求。</summary>
    [Fact]
    public async Task MissingKeyWithOnlyOfficialCandidatesPreservesSafeCredentialError()
    {
        using var folder = new Folder();
        var keys = new Keys(null);
        using var handler = Handler();
        using var downloader = new HttpFileDownloader(handler,
            new FileDownloadOptions { MaxAttemptsPerUrl = 3, RetryDelay = TimeSpan.Zero }, keys);
        var request = Request(folder, "https://edge.forgecdn.net/mod.jar") with
        { Urls = ["https://edge.forgecdn.net/mod.jar", "https://mediafilez.forgecdn.net/mod.jar"] };

        var error = await Assert.ThrowsAsync<CurseForgeCredentialException>(() => downloader.DownloadAsync(request));

        Assert.Equal(new CurseForgeCredentialException(CurseForgeCredentialFailure.NotConfigured).Message, error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(2, keys.Calls);
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>创建不重试的测试下载器，使每个地址和重定向断言对应一次请求。</summary>
    private static HttpFileDownloader Downloader(HttpMessageHandler handler, ICurseForgeApiKeyProvider keys) =>
        new(handler, new FileDownloadOptions { MaxAttemptsPerUrl = 1, RetryDelay = TimeSpan.Zero }, keys);

    /// <summary>创建包含真实 SHA-1 的固定下载请求。</summary>
    private static FileDownloadRequest Request(Folder folder, string url) => new()
    {
        DestinationPath = folder.Target, Urls = [url], ExpectedSize = Payload.Length,
        Hashes = new Dictionary<string, string> { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(Payload)) }
    };

    /// <summary>创建固定成功响应。</summary>
    private static HttpResponseMessage Response() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };

    /// <summary>创建不自动跟随的跳转响应。</summary>
    private static HttpResponseMessage Redirect(string next)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(next);
        return response;
    }

    /// <summary>返回所有请求都成功的内存传输。</summary>
    private static CaptureHandler Handler() => new((_, _, _) => Task.FromResult(Response()));

    /// <summary>为当前测试创建唯一下载目录，并只清理该目录。</summary>
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EML-cf-signing-" + Guid.NewGuid().ToString("N"));
        public string Target => System.IO.Path.Combine(Path, "mod.jar");
        /// <summary>创建隔离的测试目录。</summary>
        public Folder() => Directory.CreateDirectory(Path);
        /// <summary>清除本测试的文件，不接触真实实例。</summary>
        public void Dispose() => Directory.Delete(Path, true);
    }

    /// <summary>伪造可计数或轮换的凭证，避免测试访问实际编译密钥。</summary>
    private sealed class Keys(string? value = Secret) : ICurseForgeApiKeyProvider
    {
        public int Calls { get; private set; }
        public bool Rotate { get; init; }
        public Exception? Failure { get; init; }
        /// <summary>返回当前伪密钥，或模拟提供者故障。</summary>
        public string? GetApiKey()
        {
            Calls++;
            if (Failure is not null) throw Failure;
            return Rotate ? value + "-" + Calls : value;
        }
    }

    /// <summary>记录请求发送时的地址和密钥，避免检查已被释放的请求对象。</summary>
    private sealed record Sent(string Url, string? Key);

    /// <summary>拦截传输请求，以本地响应模拟网络和跳转。</summary>
    private sealed class CaptureHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<Sent> Requests { get; } = new();
        /// <summary>保存当前请求头后返回测试响应。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new(request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("x-api-key", out var values) ? string.Join(",", values) : null));
            return respond(Requests.Count, request, cancellationToken);
        }
    }

    /// <summary>同步交付进度，确保测试中的取消发生在最终文件发布前。</summary>
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        /// <summary>立即执行测试回调。</summary>
        public void Report(T value) => callback(value);
    }

    /// <summary>模拟响应体读取过程中回显请求头的传输故障。</summary>
    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        /// <summary>异步读取失败，供异常脱敏及临时文件清理断言使用。</summary>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException(Secret, new Exception(Secret)));
        /// <summary>同步读取也返回相同类型的敏感错误。</summary>
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException(Secret);
        /// <summary>只读流无需刷新。</summary>
        public override void Flush() { }
        /// <summary>不支持定位。</summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <summary>不支持设置长度。</summary>
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <summary>不允许写入。</summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
