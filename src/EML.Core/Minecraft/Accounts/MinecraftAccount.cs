using System.Text.Json.Serialization;

namespace EML.Core.Minecraft.Accounts;

/// <summary>可展示的 Java 版身份，不包含微软账号密码或任何登录令牌。</summary>
public sealed record MinecraftAccount(string Id, string Name);

/// <summary>只在内存中使用的游戏会话；禁止默认序列化和日志输出令牌。</summary>
public sealed class MinecraftAccountSession(MinecraftAccount account, string accessToken,
    DateTimeOffset expiresAt, string clientId, string xuid)
{
    public MinecraftAccount Account { get; } = account;
    [JsonIgnore] public string AccessToken { get; } = accessToken;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public string ClientId { get; } = clientId;
    public string Xuid { get; } = xuid;
    public override string ToString() => $"Minecraft session: {Account.Name} ({Account.Id})";
}

/// <summary>只向用户展示短验证码；用于轮询的 device_code 保留在认证层。</summary>
public sealed record MinecraftLoginPrompt(string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAt);

/// <summary>账号与实例分开管理；正版会话失败必须报错，不能偷偷降级为离线身份。</summary>
public interface IMinecraftAccountService
{
    bool IsConfigured { get; }
    Task<MinecraftAccount?> LoadAccountAsync(CancellationToken cancellationToken = default);
    Task<MinecraftAccount> SignInAsync(IProgress<MinecraftLoginPrompt>? progress = null, CancellationToken cancellationToken = default);
    Task<MinecraftAccountSession> GetSessionAsync(CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

/// <summary>仅包含可安全展示的诊断，不附带认证响应正文或带令牌的内部异常。</summary>
public sealed class MinecraftAuthenticationException(string message) : Exception(message);
