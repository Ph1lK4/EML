namespace EML.Core.Java;

/// <summary>约定 Java 检测能力，让界面与具体平台的搜索实现分开。</summary>
public interface IJavaLocator
{
    /// <summary>异步返回可运行的 Java 列表；无结果时返回空集合，支持取消。</summary>
    Task<IReadOnlyList<JavaInstallation>> FindAllAsync(
        CancellationToken cancellationToken = default);
}
