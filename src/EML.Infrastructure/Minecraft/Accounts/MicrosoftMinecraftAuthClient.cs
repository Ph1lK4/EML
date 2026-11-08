using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EML.Core.Minecraft.Accounts;

namespace EML.Infrastructure.Minecraft.Accounts;

/// <summary>微软令牌仅在交换过程中使用；刷新凭证交给加密存储，禁止输出到日志。</summary>
internal sealed class MicrosoftTokens(string accessToken, string refreshToken)
{
    internal string AccessToken { get; } = accessToken;
    internal string RefreshToken { get; } = refreshToken;
    public override string ToString() => "Microsoft tokens [redacted]";
}

internal interface IMicrosoftMinecraftAuthClient
{
    Task<MicrosoftTokens> SignInAsync(IProgress<MinecraftLoginPrompt>? progress, CancellationToken token);
    Task<MicrosoftTokens> RefreshAsync(string refreshToken, CancellationToken token);
    Task<MinecraftAccountSession> ExchangeAsync(MicrosoftTokens tokens, CancellationToken token);
}

/// <summary>设备码 → Microsoft → Xbox Live → XSTS → Minecraft；所有请求仅发送到固定官方端点。</summary>
internal sealed class MicrosoftMinecraftAuthClient : IMicrosoftMinecraftAuthClient
{
    private const string MicrosoftRoot = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
    private const string MinecraftRoot = "https://api.minecraftservices.com/";
    private const string Scope = "XboxLive.SignIn XboxLive.offline_access";
    private readonly HttpClient _http;
    private readonly MicrosoftLoginOptions _options;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>测试可替换时钟和等待，生产遵守服务器指定的轮询间隔。</summary>
    internal MicrosoftMinecraftAuthClient(HttpClient http, MicrosoftLoginOptions options,
        TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    { _http = http; _options = options; _clock = clock ?? TimeProvider.System; _delay = delay ?? Task.Delay; }

    /// <summary>取得设备码后显示微软验证网址，等待授权、拒绝、过期或用户取消。</summary>
    public async Task<MicrosoftTokens> SignInAsync(IProgress<MinecraftLoginPrompt>? progress, CancellationToken token)
    {
        _options.Validate();
        using var challenge = await FormAsync("devicecode", new() { ["scope"] = Scope }, token).ConfigureAwait(false);
        var root = challenge.RootElement;
        var deviceCode = Text(root, "device_code");
        var userCode = Text(root, "user_code");
        if (!Uri.TryCreate(Text(root, "verification_uri"), UriKind.Absolute, out var url)) throw InvalidResponse();
        if (url.Scheme != "https" || !url.IsDefaultPort || !string.IsNullOrEmpty(url.UserInfo) ||
            url.Host is not ("microsoft.com" or "www.microsoft.com" or "login.microsoftonline.com" or "login.live.com"))
            throw InvalidResponse();
        var expiry = _clock.GetUtcNow().AddSeconds(Seconds(root, "expires_in", 3600));
        var interval = root.TryGetProperty("interval", out _) ? Seconds(root, "interval", 300) : 5;
        progress?.Report(new(userCode, url, expiry));
        while (_clock.GetUtcNow() < expiry)
        {
            await _delay(TimeSpan.FromSeconds(interval), token).ConfigureAwait(false);
            if (_clock.GetUtcNow() >= expiry) break;
            using var reply = await FormAsync("token", new()
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = deviceCode
            }, token, allowPending: true).ConfigureAwait(false);
            if (reply.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind != JsonValueKind.String) throw InvalidResponse();
                if (error.GetString() == "authorization_pending") continue;
                if (error.GetString() == "slow_down") { interval = Math.Min(interval + 5, 600); continue; }
                throw OAuthError(error.GetString());
            }
            return Tokens(reply.RootElement, null);
        }
        throw new MinecraftAuthenticationException("微软登录验证码已过期，请重新点击登录。");
    }

    /// <summary>刷新采用原应用 ID；服务器未轮换 refresh_token 时继续保留原凭证。</summary>
    public async Task<MicrosoftTokens> RefreshAsync(string refreshToken, CancellationToken token)
    {
        _options.Validate();
        using var reply = await FormAsync("token", new()
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["scope"] = Scope
        }, token).ConfigureAwait(false);
        return Tokens(reply.RootElement, refreshToken);
    }

    /// <summary>交换官方会话、确认 Java 版许可和档案；返回的玩家名和 UUID 只来自服务端。</summary>
    public async Task<MinecraftAccountSession> ExchangeAsync(MicrosoftTokens tokens, CancellationToken token)
    {
        using var xbox = await JsonAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + tokens.AccessToken },
            RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT"
        }, token, xbox: true).ConfigureAwait(false);
        var userHash = Text(Claim(xbox.RootElement), "uhs");
        using var xsts = await JsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { Text(xbox.RootElement, "Token") } },
            RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT"
        }, token, xbox: true).ConfigureAwait(false);
        var claims = Claim(xsts.RootElement);
        if (Text(claims, "uhs") != userHash) throw InvalidResponse();
        var xuid = claims.TryGetProperty("xid", out var xid) && xid.ValueKind == JsonValueKind.String ? xid.GetString()! : "";
        if (xuid.Any(c => c < '0' || c > '9') || xuid.Length > 32) throw InvalidResponse();
        using var minecraft = await JsonAsync(MinecraftRoot + "launcher/login", new
        {
            xtoken = "XBL3.0 x=" + userHash + ";" + Text(xsts.RootElement, "Token"), platform = "PC_LAUNCHER"
        }, token).ConfigureAwait(false);
        var accessToken = Text(minecraft.RootElement, "access_token");
        var expires = _clock.GetUtcNow().AddSeconds(Seconds(minecraft.RootElement, "expires_in", 604800));
        using var license = await GetAsync(MinecraftRoot + "entitlements/license?requestId=" + Guid.NewGuid().ToString(), accessToken, token).ConfigureAwait(false);
        if (!license.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        if (!items.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.String && name.GetString() is "game_minecraft" or "product_minecraft"))
            throw new MinecraftAuthenticationException("此账号没有可用的 Minecraft Java 版许可，请确认购买账号或 Game Pass 订阅。");
        using var profile = await GetAsync(MinecraftRoot + "minecraft/profile", accessToken, token).ConfigureAwait(false);
        var id = Text(profile.RootElement, "id");
        var nameValue = Text(profile.RootElement, "name");
        if (!Guid.TryParseExact(id, "N", out var uuid) || uuid == Guid.Empty || nameValue.Length is < 1 or > 16 ||
            nameValue.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))) throw InvalidResponse();
        return new(new(uuid.ToString("N"), nameValue), accessToken, expires, _options.ClientId, xuid);
    }

    /// <summary>构造表单；不把敏感参数放入 URL、默认请求头或异常文本。</summary>
    private Task<JsonDocument> FormAsync(string endpoint, Dictionary<string, string> fields, CancellationToken token, bool allowPending = false)
    {
        fields["client_id"] = _options.ClientId;
        return SendAsync(new(HttpMethod.Post, MicrosoftRoot + endpoint) { Content = new FormUrlEncodedContent(fields) }, token, allowPending);
    }

    /// <summary>Xbox 请求使用固定协议版本；请求对象由统一发送层释放。</summary>
    private Task<JsonDocument> JsonAsync(string url, object body, CancellationToken token, bool xbox = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = JsonContent.Create(body, options: new JsonSerializerOptions { PropertyNamingPolicy = null }) };
        if (xbox) request.Headers.Add("x-xbl-contract-version", "1");
        return SendAsync(request, token);
    }

    /// <summary>Bearer 仅附在单次 Minecraft 请求上，不污染下一次微软或 Xbox 请求。</summary>
    private Task<JsonDocument> GetAsync(string url, string accessToken, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return SendAsync(request, token);
    }

    /// <summary>限制响应大小和时间；错误只映射为安全提示，永不回显服务端正文。</summary>
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken token, bool allowPending = false)
    {
        using (request)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            HttpStatusCode? status = null;
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                request.Headers.Accept.Add(new("application/json"));
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                status = response.StatusCode;
                if (response.Content.Headers.ContentLength > 1024 * 1024) throw InvalidResponse();
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + count > 1024 * 1024) throw InvalidResponse();
                    output.Write(buffer, 0, count);
                }
                JsonDocument document;
                try { document = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 32 }); }
                // 拒绝请求时可能返回空正文或 HTML；此时保留 HTTP 错误，不能误报为 JSON 格式问题。
                catch (JsonException) when (!response.IsSuccessStatusCode)
                { throw HttpError(request, response.StatusCode); }
                try
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        throw response.IsSuccessStatusCode ? InvalidResponse() : HttpError(request, response.StatusCode);
                    if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    {
                        // 只有设备码协议的 400 轮询响应能继续等待；403/429 等错误不得被 pending 掩盖。
                        if (allowPending && response.StatusCode == HttpStatusCode.BadRequest &&
                            error.GetString() is "authorization_pending" or "slow_down") return document;
                        if (request.RequestUri!.Host == "login.microsoftonline.com") throw OAuthError(error.GetString());
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        if (request.RequestUri!.Host == "xsts.auth.xboxlive.com" && document.RootElement.TryGetProperty("XErr", out var xerr) && xerr.ValueKind == JsonValueKind.Number && xerr.TryGetInt64(out var code))
                            throw new MinecraftAuthenticationException($"Xbox 错误码：{code}。" + (code switch
                            {
                                2148916233 => "请先在 Xbox 官网创建此微软账号的 Xbox 档案，再重新登录。",
                                2148916238 or 2148916229 => "此账号需要家长或家庭组完成 Xbox 授权，请检查家庭和多人游戏设置。",
                                2148916235 => "Xbox 服务在此账号所属地区不可用。",
                                2148916236 or 2148916237 => "请先在微软账号页面完成年龄验证或检查游玩限制。",
                                _ => "Xbox 授权失败，请检查账号状态和 Xbox 服务权限。"
                            }));
                        if (response.StatusCode == HttpStatusCode.NotFound && request.RequestUri!.AbsolutePath == "/minecraft/profile")
                            throw new MinecraftAuthenticationException("此账号尚无 Minecraft Java 版档案，请先在官网设置 Java 版玩家名。");
                        throw HttpError(request, response.StatusCode);
                    }
                    return document;
                }
                catch { document.Dispose(); throw; }
            }
            catch (MinecraftAuthenticationException error)
            { throw RequestError(request, status, error.Message); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw RequestError(request, status, "登录服务响应超时，请检查网络后重试。"); }
            catch (HttpRequestException) { throw RequestError(request, status, "无法连接官方登录服务，请检查网络后重试。"); }
            catch (IOException) { throw RequestError(request, status, "读取登录响应失败，请检查网络后重试。"); }
            catch (JsonException) { throw RequestError(request, status, InvalidResponse().Message); }
        }
    }

    /// <summary>按实际被拒绝的服务给出提示；403 本身不能证明未购买游戏或应用一定未获准入。</summary>
    private static MinecraftAuthenticationException HttpError(HttpRequestMessage request, HttpStatusCode status) => new(status switch
    {
        HttpStatusCode.Unauthorized => "登录会话无效或已过期，请重新登录微软账号。",
        HttpStatusCode.Forbidden => request.RequestUri!.Host switch
        {
            "login.microsoftonline.com" => "微软拒绝了此请求，请检查应用的个人账号支持、公共客户端流程和账号访问限制。",
            "user.auth.xboxlive.com" or "xsts.auth.xboxlive.com" => "Xbox 拒绝了此请求，请检查 Xbox 档案、账号限制及 EML 应用的 Xbox 访问权限。",
            "api.minecraftservices.com" => "Minecraft 服务拒绝了此请求。请核对 EML 应用的 Minecraft API 访问权限；仅凭 403 不能判断账号是否拥有游戏。",
            _ => "服务拒绝了此请求，请检查应用权限和账号状态。"
        },
        HttpStatusCode.TooManyRequests => "登录请求过于频繁，请稍后重试。",
        _ => "登录服务暂时不可用，请稍后重试。"
    });

    /// <summary>诊断只由固定阶段、固定接口、数字状态和已净化提示组成，不包含查询串、正文、请求头或内部异常。</summary>
    private static MinecraftAuthenticationException RequestError(HttpRequestMessage request, HttpStatusCode? status, string message)
    {
        var (stage, endpoint) = (request.RequestUri!.Host, request.RequestUri.AbsolutePath) switch
        {
            ("login.microsoftonline.com", "/consumers/oauth2/v2.0/devicecode") =>
                ("微软设备码申请", "login.microsoftonline.com/consumers/oauth2/v2.0/devicecode"),
            ("login.microsoftonline.com", "/consumers/oauth2/v2.0/token") =>
                ("微软令牌获取", "login.microsoftonline.com/consumers/oauth2/v2.0/token"),
            ("user.auth.xboxlive.com", "/user/authenticate") => ("Xbox 登录", "user.auth.xboxlive.com/user/authenticate"),
            ("xsts.auth.xboxlive.com", "/xsts/authorize") => ("Xbox XSTS 授权", "xsts.auth.xboxlive.com/xsts/authorize"),
            ("api.minecraftservices.com", "/launcher/login") => ("Minecraft 登录", "api.minecraftservices.com/launcher/login"),
            ("api.minecraftservices.com", "/entitlements/license") => ("Minecraft 游戏许可查询", "api.minecraftservices.com/entitlements/license"),
            ("api.minecraftservices.com", "/minecraft/profile") => ("Minecraft 玩家档案查询", "api.minecraftservices.com/minecraft/profile"),
            _ => ("登录请求", "未识别的登录接口")
        };
        var statusText = status is { } code ? $"HTTP {(int)code}" : "未收到 HTTP 响应";
        return new($"{stage}失败（{statusText}）。\n接口：{endpoint}\n{message}");
    }

    /// <summary>读取敏感字符串时拒绝空值、控制字符和异常长度，不在错误中包含字段值。</summary>
    private static string Text(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String) throw InvalidResponse();
        var text = item.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32768 || text.Any(char.IsControl)) throw InvalidResponse();
        return text;
    }

    /// <summary>只接受合理的正数期限，避免无界等待或时间溢出。</summary>
    private static int Seconds(JsonElement root, string name, int maximum) =>
        root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var seconds) && seconds > 0 && seconds <= maximum
            ? seconds : throw InvalidResponse();

    /// <summary>Xbox 必须返回唯一用户声明，拒绝混用其他账号的令牌。</summary>
    private static JsonElement Claim(JsonElement root)
    {
        if (!root.TryGetProperty("DisplayClaims", out var display) || display.ValueKind != JsonValueKind.Object ||
            !display.TryGetProperty("xui", out var xui) || xui.ValueKind != JsonValueKind.Array || xui.GetArrayLength() != 1 ||
            xui[0].ValueKind != JsonValueKind.Object) throw InvalidResponse();
        return xui[0];
    }

    /// <summary>新的刷新凭证优先；初次登录没有刷新凭证时不报告持久登录成功。</summary>
    private static MicrosoftTokens Tokens(JsonElement root, string? previous) => new(Text(root, "access_token"),
        root.TryGetProperty("refresh_token", out _) ? Text(root, "refresh_token") : previous ?? throw InvalidResponse());

    /// <summary>响应解析错误统一为不包含字段值的提示。</summary>
    private static MinecraftAuthenticationException InvalidResponse() => new("官方登录响应格式无效，请稍后重试或更新启动器。");
    /// <summary>只映射已知 OAuth 错误码，不透传可能含敏感内容的 error_description。</summary>
    private static MinecraftAuthenticationException OAuthError(string? code) => new(code switch
    {
        "authorization_declined" or "access_denied" => "微软登录授权已被拒绝。",
        "expired_token" => "微软登录验证码已过期，请重新登录。",
        "invalid_grant" or "interaction_required" => "保存的登录授权已失效，请重新登录微软账号。",
        "invalid_client" or "unauthorized_client" or "invalid_scope" => "微软应用配置不正确，请检查 Client ID、个人账号支持和公共客户端登录设置。",
        _ => "微软账号授权失败，请重新登录或检查应用配置。"
    });
}
