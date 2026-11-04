using System.Net;
using EML.Core.Modrinth;

namespace EML.Infrastructure.Minecraft.Modrinth.Api;

/// <summary>包含 HTTP 状态和等待时间的 API 错误，不把服务器正文或凭证拼入提示。</summary>
public sealed class ModrinthApiException : HttpRequestException
{
    public TimeSpan? RetryAfter { get; }
    public ModrinthRateLimit? RateLimit { get; }

    /// <summary>保留限流信息供调用方显示或延迟下一次请求，本层不进行无限重试。</summary>
    public ModrinthApiException(HttpStatusCode statusCode, TimeSpan? retryAfter, ModrinthRateLimit? rateLimit)
        : base(statusCode == HttpStatusCode.TooManyRequests
            ? "Modrinth 请求过于频繁，请根据 RetryAfter 等待后再试。"
            : $"Modrinth API 请求失败，HTTP {(int)statusCode}。", null, statusCode)
    {
        RetryAfter = retryAfter;
        RateLimit = rateLimit;
    }
}
