using EML.Core;

namespace EML.Tests;

/// <summary>使用独立临时目录验证 RC1、RC2 和源码启动的数据根，不接触真实玩家数据。</summary>
public sealed class LauncherPathsTests : IDisposable
{
    private const string ValidMarker = """{"schemaVersion":1,"layout":"app-directory","dataDirectory":".."}""";
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "eml-launcher-path-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>为每项测试创建独占目录，避免更改进程级工作目录影响其他测试。</summary>
    public LauncherPathsTests() => Directory.CreateDirectory(_temporary);

    /// <summary>旧版平铺布局仍以程序目录作为数据根。</summary>
    [Fact]
    public void LegacyFlatLayoutKeepsApplicationDirectory()
    {
        var root = CreateDirectory("旧版 RC1");
        Assert.Equal(root, LauncherPaths.ResolveDataRoot(root + Path.DirectorySeparatorChar));
    }

    /// <summary>仅有 app 名称或父级 EXE 不能触发目录提升。</summary>
    [Fact]
    public void DirectoryNameAndExecutableWithoutMarkerDoNotRedirect()
    {
        var application = CreateDirectory("legacy", "app");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(application)!, "EML.exe"), "synthetic");
        Assert.Equal(application, LauncherPaths.ResolveDataRoot(application));
    }

    /// <summary>中文、空格和尾分隔符不会影响明确标记的 RC2 布局。</summary>
    [Fact]
    public void StructuredLayoutReturnsParentWithChineseAndSpaces()
    {
        var application = CreateBundle("我的 启动器 RC2");
        Assert.Equal(Path.GetDirectoryName(application), LauncherPaths.ResolveDataRoot(application + Path.DirectorySeparatorChar));
    }

    /// <summary>Release 不会因为上层恰有解决方案就把数据写到上层。</summary>
    [Fact]
    public void ReleaseIgnoresAncestorSolution()
    {
        File.WriteAllText(Path.Combine(_temporary, "EML.slnx"), "synthetic");
        var application = CreateDirectory("src", "bin");
        Assert.Equal(application, LauncherPaths.ResolveDataRoot(application));
    }

    /// <summary>开发模式保留最近的解决方案目录定位规则。</summary>
    [Fact]
    public void DevelopmentUsesNearestAncestorSolution()
    {
        File.WriteAllText(Path.Combine(_temporary, "EML.slnx"), "outer");
        var solution = CreateDirectory("nested");
        File.WriteAllText(Path.Combine(solution, "EML.slnx"), "inner");
        var application = CreateDirectory("nested", "src", "bin");
        Assert.Equal(solution, LauncherPaths.ResolveDataRoot(application, developmentMode: true));
    }

    /// <summary>开发标志不能覆盖显式发布布局。</summary>
    [Fact]
    public void ExplicitLayoutTakesPrecedenceOverDevelopmentLookup()
    {
        File.WriteAllText(Path.Combine(_temporary, "EML.slnx"), "synthetic");
        var application = CreateBundle("release");
        Assert.Equal(Path.GetDirectoryName(application), LauncherPaths.ResolveDataRoot(application, developmentMode: true));
    }

    /// <summary>无解决方案的开发构建兼容平铺规则。</summary>
    [Fact]
    public void DevelopmentWithoutSolutionUsesApplicationDirectory()
    {
        var application = CreateDirectory("standalone");
        Assert.Equal(application, LauncherPaths.ResolveDataRoot(application, developmentMode: true));
    }

    /// <summary>拒绝相对输入，使解析结果不受快捷方式工作目录影响。</summary>
    [Theory]
    [InlineData("app")]
    [InlineData(".")]
    [InlineData("../app")]
    public void RelativeDirectoryIsRejected(string path) =>
        Assert.Throws<ArgumentException>(() => LauncherPaths.ResolveDataRoot(path));

    /// <summary>两个绝对安装路径独立解析，不依赖当前目录或共享状态。</summary>
    [Fact]
    public void SeparateAbsoluteInstallationsHaveIndependentRoots()
    {
        var first = CreateBundle("First");
        var second = CreateBundle("第二份 安装");
        Assert.Equal(Path.GetDirectoryName(first), LauncherPaths.ResolveDataRoot(first));
        Assert.Equal(Path.GetDirectoryName(second), LauncherPaths.ResolveDataRoot(second));
        Assert.Equal(Path.GetDirectoryName(first), LauncherPaths.ResolveDataRoot(first));
    }

    /// <summary>损坏、未知或可重定向的标记必须报错，不能静默使用 app 目录。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"layout\":\"app-directory\",\"dataDirectory\":\"..\"}")]
    [InlineData("{\"schemaVersion\":\"1\",\"layout\":\"app-directory\",\"dataDirectory\":\"..\"}")]
    [InlineData("{\"schemaVersion\":1,\"layout\":\"flat\",\"dataDirectory\":\"..\"}")]
    [InlineData("{\"schemaVersion\":1,\"layout\":\"app-directory\",\"dataDirectory\":\"../../other\"}")]
    [InlineData("{\"schemaVersion\":1,\"layout\":\"app-directory\",\"dataDirectory\":null}")]
    [InlineData("{\"schemaVersion\":1,\"layout\":\"app-directory\"}")]
    [InlineData("{\"schemaVersion\":1,\"layout\":\"app-directory\",\"dataDirectory\":\"..\",\"other\":true}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"layout\":\"app-directory\",\"dataDirectory\":\"..\"}")]
    public void InvalidMarkerFailsClosed(string marker)
    {
        var application = CreateBundle("Invalid");
        File.WriteAllText(Path.Combine(application, LauncherPaths.LayoutFileName), marker);
        Assert.ThrowsAny<IOException>(() => LauncherPaths.ResolveDataRoot(application));
    }

    /// <summary>限制布局文件大小，防止无界解析意外的大文件。</summary>
    [Fact]
    public void OversizedMarkerIsRejected()
    {
        var application = CreateBundle("Large");
        File.WriteAllText(Path.Combine(application, LauncherPaths.LayoutFileName), ValidMarker + new string(' ', 1024));
        Assert.ThrowsAny<IOException>(() => LauncherPaths.ResolveDataRoot(application));
    }

    /// <summary>只复制 app 文件夹不构成可用安装，应明确失败。</summary>
    [Fact]
    public void MissingRootExecutableIsRejected()
    {
        var application = CreateDirectory("Missing", "app");
        File.WriteAllText(Path.Combine(application, LauncherPaths.LayoutFileName), ValidMarker);
        Assert.ThrowsAny<IOException>(() => LauncherPaths.ResolveDataRoot(application));
    }

    /// <summary>同名目录不能冒充根目录启动入口。</summary>
    [Fact]
    public void DirectoryInsteadOfRootExecutableIsRejected()
    {
        var application = CreateDirectory("DirectoryEntry", "app");
        File.WriteAllText(Path.Combine(application, LauncherPaths.LayoutFileName), ValidMarker);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(application)!, "EML.exe"));
        Assert.ThrowsAny<IOException>(() => LauncherPaths.ResolveDataRoot(application));
    }

    /// <summary>标记只适用于固定 app 子目录，不能任意提升目录。</summary>
    [Fact]
    public void MarkerOutsideAppDirectoryIsRejected()
    {
        var application = CreateDirectory("Wrong", "program");
        File.WriteAllText(Path.Combine(application, LauncherPaths.LayoutFileName), ValidMarker);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(application)!, "EML.exe"), "synthetic");
        Assert.ThrowsAny<IOException>(() => LauncherPaths.ResolveDataRoot(application));
    }

    /// <summary>损坏的布局标记不能以开发回退规则绕过。</summary>
    [Fact]
    public void InvalidMarkerDoesNotFallBackToDevelopmentSolution()
    {
        File.WriteAllText(Path.Combine(_temporary, "EML.slnx"), "synthetic");
        var application = CreateBundle("Broken");
        File.WriteAllText(Path.Combine(application, LauncherPaths.LayoutFileName), "{}");
        Assert.ThrowsAny<IOException>(() => LauncherPaths.ResolveDataRoot(application, developmentMode: true));
    }

    /// <summary>标记被同名目录占用时不能被当作未配置处理。</summary>
    [Fact]
    public void DirectoryInsteadOfMarkerIsRejected()
    {
        var application = CreateDirectory("DirectoryMarker", "app");
        Directory.CreateDirectory(Path.Combine(application, LauncherPaths.LayoutFileName));
        Assert.ThrowsAny<IOException>(() => LauncherPaths.ResolveDataRoot(application));
    }

    /// <summary>创建只含合成入口和布局标记的最小安装夹具。</summary>
    private string CreateBundle(string name)
    {
        var application = CreateDirectory(name, "app");
        File.WriteAllText(Path.Combine(application, LauncherPaths.LayoutFileName), ValidMarker);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(application)!, "EML.exe"), "synthetic; not executable");
        return application;
    }

    /// <summary>仅在当前测试专属根目录内创建子目录。</summary>
    private string CreateDirectory(params string[] parts)
    {
        var path = Path.Combine(new[] { _temporary }.Concat(parts).ToArray());
        return Directory.CreateDirectory(path).FullName;
    }

    /// <summary>只清理本测试生成的唯一临时目录，不接触安装目录和用户实例。</summary>
    public void Dispose() => Directory.Delete(_temporary, recursive: true);
}
