using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EML.Core.CurseForge;

namespace EML.Infrastructure.Minecraft.CurseForge.Api;

/// <summary>固定访问官方 HTTPS API，按需读取密钥，限制 JSON 大小，拒绝重定向及无限重试。</summary>
public sealed class CurseForgeApiClient : ICurseForgeApiClient, IDisposable
{
    private static readonly Uri BaseAddress = new("https://api.curseforge.com/v1/");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 64 };
    private static readonly HttpClient SharedClient = CreateClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }, true);
    private readonly HttpClient _client;
    private readonly ICurseForgeApiKeyProvider _keyProvider;
    private readonly CurseForgeApiOptions _options;
    private readonly bool _ownsClient;
    private CurseForgeRateLimit? _lastRateLimit;

    public CurseForgeRateLimit? LastRateLimit => Volatile.Read(ref _lastRateLimit);

    /// <summary>使用共享连接池与构建时注入的凭证；运行时不读取环境变量或本机凭据。</summary>
    public CurseForgeApiClient(ICurseForgeApiKeyProvider? keyProvider = null, CurseForgeApiOptions? options = null)
    {
        _keyProvider = keyProvider ?? new EmbeddedCurseForgeApiKeyProvider();
        _options = ValidateOptions(options ?? new CurseForgeApiOptions());
        _client = SharedClient;
    }

    /// <summary>注入可信传输及凭证提供者；传输禁止自行重定向，并由调用方负责释放。</summary>
    public CurseForgeApiClient(HttpMessageHandler handler, ICurseForgeApiKeyProvider keyProvider, CurseForgeApiOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(keyProvider);
        DisableRedirects(handler);
        _keyProvider = keyProvider;
        _options = ValidateOptions(options ?? new CurseForgeApiOptions());
        _client = CreateClient(handler, false);
        _ownsClient = true;
    }

    /// <summary>验证并去重文件 ID，按最多 50 个一批查询，不凭空补全服务器未返回的条目。</summary>
    public async Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(IReadOnlyList<long> fileIds, CancellationToken cancellationToken = default)
    {
        var ids = ValidateIds(fileIds);
        var files = new List<CurseForgeFile>();
        foreach (var batch in ids.Chunk(_options.BatchSize))
        {
            var envelope = await SendAsync<ApiEnvelope<List<CurseForgeFile>>>(HttpMethod.Post, "mods/files",
                new { fileIds = batch }, cancellationToken).ConfigureAwait(false);
            var data = envelope.Data ?? throw new InvalidDataException("CurseForge 文件响应缺少 data。");
            ValidateBatch(data, file => file.Id, batch);
            files.AddRange(data);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return files;
    }

    /// <summary>按最多 50 个一批读取项目；保留作者分发偏好的 null、true 与 false。</summary>
    public async Task<IReadOnlyList<CurseForgeMod>> GetModsAsync(IReadOnlyList<long> projectIds, CancellationToken cancellationToken = default)
    {
        var ids = ValidateIds(projectIds);
        var mods = new List<CurseForgeMod>();
        foreach (var batch in ids.Chunk(_options.BatchSize))
        {
            var envelope = await SendAsync<ApiEnvelope<List<CurseForgeMod>>>(HttpMethod.Post, "mods",
                new { modIds = batch, filterPcOnly = true }, cancellationToken).ConfigureAwait(false);
            var data = envelope.Data ?? throw new InvalidDataException("CurseForge 项目响应缺少 data。");
            ValidateBatch(data, mod => mod.Id, batch);
            mods.AddRange(data);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return mods;
    }

    /// <summary>获取并核实项目和文件的双重固定 ID。</summary>
    public async Task<CurseForgeFile> GetFileAsync(long projectId, long fileId, CancellationToken cancellationToken = default)
    {
        ValidateId(projectId);
        ValidateId(fileId);
        var envelope = await SendAsync<ApiEnvelope<CurseForgeFile>>(HttpMethod.Get,
            $"mods/{Number(projectId)}/files/{Number(fileId)}", null, cancellationToken).ConfigureAwait(false);
        var file = envelope.Data ?? throw new InvalidDataException("CurseForge 文件响应缺少 data。");
        if (file.Id != fileId || file.ModId != projectId) throw new InvalidDataException("CurseForge 返回的文件与请求的项目或版本不一致。");
        return file;
    }

    /// <summary>获取项目详情并核实服务器返回的固定 ID。</summary>
    public async Task<CurseForgeMod> GetModAsync(long projectId, CancellationToken cancellationToken = default)
    {
        ValidateId(projectId);
        var envelope = await SendAsync<ApiEnvelope<CurseForgeMod>>(HttpMethod.Get, "mods/" + Number(projectId), null, cancellationToken)
            .ConfigureAwait(false);
        var mod = envelope.Data ?? throw new InvalidDataException("CurseForge 项目响应缺少 data。");
        if (mod.Id != projectId) throw new InvalidDataException("CurseForge 返回的项目 ID 与请求不一致。");
        return mod;
    }

    /// <summary>将类型化筛选转成独立编码的查询参数，遵循最多 50 条及一万条窗口限制。</summary>
    public async Task<CurseForgeSearchResult> SearchAsync(CurseForgeSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.GameId <= 0 || options.ClassId is <= 0 || options.CategoryId is <= 0 ||
            options.Index < 0 || options.PageSize is < 1 or > 50 || options.Index > 10000 - options.PageSize)
            throw new ArgumentOutOfRangeException(nameof(options), "搜索编号或分页超出允许范围。");
        if (!Enum.IsDefined(options.SortField) || options.ModLoaderType.HasValue && !Enum.IsDefined(options.ModLoaderType.Value))
            throw new ArgumentException("搜索排序或加载器编号无效。", nameof(options));
        if (options.ModLoaderType.HasValue && string.IsNullOrWhiteSpace(options.GameVersion))
            throw new ArgumentException("加载器筛选必须同时指定 Minecraft 版本。", nameof(options));
        var query = new List<KeyValuePair<string, string>>
        {
            new("gameId", Number(options.GameId)), new("searchFilter", ValidateText(options.SearchFilter, 1024)),
            new("sortField", Number((int)options.SortField)), new("sortOrder", options.SortDescending ? "desc" : "asc"),
            new("index", Number(options.Index)), new("pageSize", Number(options.PageSize))
        };
        if (options.ClassId.HasValue) query.Add(new("classId", Number(options.ClassId.Value)));
        if (options.CategoryId.HasValue) query.Add(new("categoryId", Number(options.CategoryId.Value)));
        if (options.GameVersion is not null) query.Add(new("gameVersion", ValidateText(options.GameVersion, 128)));
        if (options.ModLoaderType.HasValue) query.Add(new("modLoaderType", Number((int)options.ModLoaderType.Value)));
        if (options.Slug is not null) query.Add(new("slug", ValidateText(options.Slug, 256)));
        var result = await SendAsync<CurseForgeSearchResult>(HttpMethod.Get, "mods/search?" +
            string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))),
            null, cancellationToken).ConfigureAwait(false);
        if (result.Data is null || result.Pagination is null) throw new InvalidDataException("CurseForge 搜索响应缺少结果或分页。");
        return result;
    }

    /// <summary>读取真实文件列表并核对项目归属，分页与查询参数使用独立编码。</summary>
    public async Task<CurseForgeFilePage> GetModFilesAsync(long projectId, CurseForgeFileFilter filter,
        CancellationToken cancellationToken = default)
    {
        ValidateId(projectId);
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Index < 0 || filter.PageSize is < 1 or > 50 || filter.Index > 10000 - filter.PageSize)
            throw new ArgumentOutOfRangeException(nameof(filter), "文件列表分页超出允许范围。");
        if (filter.ModLoaderType is { } loader && (!Enum.IsDefined(loader) || string.IsNullOrWhiteSpace(filter.GameVersion)))
            throw new ArgumentException("加载器筛选须指定有效编号和 Minecraft 版本。", nameof(filter));
        var query = new List<KeyValuePair<string, string>>
        {
            new("index", Number(filter.Index)), new("pageSize", Number(filter.PageSize))
        };
        if (filter.GameVersion is not null) query.Add(new("gameVersion", ValidateText(filter.GameVersion, 128)));
        if (filter.ModLoaderType.HasValue) query.Add(new("modLoaderType", Number((int)filter.ModLoaderType.Value)));
        var result = await SendAsync<CurseForgeFilePage>(HttpMethod.Get, $"mods/{Number(projectId)}/files?" +
            string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))),
            null, cancellationToken).ConfigureAwait(false);
        if (result.Data is null || result.Pagination is null || result.Data.Any(file => file is null || file.ModId != projectId || file.Id <= 0) ||
            result.Data.Select(file => file.Id).Distinct().Count() != result.Data.Count)
            throw new InvalidDataException("CurseForge 文件列表缺失或包含其他项目的文件。");
        return result;
    }

    /// <summary>获取正文 data 字符串；仍使用统一的有界响应和凭据保护。</summary>
    public async Task<string> GetModDescriptionAsync(long projectId, CancellationToken cancellationToken = default)
    {
        ValidateId(projectId);
        var result = await SendAsync<ApiEnvelope<string>>(HttpMethod.Get, $"mods/{Number(projectId)}/description",
            null, cancellationToken).ConfigureAwait(false);
        return result.Data ?? "";
    }

    /// <summary>只请求 Minecraft 模组分类，不把整合包或资源包分类混入筛选。</summary>
    public async Task<IReadOnlyList<CurseForgeCategory>> GetModCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<ApiEnvelope<List<CurseForgeCategory>>>(HttpMethod.Get,
            "categories?gameId=432&classId=6", null, cancellationToken).ConfigureAwait(false);
        return result.Data?.Where(category => category is not null && category.GameId == 432 && category.ClassId is null or 6).ToArray()
            ?? throw new InvalidDataException("CurseForge 分类响应缺少 data。");
    }

    /// <summary>只向固定官方主机发送当前密钥，并将传输异常换成不含秘密的固定诊断。</summary>
    private async Task<T> SendAsync<T>(HttpMethod method, string relativePath, object? body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.RequestTimeout);
        try
        {
            var address = new Uri(BaseAddress, relativePath);
            if (address.Scheme != Uri.UriSchemeHttps || address.Host != "api.curseforge.com" || address.Port != 443 ||
                address.UserInfo.Length != 0 || !address.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal))
                throw new InvalidOperationException("CurseForge 凭证只能发送到官方 HTTPS API。");
            using var request = new HttpRequestMessage(method, address);
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            CurseForgeRequestHeaders.AddApiKey(request, _keyProvider);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var retry = RetryAfter(response);
            var rate = new CurseForgeRateLimit(HeaderInt(response, "X-RateLimit-Limit"), HeaderInt(response, "X-RateLimit-Remaining"), retry);
            Volatile.Write(ref _lastRateLimit, rate);
            if (!response.IsSuccessStatusCode) throw new CurseForgeApiException(response.StatusCode, retry, rate);
            if (response.Content.Headers.ContentLength > _options.MaximumJsonBytes)
                throw new InvalidDataException("CurseForge API 响应超过 JSON 大小限制。");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var count = await input.ReadAsync(chunk.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (buffer.Length + count > _options.MaximumJsonBytes) throw new InvalidDataException("CurseForge API 响应超过 JSON 大小限制。");
                buffer.Write(chunk, 0, count);
            }
            deadline.Token.ThrowIfCancellationRequested();
            return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), JsonOptions)
                ?? throw new InvalidDataException("CurseForge API 返回空 JSON 数据。");
        }
        catch (CurseForgeApiException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("CurseForge API 请求已取消。", cancellationToken); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { throw new TimeoutException("CurseForge API 请求超时。"); }
        catch (JsonException) { throw new InvalidDataException("CurseForge API 返回了无法解析的 JSON。"); }
        catch (HttpRequestException) { throw new HttpRequestException("CurseForge API 连接失败，请检查网络后重试。"); }
        catch (IOException) { throw new IOException("无法读取 CurseForge API 响应，请检查网络后重试。"); }
        catch (InvalidDataException) { throw new InvalidDataException("CurseForge API 返回的数据无效或超出大小限制。"); }
        catch (CurseForgeCredentialException) { throw; }
        catch (Exception) { throw new InvalidOperationException("CurseForge API 请求未完成，请检查网络后重试。"); }
    }

    /// <summary>验证不含秘密的连接选项，批量请求最多 50 条。</summary>
    private static CurseForgeApiOptions ValidateOptions(CurseForgeApiOptions options)
    {
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(5) ||
            options.MaximumJsonBytes is < 1 or > 64 * 1024 * 1024 || options.BatchSize is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(options), "CurseForge 超时、JSON 大小或批量数量无效。");
        if (string.IsNullOrWhiteSpace(options.UserAgent) || options.UserAgent.Length > 512 || options.UserAgent.Any(char.IsControl))
            throw new ArgumentException("必须提供有效的 EML User-Agent。", nameof(options));
        using var probe = new HttpRequestMessage();
        probe.Headers.UserAgent.ParseAdd(options.UserAgent);
        return options;
    }

    /// <summary>检查输入列表并去重，限制单次公开调用的数量，避免无限批处理。</summary>
    private static long[] ValidateIds(IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count > 10000) throw new ArgumentOutOfRangeException(nameof(ids), "单次查询最多接受一万个 ID。");
        foreach (var id in ids) ValidateId(id);
        return ids.Distinct().ToArray();
    }

    /// <summary>拒绝零和负编号，不允许字符串进入官方端点路径。</summary>
    private static void ValidateId(long id)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id), "CurseForge ID 必须大于零。");
    }

    /// <summary>拒绝响应中不属于当前批次或重复的 ID，不把来源混淆交给下载阶段。</summary>
    private static void ValidateBatch<T>(IReadOnlyList<T> values, Func<T, long> getId, IReadOnlyList<long> requested)
    {
        var allowed = requested.ToHashSet();
        var seen = new HashSet<long>();
        foreach (var value in values)
            if (value is null || !allowed.Contains(getId(value)) || !seen.Add(getId(value)))
                throw new InvalidDataException("CurseForge 批量响应包含重复或非请求 ID。");
    }

    /// <summary>限制搜索文本长度和控制字符，后续再独立转义到 URL。</summary>
    private static string ValidateText(string value, int maximumLength)
    {
        if (value is null || value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException("CurseForge 搜索文本格式无效。");
        return value;
    }

    /// <summary>使用固定文化格式生成 URL 中的整数。</summary>
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>保留 HTTP Retry-After 秒数或时间，未声明时不推测固定额度。</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter?.Delta;
        if (!retry.HasValue && response.Headers.RetryAfter?.Date is { } date) retry = date - DateTimeOffset.UtcNow;
        return retry < TimeSpan.Zero ? TimeSpan.Zero : retry;
    }

    /// <summary>读取非负整数限流头，不信任无法解析的头值。</summary>
    private static int? HeaderInt(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) &&
        int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;

    /// <summary>创建按请求单独设置截止时间的客户端。</summary>
    private static HttpClient CreateClient(HttpMessageHandler handler, bool disposeHandler) =>
        new(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>关闭已知传输的重定向，防止 x-api-key 被带到其他主机。</summary>
    private static void DisableRedirects(HttpMessageHandler handler)
    {
        if (handler is HttpClientHandler client) client.AllowAutoRedirect = false;
        if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
        if (handler is DelegatingHandler { InnerHandler: not null } delegating) DisableRedirects(delegating.InnerHandler);
    }

    /// <summary>只释放自建客户端，调用方注入的传输和共享连接池保持可复用。</summary>
    public void Dispose() { if (_ownsClient) _client.Dispose(); }

    /// <summary>官方单项与批量响应的 data 包装。</summary>
    private sealed record ApiEnvelope<T>
    {
        public T? Data { get; init; }
    }
}
