using System.Net;
using System.Text;
using System.Text.Json;
using EML.Core.Minecraft.Accounts;
using EML.Infrastructure.Minecraft.Accounts;

namespace EML.Tests;

/// <summary>固定响应测试真实 HTTP 编码和协议流，不使用真实用户授权或请求官方服务。</summary>
public sealed class MicrosoftMinecraftAuthTests
{
    internal const string ClientId = "11111111-2222-3333-4444-555555555555";
    internal const string PlayerId = "123456781234123412341234567890ab";
    private static readonly MicrosoftLoginOptions Options = new() { ClientId = ClientId };

    /// <summary>轮询遵循 interval 和 slow_down；只有短验证码被发送到 UI。</summary>
    [Fact]
    public async Task DeviceLoginPollsAndKeepsDeviceCodePrivate()
    {
        var http = new ScriptHandler();
        http.Add("/consumers/oauth2/v2.0/devicecode", new { device_code = "private-device", user_code = "ABCD-EFGH",
            verification_uri = "https://www.microsoft.com/link", expires_in = 900, interval = 5 });
        http.Add("/consumers/oauth2/v2.0/token", new { error = "authorization_pending" }, HttpStatusCode.BadRequest);
        http.Add("/consumers/oauth2/v2.0/token", new { error = "slow_down" }, HttpStatusCode.BadRequest);
        http.Add("/consumers/oauth2/v2.0/token", new { access_token = "msa-access", refresh_token = "msa-refresh" });
        var clock = new TestClock();
        var prompts = new List<MinecraftLoginPrompt>();
        using var client = new HttpClient(http);
        var tokens = await new MicrosoftMinecraftAuthClient(client, Options, clock, clock.Delay)
            .SignInAsync(new InlineProgress<MinecraftLoginPrompt>(prompts.Add), default);
        Assert.Equal("msa-refresh", tokens.RefreshToken);
        Assert.Equal(new[] { 5d, 5d, 10d }, clock.Delays.Select(value => value.TotalSeconds));
        Assert.Single(prompts);
        Assert.DoesNotContain("private-device", JsonSerializer.Serialize(prompts));
        Assert.Contains("scope=XboxLive.SignIn+XboxLive.offline_access", http.Requests[0].Body);
        Assert.All(http.Requests.Skip(1), request => Assert.Contains("device_code=private-device", request.Body));
        Assert.All(http.Requests, request => Assert.Null(request.Authorization));
    }

    /// <summary>拒绝、过期、应用配置错误都给固定提示，服务返回的敏感描述不回显。</summary>
    [Theory]
    [InlineData("authorization_declined", "拒绝")]
    [InlineData("expired_token", "过期")]
    [InlineData("invalid_client", "配置")]
    [InlineData("invalid_grant", "失效")]
    public async Task OAuthErrorsAreSanitized(string error, string expected)
    {
        var http = new ScriptHandler();
        http.Add("/consumers/oauth2/v2.0/token", new { error, error_description = "sensitive-response" }, HttpStatusCode.BadRequest);
        using var client = new HttpClient(http);
        var exception = await Assert.ThrowsAsync<MinecraftAuthenticationException>(() =>
            new MicrosoftMinecraftAuthClient(client, Options).RefreshAsync("refresh-private", default));
        Assert.Contains(expected, exception.Message);
        Assert.Contains("login.microsoftonline.com/consumers/oauth2/v2.0/token", exception.Message);
        Assert.Contains("400", exception.Message);
        Assert.DoesNotContain("sensitive-response", exception.ToString());
        Assert.DoesNotContain("refresh-private", exception.ToString());
    }

