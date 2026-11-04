namespace EML.Infrastructure.Minecraft.CurseForge.Api;

/// <summary>官方请求的凭证来源；生产实现只读取构建时生成的程序集常量。</summary>
public interface ICurseForgeApiKeyProvider
{
    /// <summary>取得本次请求的密钥，未配置返回 null；调用方不得记录返回值。</summary>
    string? GetApiKey();
}

/// <summary>读取构建内置的密钥；未配置构建凭据时返回 null，不读取运行时环境或本机文件。</summary>
public sealed class EmbeddedCurseForgeApiKeyProvider : ICurseForgeApiKeyProvider
{
    /// <summary>返回生成器在编译阶段提供的值，不进行任何外部凭证读取。</summary>
    public string? GetApiKey() => string.IsNullOrEmpty(GeneratedCurseForgeSecret.Value) ? null : GeneratedCurseForgeSecret.Value;
}
