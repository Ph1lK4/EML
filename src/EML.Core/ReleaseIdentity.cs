using System.Reflection;

namespace EML.Core;

/// <summary>从统一的编译元数据读取发布身份，避免界面与发布包各自维护版本号。</summary>
public static class ReleaseIdentity
{
    /// <summary>产品的语义版本，包括 RC 等预发布标记。</summary>
    public static string Version { get; } = typeof(ReleaseIdentity).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>面向玩家显示的短版本名；未配置时回退到程序集产品版本。</summary>
    public static string DisplayVersion { get; } = typeof(ReleaseIdentity).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "EMLDisplayVersion")?.Value ?? Version;

    /// <summary>标题中标明测试版身份，便于用户反馈问题时核对所运行的版本。</summary>
    public static string WindowTitle => $"EML {DisplayVersion} · 我的世界启动器";
}
