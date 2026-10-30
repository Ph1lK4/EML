using System.Net;
using EML.Core.CurseForge;

namespace EML.Infrastructure.Minecraft.CurseForge.Api;

/// <summary>只含公开状态码和等待时间的 API 错误，绝不包含响应正文、请求或密钥。</summary>
public sealed class CurseForgeApiException : HttpRequestException
{
    public TimeSpan? RetryAfter { get; }
    public CurseForgeRateLimit? RateLimit { get; }

    /// <summary>将常见身份、限流和文件不可获取状态转成可读提示。</summary>
    public CurseForgeApiException(HttpStatusCode statusCode, TimeSpan? retryAfter = null, CurseForgeRateLimit? rateLimit = null)
        : base(MessageFor(statusCode), null, statusCode)
    {
        RetryAfter = retryAfter;
        RateLimit = rateLimit;
    }

    /// <summary>按状态码生成固定文本，不使用可能回显凭证的服务器诊断内容。</summary>
    private static string MessageFor(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => "此构建的 CurseForge 授权未通过验证，请联系启动器维护者。",
        HttpStatusCode.Forbidden => "CurseForge 拒绝访问，请检查 API 授权及该资源的分发许可。",
        HttpStatusCode.NotFound => "CurseForge 未返回所请求的项目或文件，可能已移除或无权访问。",
        HttpStatusCode.TooManyRequests => "CurseForge 请求过于频繁，请根据 RetryAfter 等待后再试。",
        _ => $"CurseForge API 请求失败，HTTP {(int)statusCode}。"
    };
}
