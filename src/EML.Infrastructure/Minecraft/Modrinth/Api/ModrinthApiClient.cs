using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using EML.Core.Modrinth;

namespace EML.Infrastructure.Minecraft.Modrinth.Api;

/// <summary>Modrinth v2 公开查询客户端，使用有界 JSON、取消与独立超时，不需要访问令牌。</summary>
public sealed class ModrinthApiClient : IModrinthApiClient, IDisposable
{
    private static readonly HttpClient SharedClient = CreateClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, MaxDepth = 64
    };
    private readonly HttpClient _client;
    private readonly ModrinthApiOptions _options;
    private readonly bool _ownsClient;
    private ModrinthRateLimit? _lastRateLimit;

    public ModrinthRateLimit? LastRateLimit => Volatile.Read(ref _lastRateLimit);

    /// <summary>使用共享连接池创建公开 API 客户端。</summary>
    public ModrinthApiClient(ModrinthApiOptions? options = null)
    {
        _options = ValidateOptions(options ?? new ModrinthApiOptions());
        _client = SharedClient;
    }

    /// <summary>注入测试或自定义传输，传输由调用方释放且不得自行跟随重定向。</summary>
    public ModrinthApiClient(HttpMessageHandler handler, ModrinthApiOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _options = ValidateOptions(options ?? new ModrinthApiOptions());
        DisableRedirects(handler);
        _client = CreateClient(handler, false);
        _ownsClient = true;
    }

    /// <summary>将类型化条件转为 facets：组间 AND、组内 OR，再执行分页搜索。</summary>
    public Task<ModrinthSearchResult> SearchAsync(ModrinthSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Offset < 0 || options.Limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(options), "分页偏移不得为负，单页数量须为 1–100。");
        if (options.Query is null || options.Query.Length > 2048)
            throw new ArgumentException("关键词长度不得超过 2048。", nameof(options));
        ArgumentNullException.ThrowIfNull(options.ProjectTypes);
        ArgumentNullException.ThrowIfNull(options.AdditionalFacetGroups);
        var groups = new List<string[]>();
        AddGroup(groups, "project_type", options.ProjectTypes.Select(ProjectTypeName).ToArray());
        AddGroup(groups, "versions", options.GameVersions);
        AddGroup(groups, "categories", options.Loaders);
        AddGroup(groups, "categories", options.Categories);
        AddGroup(groups, "environment", options.Environments);
        foreach (var group in options.AdditionalFacetGroups)
        {
            if (group is null || group.Count == 0) throw new ArgumentException("额外筛选组不能为空。", nameof(options));
            groups.Add(group.Select(facet => $"{FacetFieldName(facet.Field)}:{ValidateFacet(facet.Value)}").ToArray());
        }
        var query = new List<KeyValuePair<string, string>>
        {
            new("query", options.Query), new("index", SortName(options.Sort)),
            new("offset", options.Offset.ToString(CultureInfo.InvariantCulture)),
            new("limit", options.Limit.ToString(CultureInfo.InvariantCulture))
        };
        if (groups.Count > 0) query.Add(new("facets", JsonSerializer.Serialize(groups)));
        return GetAsync<ModrinthSearchResult>("search" + BuildQuery(query), cancellationToken);
    }

    /// <summary>查询一个项目，编码路径标识以避免其被解释为额外 API 路径。</summary>
    public async Task<ModrinthProject> GetProjectAsync(string projectIdOrSlug, CancellationToken cancellationToken = default)
    {
        var result = await GetAsync<ModrinthProject>($"project/{EncodeId(projectIdOrSlug)}", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Id)) throw new InvalidDataException("Modrinth 返回的项目缺少固定 ID。");
        return result;
    }

    /// <summary>查询一个固定版本，保留其全部文件与依赖供后续下载规划使用。</summary>
    public async Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken = default)
    {
        var result = await GetAsync<ModrinthVersion>($"version/{EncodeId(versionId)}", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Id)) throw new InvalidDataException("Modrinth 返回的版本缺少固定 ID。");
        return result;
    }

    /// <summary>按游戏版本、加载器和精选标记获取项目版本列表。</summary>
    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectIdOrSlug,
        ModrinthVersionFilter? filter = null, CancellationToken cancellationToken = default)
    {
        filter ??= new ModrinthVersionFilter();
        var query = new List<KeyValuePair<string, string>>();
        AddArrayQuery(query, "game_versions", filter.GameVersions);
        AddArrayQuery(query, "loaders", filter.Loaders);
        if (filter.Featured.HasValue) query.Add(new("featured", filter.Featured.Value ? "true" : "false"));
        query.Add(new("include_changelog", filter.IncludeChangelog ? "true" : "false"));
        return await GetAsync<List<ModrinthVersion>>($"project/{EncodeId(projectIdOrSlug)}/version{BuildQuery(query)}", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>执行一次请求并有界读取 JSON；限流返回等待信息，由调用方决定是否重试。</summary>
    private async Task<T> GetAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseAddress, relativePath));
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var resetSeconds = HeaderSeconds(response, "X-Ratelimit-Reset");
            var rate = new ModrinthRateLimit(HeaderInt(response, "X-Ratelimit-Limit"),
                HeaderInt(response, "X-Ratelimit-Remaining"), resetSeconds.HasValue ? now.AddSeconds(resetSeconds.Value) : null);
            Volatile.Write(ref _lastRateLimit, rate);
            if (!response.IsSuccessStatusCode)
            {
                var retry = response.Headers.RetryAfter?.Delta;
                if (!retry.HasValue && response.Headers.RetryAfter?.Date is { } date) retry = date - now;
                if (!retry.HasValue && resetSeconds.HasValue) retry = TimeSpan.FromSeconds(resetSeconds.Value);
                if (retry < TimeSpan.Zero) retry = TimeSpan.Zero;
                throw new ModrinthApiException(response.StatusCode, retry, rate);
            }
            if (response.Content.Headers.ContentLength > _options.MaximumJsonBytes)
                throw new InvalidDataException("Modrinth API 响应超过 JSON 大小限制。");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var count = await input.ReadAsync(chunk.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (buffer.Length + count > _options.MaximumJsonBytes)
                    throw new InvalidDataException("Modrinth API 响应超过 JSON 大小限制。");
                buffer.Write(chunk, 0, count);
            }
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), JsonOptions)
                    ?? throw new InvalidDataException("Modrinth API 返回空 JSON 数据。");
            }
            catch (JsonException ex) { throw new InvalidDataException("Modrinth API 返回了无法解析的 JSON。", ex); }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Modrinth API 请求超时。", ex);
        }
    }

    /// <summary>验证 HTTPS 地址、应用身份及可控的响应大小和超时。</summary>
    private static ModrinthApiOptions ValidateOptions(ModrinthApiOptions options)
    {
        if (options.BaseAddress is null || !options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme != Uri.UriSchemeHttps ||
            options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0 ||
            !options.BaseAddress.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("API 基础地址必须为以 / 结尾且不含凭证、查询或片段的 HTTPS 地址。");
        if (string.IsNullOrWhiteSpace(options.UserAgent) || options.UserAgent.Length > 512 ||
            options.UserAgent.Contains('\r') || options.UserAgent.Contains('\n'))
            throw new ArgumentException("必须提供可识别 EML 的 User-Agent。");
        using var probe = new HttpRequestMessage();
        probe.Headers.UserAgent.ParseAdd(options.UserAgent);
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(5) ||
            options.MaximumJsonBytes is < 1 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "API 超时或 JSON 大小限制无效。");
        return options;
    }

    /// <summary>创建无全局超时的客户端，各次请求使用独立的截止令牌。</summary>
    private static HttpClient CreateClient(HttpMessageHandler handler, bool disposeHandler) =>
        new(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>关闭标准传输自动重定向，避免跳到未经检查的 HTTP 地址。</summary>
    private static void DisableRedirects(HttpMessageHandler handler)
    {
        if (handler is HttpClientHandler client) client.AllowAutoRedirect = false;
        if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
        if (handler is DelegatingHandler { InnerHandler: not null } delegating) DisableRedirects(delegating.InnerHandler);
    }

    /// <summary>拒绝路径分隔符及控制字符，随后编码单个项目或版本标识。</summary>
    private static string EncodeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value is "." or ".." ||
            value.Any(c => char.IsControl(c) || c is '/' or '\\'))
            throw new ArgumentException("Modrinth 项目或版本标识无效。", nameof(value));
        return Uri.EscapeDataString(value);
    }

    /// <summary>验证 facet 值，阻止操作符进入简单相等条件。</summary>
    private static string ValidateFacet(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(c => char.IsControl(c) || c is ':' or '=' or '>' or '<'))
            throw new ArgumentException("Modrinth 筛选值无效。", nameof(value));
        return value;
    }

    /// <summary>把同类快捷筛选值加入一个 OR 分组。</summary>
    private static void AddGroup(List<string[]> groups, string field, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > 0) groups.Add(values.Select(value => $"{field}:{ValidateFacet(value)}").Distinct().ToArray());
    }

    /// <summary>按 API 约定把版本或加载器数组编码为 JSON 查询值。</summary>
    private static void AddArrayQuery(List<KeyValuePair<string, string>> query, string name, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > 0) query.Add(new(name, JsonSerializer.Serialize(values.Select(ValidateFacet).Distinct().ToArray())));
    }

    /// <summary>独立转义每个键值，避免关键词或 JSON 改变 URL 的结构。</summary>
    private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> values)
    {
        var text = string.Join('&', values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return text.Length == 0 ? "" : "?" + text;
    }

    /// <summary>读取非负的整数速率响应头。</summary>
    private static int? HeaderInt(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) &&
        int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;

    /// <summary>读取有限的重置秒数，异常响应头不参与等待时间计算。</summary>
    private static double? HeaderSeconds(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) &&
        double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value) && value is >= 0 and <= 86400 ? value : null;

    /// <summary>把项目类型转换为 API 使用的小写名称。</summary>
    private static string ProjectTypeName(ModrinthProjectType type) => type switch
    {
        ModrinthProjectType.Mod => "mod", ModrinthProjectType.Modpack => "modpack",
        ModrinthProjectType.ResourcePack => "resourcepack", ModrinthProjectType.Shader => "shader",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    /// <summary>把界面排序选项转换为 API 索引名称。</summary>
    private static string SortName(ModrinthSearchSort sort) => sort switch
    {
        ModrinthSearchSort.Relevance => "relevance", ModrinthSearchSort.Downloads => "downloads",
        ModrinthSearchSort.Follows => "follows", ModrinthSearchSort.Newest => "newest", ModrinthSearchSort.Updated => "updated",
        _ => throw new ArgumentOutOfRangeException(nameof(sort))
    };

    /// <summary>把允许的筛选字段转换为 API 字段名称。</summary>
    private static string FacetFieldName(ModrinthFacetField field) => field switch
    {
        ModrinthFacetField.ProjectType => "project_type", ModrinthFacetField.Version => "versions",
        ModrinthFacetField.Category => "categories", ModrinthFacetField.License => "license",
        ModrinthFacetField.Author => "author", ModrinthFacetField.ClientSide => "client_side",
        ModrinthFacetField.Environment => "environment",
        ModrinthFacetField.ServerSide => "server_side", _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    /// <summary>只释放本对象创建的客户端，不释放共享连接池或调用方提供的传输。</summary>
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
