using System.Net;
using EML.App.Services;

namespace EML.Tests;

public sealed class ModIconCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-icons-" + Guid.NewGuid().ToString("N"));

    /// <summary>不访问非平台地址、凭据 URI 和非 HTTPS 资源。</summary>
    [Theory]
    [InlineData("http://cdn.modrinth.com/icon.png")]
    [InlineData("https://evil.example/icon.png")]
    [InlineData("file:///C:/private.png")]
    [InlineData("https://secret@cdn.modrinth.com/icon.png")]
    public async Task RejectsUnsafeAddresses(string url)
    {
        using var transport = new Transport(); using var cache = new ModIconCache(_root, bytes => bytes, transport);
        Assert.Null(await cache.GetAsync(url)); Assert.Equal(0, transport.Calls);
    }

    /// <summary>同一图标只下载一次，输出放在启动器缓存而不是实例目录。</summary>
    [Fact]
    public async Task UsesValidatedDiskAndMemoryCache()
    {
        using var transport = new Transport(); using var cache = new ModIconCache(_root, bytes => bytes, transport);
        var first = await cache.GetAsync("https://cdn.modrinth.com/icon.png");
        Assert.NotNull(first); Assert.True(File.Exists(first)); Assert.Contains("mod-cache", first);
        Assert.Equal(first, await cache.GetAsync("https://cdn.modrinth.com/icon.png")); Assert.Equal(1, transport.Calls);
        using var other = new ModIconCache(_root, bytes => bytes, transport);
        Assert.Equal(first, await other.GetAsync("https://cdn.modrinth.com/icon.png")); Assert.Equal(1, transport.Calls);
    }

    /// <summary>图标响应实际大小超过限制时不调用解码器、不写缓存。</summary>
    [Fact]
    public async Task OversizedIconUsesPlaceholder()
    {
        var decoded = false;
        using var transport = new Transport { Reply = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]) } };
        using var cache = new ModIconCache(_root, bytes => { decoded = true; return bytes; }, transport);
        Assert.Null(await cache.GetAsync("https://cdn.modrinth.com/oversized.png")); Assert.False(decoded);
        Assert.False(Directory.Exists(_root));
    }

    /// <summary>损坏或无法安全解码的图像不阻断搜索，也不保存坏缓存。</summary>
    [Fact]
    public async Task DecoderFailureUsesPlaceholder()
    {
        using var transport = new Transport();
        using var cache = new ModIconCache(_root, _ => throw new InvalidDataException("bad image"), transport);
        Assert.Null(await cache.GetAsync("https://cdn.modrinth.com/broken.png")); Assert.False(Directory.Exists(_root));
    }

    /// <summary>解码器主动拒绝超限图标时直接使用占位图，不写入空缓存。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecoderRejectionUsesPlaceholder(bool emptyResult)
    {
        using var transport = new Transport();
        using var cache = new ModIconCache(_root, _ => emptyResult ? [] : null, transport);
        Assert.Null(await cache.GetAsync("https://cdn.modrinth.com/rejected.png"));
        Assert.Equal(1, transport.Calls);
        Assert.False(Directory.Exists(_root));
    }

    /// <summary>磁盘缓存解码被拒绝后重新下载，不把无效文件登记成可显示图标。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedDiskCacheIsDownloadedAgain(bool emptyResult)
    {
        const string address = "https://cdn.modrinth.com/cached.png";
        using var initialTransport = new Transport();
        using var initialCache = new ModIconCache(_root, bytes => bytes, initialTransport);
        var originalPath = await initialCache.GetAsync(address);
        Assert.NotNull(originalPath);

        byte[] replacement = [4, 5, 6];
        using var transport = new Transport { Reply = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(replacement) } };
        var attempts = 0;
        using var cache = new ModIconCache(_root, bytes =>
        {
            ++attempts;
            return bytes.SequenceEqual(replacement) ? bytes : emptyResult ? [] : null;
        }, transport);

        Assert.Equal(originalPath, await cache.GetAsync(address));
        Assert.Equal(replacement, await File.ReadAllBytesAsync(originalPath));
        Assert.Equal(2, attempts);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(originalPath, await cache.GetAsync(address));
        Assert.Equal(2, attempts);
        Assert.Equal(1, transport.Calls);
    }

    /// <summary>缓存与重新下载的图片都被拒绝时，不返回旧文件路径也不覆盖旧缓存。</summary>
    [Fact]
    public async Task RejectedDiskCacheAndDownloadUsePlaceholder()
    {
        const string address = "https://cdn.modrinth.com/still-rejected.png";
        using var transport = new Transport();
        using var initialCache = new ModIconCache(_root, bytes => bytes, transport);
        var originalPath = await initialCache.GetAsync(address);
        Assert.NotNull(originalPath);
        var originalBytes = await File.ReadAllBytesAsync(originalPath);

        using var cache = new ModIconCache(_root, _ => null, transport);
        Assert.Null(await cache.GetAsync(address));
        Assert.Equal(2, transport.Calls);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(originalPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(originalPath)!));
    }

    /// <summary>平台图标重定向到任意其他主机时不继续访问。</summary>
    [Fact]
    public async Task RejectsCrossHostRedirect()
    {
        using var transport = new Transport { Reply = _ => new(HttpStatusCode.Redirect) { Headers = { Location = new("https://evil.example/icon") } } };
        using var cache = new ModIconCache(_root, bytes => bytes, transport);
        Assert.Null(await cache.GetAsync("https://media.forgecdn.net/icon.png")); Assert.Equal(1, transport.Calls);
    }

    /// <summary>调用方取消被保留，缓存服务不会把取消误报成成功。</summary>
    [Fact]
    public async Task CancellationIsPropagated()
    {
        using var transport = new Transport(); using var cache = new ModIconCache(_root, bytes => bytes, transport);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync("https://cdn.modrinth.com/icon.png", cancellation.Token));
        Assert.Equal(0, transport.Calls);
    }

    /// <summary>只清理测试自行创建的随机缓存目录。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    /// <summary>所有请求均在内存处理，并验证图标请求不携带 API 授权头。</summary>
    private sealed class Transport : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? Reply { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            ++Calls; Assert.False(request.Headers.Contains("x-api-key")); Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Reply?.Invoke(request) ?? new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }
    }
}
