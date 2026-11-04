namespace EML.Infrastructure.Minecraft.Modrinth.Api;

/// <summary>公开 API 的连接参数；版本号和项目地址可由将来的应用配置统一提供。</summary>
public sealed record ModrinthApiOptions
{
    public Uri BaseAddress { get; init; } = new("https://api.modrinth.com/v2/");
    public string UserAgent { get; init; } = "EML/0.1.0 (https://github.com/Ph1lK4/EML)";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumJsonBytes { get; init; } = 8 * 1024 * 1024;
}
