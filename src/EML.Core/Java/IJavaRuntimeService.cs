namespace EML.Core.Java;

/// <summary>官方 Java 压缩包及其完整性信息；不包含可执行脚本或安装命令。</summary>
public sealed record JavaRuntimePackage(int MajorVersion, string Version, string Architecture,
    string FileName, string DownloadUrl, string Sha256, long Size)
{
    public string DisplayName => $"Eclipse Temurin Java {Version} · {Architecture}";
}

/// <summary>Java 文件下载与解压阶段；下载完成不等于运行环境已安装。</summary>
public sealed record JavaRuntimeInstallProgress(string Stage, double? Percent = null);

/// <summary>查询官方 Windows Java 包并安全安装到启动器托管目录，不修改系统环境变量。</summary>
public interface IJavaRuntimeService
{
    /// <summary>返回启动器管理的安装目录，不依赖系统 Java 安装位置。</summary>
    string InstallationRoot { get; }
    /// <summary>查询指定主版本及架构的官方安装包；没有对应包时返回空列表。</summary>
    Task<IReadOnlyList<JavaRuntimePackage>> GetPackagesAsync(int majorVersion, string architecture,
        CancellationToken cancellationToken = default);
    /// <summary>下载、校验并安装运行时；仅在实际 Java 验证成功后返回可用路径。</summary>
    Task<JavaInstallation> InstallAsync(JavaRuntimePackage package,
        IProgress<JavaRuntimeInstallProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>只用于可通过换用 Java 解决的启动准备失败，不代表模组或游戏运行错误。</summary>
public sealed class JavaRuntimeRequiredException : InvalidOperationException
{
    public IReadOnlyList<int> CompatibleMajorVersions { get; }
    public string Architecture { get; }
    public string? RequestedJavaPath { get; }

    /// <summary>携带运行配置的明确版本和架构要求，调用方不通过错误字符串猜测下载版本。</summary>
    public JavaRuntimeRequiredException(string message, IEnumerable<int> compatibleMajorVersions,
        string architecture, string? requestedJavaPath = null, Exception? innerException = null)
        : base(message, innerException)
    {
        CompatibleMajorVersions = compatibleMajorVersions.Distinct().OrderDescending().ToArray();
        Architecture = architecture;
        RequestedJavaPath = requestedJavaPath;
    }
}
