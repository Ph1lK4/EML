using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using EML.Core.Downloads;
using EML.Infrastructure.Downloads;

namespace EML.Tests;

/// <summary>用确定性传输验证缓存、断点及限速，不访问网络或真实实例。</summary>
public sealed class FileDownloadCacheTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("0123456789-verified-download-payload");

    /// <summary>不同下载器与目标共享摘要缓存，命中不再连接网络。</summary>
    [Fact]
    public async Task Cache_ReusesAcrossDownloaderInstances()
    {
        using var folder = new Folder();
        using var firstHandler = new Handler((_, _) => Response(Payload));
        using (var first = new HttpFileDownloader(firstHandler, folder.Options))
            Assert.False((await first.DownloadAsync(folder.Request("one"))).CacheHit);
        using var secondHandler = new Handler((_, _) => throw new InvalidOperationException("不应联网"));
        using var second = new HttpFileDownloader(secondHandler, folder.Options);
        var progress = new List<FileDownloadProgress>();
        var result = await second.DownloadAsync(folder.Request("two"), new ProgressSink(progress.Add));
        Assert.True(result.CacheHit); Assert.True(result.HashVerified);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.DestinationPath));
        Assert.Equal(1, firstHandler.Calls); Assert.Equal(0, secondHandler.Calls);
        Assert.Contains(progress, item => item.FromCache && item.Phase == FileDownloadPhase.CopyingFromCache);
        Assert.Equal(FileDownloadPhase.Completed, progress[^1].Phase);
    }

    /// <summary>相同缓存目录和摘要的并发请求只需要一次网络传输。</summary>
    [Fact]
    public async Task Cache_DeduplicatesConcurrentRequests()
    {
        using var folder = new Folder();
        using var handler = new Handler((_, _) => Response(Payload));
        using var first = new HttpFileDownloader(handler, folder.Options);
        using var second = new HttpFileDownloader(handler, folder.Options);
        var results = await Task.WhenAll(first.DownloadAsync(folder.Request("one")), second.DownloadAsync(folder.Request("two")));
        Assert.Equal(1, handler.Calls); Assert.Single(results, result => result.CacheHit);
        Assert.All(results, result => Assert.Equal(Payload, File.ReadAllBytes(result.DestinationPath)));
    }

    /// <summary>缓存存在也不能覆盖调用方已经存在的目标。</summary>
    [Fact]
    public async Task Cache_NeverOverwritesDestination()
    {
        using var folder = new Folder();
        using var handler = new Handler((_, _) => Response(Payload));
        using var downloader = new HttpFileDownloader(handler, folder.Options);
        await downloader.DownloadAsync(folder.Request("one"));
        var request = folder.Request("existing");
        await File.WriteAllTextAsync(request.DestinationPath, "player data");
        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(request));
        Assert.Equal("player data", await File.ReadAllTextAsync(request.DestinationPath));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>缓存内容被破坏后重新下载并保留坏对象副本，不能把摘要文件名当作完整性证明。</summary>
    [Fact]
    public async Task Cache_RevalidatesBytesAndRecoversCorruption()
    {
        using var folder = new Folder();
        using var handler = new Handler((_, _) => Response(Payload));
        using var downloader = new HttpFileDownloader(handler, folder.Options);
        await downloader.DownloadAsync(folder.Request("one"));
        var blob = Assert.Single(Directory.GetFiles(folder.Cache, "*.blob"));
        var wrong = Payload.ToArray(); wrong[0] ^= 1;
        await File.WriteAllBytesAsync(blob, wrong);
        var result = await downloader.DownloadAsync(folder.Request("two"));
        Assert.False(result.CacheHit); Assert.Equal(2, handler.Calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.DestinationPath));
        Assert.Single(Directory.GetFiles(folder.Cache, "*.invalid-*"));
    }

    /// <summary>相同强摘要仍需要检查本次新增的 SHA-1 条件，不能只验证缓存键。</summary>
    [Fact]
    public async Task Cache_VerifiesAllCurrentHashes()
    {
        using var folder = new Folder();
        using var handler = new Handler((_, _) => Response(Payload));
        using var downloader = new HttpFileDownloader(handler, folder.Options);
        await downloader.DownloadAsync(folder.Request("one"));
        var request = folder.Request("two") with
        {
            Hashes = new Dictionary<string, string>
            { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Payload)), ["sha1"] = new string('a', 40) }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(request));
        Assert.False(File.Exists(request.DestinationPath));
    }

    /// <summary>无哈希请求即使配置缓存也每次联网，取消仍采用原来的临时文件清理。</summary>
    [Fact]
    public async Task Cache_DoesNotCacheUnverifiedRequests()
    {
        using var folder = new Folder();
        using var handler = new Handler((_, _) => Response(Payload));
        using var downloader = new HttpFileDownloader(handler, folder.Options);
        for (var index = 0; index < 2; index++)
        {
            var result = await downloader.DownloadAsync(folder.Request("plain" + index) with { Hashes = new Dictionary<string, string>() });
            Assert.False(result.CacheHit); Assert.False(result.HashVerified);
        }
        Assert.Equal(2, handler.Calls); Assert.False(Directory.Exists(folder.Cache));
    }

    /// <summary>取消保留强 ETag 片段，新下载器按精确 Range 和 If-Range 接着下载。</summary>
    [Fact]
    public async Task Resume_ContinuesAfterCancellationInNewDownloader()
    {
        using var folder = new Folder();
        await SavePartialAsync(folder);
        var metadata = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(folder.Cache, "*.resume.json")));
        Assert.DoesNotContain("secret-query", metadata); Assert.DoesNotContain("https://", metadata);
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(10, Assert.Single(request.Headers.Range!.Ranges).From);
            Assert.Equal("\"entity-one\"", request.Headers.IfRange!.EntityTag!.ToString());
            return Partial(Payload, 10);
        });
        using var downloader = new HttpFileDownloader(handler, folder.Options);
        var result = await downloader.DownloadAsync(folder.Request("resumed"));
        Assert.Equal(10, result.ResumedBytes); Assert.False(result.CacheHit);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.DestinationPath));
        Assert.Empty(Directory.GetFiles(folder.Cache, "*.part"));
        Assert.Empty(Directory.GetFiles(folder.Cache, "*.resume.json"));
    }

    /// <summary>服务器忽略 Range 返回 200 时丢弃旧前缀，从头校验完整响应。</summary>
    [Fact]
    public async Task Resume_FullResponseRestartsWithoutConcatenating()
    {
        using var folder = new Folder(); await SavePartialAsync(folder);
        using var handler = new Handler((request, _) => { Assert.NotNull(request.Headers.Range); return Response(Payload); });
        using var downloader = new HttpFileDownloader(handler, folder.Options);
        var result = await downloader.DownloadAsync(folder.Request("restarted"));
        Assert.Equal(0, result.ResumedBytes); Assert.Equal(Payload, await File.ReadAllBytesAsync(result.DestinationPath));
    }

    /// <summary>416 清理旧片段并进行一次无 Range 重试，不无限重置。</summary>
    [Fact]
    public async Task Resume_RangeNotSatisfiableResetsOnce()
    {
        using var folder = new Folder(); await SavePartialAsync(folder);
        using var handler = new Handler((request, call) =>
        {
            if (call == 1) { Assert.NotNull(request.Headers.Range); return new(HttpStatusCode.RequestedRangeNotSatisfiable); }
            Assert.Null(request.Headers.Range); return Response(Payload);
        });
        using var downloader = new HttpFileDownloader(handler, folder.Options);
        var result = await downloader.DownloadAsync(folder.Request("reset"));
        Assert.Equal(2, handler.Calls); Assert.Equal(0, result.ResumedBytes);
    }

    /// <summary>206 里的 ETag 改变时不能追加旧前缀，必须再次请求完整文件。</summary>
    [Fact]
    public async Task Resume_ChangedEntityTagRestarts()
    {
        using var folder = new Folder(); await SavePartialAsync(folder);
        using var handler = new Handler((request, call) =>
        {
            if (call == 1) return Partial(Payload, 10, "\"entity-two\"");
            Assert.Null(request.Headers.Range); return Response(Payload, "\"entity-two\"");
        });
        using var downloader = new HttpFileDownloader(handler, folder.Options);
        var result = await downloader.DownloadAsync(folder.Request("changed"));
        Assert.Equal(2, handler.Calls); Assert.Equal(0, result.ResumedBytes);
    }

    /// <summary>范围起点、总长度或实体长度异常不能发布，也不能保留错误续传状态。</summary>
    [Theory]
    [InlineData("start")]
    [InlineData("total")]
    [InlineData("length")]
    public async Task Resume_RejectsInvalidContentRange(string kind)
    {
        using var folder = new Folder(); await SavePartialAsync(folder);
        using var handler = new Handler((_, _) =>
        {
            var response = Partial(Payload, 10);
            if (kind == "start") response.Content.Headers.ContentRange = new ContentRangeHeaderValue(9, Payload.Length - 2, Payload.Length);
            if (kind == "total") response.Content.Headers.ContentRange = new ContentRangeHeaderValue(10, Payload.Length - 1, Payload.Length + 1);
            if (kind == "length") response.Content.Headers.ContentLength = 1;
            return response;
        });
        using var downloader = new HttpFileDownloader(handler, folder.Options);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(folder.Request("invalid")));
        Assert.False(File.Exists(folder.Request("invalid").DestinationPath));
        Assert.Empty(Directory.GetFiles(folder.Cache, "*.part"));
    }

    /// <summary>换镜像或签名 URL 时不发送旧来源的范围，最终内容仍受相同摘要约束。</summary>
    [Fact]
    public async Task Resume_MirrorChangeDiscardsOldPartial()
    {
        using var folder = new Folder(); await SavePartialAsync(folder);
        using var handler = new Handler((request, _) => { Assert.Null(request.Headers.Range); return Response(Payload); });
        using var downloader = new HttpFileDownloader(handler, folder.Options);
        var result = await downloader.DownloadAsync(folder.Request("mirror") with { Urls = ["https://mirror.test/file"] });
        Assert.Equal(0, result.ResumedBytes);
    }

    /// <summary>错误完整内容不留下可复用片段，后续镜像必须从头下载。</summary>
    [Fact]
    public async Task Resume_HashFailureResetsBeforeNextMirror()
    {
        using var folder = new Folder(); await SavePartialAsync(folder);
        using var handler = new Handler((request, call) =>
        {
            if (call == 1)
            {
                var wrong = Payload.ToArray(); wrong[^1] ^= 1;
                return Partial(wrong, 10);
            }
            Assert.Equal("mirror.test", request.RequestUri!.Host); Assert.Null(request.Headers.Range);
            return Response(Payload);
        });
        using var downloader = new HttpFileDownloader(handler, folder.Options);
        var result = await downloader.DownloadAsync(folder.Request("mirror") with
        { Urls = ["https://download.test/file?token=secret-query", "https://mirror.test/file"] });
        Assert.Equal(2, handler.Calls); Assert.Equal(0, result.ResumedBytes);
    }

    /// <summary>无强 ETag 的中断不能跨请求续传，弱标签不被用于 If-Range。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("W/\"weak\"")]
    public async Task Resume_DiscardsPartialWithoutStrongEntityTag(string? tag)
    {
        using var folder = new Folder(); await SavePartialAsync(folder, tag);
        Assert.Empty(Directory.GetFiles(folder.Cache, "*.part"));
        Assert.Empty(Directory.GetFiles(folder.Cache, "*.resume.json"));
    }

    /// <summary>相对路径和目录穿越配置不能借缓存功能写入任意位置。</summary>
    [Theory]
    [InlineData("../cache")]
    [InlineData("C:\\cache:stream")]
    public void Cache_RejectsUnsafeDirectory(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => new HttpFileDownloader(new FileDownloadOptions { CacheDirectory = path }));
    }

    /// <summary>限速等待可以长于网络超时，且无缓存请求的所有网络字节同样计入共享限速器。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BandwidthLimiter_DoesNotCountThrottleTimeAsNetworkTimeout(bool cached)
    {
        using var folder = new Folder();
        using var handler = new Handler((_, _) => Response(Payload));
        var limiter = new Limiter();
        using var downloader = new HttpFileDownloader(handler,
            folder.Options with { CacheDirectory = cached ? folder.Cache : null, AttemptTimeout = TimeSpan.FromMilliseconds(100) }, bandwidthLimiter: limiter);
        var result = await downloader.DownloadAsync(folder.Request("limited"));
        Assert.Equal(Payload.Length, limiter.Bytes);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.DestinationPath));
        if (cached)
        {
            await downloader.DownloadAsync(folder.Request("cached"));
            Assert.Equal(Payload.Length, limiter.Bytes);
        }
    }

    /// <summary>完成通知抛异常不会将已经发布成功的文件报告为失败或再次下载。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Progress_CompletedObserverFailureDoesNotChangeCommit(bool cached)
    {
        using var folder = new Folder();
        using var handler = new Handler((_, _) => Response(Payload));
        using var downloader = new HttpFileDownloader(handler, folder.Options with { CacheDirectory = cached ? folder.Cache : null });
        var result = await downloader.DownloadAsync(folder.Request("complete"), new ProgressSink(p =>
        { if (p.Phase == FileDownloadPhase.Completed) throw new IOException("observer"); }));
        Assert.True(File.Exists(result.DestinationPath)); Assert.Equal(1, handler.Calls);
    }

    /// <summary>只留下前十字节并在第二次读取时取消，模拟应用暂停或退出。</summary>
    private static async Task SavePartialAsync(Folder folder, string? tag = "\"entity-one\"")
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new InterruptStream(Payload[..10], cancellation)) };
            response.Content.Headers.ContentLength = Payload.Length;
            if (tag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(tag);
            return response;
        });
        using var downloader = new HttpFileDownloader(handler, folder.Options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(folder.Request("paused"), cancellationToken: cancellation.Token));
        Assert.False(File.Exists(folder.Request("paused").DestinationPath));
    }

    /// <summary>创建有明确完整长度和实体标签的内存响应。</summary>
    private static HttpResponseMessage Response(byte[] bytes, string tag = "\"entity-one\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Headers.ETag = EntityTagHeaderValue.Parse(tag); return response;
    }

    /// <summary>创建服务端严格响应指定后缀的 206 范围响应。</summary>
    private static HttpResponseMessage Partial(byte[] bytes, int offset, string tag = "\"entity-one\"")
    {
        var response = Response(bytes[offset..], tag); response.StatusCode = HttpStatusCode.PartialContent;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
        return response;
    }

    /// <summary>固定传输回调，计数用于检查重试、缓存去重和协议重置。</summary>
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(response(request, ++Calls)); }
    }

    /// <summary>测试同步进度，避免线程池回调改变取消和断言顺序。</summary>
    private sealed class ProgressSink(Action<FileDownloadProgress> report) : IProgress<FileDownloadProgress>
    { public void Report(FileDownloadProgress value) => report(value); }

    /// <summary>用可取消等待模拟主动节流，并累计所有网络读取字节。</summary>
    private sealed class Limiter : IDownloadBandwidthLimiter
    {
        public int Bytes { get; private set; }
        public async ValueTask WaitAsync(int byteCount, CancellationToken cancellationToken = default)
        { Bytes += byteCount; await Task.Delay(250, cancellationToken); }
    }

    /// <summary>首次读取给出可信前缀，第二次读取触发用户取消。</summary>
    private sealed class InterruptStream(byte[] prefix, CancellationTokenSource cancellation) : Stream
    {
        private bool _read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_read) { _read = true; prefix.AsMemory().CopyTo(buffer); return prefix.Length; }
            cancellation.Cancel(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>临时目录独占缓存与目标，释放时只删除本测试创建的内容。</summary>
    private sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "eml-download-cache-" + Guid.NewGuid().ToString("N"));
        public string Cache => Path.Combine(Root, "cache");
        public FileDownloadOptions Options => new() { CacheDirectory = Cache, MaxAttemptsPerUrl = 1, RetryDelay = TimeSpan.Zero };
        public Folder() => Directory.CreateDirectory(Root);
        public FileDownloadRequest Request(string name) => new()
        {
            DestinationPath = Path.Combine(Root, name), Urls = ["https://download.test/file?token=secret-query"], ExpectedSize = Payload.Length,
            Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Payload)) }
        };
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