    /// <summary>不接受其他主机或脚本网址，避免把授权引导到非微软页面。</summary>
    [Theory]
    [InlineData("https://microsoft.com.evil.example/link")]
    [InlineData("http://www.microsoft.com/link")]
    [InlineData("https://evil@www.microsoft.com/link")]
    [InlineData("not a uri")]
    public async Task UnsafeVerificationUriIsRejected(string uri)
    {
        var http = new ScriptHandler();
        http.Add("/consumers/oauth2/v2.0/devicecode", new { device_code = "private", user_code = "code", verification_uri = uri, expires_in = 900 });
        using var client = new HttpClient(http);
        var shown = false;
        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => new MicrosoftMinecraftAuthClient(client, Options)
            .SignInAsync(new InlineProgress<MinecraftLoginPrompt>(_ => shown = true), default));
        Assert.False(shown);
        Assert.Single(http.Requests);
    }

    /// <summary>本地有效期到了便停止，不能在验证码失效后继续发请求。</summary>
    [Fact]
    public async Task LocalExpiryStopsPolling()
    {
        var http = new ScriptHandler();
        http.Add("/consumers/oauth2/v2.0/devicecode", new { device_code = "private", user_code = "code",
            verification_uri = "https://www.microsoft.com/link", expires_in = 5, interval = 5 });
        var clock = new TestClock();
        using var client = new HttpClient(http);
        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => new MicrosoftMinecraftAuthClient(client, Options, clock, clock.Delay).SignInAsync(null, default));
        Assert.Single(http.Requests);
    }

    /// <summary>用户取消立即结束等待，不再换取或保存任何凭证。</summary>
    [Fact]
    public async Task CancelDuringDevicePromptStopsBeforeTokenRequest()
    {
        var http = new ScriptHandler();
        http.Add("/consumers/oauth2/v2.0/devicecode", new { device_code = "private", user_code = "code",
            verification_uri = "https://www.microsoft.com/link", expires_in = 900 });
        using var client = new HttpClient(http);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MicrosoftMinecraftAuthClient(client, Options)
            .SignInAsync(new InlineProgress<MinecraftLoginPrompt>(_ => cancellation.Cancel()), cancellation.Token));
        Assert.Single(http.Requests);
    }

    /// <summary>未轮换 refresh_token 时沿用原值，避免成功刷新后丢失下次登录能力。</summary>
    [Fact]
    public async Task RefreshPreservesTokenWhenServerDoesNotRotate()
    {
        var http = new ScriptHandler();
        http.Add("/consumers/oauth2/v2.0/token", new { access_token = "new-access" });
        using var client = new HttpClient(http);
        var result = await new MicrosoftMinecraftAuthClient(client, Options).RefreshAsync("keep-refresh", default);
        Assert.Equal("keep-refresh", result.RefreshToken);
        Assert.DoesNotContain("keep-refresh", result.ToString());
    }

    /// <summary>使用固定的 Xbox JSON 大小写、目标服务和官方返回身份，不信任用户输入的玩家名。</summary>
    [Fact]
    public async Task ExchangeChecksLicenseAndProfileWithCorrectHeadersAndPayload()
    {
        var http = new ScriptHandler();
        AddXbox(http);
        AddMinecraft(http);
        using var client = new HttpClient(http);
        var session = await new MicrosoftMinecraftAuthClient(client, Options).ExchangeAsync(new("msa-secret", "refresh-secret"), default);
        Assert.Equal(new MinecraftAccount(PlayerId, "OnlineAlex"), session.Account);
        Assert.Equal("minecraft-secret", session.AccessToken);
        Assert.Equal("1234567", session.Xuid);
        Assert.Equal(ClientId, session.ClientId);
        using var xbox = JsonDocument.Parse(http.Requests[0].Body);
        Assert.Equal("d=msa-secret", xbox.RootElement.GetProperty("Properties").GetProperty("RpsTicket").GetString());
        Assert.Equal("JWT", xbox.RootElement.GetProperty("TokenType").GetString());
        using var xsts = JsonDocument.Parse(http.Requests[1].Body);
        Assert.Equal("rp://api.minecraftservices.com/", xsts.RootElement.GetProperty("RelyingParty").GetString());
        using var minecraft = JsonDocument.Parse(http.Requests[2].Body);
        Assert.Equal("XBL3.0 x=user-hash;xsts-secret", minecraft.RootElement.GetProperty("xtoken").GetString());
        Assert.Equal("PC_LAUNCHER", minecraft.RootElement.GetProperty("platform").GetString());
        Assert.All(http.Requests.Take(3), request => Assert.Null(request.Authorization));
        Assert.All(http.Requests.Skip(3), request => Assert.Equal("Bearer minecraft-secret", request.Authorization));
        Assert.DoesNotContain("minecraft-secret", JsonSerializer.Serialize(session));
        Assert.DoesNotContain("minecraft-secret", session.ToString());
    }

    /// <summary>无 Java 许可或没有 Java 档案时不把微软登录视为可启动的游戏会话。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOwnershipOrProfileIsRejected(bool hasLicense)
    {
        var http = new ScriptHandler();
        AddXbox(http);
        http.Add("/launcher/login", new { access_token = "minecraft-secret", expires_in = 3600 });
        http.Add("/entitlements/license", new { items = hasLicense ? new[] { new { name = "game_minecraft" } } : [] });
        if (hasLicense) http.Add("/minecraft/profile", new { error = "NOT_FOUND" }, HttpStatusCode.NotFound);
        using var client = new HttpClient(http);
        var error = await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => new MicrosoftMinecraftAuthClient(client, Options)
            .ExchangeAsync(new("msa", "refresh"), default));
        Assert.Contains(hasLicense ? "档案" : "许可", error.Message);
    }

    /// <summary>XSTS 不能换成另一用户，交换出现身份不一致时停止后续 Minecraft 请求。</summary>
    [Fact]
    public async Task MismatchedXboxUserHashIsRejected()
    {
        var http = new ScriptHandler();
        AddXbox(http, xstsHash: "someone-else");
        using var client = new HttpClient(http);
        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => new MicrosoftMinecraftAuthClient(client, Options).ExchangeAsync(new("msa", "refresh"), default));
        Assert.Equal(2, http.Requests.Count);
    }

    /// <summary>常见 Xbox 前置条件通过明确提示呈现，不输出 XSTS 原始响应。</summary>
    [Theory]
    [InlineData(2148916233L, "档案")]
    [InlineData(2148916238L, "家长")]
    [InlineData(2148916235L, "地区")]
    public async Task XboxFailureExplainsRequiredAccountAction(long code, string expected)
    {
        var http = new ScriptHandler();
        http.Add("/user/authenticate", new { Token = "xbox", DisplayClaims = new { xui = new[] { new { uhs = "hash" } } } });
        http.Add("/xsts/authorize", new { XErr = code, Message = "private-server-message" }, HttpStatusCode.Unauthorized);
        using var client = new HttpClient(http);
        var error = await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => new MicrosoftMinecraftAuthClient(client, Options).ExchangeAsync(new("msa", "refresh"), default));
        Assert.Contains(expected, error.Message);
        Assert.Contains("xsts.auth.xboxlive.com/xsts/authorize", error.Message);
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("private-server-message", error.ToString());
        Assert.Equal(2, http.Requests.Count);
    }

    /// <summary>验证码申请或轮询被拒绝时指出具体微软接口，不把网页授权当成游戏登录成功。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MicrosoftForbiddenIdentifiesDeviceOrTokenRequest(bool afterDevicePrompt)
    {
        var http = new ScriptHandler();
        if (afterDevicePrompt)
            http.Add("/consumers/oauth2/v2.0/devicecode", new { device_code = "private-device", user_code = "public-code",
                verification_uri = "https://www.microsoft.com/link", expires_in = 900, interval = 5 });
        var path = afterDevicePrompt ? "/consumers/oauth2/v2.0/token" : "/consumers/oauth2/v2.0/devicecode";
        http.Add(path, new { message = "private-response", access_token = "private-response-token" }, HttpStatusCode.Forbidden);
        var clock = new TestClock();
        using var client = new HttpClient(http);
        var error = await Assert.ThrowsAsync<MinecraftAuthenticationException>(() =>
            new MicrosoftMinecraftAuthClient(client, Options, clock, clock.Delay).SignInAsync(null, default));

        Assert.Contains("login.microsoftonline.com" + path, error.Message);
        Assert.Contains("403", error.Message);
        Assert.DoesNotContain("private-response", error.ToString());
        Assert.DoesNotContain("private-device", error.ToString());
        Assert.Equal(afterDevicePrompt ? 2 : 1, http.Requests.Count);
    }

    /// <summary>刷新被拒绝时保留接口和 HTTP 状态，但不能把旧刷新凭证写进异常。</summary>
    [Fact]
    public async Task RefreshForbiddenIdentifiesRequestWithoutLeakingCredential()
    {
        var http = new ScriptHandler();
        http.Add("/consumers/oauth2/v2.0/token", new { message = "private-response" }, HttpStatusCode.Forbidden);
        using var client = new HttpClient(http);
        var error = await Assert.ThrowsAsync<MinecraftAuthenticationException>(() =>
            new MicrosoftMinecraftAuthClient(client, Options).RefreshAsync("private-refresh-token", default));

        Assert.Contains("login.microsoftonline.com/consumers/oauth2/v2.0/token", error.Message);
        Assert.Contains("403", error.Message);
        Assert.DoesNotContain("private-response", error.ToString());
        Assert.DoesNotContain("private-refresh-token", error.ToString());
        Assert.Single(http.Requests);
    }

    /// <summary>HTTP 状态不是 OAuth 的正常轮询响应时不能继续等待；403 的 pending 也必须定位为失败。</summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "authorization_pending")]
    [InlineData(HttpStatusCode.TooManyRequests, "slow_down")]
    public async Task PollingErrorStatusDoesNotContinueAfterPendingLikeBody(HttpStatusCode status, string oauthError)
    {
        var http = new ScriptHandler();
        http.Add("/consumers/oauth2/v2.0/devicecode", new { device_code = "private-device", user_code = "public-code",
            verification_uri = "https://www.microsoft.com/link", expires_in = 900, interval = 5 });
        http.Add("/consumers/oauth2/v2.0/token", new { error = oauthError, error_description = "private-response" }, status);
        var clock = new TestClock();
        using var client = new HttpClient(http);
        var error = await Assert.ThrowsAsync<MinecraftAuthenticationException>(() =>
            new MicrosoftMinecraftAuthClient(client, Options, clock, clock.Delay).SignInAsync(null, default));

        Assert.Contains("login.microsoftonline.com/consumers/oauth2/v2.0/token", error.Message);
        Assert.Contains(((int)status).ToString(), error.Message);
        Assert.DoesNotContain("private-response", error.ToString());
        Assert.Equal(2, http.Requests.Count);
    }

    /// <summary>交换链每个阶段的 403 都能单独定位，失败后停止下游请求且不泄漏身份和令牌。</summary>
    [Theory]
    [InlineData("/user/authenticate", "user.auth.xboxlive.com", 1)]
    [InlineData("/xsts/authorize", "xsts.auth.xboxlive.com", 2)]
    [InlineData("/launcher/login", "api.minecraftservices.com", 3)]
    [InlineData("/entitlements/license", "api.minecraftservices.com", 4)]
    [InlineData("/minecraft/profile", "api.minecraftservices.com", 5)]
    public async Task ExchangeForbiddenIdentifiesFailingStageAndStops(string path, string host, int requestCount)
    {
        var http = new ScriptHandler();
        AddExchangeUntilFailure(http, path, "{\"message\":\"private-response\",\"access_token\":\"private-response-token\"}", HttpStatusCode.Forbidden);
        using var client = new HttpClient(http);
        var error = await Assert.ThrowsAsync<MinecraftAuthenticationException>(() =>
            new MicrosoftMinecraftAuthClient(client, Options).ExchangeAsync(new("msa-secret", "refresh-secret"), default));

        Assert.Contains(host + path, error.Message);
        Assert.Contains("403", error.Message);
        Assert.Equal(requestCount, http.Requests.Count);
        foreach (var secret in new[] { "private-response", "msa-secret", "refresh-secret", "xbox-secret", "xsts-secret", "minecraft-secret", "user-hash" })
            Assert.DoesNotContain(secret, error.ToString());
        Assert.Null(error.InnerException);
        if (path == "/entitlements/license")
        {
            var query = http.Requests[^1].Uri.Query;
            Assert.StartsWith("?requestId=", query);
            Assert.DoesNotContain(query, error.Message);
            Assert.DoesNotContain(query.Split('=')[1], error.Message);
        }
    }

    /// <summary>网关返回 HTML、空白或非对象 JSON 时仍报告真实 HTTP 状态，不误报成功或暴露正文。</summary>
    [Theory]
    [InlineData("", 403)]
    [InlineData("<html>private-response</html>", 403)]
    [InlineData("[\"private-response\"]", 403)]
    [InlineData("\"private-response\"", 403)]
    [InlineData("null", 403)]
    [InlineData("<html>private-response</html>", 429)]
    [InlineData("<html>private-response</html>", 502)]
    public async Task NonObjectFailurePreservesHttpStatus(string body, int status)
    {
        var http = new ScriptHandler();
        AddExchangeUntilFailure(http, "/launcher/login", body, (HttpStatusCode)status);
        using var client = new HttpClient(http);
        var error = await Assert.ThrowsAsync<MinecraftAuthenticationException>(() =>
            new MicrosoftMinecraftAuthClient(client, Options).ExchangeAsync(new("msa-secret", "refresh-secret"), default));

        Assert.Contains("api.minecraftservices.com/launcher/login", error.Message);
        Assert.Contains(status.ToString(), error.Message);
        Assert.DoesNotContain("private-response", error.ToString());
        Assert.DoesNotContain("msa-secret", error.ToString());
        Assert.DoesNotContain("xsts-secret", error.ToString());
        Assert.Equal(3, http.Requests.Count);
        Assert.Null(error.InnerException);
    }

    /// <summary>HTTP 200 也必须返回可解析对象，错误页不能被当成有效 Minecraft 会话。</summary>
    [Theory]
    [InlineData("<html>private-response</html>")]
    [InlineData("[\"private-response\"]")]
    public async Task NonObjectSuccessIsRejectedWithSafeEndpoint(string body)
    {
        var http = new ScriptHandler();
        AddExchangeUntilFailure(http, "/launcher/login", body, HttpStatusCode.OK);
        using var client = new HttpClient(http);
        var error = await Assert.ThrowsAsync<MinecraftAuthenticationException>(() =>
            new MicrosoftMinecraftAuthClient(client, Options).ExchangeAsync(new("msa-secret", "refresh-secret"), default));

        Assert.Contains("api.minecraftservices.com/launcher/login", error.Message);
        Assert.Contains("格式", error.Message);
        Assert.DoesNotContain("private-response", error.ToString());
        Assert.Equal(3, http.Requests.Count);
        Assert.Null(error.InnerException);
    }

    /// <summary>没有真实公开 ID 时不发任何网络请求。</summary>
    [Fact]
    public async Task MissingClientIdFailsBeforeNetwork()
    {
        var http = new ScriptHandler();
        using var client = new HttpClient(http);
        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => new MicrosoftMinecraftAuthClient(client, new()).SignInAsync(null, default));
        Assert.Empty(http.Requests);
    }

    /// <summary>只安排失败阶段之前的有效响应，使意外继续请求立即暴露为测试失败。</summary>
    private static void AddExchangeUntilFailure(ScriptHandler http, string failedPath, string body, HttpStatusCode status)
    {
        (string Path, object Body)[] replies =
        [
            ("/user/authenticate", new { Token = "xbox-secret", DisplayClaims = new { xui = new[] { new { uhs = "user-hash" } } } }),
            ("/xsts/authorize", new { Token = "xsts-secret", DisplayClaims = new { xui = new[] { new { uhs = "user-hash", xid = "1234567" } } } }),
            ("/launcher/login", new { access_token = "minecraft-secret", expires_in = 3600 }),
            ("/entitlements/license", new { items = new[] { new { name = "game_minecraft" } } }),
            ("/minecraft/profile", new { id = PlayerId, name = "OnlineAlex" })
        ];
        foreach (var reply in replies)
        {
            if (reply.Path == failedPath)
            {
                http.AddRaw(reply.Path, body, status);
                return;
            }
            http.Add(reply.Path, reply.Body);
        }
        throw new ArgumentException("Unknown test endpoint.", nameof(failedPath));
    }

    /// <summary>提供无真实凭证的 Xbox 与 XSTS 成功响应。</summary>
    private static void AddXbox(ScriptHandler http, string xstsHash = "user-hash")
    {
        http.Add("/user/authenticate", new { Token = "xbox-secret", DisplayClaims = new { xui = new[] { new { uhs = "user-hash" } } } });
        http.Add("/xsts/authorize", new { Token = "xsts-secret", DisplayClaims = new { xui = new[] { new { uhs = xstsHash, xid = "1234567" } } } });
    }
    /// <summary>提供完整的 Minecraft 登录、许可和玩家档案响应。</summary>
    private static void AddMinecraft(ScriptHandler http)
    {
        http.Add("/launcher/login", new { access_token = "minecraft-secret", expires_in = 3600 });
        http.Add("/entitlements/license", new { items = new[] { new { name = "game_minecraft" } } });
        http.Add("/minecraft/profile", new { id = PlayerId, name = "OnlineAlex" });
    }

    /// <summary>只接受按顺序预先声明的请求，记录脱离真实账号的测试参数。</summary>
    private sealed class ScriptHandler : HttpMessageHandler
    {
        private readonly Queue<(string Path, string Body, HttpStatusCode Status)> _replies = new();
        internal List<(Uri Uri, string Body, string? Authorization)> Requests { get; } = [];
        /// <summary>把模拟服务对象编码为 JSON，供正常协议及结构化错误测试使用。</summary>
        internal void Add(string path, object body, HttpStatusCode status = HttpStatusCode.OK) => AddRaw(path, JsonSerializer.Serialize(body), status);
        /// <summary>保留原始响应文本，覆盖空白、HTML 和非对象 JSON 错误页。</summary>
        internal void AddRaw(string path, string body, HttpStatusCode status) => _replies.Enqueue((path, body, status));
        /// <summary>验证请求顺序并返回模拟响应，不接触外部服务。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var next = _replies.Dequeue();
            Assert.Equal(next.Path, request.RequestUri!.AbsolutePath);
            Assert.Equal("https", request.RequestUri.Scheme);
            Requests.Add((request.RequestUri, request.Content is null ? "" : await request.Content.ReadAsStringAsync(token), request.Headers.Authorization?.ToString()));
            return new(next.Status) { Content = new StringContent(next.Body, Encoding.UTF8, "application/json") };
        }
    }

    internal sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        internal List<TimeSpan> Delays { get; } = [];
        public override DateTimeOffset GetUtcNow() => Now;
        internal Task Delay(TimeSpan duration, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Delays.Add(duration); Now += duration; return Task.CompletedTask; }
    }
    internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
