// ════════════════════════════════════════════════════════════════════
//  文件：PreflightTests.cs
//  位置：tests\EML.Tests
//  作用：验证「启动前检查」查得对不对。
//
//  测试策略：
//    和 LaunchPlannerTests 一样，不依赖真实 .minecraft。
//    每个测试自己造临时目录 + 最小版本 JSON，
//    然后用「造了哪些文件 / 没造哪些文件」来断言检查结果。
// ════════════════════════════════════════════════════════════════════

using EML.Core.Launch;
using EML.Infrastructure.Launch;

namespace EML.Tests;

/// <summary>
/// 启动前检查的测试。
/// </summary>
public sealed class PreflightTests : IDisposable
{
    /// <summary>每个测试独享一个临时游戏目录。</summary>
    private readonly string gameDirectory = Path.Combine(
        Path.GetTempPath(), "eml-preflight-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>测试结束删掉临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(gameDirectory))
        {
            Directory.Delete(gameDirectory, true);
        }
    }

    /// <summary>Java 路径：测试里用一个真实存在的空文件代替。</summary>
    private string JavaPath => Path.Combine(gameDirectory, "java.exe");

    /// <summary>版本目录：versions\1.7.10。</summary>
    private string VersionDirectory =>
        Path.Combine(gameDirectory, "versions", "1.7.10");

    /// <summary>客户端 jar 路径。</summary>
    private string ClientJarPath => Path.Combine(VersionDirectory, "1.7.10.jar");

    /// <summary>拼一次检查请求。</summary>
    private LaunchRequest CreateRequest()
    {
        return new LaunchRequest(JavaPath, gameDirectory, "1.7.10", "Steve");
    }

    /// <summary>
    /// 正常路径：Java、版本 JSON、客户端 jar、库都在 → 可以直接启动。
    /// </summary>
    [Fact]
    public void Check_文件齐全时返回IsReady()
    {
        // ① 造一份带一个库的版本 JSON，再把声明到的文件都造出来。
        WriteVersionJson(OneLibraryJson);

        CreateFile(JavaPath);
        CreateFile(ClientJarPath);
        CreateFile(Path.Combine(
            gameDirectory, "libraries", "com", "example", "lib", "1.0", "lib-1.0.jar"));

        // ② 检查。
        var result = new VanillaPreflight().Check(CreateRequest());

        // ③ 应该齐全，而且确实检查了不止一个文件。
        Assert.True(result.IsReady, FormatMissing(result));
        Assert.True(result.Checked >= 3, "检查数量应该包含 Java、版本 JSON、客户端 jar 和库");
    }

    /// <summary>
    /// 缺客户端 jar 和库时，要分别按 client / library 报出来。
    /// </summary>
    [Fact]
    public void Check_缺客户端jar和库时报出分类与路径()
    {
        WriteVersionJson(OneLibraryJson);
        CreateFile(JavaPath);

        var result = new VanillaPreflight().Check(CreateRequest());

        Assert.False(result.IsReady);
        Assert.Contains(result.Missing, item => item.Kind == "client");
        Assert.Contains(
            result.Missing,
            item => item.Kind == "library" && item.Path.EndsWith("lib-1.0.jar"));
    }

    /// <summary>
    /// natives 分类器也要查，而且只查 Windows 那一份。
    /// </summary>
    [Fact]
    public void Check_缺natives分类器时按Windows分类器报()
    {
        WriteVersionJson(OneNativeLibraryJson);
        CreateFile(JavaPath);
        CreateFile(ClientJarPath);

        var result = new VanillaPreflight().Check(CreateRequest());

        Assert.False(result.IsReady);
        Assert.Contains(
            result.Missing,
            item => item.Kind == "library"
                && item.Path.EndsWith("native-1.0-natives-windows.jar"));
    }

    /// <summary>
    /// 版本 JSON 都没有时，后面没有依据可查，直接返回这一条。
    /// </summary>
    [Fact]
    public void Check_缺版本JSON时直接返回()
    {
        CreateFile(JavaPath);

        var result = new VanillaPreflight().Check(CreateRequest());

        Assert.False(result.IsReady);
        Assert.Single(result.Missing);
        Assert.Equal("version", result.Missing[0].Kind);
    }

    // ────────────────────────────────────────────────────────────────
    // 测试辅助
    // ────────────────────────────────────────────────────────────────

    /// <summary>带一个普通库的最小版本 JSON。</summary>
    private const string OneLibraryJson = """
        {
          "id": "1.7.10",
          "mainClass": "net.minecraft.client.main.Main",
          "libraries": [
            {
              "name": "com.example:lib:1.0",
              "downloads": {
                "artifact": {
                  "path": "com/example/lib/1.0/lib-1.0.jar",
                  "url": "https://example.invalid/lib-1.0.jar"
                }
              }
            }
          ]
        }
        """;

    /// <summary>带一个 natives 库（Windows 分类器）的最小版本 JSON。</summary>
    private const string OneNativeLibraryJson = """
        {
          "id": "1.7.10",
          "mainClass": "net.minecraft.client.main.Main",
          "libraries": [
            {
              "name": "org.example:native:1.0",
              "natives": { "windows": "natives-windows" },
              "downloads": {
                "classifiers": {
                  "natives-windows": {
                    "path": "org/example/native/1.0/native-1.0-natives-windows.jar"
                  }
                }
              }
            }
          ]
        }
        """;

    /// <summary>把版本 JSON 写进 versions\1.7.10。</summary>
    private void WriteVersionJson(string json)
    {
        Directory.CreateDirectory(VersionDirectory);
        File.WriteAllText(Path.Combine(VersionDirectory, "1.7.10.json"), json);
    }

    /// <summary>造一个空文件（父目录不存在就建）。</summary>
    private static void CreateFile(string path)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, string.Empty);
    }

    /// <summary>断言失败时把缺失清单打出来，方便定位。</summary>
    private static string FormatMissing(LaunchPreflightResult result)
    {
        return "缺失：" + string.Join(
            " | ",
            result.Missing.Select(item => $"{item.Kind}:{item.Path}"));
    }
}
