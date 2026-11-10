using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EML.App.Services;

namespace EML.App.Tests;

public sealed class WpfModIconDecoderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-wpf-icons-" + Guid.NewGuid().ToString("N"));

    /// <summary>真实 PNG 被解码为最多 96 像素的单帧 PNG，不沿用原始大图。</summary>
    [Fact]
    public void ValidPngProducesBoundedSingleFrameThumbnail()
    {
        var png = WpfModIconDecoder.Decode(CreatePng(512, 512));

        Assert.NotNull(png);
        using var stream = new MemoryStream(png, writable: false);
        var decoded = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = Assert.Single(decoded.Frames);
        Assert.Equal(96, frame.PixelWidth);
        Assert.Equal(96, frame.PixelHeight);
    }

    /// <summary>最大允许边长仍能显示，不把边界内的正常图标误判为超限。</summary>
    [Theory]
    [InlineData(2048, 64)]
    [InlineData(64, 2048)]
    [InlineData(2048, 2048)]
    public void MaximumAllowedDimensionIsAccepted(int width, int height)
    {
        var png = WpfModIconDecoder.Decode(CreatePng(width, height));

        Assert.NotNull(png);
        var size = ReadPngSize(png);
        Assert.InRange(size.Width, 1, 96);
        Assert.InRange(size.Height, 1, 96);
    }

    /// <summary>尺寸超限是正常的拒绝结果，不再主动抛出 InvalidDataException。</summary>
    [Theory]
    [InlineData(2049, 32)]
    [InlineData(32, 2049)]
    public void ExcessiveDimensionReturnsNullWithoutThrowing(int width, int height)
    {
        var bytes = CreatePng(width, height);
        byte[]? result = null;

        var exception = Record.Exception(() => result = WpfModIconDecoder.Decode(bytes));

        Assert.Null(exception);
        Assert.Null(result);
    }

    /// <summary>按真实多帧图片验证帧数边界，超限只返回空结果。</summary>
    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void FrameCountLimitIsPreserved(int count, bool accepted)
    {
        var encoder = new TiffBitmapEncoder();
        for (var index = 0; index < count; index++) encoder.Frames.Add(CreateFrame(8, 8));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        byte[]? result = null;

        var exception = Record.Exception(() => result = WpfModIconDecoder.Decode(stream.ToArray()));

        Assert.Null(exception);
        Assert.Equal(accepted, result is not null);
        if (accepted) Assert.Equal((8, 8), ReadPngSize(result!));
    }

    /// <summary>只缩放最长边，横向和纵向图标均保留原有宽高比。</summary>
    [Theory]
    [InlineData(384, 192, 96, 48)]
    [InlineData(192, 384, 48, 96)]
    public void ThumbnailPreservesAspectRatio(int width, int height, int expectedWidth, int expectedHeight)
    {
        var png = WpfModIconDecoder.Decode(CreatePng(width, height));

        Assert.NotNull(png);
        Assert.Equal((expectedWidth, expectedHeight), ReadPngSize(png));
    }

    /// <summary>小于缩略图边界的图片不放大，避免模糊和无意义的缓存增长。</summary>
    [Theory]
    [InlineData(32, 16)]
    [InlineData(16, 32)]
    [InlineData(1, 1)]
    public void SmallImagesAreNotUpscaled(int width, int height)
    {
        var png = WpfModIconDecoder.Decode(CreatePng(width, height));

        Assert.NotNull(png);
        Assert.Equal((width, height), ReadPngSize(png));
    }

    /// <summary>使用生产解码器验证损坏下载降级为占位图，不写入坏缓存。</summary>
    [Fact]
    public async Task CorruptDownloadUsesPlaceholderWithoutWritingCache()
    {
        using var transport = new ImageTransport([0, 1, 2, 3, 4]);
        using var cache = new ModIconCache(_root, WpfModIconDecoder.Decode, transport);

        var path = await cache.GetAsync("https://cdn.modrinth.com/corrupt-icon.png");

        Assert.Null(path);
        Assert.False(Directory.Exists(_root));
    }

    /// <summary>尺寸拒绝同样经过真实缓存调用链，并且不会生成缓存文件。</summary>
    [Fact]
    public async Task OversizedDownloadUsesPlaceholderWithoutWritingCache()
    {
        using var transport = new ImageTransport(CreatePng(2049, 32));
        using var cache = new ModIconCache(_root, WpfModIconDecoder.Decode, transport);

        var path = await cache.GetAsync("https://cdn.modrinth.com/oversized-icon.png");

        Assert.Null(path);
        Assert.False(Directory.Exists(_root));
    }

    /// <summary>缓存在线程池执行解码，实际 WPF 解码器也必须支持该调用方式。</summary>
    [Fact]
    public async Task DecoderWorksOnBackgroundMtaThread()
    {
        await Task.Run(() =>
        {
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            var png = WpfModIconDecoder.Decode(CreatePng(192, 96));
            Assert.NotNull(png);
            Assert.Equal((96, 48), ReadPngSize(png));
        });
    }

    /// <summary>在内存中生成尺寸确定的 PNG，测试不依赖网络图片或外部素材。</summary>
    private static byte[] CreatePng(int width, int height)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(CreateFrame(width, height));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>生成冻结的透明帧，供真实 PNG 和多帧编码器创建回归素材。</summary>
    private static BitmapFrame CreateFrame(int width, int height)
    {
        var stride = checked(width * 4);
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[checked(stride * height)], stride);
        image.Freeze();
        return BitmapFrame.Create(image);
    }

    /// <summary>重新读取输出 PNG 的实际像素，避免只验证非空而漏掉拉伸问题。</summary>
    private static (int Width, int Height) ReadPngSize(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = Assert.Single(decoder.Frames);
        return (frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>只清理本测试创建的随机临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>返回内存中的图片响应，不访问在线平台。</summary>
    private sealed class ImageTransport(byte[] bytes) : HttpMessageHandler
    {
        /// <summary>模拟成功下载，让生产缓存和解码逻辑处理实际响应内容。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
