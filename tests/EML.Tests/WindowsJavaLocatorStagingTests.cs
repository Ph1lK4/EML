using EML.Infrastructure.Java;

namespace EML.Tests;

/// <summary>仅枚举临时假文件，不运行 Java，也不读取环境变量或注册表。</summary>
public sealed class WindowsJavaLocatorStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-java-locator-test-" + Guid.NewGuid().ToString("N"));
    private const string StagingName = ".install-0123456789abcdef0123456789abcdef";

    /// <summary>由启动器父目录搜索时，未发布 Java 不进入候选集，已发布版本仍然可见。</summary>
    [Fact]
    public void ParentScanExcludesUnpublishedJava()
    {
        CreateJava(Path.Combine("runtimes", "java", StagingName, "jdk-21", "bin"));
        var installed = CreateJava(Path.Combine("runtimes", "java", "temurin-21-x64-published", "bin"));
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        WindowsJavaLocator.ScanRoot(_root, 8, candidates, CancellationToken.None);

        Assert.Equal(installed, Assert.Single(candidates));
    }

    /// <summary>直接以安装临时目录或其后代作为搜索根，也不能探测未发布文件。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("jdk-21")]
    [InlineData("jdk-21/bin")]
    public void DirectStagingRootIsExcluded(string relative)
    {
        CreateJava(Path.Combine("runtimes", "java", StagingName, "jdk-21", "bin"));
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        WindowsJavaLocator.ScanRoot(Path.Combine(_root, "runtimes", "java", StagingName, relative),
            8, candidates, CancellationToken.None);

        Assert.Empty(candidates);
    }

    /// <summary>PATH 候选绕过递归扫描时，仍不能登记临时 Java。</summary>
    [Fact]
    public void DirectPathCandidateIsExcluded()
    {
        var executable = CreateJava(Path.Combine("runtimes", "java", StagingName, "jdk-21", "bin"));
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        WindowsJavaLocator.AddExecutable(Path.GetDirectoryName(executable), candidates);

        Assert.Empty(candidates);
    }

    /// <summary>非保留位置、非 GUID 名称及正式发布目录，不会因同名前缀被误排除。</summary>
    [Theory]
    [InlineData("user-java/.install-0123456789abcdef0123456789abcdef/bin")]
    [InlineData("runtimes/java/.install-personal/bin")]
    [InlineData("runtimes/java/temurin-21-x64-published/bin")]
    public void OrdinaryJavaRemainsDiscoverable(string relative)
    {
        var executable = CreateJava(relative);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        WindowsJavaLocator.ScanRoot(_root, 8, candidates, CancellationToken.None);

        Assert.Equal(executable, Assert.Single(candidates));
    }

    /// <summary>创建只供路径枚举使用的空文件。</summary>
    private string CreateJava(string relativeDirectory)
    {
        var directory = Path.GetFullPath(Path.Combine(_root, relativeDirectory));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "java.exe");
        File.WriteAllText(path, "not executable");
        return path;
    }

    /// <summary>仅清理本测试创建的唯一临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
