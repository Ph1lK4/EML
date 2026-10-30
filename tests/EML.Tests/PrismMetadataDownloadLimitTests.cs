using System.Net;
using EML.Core.Downloads;
using EML.Infrastructure.Downloads;

namespace EML.Tests;

/// <summary>验证元数据请求可以单独收紧下载上限，所有 HTTP 内容均来自内存替身。</summary>
public sealed class PrismMetadataDownloadLimitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-metadata-limit-tests-" + Guid.NewGuid().ToString("N"));
    private string Destination => Path.Combine(_root, "metadata", "component.json");

    /// <summary>生成固定 HTTPS 元数据请求，允许测试不同的单次大小上限。</summary>
    private FileDownloadRequest Request(long? maximumBytes = null, long? expectedSize = null) => new()
    {
        DestinationPath = Destination, Urls = ["https://example.test/component.json"],
        MaximumBytes = maximumBytes, ExpectedSize = expectedSize
    };

    /// <summary>没有 Content-Length 时仍按实际读取量执行请求上限，失败不留下完整或临时文件。</summary>
    [Fact]
    public async Task RequestLimitRejectsUnknownLengthResponseDuringStreaming()
    {
        using var handler = new MetadataHandler(() => new StreamContent(new UnknownLengthStream([1, 2, 3, 4])));
        using var downloader = new HttpFileDownloader(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Request(maximumBytes: 3)));

        Assert.Equal(1, handler.Calls);
        AssertNoDownloadedFiles();
    }

    /// <summary>响应头声明超过请求上限时直接拒绝，不读取正文或创建临时文件。</summary>
    [Fact]
    public async Task RequestLimitRejectsAdvertisedLengthBeforeReadingBody()
    {
        var body = new CountingStream([1, 2, 3, 4]);
        using var handler = new MetadataHandler(() => new StreamContent(body));
        using var downloader = new HttpFileDownloader(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Request(maximumBytes: 3)));

        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, body.ReadCalls);
        AssertNoDownloadedFiles();
    }

    /// <summary>预期大小超过实际生效上限时在联网和创建目标目录前拒绝。</summary>
    [Theory]
    [InlineData(3L, 10L)]
    [InlineData(10L, 3L)]
    public async Task RejectsExpectedSizeAboveEffectiveLimitBeforeNetwork(long requestLimit, long globalLimit)
    {
        using var handler = new MetadataHandler(() => new ByteArrayContent([1, 2, 3, 4]));
        using var downloader = new HttpFileDownloader(handler, new FileDownloadOptions { MaximumFileBytes = globalLimit });

        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync(Request(requestLimit, expectedSize: 4)));

        Assert.Equal(0, handler.Calls);
        Assert.False(Directory.Exists(_root));
    }

    /// <summary>零和负数请求上限属于参数错误，不能访问网络或创建下载目录。</summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task RejectsInvalidRequestLimitBeforeNetwork(long limit)
    {
        using var handler = new MetadataHandler(() => new ByteArrayContent([1]));
        using var downloader = new HttpFileDownloader(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => downloader.DownloadAsync(Request(limit)));

        Assert.Equal(0, handler.Calls);
        Assert.False(Directory.Exists(_root));
    }

    /// <summary>单次请求给出更大上限不能绕过全局限制，响应头和无长度流均执行较小值。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RequestCannotRelaxGlobalDownloadLimit(bool advertiseLength)
    {
        using var handler = new MetadataHandler(() => advertiseLength
            ? new ByteArrayContent([1, 2, 3, 4]) : new StreamContent(new UnknownLengthStream([1, 2, 3, 4])));
        using var downloader = new HttpFileDownloader(handler, new FileDownloadOptions { MaximumFileBytes = 3 });

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Request(maximumBytes: 10)));

        Assert.Equal(1, handler.Calls);
        AssertNoDownloadedFiles();
    }

    /// <summary>恰好达到请求上限仍可以完成下载，未设置时保持原有全局上限行为。</summary>
    [Theory]
    [InlineData(4L)]
    [InlineData(null)]
    public async Task AllowsExactLimitAndPreservesDefaultBehavior(long? limit)
    {
        byte[] content = [1, 2, 3, 4];
        using var handler = new MetadataHandler(() => new ByteArrayContent(content));
        using var downloader = new HttpFileDownloader(handler, new FileDownloadOptions { MaximumFileBytes = 4 });

        var result = await downloader.DownloadAsync(Request(limit, expectedSize: 4));

        Assert.Equal(1, handler.Calls);
        Assert.Equal(4, result.BytesWritten);
        Assert.False(result.HashVerified);
        Assert.Equal(content, File.ReadAllBytes(Destination));
        Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    /// <summary>失败后目标与临时下载文件都不应残留，空目录可以保留。</summary>
    private void AssertNoDownloadedFiles()
    {
        Assert.False(File.Exists(Destination));
        if (Directory.Exists(_root)) Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    /// <summary>仅回收本测试创建且带随机标识的临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>使用可注入正文模拟成功 HTTP 响应并记录请求次数，避免真实联网。</summary>
    private sealed class MetadataHandler : HttpMessageHandler
    {
        private readonly Func<HttpContent> _content;
        public int Calls { get; private set; }

        /// <summary>保存每次请求的响应正文工厂。</summary>
        public MetadataHandler(Func<HttpContent> content) => _content = content;

        /// <summary>同步生成响应内容并转为完成任务，取消时不继续构造响应。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = _content() });
        }
    }

    /// <summary>保留内存读取行为，但不让 StreamContent 从流获取长度。</summary>
    private sealed class UnknownLengthStream : MemoryStream
    {
        public override bool CanSeek => false;

        /// <summary>使用固定测试字节创建只读且不公开长度的响应流。</summary>
        public UnknownLengthStream(byte[] content) : base(content, writable: false) { }
    }

    /// <summary>统计正文读取次数，证明超大的响应头会在消费正文前被拒绝。</summary>
    private sealed class CountingStream : MemoryStream
    {
        public int ReadCalls { get; private set; }

        /// <summary>使用可报告长度的内存流模拟已知大小响应。</summary>
        public CountingStream(byte[] content) : base(content, writable: false) { }

        /// <summary>记录下载器的异步正文读取，不改变实际返回内容。</summary>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
