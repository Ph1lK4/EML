using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace EML.App.Services;

/// <summary>有界下载与本机图标缓存；只访问两平台的图片主机，不发送授权信息。</summary>
public sealed class ModIconCache : IModIconCache, IDisposable
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private readonly string _root;
    private readonly HttpClient _client;
    private readonly Func<byte[], byte[]?> _decode;
    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly ConcurrentDictionary<string, string> _memory = new(StringComparer.Ordinal);

    /// <summary>解码器输出不超过 96 像素的 PNG，拒绝时返回空结果；测试可注入受控传输。</summary>
    public ModIconCache(string launcherRoot, Func<byte[], byte[]?> decode, HttpMessageHandler? handler = null)
    {
        _root = Path.Combine(Path.GetFullPath(launcherRoot), ".eml-runtime", "mod-cache", "icons");
        _decode = decode;
        if (handler is HttpClientHandler clientHandler) { clientHandler.AllowAutoRedirect = false; clientHandler.UseCookies = false; }
        if (handler is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; }
        _client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, handler is null)
            { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>读取缓存或下载并解码图标，任何展示错误均降级为占位图。</summary>
    public async Task<string?> GetAsync(string? address, CancellationToken token = default)
    {
        if (!Allowed(address, out var uri)) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var entered = false;
        string? temporary = null;
        try
        {
            await _slots.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
            EnsureNoLinks(_root);
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri!.AbsoluteUri)));
            var destination = Path.Combine(_root, key + ".png");
            if (_memory.TryGetValue(key, out var known) && Fresh(known)) return known;
            if (Fresh(destination))
            {
                // 本机缓存同样受大小和解码限制，坏缓存只会重新下载。
                try
                {
                    var cachedPng = _decode(await File.ReadAllBytesAsync(destination, deadline.Token).ConfigureAwait(false));
                    if (cachedPng is { Length: > 0 and <= MaximumBytes })
                    {
                        Remember(key, destination); return destination;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
            var bytes = await DownloadAsync(uri, deadline.Token).ConfigureAwait(false);
            var png = await Task.Run(() => _decode(bytes), deadline.Token).ConfigureAwait(false);
            if (png is not { Length: > 0 and <= MaximumBytes }) return null;
            deadline.Token.ThrowIfCancellationRequested();
            EnsureNoLinks(_root); Directory.CreateDirectory(_root); EnsureNoLinks(_root);
            temporary = Path.Combine(_root, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
            {
                await output.WriteAsync(png, deadline.Token).ConfigureAwait(false);
            }
            EnsureNoLinks(destination); deadline.Token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true); temporary = null;
            Remember(key, destination); return destination;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
        finally
        {
            if (temporary is not null)
                try { EnsureNoLinks(temporary); File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (entered) _slots.Release();
        }
    }

    /// <summary>逐跳核对 HTTPS 图片主机，响应体不信任 Content-Length 并限制实际读取量。</summary>
    private async Task<byte[]> DownloadAsync(Uri uri, CancellationToken token)
    {
        for (var redirect = 0; redirect <= 3; ++redirect)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("EML/1.0 (online-mod-icons)");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (response.Headers.Location is not { } location) break;
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!Allowed(next.AbsoluteUri, out var accepted)) break;
                uri = accepted!; continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("图标过大。");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream(); var chunk = new byte[8192];
            while (true)
            {
                var count = await input.ReadAsync(chunk, token).ConfigureAwait(false);
                if (count == 0) return buffer.ToArray();
                if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("图标过大。");
                buffer.Write(chunk, 0, count);
            }
        }
        throw new InvalidDataException("图标地址重定向不受支持。");
    }

    /// <summary>内存索引有固定上限；超过上限只丢弃索引，磁盘缓存仍可使用。</summary>
    private void Remember(string key, string path)
    {
        if (_memory.Count >= 512) _memory.Clear();
        _memory[key] = path;
    }

    /// <summary>缓存仅使用近期、大小有界且没有符号链接的本机文件。</summary>
    private static bool Fresh(string path)
    {
        EnsureNoLinks(path);
        var info = new FileInfo(path);
        return info.Exists && info.Length is > 0 and <= MaximumBytes && DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromDays(7);
    }

    /// <summary>拒绝凭据、非标准端口与任意外部主机。</summary>
    private static bool Allowed(string? address, out Uri? uri) => Uri.TryCreate(address, UriKind.Absolute, out uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Host is
        "cdn.modrinth.com" or "media.forgecdn.net" or "mediafilez.forgecdn.net";

    /// <summary>缓存目录及祖先不能通过链接指向实例文件或其他本机位置。</summary>
    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("图标缓存路径不能使用链接。");
    }

    /// <summary>宿主等待后台查询退出后释放连接和并发控制资源。</summary>
    public void Dispose() { _client.Dispose(); _slots.Dispose(); }
}
