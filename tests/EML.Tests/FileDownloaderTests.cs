using System.Net;
using System.Security.Cryptography;
using System.Text;
using EML.Core.Downloads;
using EML.Infrastructure.Downloads;

namespace EML.Tests;

/// <summary>通过内存 HTTP 响应验证下载边界，不连接网络或修改真实实例。</summary>
public sealed class FileDownloaderTests
{
    /// <summary>下载成功时同时验证所有声明哈希，并只留下完整目标文件。</summary>
    [Fact]
    public async Task Download_VerifiesEveryHashAndPublishesFile()
    {
        using var folder = new TestFolder();
        var bytes = Encoding.UTF8.GetBytes("Minecraft 文件下载测试");
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response(bytes)));
        using var downloader = CreateDownloader(handler);
        var progress = new List<FileDownloadProgress>();
        var result = await downloader.DownloadAsync(Request(folder, bytes), new InlineProgress<FileDownloadProgress>(progress.Add));
        Assert.True(result.HashVerified);
        Assert.Equal(bytes.Length, result.BytesWritten);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(folder.Target));
        Assert.Equal(100d, progress[^1].Percent);
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    /// <summary>通用下载允许没有哈希，但返回结果明确说明未做内容校验。</summary>
    [Fact]
    public async Task Download_WithoutHashDoesNotClaimVerification()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response([1, 2])));
        using var downloader = CreateDownloader(handler);
        var result = await downloader.DownloadAsync(new FileDownloadRequest { DestinationPath = folder.Target, Urls = ["https://example.test/file"] });
        Assert.False(result.HashVerified);
        Assert.Equal(2, result.BytesWritten);
    }

    /// <summary>任何一个哈希错误都不能发布文件，也不重试同一错误内容。</summary>
    [Fact]
    public async Task Download_RejectsSameSizeCorruptionAndCleansTemporaryFile()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response([1, 2, 4])));
        using var downloader = CreateDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Request(folder, [1, 2, 3])));
        Assert.Equal(1, handler.Calls);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>即使 SHA-256 正确，第二个声明哈希错误也必须阻止发布。</summary>
    [Fact]
    public async Task Download_DoesNotIgnoreAdditionalDeclaredHash()
    {
        using var folder = new TestFolder();
        byte[] bytes = [1, 2, 3];
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response(bytes)));
        using var downloader = CreateDownloader(handler);
        var request = Request(folder, bytes) with
        {
            Hashes = new Dictionary<string, string>
            {
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)),
                ["sha512"] = new string('0', 128)
            }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(request));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>响应头大小与清单不符时立即拒绝下载。</summary>
    [Fact]
    public async Task Download_RejectsAdvertisedSizeMismatch()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response([1, 2, 3])));
        using var downloader = CreateDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Request(folder, [1, 2])));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>没有 Content-Length 的响应仍然受实际写入大小上限约束。</summary>
    [Fact]
    public async Task Download_EnforcesSizeLimitWhileStreaming()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream([1, 2, 3, 4]))
        }));
        using var downloader = CreateDownloader(handler, new FileDownloadOptions { MaximumFileBytes = 3, MaxAttemptsPerUrl = 1 });
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(new FileDownloadRequest
        {
            DestinationPath = folder.Target, Urls = ["https://example.test/file"]
        }));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>取消发生在已写入部分数据后，临时文件和最终文件均不残留。</summary>
    [Fact]
    public async Task Download_CancellationCleansPartialFile()
    {
        using var folder = new TestFolder();
        using var cancellation = new CancellationTokenSource();
        var bytes = new byte[256 * 1024];
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response(bytes)));
        using var downloader = CreateDownloader(handler);
        var progress = new InlineProgress<FileDownloadProgress>(p => { if (p.BytesReceived > 0) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(Request(folder, bytes), progress, cancellation.Token));
        Assert.Empty(Directory.GetFiles(folder.Path));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>外部程序阻止删除临时文件时，清理错误不能掩盖用户主动取消。</summary>
    [Fact]
    public async Task Download_CleanupFailureDoesNotMaskCancellation()
    {
        using var folder = new TestFolder();
        using var cancellation = new CancellationTokenSource();
        byte[] bytes = [1, 2, 3];
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response(bytes)));
        using var downloader = CreateDownloader(handler);
        var progress = new InlineProgress<FileDownloadProgress>(p =>
        {
            if (p.BytesReceived == 0) return;
            foreach (var temporary in Directory.GetFiles(folder.Path, "*.part"))
                File.SetAttributes(temporary, FileAttributes.ReadOnly);
            cancellation.Cancel();
        });
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(Request(folder, bytes), progress, cancellation.Token));
            Assert.False(File.Exists(folder.Target));
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            // 测试主动设置了只读属性，只恢复本测试临时文件以便释放测试目录。
            foreach (var temporary in Directory.GetFiles(folder.Path, "*.part"))
                File.SetAttributes(temporary, FileAttributes.Normal);
        }
    }

    /// <summary>已有文件在发起网络请求之前被拒绝，原内容保持不变。</summary>
    [Fact]
    public async Task Download_NeverOverwritesExistingFile()
    {
        using var folder = new TestFolder();
        await File.WriteAllTextAsync(folder.Target, "用户文件");
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response([1])));
        using var downloader = CreateDownloader(handler);
        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(Request(folder, [1])));
        Assert.Equal("用户文件", await File.ReadAllTextAsync(folder.Target));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>下载过程中出现的同名文件也不能被覆盖。</summary>
    [Fact]
    public async Task Download_PreservesDestinationCreatedDuringDownload()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response([1, 2])));
        using var downloader = CreateDownloader(handler);
        var progress = new InlineProgress<FileDownloadProgress>(p =>
        {
            if (p.BytesReceived == 0) File.WriteAllText(folder.Target, "后来创建的文件");
        });
        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(Request(folder, [1, 2]), progress));
        Assert.Equal("后来创建的文件", await File.ReadAllTextAsync(folder.Target));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    /// <summary>临时服务器故障会重试，同一文件成功后只发布一次。</summary>
    [Fact]
    public async Task Download_Retries503()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, call, _) => Task.FromResult(call == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Response([7])));
        using var downloader = CreateDownloader(handler);
        await downloader.DownloadAsync(Request(folder, [7]));
        Assert.Equal(2, handler.Calls);
        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(folder.Target));
    }

    /// <summary>持续出现临时故障时也只能重试配置次数。</summary>
    [Fact]
    public async Task Download_BoundsTransientRetries()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var downloader = CreateDownloader(handler);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => downloader.DownloadAsync(Request(folder, [7])));
        Assert.Equal(3, handler.Calls);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>404 不反复重试，最终异常保留 HTTP 状态。</summary>
    [Fact]
    public async Task Download_DoesNotRetry404()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var downloader = CreateDownloader(handler);
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => downloader.DownloadAsync(Request(folder, [7])));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>第一个镜像不存在时可继续使用清单中明确声明的另一个地址。</summary>
    [Fact]
    public async Task Download_TriesNextMirrorAfter404()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, call, _) => Task.FromResult(call == 1
            ? new HttpResponseMessage(HttpStatusCode.NotFound) : Response([7])));
        using var downloader = CreateDownloader(handler);
        await downloader.DownloadAsync(Request(folder, [7]) with { Urls = ["https://one.test/file", "https://two.test/file"] });
        Assert.Equal(new[] { "https://one.test/file", "https://two.test/file" }, handler.Urls);
    }

    /// <summary>镜像返回错误内容时不发布该内容，并允许改用下一个镜像。</summary>
    [Fact]
    public async Task Download_TriesNextMirrorAfterHashMismatch()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, call, _) => Task.FromResult(Response(call == 1 ? [8] : [7])));
        using var downloader = CreateDownloader(handler);
        await downloader.DownloadAsync(Request(folder, [7]) with { Urls = ["https://one.test/file", "https://two.test/file"] });
        Assert.Equal(2, handler.Calls);
        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(folder.Target));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    /// <summary>允许 HTTPS 相对重定向，但仍在每一跳进行验证。</summary>
    [Fact]
    public async Task Download_FollowsHttpsRedirect()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, call, _) => Task.FromResult(call == 1
            ? Redirect("/download") : Response([7])));
        using var downloader = CreateDownloader(handler);
        await downloader.DownloadAsync(Request(folder, [7]));
        Assert.Equal("https://example.test/download", handler.Urls[1]);
    }

    /// <summary>HTTPS 降级或带凭据的重定向不得进入第二次网络请求。</summary>
    [Theory]
    [InlineData("http://example.test/insecure")]
    [InlineData("https://user:password@example.test/file")]
    public async Task Download_RejectsUnsafeRedirect(string target)
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Redirect(target)));
        using var downloader = CreateDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Request(folder, [7])));
        Assert.Equal(1, handler.Calls);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>循环重定向达到配置上限后结束，不无限请求。</summary>
    [Fact]
    public async Task Download_BoundsRedirects()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Redirect("/again")));
        using var downloader = CreateDownloader(handler, new FileDownloadOptions { MaxRedirects = 2, MaxAttemptsPerUrl = 1 });
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Request(folder, [7])));
        Assert.Equal(3, handler.Calls);
    }

    /// <summary>响应头迟迟不返回时，单次尝试超时仍能终止请求。</summary>
    [Fact]
    public async Task Download_AppliesDeadlineToHeaders()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Response([7]);
        });
        using var downloader = CreateDownloader(handler, new FileDownloadOptions
        {
            AttemptTimeout = TimeSpan.FromMilliseconds(100), MaxAttemptsPerUrl = 1
        });
        await Assert.ThrowsAsync<TimeoutException>(() => downloader.DownloadAsync(Request(folder, [7])));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>已经收到响应头后，停滞的响应体同样受本次尝试的超时约束。</summary>
    [Fact]
    public async Task Download_AppliesDeadlineToResponseBody()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new WaitingStream())
        }));
        using var downloader = CreateDownloader(handler, new FileDownloadOptions
        {
            AttemptTimeout = TimeSpan.FromMilliseconds(100), MaxAttemptsPerUrl = 1
        });
        await Assert.ThrowsAsync<TimeoutException>(() => downloader.DownloadAsync(Request(folder, [7])));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>用户可以在退避等待期间取消，无需等待下一次重试。</summary>
    [Fact]
    public async Task Download_CancelsRetryDelay()
    {
        using var folder = new TestFolder();
        using var cancellation = new CancellationTokenSource();
        using var handler = new StubHandler((_, _, _) =>
        {
            cancellation.CancelAfter(100);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using var downloader = CreateDownloader(handler, new FileDownloadOptions
        {
            RetryDelay = TimeSpan.FromSeconds(20), MaximumRetryDelay = TimeSpan.FromSeconds(20)
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(Request(folder, [7]), cancellationToken: cancellation.Token));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>不安全的起始地址在网络请求前被拒绝。</summary>
    [Theory]
    [InlineData("http://example.test/file")]
    [InlineData("file:///C:/file")]
    [InlineData("https://name:password@example.test/file")]
    public async Task Download_RejectsUnsafeSource(string source)
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response([7])));
        using var downloader = CreateDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Request(folder, [7]) with { Urls = [source] }));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>目录不能当作下载文件覆盖。</summary>
    [Fact]
    public async Task Download_RejectsDirectoryTarget()
    {
        using var folder = new TestFolder();
        Directory.CreateDirectory(folder.Target);
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response([7])));
        using var downloader = CreateDownloader(handler);
        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(Request(folder, [7])));
        Assert.True(Directory.Exists(folder.Target));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>未知哈希算法必须报告错误，不能在遗漏校验时声称成功。</summary>
    [Fact]
    public async Task Download_RejectsUnknownHashAlgorithm()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response([7])));
        using var downloader = CreateDownloader(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync(Request(folder, [7]) with
        {
            Hashes = new Dictionary<string, string> { ["md5"] = new string('0', 32) }
        }));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>没有请求断点续传时，服务器的部分内容不能当作完整文件发布。</summary>
    [Fact]
    public async Task Download_RejectsUnexpectedPartialResponse()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent([1])
        }));
        using var downloader = CreateDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(new FileDownloadRequest
        {
            DestinationPath = folder.Target, Urls = ["https://example.test/file"]
        }));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>下载保留中文、全角标点和合法空白，实际落盘名称与请求逐字相同。</summary>
    [Theory]
    [InlineData(" 旅行地图", "中文坐标（家）：矿脉？.json")]
    [InlineData("\u3000地图目录\u3000", "\u3000中文坐标.json\u3000")]
    [InlineData("\u00a0地图目录\u00a0", "\u00a0中文坐标.json\u00a0")]
    [InlineData("配置。", "【中文】“家”，矿脉！；《记录》、.json")]
    [InlineData("CON\u3000", "NUL\u00a0.json")]
    public async Task Download_PreservesLegalUnicodeDestinationNames(string directory, string filename)
    {
        using var folder = new TestFolder();
        byte[] bytes = [1, 2, 3];
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response(bytes)));
        using var downloader = CreateDownloader(handler);
        var destination = Path.Combine(folder.Path, directory, filename);
        var result = await downloader.DownloadAsync(Request(folder, bytes) with { DestinationPath = destination });

        Assert.True(result.HashVerified);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.Equal(directory, Path.GetFileName(Assert.Single(Directory.GetDirectories(folder.Path))));
        Assert.Equal(filename, Path.GetFileName(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(destination)!))));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Windows 备用数据流、保留名称和尾随点等非法目标在网络请求前被拒绝。</summary>
    [Theory]
    [InlineData("mod.jar:stream")]
    [InlineData("NUL.jar")]
    [InlineData("CON .jar")]
    [InlineData("COM¹.jar")]
    [InlineData("mod.jar.")]
    [InlineData("中文模组.jar ")]
    [InlineData("中文?.jar")]
    [InlineData("中文\t.jar")]
    [InlineData("../escape.jar")]
    public async Task Download_RejectsUnsafeDestinationNames(string filename)
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response([7])));
        using var downloader = CreateDownloader(handler);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => downloader.DownloadAsync(Request(folder, [7]) with
        {
            DestinationPath = Path.Combine(folder.Path, filename)
        }));
        Assert.Equal(0, handler.Calls);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>错误信息区分中文名称与真正非法的半角字符，并给出具体位置。</summary>
    [Fact]
    public async Task Download_ExplainsInvalidCharacterInChineseFilename()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response([7])));
        using var downloader = CreateDownloader(handler);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync(Request(folder, [7]) with
        {
            DestinationPath = Path.Combine(folder.Path, "中文坐标:矿脉.json")
        }));

        Assert.Contains("中文坐标:矿脉.json", error.Message);
        Assert.Contains("U+003A", error.Message);
        Assert.Equal(0, handler.Calls);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>释放下载器不会释放调用方注入的传输，传输仍可用于后续下载器。</summary>
    [Fact]
    public async Task Dispose_LeavesInjectedHandlerOwnedByCaller()
    {
        using var folder = new TestFolder();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Response([7])));
        var first = CreateDownloader(handler);
        first.Dispose();
        using var second = CreateDownloader(handler);
        await second.DownloadAsync(Request(folder, [7]));
        Assert.False(handler.WasDisposed);
    }

    /// <summary>创建具有全部受支持哈希的下载请求。</summary>
    private static FileDownloadRequest Request(TestFolder folder, byte[] bytes) => new()
    {
        DestinationPath = folder.Target,
        Urls = ["https://example.test/file"],
        ExpectedSize = bytes.Length,
        Hashes = new Dictionary<string, string>
        {
            ["sha1"] = Convert.ToHexString(SHA1.HashData(bytes)),
            ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)),
            ["sha512"] = Convert.ToHexString(SHA512.HashData(bytes))
        }
    };

    /// <summary>创建没有真实退避等待的测试下载器，特定测试可覆盖配置。</summary>
    private static HttpFileDownloader CreateDownloader(HttpMessageHandler handler, FileDownloadOptions? options = null) =>
        new(handler, options ?? new FileDownloadOptions { RetryDelay = TimeSpan.Zero, MaximumRetryDelay = TimeSpan.Zero });

    /// <summary>生成固定字节响应，并由下载器负责释放响应内容。</summary>
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    /// <summary>生成用于验证跳转规则的重定向响应。</summary>
    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    /// <summary>将 HTTP 请求交给内存回调，记录次数和地址以验证重试行为。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _respond;
        public int Calls { get; private set; }
        public bool WasDisposed { get; private set; }
        public List<string> Urls { get; } = [];

        /// <summary>保存本测试的响应生成函数。</summary>
        public StubHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        /// <summary>记录请求并传递取消令牌，完全避免真实网络访问。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(WasDisposed, this);
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return _respond(request, ++Calls, cancellationToken);
        }

        /// <summary>记录传输释放，验证下载器不会接管注入对象的生命周期。</summary>
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>同步执行进度回调，使取消和竞争条件测试具有确定顺序。</summary>
    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        /// <summary>保存同步进度回调。</summary>
        public InlineProgress(Action<T> report) => _report = report;

        /// <summary>立即执行回调，避免测试依赖线程池调度。</summary>
        public void Report(T value) => _report(value);
    }

    /// <summary>为每个测试创建并回收独立临时目录。</summary>
    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "eml-download-test-" + Guid.NewGuid().ToString("N"));
        public string Target => System.IO.Path.Combine(Path, "download.jar");

        /// <summary>建立唯一测试目录。</summary>
        public TestFolder() => Directory.CreateDirectory(Path);

        /// <summary>仅清理该测试自己创建的目录。</summary>
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>模拟不知道长度的响应流，防止大小检查仅依赖响应头。</summary>
    private class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;

        /// <summary>使用测试字节创建只读流。</summary>
        public NonSeekableStream(byte[] bytes) : base(bytes, writable: false) { }
    }

    /// <summary>模拟响应体一直没有新数据但支持取消的网络流。</summary>
    private sealed class WaitingStream : NonSeekableStream
    {
        /// <summary>创建不报告长度的等待流。</summary>
        public WaitingStream() : base([]) { }

        /// <summary>等待取消令牌触发，以验证下载器的响应体超时。</summary>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
