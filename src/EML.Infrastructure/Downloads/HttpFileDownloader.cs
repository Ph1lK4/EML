using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using EML.Core.Downloads;
using EML.Infrastructure.IO;
using EML.Infrastructure.Minecraft.CurseForge.Api;

namespace EML.Infrastructure.Downloads;

/// <summary>HTTPS 流式下载器：支持镜像、有限重试、取消和哈希校验，只发布验证完成的新文件。</summary>
public sealed partial class HttpFileDownloader : IFileDownloader, IDisposable
{
    private const string CleanupErrorKey = "TemporaryFileCleanupError";
    private static readonly HttpClient SharedClient = CreateClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }, true);
    private readonly HttpClient _client;
    private readonly FileDownloadOptions _options;
    private readonly bool _ownsClient;
    private readonly ICurseForgeApiKeyProvider _curseForgeKeys;
    private readonly IDownloadBandwidthLimiter? _bandwidthLimiter;

    /// <summary>使用共享连接池创建下载器，各次下载仍有独立的超时和取消令牌。</summary>
    public HttpFileDownloader(FileDownloadOptions? options = null, ICurseForgeApiKeyProvider? curseForgeKeys = null,
        IDownloadBandwidthLimiter? bandwidthLimiter = null)
    {
        _client = SharedClient;
        _options = ValidateOptions(options ?? new FileDownloadOptions());
        _curseForgeKeys = curseForgeKeys ?? new EmbeddedCurseForgeApiKeyProvider();
        _bandwidthLimiter = bandwidthLimiter;
    }

    /// <summary>注入测试或自定义传输；传输不得自行跟随重定向，且仍由调用方负责释放。</summary>
    public HttpFileDownloader(HttpMessageHandler handler, FileDownloadOptions? options = null,
        ICurseForgeApiKeyProvider? curseForgeKeys = null, IDownloadBandwidthLimiter? bandwidthLimiter = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        DisableAutomaticRedirects(handler);
        _options = ValidateOptions(options ?? new FileDownloadOptions());
        _client = CreateClient(handler, false);
        _ownsClient = true;
        _curseForgeKeys = curseForgeKeys ?? new EmbeddedCurseForgeApiKeyProvider();
        _bandwidthLimiter = bandwidthLimiter;
    }

    /// <summary>逐个尝试地址；临时文件校验完成后以禁止覆盖的方式改名为最终文件。</summary>
    public async Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request,
        IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = ValidateDestination(request.DestinationPath);
        if (request.Urls is null || request.Urls.Count == 0)
            throw new ArgumentException("至少需要一个 HTTPS 下载地址。", nameof(request));
        var urls = request.Urls.Select(ValidateUrl).ToArray();
        var hashes = ValidateHashes(request.Hashes);
        if (request.MaximumBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "单次下载大小上限必须大于零。");
        // 元数据等小文件可以收紧限制，但单次请求不能放宽下载器的全局限制。
        var maximumBytes = Math.Min(_options.MaximumFileBytes, request.MaximumBytes ?? _options.MaximumFileBytes);
        if (request.ExpectedSize is < 0 || request.ExpectedSize > maximumBytes)
            throw new ArgumentException("文件大小超出下载限制。", nameof(request));
        EnsureDestinationAvailable(destination);
        var parent = Path.GetDirectoryName(destination)!;
        EnsureNoLinks(parent);
        Directory.CreateDirectory(parent);
        EnsureNoLinks(parent);

        if (_options.CacheDirectory is not null && hashes.Count > 0)
            return await DownloadCachedAsync(destination, urls, request.ExpectedSize, maximumBytes, hashes,
                progress, cancellationToken).ConfigureAwait(false);

        Exception? lastFailure = null;
        var attemptNumber = 0;
        foreach (var url in urls)
        {
            for (var attempt = 1; attempt <= _options.MaxAttemptsPerUrl; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureDestinationAvailable(destination);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(_options.AttemptTimeout);
                try
                {
                    return await DownloadAttemptAsync(destination, url, request.ExpectedSize, maximumBytes, hashes,
                        progress, ++attemptNumber, deadline.Token, deadline).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                {
                    lastFailure = new TimeoutException("本次下载超时。", ex);
                    if (ex.Data.Contains(CleanupErrorKey)) throw lastFailure;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TimeoutException or
                    CurseForgeCredentialException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ex.Data.Contains(CleanupErrorKey)) throw;
                    EnsureDestinationAvailable(destination);
                    // 固定且已脱敏的凭证失败不重试当前地址，但仍允许清单中显式提供的下一候选。
                    lastFailure = ex;
                }

                if (!IsTransient(lastFailure!) || attempt == _options.MaxAttemptsPerUrl) break;
                await Task.Delay(GetRetryDelay(lastFailure!, attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        throw lastFailure ?? new IOException("所有下载地址均未成功。");
    }

    /// <summary>下载一个候选地址，边写入边计算所有声明的哈希，失败时清理自己的临时文件。</summary>
    private async Task<FileDownloadResult> DownloadAttemptAsync(string destination, Uri url, long? expectedSize, long maximumBytes,
        IReadOnlyList<HashExpectation> expectations, IProgress<FileDownloadProgress>? progress,
        int attempt, CancellationToken cancellationToken, CancellationTokenSource deadline)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".eml-download-{Guid.NewGuid():N}.part");
        var ownsTemporary = false;
        Exception? originalFailure = null;
        var authentication = new DownloadAuthenticationState();
        try
        {
            using var response = await SendAsync(url, authentication, cancellationToken).ConfigureAwait(false);
            var advertisedSize = response.Content.Headers.ContentLength;
            if (advertisedSize > maximumBytes ||
                (expectedSize.HasValue && advertisedSize.HasValue && advertisedSize != expectedSize))
                throw new InvalidDataException("服务器返回的文件大小与要求不符。");

            EnsureDestinationAvailable(destination);
            EnsureNoLinks(Path.GetDirectoryName(destination)!);
            long received = 0;
            using var hashes = new HashCollection(expectations);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ownsTemporary = true;
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    progress?.Report(new FileDownloadProgress(0, expectedSize ?? advertisedSize, attempt));
                    var lastProgress = Stopwatch.GetTimestamp();
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                        if (count == 0) break;
                        await ThrottleAsync(count, deadline, cancellationToken).ConfigureAwait(false);
                        received = checked(received + count);
                        if (received > maximumBytes || (expectedSize.HasValue && received > expectedSize.Value))
                            throw new InvalidDataException("下载内容超过允许的文件大小。");
                        hashes.Append(buffer.AsSpan(0, count));
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        if (Stopwatch.GetElapsedTime(lastProgress) >= TimeSpan.FromMilliseconds(100))
                        {
                            progress?.Report(new FileDownloadProgress(received, expectedSize ?? advertisedSize, attempt));
                            lastProgress = Stopwatch.GetTimestamp();
                        }
                    }
                    progress?.Report(new FileDownloadProgress(received, expectedSize ?? advertisedSize, attempt));
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                if ((expectedSize.HasValue && received != expectedSize.Value) ||
                    (advertisedSize.HasValue && received != advertisedSize.Value))
                    throw new InvalidDataException("下载的实际文件大小与要求不符。");
                progress?.Report(new FileDownloadProgress(received, expectedSize ?? advertisedSize, attempt) { Phase = FileDownloadPhase.Verifying });
                hashes.Verify();
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDestinationAvailable(destination);
            EnsureNoLinks(temporary);
            File.Move(temporary, destination, overwrite: false);
            ownsTemporary = false;
            ReportCompleted(progress, received, attempt, false, 0);
            return new FileDownloadResult(destination, received, expectations.Count > 0);
        }
        catch (Exception ex)
        {
            // 自定义传输可能把请求头放进异常正文或 InnerException；受保护请求只保留固定诊断。
            originalFailure = authentication.IsProtected ? SanitizeCurseForgeFailure(ex) : ex;
            if (ReferenceEquals(originalFailure, ex)) throw;
            throw originalFailure;
        }
        finally
        {
            if (ownsTemporary)
            {
                // 只删除本次调用以 CreateNew 创建的随机临时文件，绝不删除最终目标。
                try
                {
                    EnsureNoLinks(temporary);
                    File.Delete(temporary);
                }
                catch (Exception ex) when (originalFailure is not null &&
                    ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // 杀毒软件或外部操作可能阻止清理；保留取消/下载错误及清理诊断，并停止继续重试。
                    originalFailure.Data[CleanupErrorKey] = ex.Message;
                    originalFailure.Data["TemporaryFilePath"] = temporary;
                }
            }
        }
    }

    /// <summary>每一跳新建请求并重新判断凭据范围；CurseForge 文件不能跳转到未批准的主机。</summary>
    private async Task<HttpResponseMessage> SendAsync(Uri initialUrl, DownloadAuthenticationState authentication,
        CancellationToken cancellationToken, long? resumeOffset = null, string? ifRange = null, bool allowRange = false)
    {
        var url = initialUrl;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (allowRange) request.Headers.AcceptEncoding.ParseAdd("identity");
            if (resumeOffset is > 0)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeOffset, null);
                if (ifRange is not null) request.Headers.IfRange = new System.Net.Http.Headers.RangeConditionHeaderValue(
                    System.Net.Http.Headers.EntityTagHeaderValue.Parse(ifRange));
            }
            if (CurseForgeRequestHeaders.IsCdnUri(url))
            {
                authentication.IsProtected = true;
                CurseForgeRequestHeaders.AddApiKey(request, _curseForgeKeys);
            }
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (allowRange && (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable))
                return response;
            if (response.IsSuccessStatusCode)
            {
                if (response.StatusCode != HttpStatusCode.PartialContent && response.Content.Headers.ContentRange is null)
                    return response;
                response.Dispose();
                throw new InvalidDataException("服务器只返回了文件片段，无法作为完整下载发布。");
            }
            using (response)
            {
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects >= _options.MaxRedirects || response.Headers.Location is null)
                        throw new InvalidDataException("下载重定向次数过多或缺少目标地址。");
                    var next = ValidateUrl(new Uri(url, response.Headers.Location).AbsoluteUri);
                    if (authentication.IsProtected && !CurseForgeRequestHeaders.IsCdnUri(next))
                        throw new InvalidDataException("CurseForge 文件不能重定向到未经批准的下载主机。");
                    url = next;
                    continue;
                }
                var retryAfter = response.Headers.RetryAfter;
                var delay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
                throw new DownloadHttpException(response.StatusCode, delay);
            }
        }
    }

    /// <summary>校验重试、文件大小和超时选项，拒绝无界等待配置。</summary>
    private static FileDownloadOptions ValidateOptions(FileDownloadOptions options)
    {
        if (options.MaxAttemptsPerUrl is < 1 or > 10 || options.MaxRedirects is < 0 or > 20 ||
            options.MaximumFileBytes <= 0 || options.AttemptTimeout <= TimeSpan.Zero ||
            options.AttemptTimeout > TimeSpan.FromDays(1) || options.RetryDelay < TimeSpan.Zero ||
            options.MaximumRetryDelay < TimeSpan.Zero || options.MaximumRetryDelay > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(options), "下载参数超出允许范围。");
        if (options.CacheDirectory is not null)
        {
            if (string.IsNullOrWhiteSpace(options.CacheDirectory) || !Path.IsPathFullyQualified(options.CacheDirectory))
                throw new ArgumentException("下载缓存目录必须是绝对路径。", nameof(options));
            var cache = Path.TrimEndingDirectorySeparator(options.CacheDirectory);
            // 复用普通路径检查，不接受设备路径、备用数据流或保留名。
            _ = ValidateDestination(Path.Combine(cache, "cache-path-check"));
            EnsureNoLinks(Path.GetFullPath(cache));
            return options with { CacheDirectory = Path.GetFullPath(cache) };
        }
        return options;
    }

    /// <summary>创建无全局超时的客户端，由每次尝试的令牌统一覆盖响应头和响应体。</summary>
    private static HttpClient CreateClient(HttpMessageHandler handler, bool disposeHandler)
    {
        var client = new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EML/1.0");
        return client;
    }

    /// <summary>关闭已知传输中的自动重定向，确保每次地址跳转都经过本类验证。</summary>
    private static void DisableAutomaticRedirects(HttpMessageHandler handler)
    {
        if (handler is HttpClientHandler http) http.AllowAutoRedirect = false;
        if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
        if (handler is DelegatingHandler { InnerHandler: not null } delegating)
            DisableAutomaticRedirects(delegating.InnerHandler);
    }

    /// <summary>只接收无用户名密码的 HTTPS 地址，官方 CDN 还必须通过端口及查询凭据检查。</summary>
    private static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host))
            throw new InvalidDataException("下载地址必须是无用户名密码的 HTTPS 地址。");
        if ((uri.IdnHost.Equals("edge.forgecdn.net", StringComparison.OrdinalIgnoreCase) ||
             uri.IdnHost.Equals("mediafilez.forgecdn.net", StringComparison.OrdinalIgnoreCase)) &&
            !CurseForgeRequestHeaders.IsCdnUri(uri))
            throw new InvalidDataException("CurseForge 文件地址含未批准的端口、片段或查询凭据。");
        if (uri.IdnHost.Equals("api.curseforge.com", StringComparison.OrdinalIgnoreCase) &&
            !CurseForgeRequestHeaders.IsApiUri(uri))
            throw new InvalidDataException("CurseForge API 地址含未批准的端口、片段或查询凭据。");
        return uri;
    }

    /// <summary>删除受保护请求异常中的原文、内部异常和附加数据，保留重试及取消所需的类型信息。</summary>
    private static Exception SanitizeCurseForgeFailure(Exception error) => error switch
    {
        DownloadHttpException known => known,
        CurseForgeCredentialException credential => credential,
        OperationCanceledException canceled => new OperationCanceledException("CurseForge 文件下载已取消。", canceled.CancellationToken),
        InvalidDataException => new InvalidDataException("CurseForge 文件内容或下载地址未通过校验。"),
        HttpRequestException request => new HttpRequestException("CurseForge 文件传输失败，请稍后重试。", null, request.StatusCode),
        TimeoutException => new TimeoutException("CurseForge 文件下载超时。"),
        UnauthorizedAccessException => new UnauthorizedAccessException("无法写入 CurseForge 下载文件，请检查目录权限。"),
        IOException => new IOException("CurseForge 文件传输或保存失败。"),
        _ => new InvalidOperationException("CurseForge 下载认证或传输配置无效。")
    };

    /// <summary>记录当前尝试是否进入过认证域，重定向后的内容读取错误也必须脱敏。</summary>
    private sealed class DownloadAuthenticationState
    {
        public bool IsProtected { get; set; }
    }

    /// <summary>要求绝对普通文件路径，并拒绝设备路径、备用数据流及 Windows 保留文件名。</summary>
    private static string ValidateDestination(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) ||
            value.Replace('/', '\\').StartsWith(@"\\?\", StringComparison.Ordinal) ||
            value.Replace('/', '\\').StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("下载目标必须是普通文件的绝对路径。", nameof(value));
        // 必须先检查原始名称；Windows 的 GetFullPath 会先去掉尾随点，掩盖非法输入。
        var root = Path.GetPathRoot(value)!;
        var parts = value[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        foreach (var part in parts)
        {
            if (WindowsFileNameRules.InvalidReason(part) is { } reason)
                throw new ArgumentException($"下载目标含非法文件名：{value}。文件名“{part}”：{reason}。", nameof(value));
        }
        return Path.GetFullPath(value);
    }

    /// <summary>验证所有哈希算法和值；未知算法不能被静默忽略。</summary>
    private static IReadOnlyList<HashExpectation> ValidateHashes(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new List<HashExpectation>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, text) in values)
        {
            var (algorithm, bytes) = name.ToLowerInvariant() switch
            {
                "sha1" => (HashAlgorithmName.SHA1, 20),
                "sha256" => (HashAlgorithmName.SHA256, 32),
                "sha512" => (HashAlgorithmName.SHA512, 64),
                _ => throw new ArgumentException("不支持的哈希算法：" + name, nameof(values))
            };
            if (!names.Add(name) || text is null || text.Length != bytes * 2 || !text.All(Uri.IsHexDigit))
                throw new ArgumentException("哈希值无效或算法重复。", nameof(values));
            result.Add(new HashExpectation(algorithm, Convert.FromHexString(text)));
        }
        return result;
    }

    /// <summary>检查父目录中的链接和现有目标，读取失败会保留原始错误而不是视为文件缺失。</summary>
    private static void EnsureDestinationAvailable(string destination)
    {
        EnsureNoLinks(destination);
        try
        {
            _ = File.GetAttributes(destination);
            throw new IOException("下载目标已存在，不能覆盖：" + destination);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>逐层拒绝符号链接和目录联接；尚未创建的路径允许继续检查其祖先。</summary>
    private static void EnsureNoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("下载路径含符号链接或目录联接：" + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>仅对超时、网络错误和临时 HTTP 状态重试，校验失败和 404 不重试同一地址。</summary>
    private static bool IsTransient(Exception error) => error switch
    {
        InvalidDataException => false,
        HttpRequestException { StatusCode: { } status } => status is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests || (int)status >= 500,
        HttpRequestException or IOException or TimeoutException => true,
        _ => false
    };

    /// <summary>计算有上限的退避时间，尊重服务器 Retry-After 但不允许无限等待。</summary>
    private TimeSpan GetRetryDelay(Exception error, int attempt)
    {
        var suggested = error is DownloadHttpException { RetryAfter: { } delay }
            ? Math.Max(0, delay.TotalMilliseconds)
            : _options.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(suggested, _options.MaximumRetryDelay.TotalMilliseconds));
    }

    /// <summary>主动限速等待不计入网络超时；有限速器时每个数据块重新开始网络空闲期限。</summary>
    private async ValueTask ThrottleAsync(int count, CancellationTokenSource deadline, CancellationToken token)
    {
        if (_bandwidthLimiter is null) return;
        deadline.CancelAfter(Timeout.InfiniteTimeSpan);
        try { await _bandwidthLimiter.WaitAsync(count, token).ConfigureAwait(false); }
        finally { if (!deadline.IsCancellationRequested) deadline.CancelAfter(_options.AttemptTimeout); }
    }

    /// <summary>只释放本对象创建的客户端包装器；共享连接池及注入的传输不受影响。</summary>
    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    /// <summary>保存每种哈希的预期值，用于流式计算结束后的统一比较。</summary>
    private sealed record HashExpectation(HashAlgorithmName Algorithm, byte[] Expected);

    /// <summary>统一管理一次下载需要的所有增量哈希对象。</summary>
    private sealed class HashCollection : IDisposable
    {
        private readonly IReadOnlyList<HashExpectation> _expectations;
        private readonly IncrementalHash[] _hashes;

        /// <summary>为清单中的每种算法建立一个增量计算器。</summary>
        public HashCollection(IReadOnlyList<HashExpectation> expectations)
        {
            _expectations = expectations;
            _hashes = expectations.Select(x => IncrementalHash.CreateHash(x.Algorithm)).ToArray();
        }

        /// <summary>同一块数据同时进入全部哈希计算器，避免再次读取文件。</summary>
        public void Append(ReadOnlySpan<byte> bytes)
        {
            foreach (var hash in _hashes) hash.AppendData(bytes);
        }

        /// <summary>检查所有计算结果，任一不一致都会阻止发布最终文件。</summary>
        public void Verify()
        {
            for (var i = 0; i < _hashes.Length; i++)
                if (!CryptographicOperations.FixedTimeEquals(_hashes[i].GetHashAndReset(), _expectations[i].Expected))
                    throw new InvalidDataException("下载文件的 " + _expectations[i].Algorithm.Name + " 校验失败。");
        }

        /// <summary>释放原生哈希计算资源。</summary>
        public void Dispose()
        {
            foreach (var hash in _hashes) hash.Dispose();
        }
    }

    /// <summary>保留 HTTP 状态和服务器建议等待时间，用于决定是否重试。</summary>
    private sealed class DownloadHttpException : HttpRequestException
    {
        public TimeSpan? RetryAfter { get; }

        /// <summary>构造不含完整 URL 或查询凭据的 HTTP 下载错误。</summary>
        public DownloadHttpException(HttpStatusCode status, TimeSpan? retryAfter)
            : base($"下载服务器返回 HTTP {(int)status}。", null, status)
        {
            RetryAfter = retryAfter;
        }
    }
}
