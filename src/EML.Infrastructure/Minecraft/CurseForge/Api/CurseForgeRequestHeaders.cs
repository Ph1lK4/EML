namespace EML.Infrastructure.Minecraft.CurseForge.Api;

/// <summary>统一验证官方请求目标并添加凭证，不允许通过 URL 查询参数传递密钥。</summary>
public static class CurseForgeRequestHeaders
{
    /// <summary>判断地址是否为经过允许的官方 API HTTPS 主机。</summary>
    public static bool IsApiUri(Uri uri) => IsSafeUri(uri) && uri.Host.Equals("api.curseforge.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>只允许官方已采用的 edge 与 mediafilez 下载主机，不接受任意子域。</summary>
    public static bool IsCdnUri(Uri uri) => IsSafeUri(uri) &&
        (uri.Host.Equals("edge.forgecdn.net", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("mediafilez.forgecdn.net", StringComparison.OrdinalIgnoreCase));

    /// <summary>核对目标后才读取密钥，仅添加 x-api-key，不修改 URL 或已有身份信息。</summary>
    public static void AddApiKey(HttpRequestMessage request, ICurseForgeApiKeyProvider provider)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(provider);
        if (request.RequestUri is not { } uri || !(IsApiUri(uri) || IsCdnUri(uri)))
            throw new InvalidOperationException("CurseForge 凭证只能发送到受支持的官方 HTTPS 地址。");
        if (request.Headers.Contains("x-api-key") || request.Headers.Authorization is not null)
            throw new InvalidOperationException("请求已包含身份信息，拒绝重复添加 CurseForge 凭证。");
        string? value;
        try { value = provider.GetApiKey(); }
        catch { throw new CurseForgeCredentialException(CurseForgeCredentialFailure.Unreadable); }
        if (string.IsNullOrWhiteSpace(value)) throw new CurseForgeCredentialException(CurseForgeCredentialFailure.NotConfigured);
        if (value.Length > 1280 || value.Any(c => c < 33 || c > 126))
            throw new CurseForgeCredentialException(CurseForgeCredentialFailure.Invalid);
        request.Headers.Add("x-api-key", value);
    }

    /// <summary>拒绝凭证、端口变更、片段以及可能携带签名秘密的查询参数。</summary>
    private static bool IsSafeUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        foreach (var query in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = query.Split('=', 2)[0];
            try { key = Uri.UnescapeDataString(Uri.UnescapeDataString(key)); }
            catch (UriFormatException) { return false; }
            var normalized = new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            if (new[] { "key", "token", "auth", "signature", "credential", "password", "secret", "signed", "policy" }
                .Any(value => normalized.Contains(value, StringComparison.Ordinal))) return false;
        }
        return true;
    }
}

/// <summary>仅保存固定失败类别，绝不接受来自凭证提供者的异常文本。</summary>
public enum CurseForgeCredentialFailure { NotConfigured, Invalid, Unreadable }

/// <summary>不会携带密钥或原始内部异常的凭证错误。</summary>
public sealed class CurseForgeCredentialException : InvalidOperationException
{
    /// <summary>按固定失败类型生成提示，不提供传入自定义秘密文本的构造方式。</summary>
    public CurseForgeCredentialException(CurseForgeCredentialFailure failure) : base(failure switch
    {
        CurseForgeCredentialFailure.NotConfigured => "当前构建尚未配置 CurseForge API Key，请使用维护者提供的已配置版本。",
        CurseForgeCredentialFailure.Invalid => "当前构建的 CurseForge API Key 格式无效，请联系维护者。",
        _ => "无法读取内置 CurseForge API Key，请联系维护者。"
    }) { }
}
