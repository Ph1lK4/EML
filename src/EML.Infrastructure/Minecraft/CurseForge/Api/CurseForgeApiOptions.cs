namespace EML.Infrastructure.Minecraft.CurseForge.Api;

/// <summary>不含凭证的连接选项；API 主机固定为官方地址，不允许把密钥发往自定义镜像。</summary>
public sealed record CurseForgeApiOptions
{
    public string UserAgent { get; init; } = "EML/0.1.0 (https://github.com/Ph1lK4/EML)";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumJsonBytes { get; init; } = 8 * 1024 * 1024;
    public int BatchSize { get; init; } = 50;
}
