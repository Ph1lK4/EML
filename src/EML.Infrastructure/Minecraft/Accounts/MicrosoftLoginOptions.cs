using System.Text.Json;
using EML.Core.Minecraft.Accounts;

namespace EML.Infrastructure.Minecraft.Accounts;

/// <summary>EML 自己注册的公开应用 ID；桌面设备码登录不使用客户端密钥。</summary>
public sealed record MicrosoftLoginOptions
{
    public string ClientId { get; init; } = "";
    public bool IsConfigured => Guid.TryParse(ClientId, out var id) && id != Guid.Empty;

    /// <summary>优先读取开发环境变量，否则读取 EXE 旁的公开配置；不使用其他启动器的应用 ID。</summary>
    public static MicrosoftLoginOptions Load(string applicationDirectory)
    {
        var configured = Environment.GetEnvironmentVariable("EML_MICROSOFT_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(configured)) return new() { ClientId = configured.Trim() };
        var path = Path.Combine(applicationDirectory, "microsoft-login.json");
        if (!File.Exists(path)) return new();
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 4096) return new();
            return JsonSerializer.Deserialize<MicrosoftLoginOptions>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    /// <summary>配置缺失时在请求网络前失败，给开发者明确的下一步。</summary>
    internal void Validate()
    {
        if (!IsConfigured) throw new MinecraftAuthenticationException(
            "尚未配置 EML 的微软应用 Client ID，请先按 docs/microsoft-login.md 注册并配置应用。");
    }
}
