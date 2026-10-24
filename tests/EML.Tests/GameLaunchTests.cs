// ════════════════════════════════════════════════════════════════════
//  文件：GameLaunchTests.cs
//  位置：tests\EML.Tests
//  作用：验证「解压本机库」和「真正拉起进程收日志」。
//
//  测试策略：
//    不依赖真实游戏、不联网：
//      - 解压测试自己造一个假版本 JSON + 假 natives jar；
//      - 进程测试用 cmd.exe 回显一行字，验证输出能被收到。
//    测完在 Dispose 里删掉临时目录。
// ════════════════════════════════════════════════════════════════════

using System.IO.Compression;
using System.Text;
using EML.Core.Launch;
using EML.Infrastructure.Launch;

namespace EML.Tests;

/// <summary>
/// 本机库解压 + 进程启动的测试。
/// </summary>
public sealed class GameLaunchTests : IDisposable
{
    /// <summary>每个测试独享一个临时目录。</summary>
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "eml-game-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>测试结束删掉临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// 版本 JSON 里声明了 natives.windows 时，
    /// 要把 jar 里的 .dll 解压到 &lt;版本&gt;-natives，并跳过 META-INF。
    /// </summary>
    [Fact]
    public void Extract_解压本机库并跳过METAINF()
    {
        // ① 造目录：游戏目录 / versions / 1.7.10
        var versionDirectory = Path.Combine(root, "versions", "1.7.10");
        Directory.CreateDirectory(versionDirectory);

        // ② 造一份版本 JSON：只有一个带 natives 的库
        File.WriteAllText(
            Path.Combine(versionDirectory, "1.7.10.json"),
            """
            {
              "libraries": [
                {
                  "name": "org.lwjgl.lwjgl:lwjgl-platform:2.9.1",
                  "natives": { "windows": "natives-windows" },
                  "downloads": {
                    "classifiers": {
                      "natives-windows": {
                        "path": "org/lwjgl/lwjgl/lwjgl-platform/2.9.1/lwjgl-platform-2.9.1-natives-windows.jar"
                      }
                    }
                  }
                }
              ]
            }
            """);

        // ③ 造一个假的 natives jar：里面有一个 dll 和一个 META-INF 文件
        var jarPath = Path.Combine(
            root, "libraries", "org", "lwjgl", "lwjgl", "lwjgl-platform",
            "2.9.1", "lwjgl-platform-2.9.1-natives-windows.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(jarPath)!);

        using (var archive = ZipFile.Open(jarPath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "META-INF/MANIFEST.MF", "not-a-real-manifest");
            WriteEntry(archive, "lwjgl.dll", "not-a-real-dll");
        }

        // ④ 解压
        var nativesDirectory = new NativesExtractor().Extract(root, "1.7.10");

        // ⑤ 断言：目录返回了，dll 在，META-INF 没被解出来
        Assert.Equal(
            Path.Combine(root, "versions", "1.7.10", "1.7.10-natives"),
            nativesDirectory);
        Assert.True(File.Exists(Path.Combine(nativesDirectory, "lwjgl.dll")));
        Assert.False(File.Exists(Path.Combine(nativesDirectory, "META-INF", "MANIFEST.MF")));
    }

    /// <summary>
    /// 版本 JSON 里没有任何 natives 时，返回空串，
    /// 调用方就不会往命令里塞 -Djava.library.path。
    /// </summary>
    [Fact]
    public void Extract_没有本机库时返回空串()
    {
        var versionDirectory = Path.Combine(root, "versions", "1.7.10");
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(
            Path.Combine(versionDirectory, "1.7.10.json"),
            """{ "libraries": [] }""");

        var result = new NativesExtractor().Extract(root, "1.7.10");

        Assert.Equal(string.Empty, result);
    }

    /// <summary>
    /// 进程启动测试：用 cmd.exe 回显一行字，
    /// 验证「能启动、能收到输出、能拿到退出码」。
    /// </summary>
    [Fact]
    public async Task GameLauncher_能启动进程并收到输出()
    {
        // 工作目录必须真实存在，否则 Process.Start 会报「目录名称无效」
        Directory.CreateDirectory(root);

        var plan = new LaunchPlan(
            "cmd.exe",
            "/d /c echo hello-from-eml",
            root);

        var lines = new List<string>();

        using var process = new GameLauncher().Start(plan);
        process.OutputReceived += (_, line) =>
        {
            lock (lines)
            {
                lines.Add(line);
            }
        };

        var exitCode = await process.WaitForExitAsync();

        // 输出事件可能在退出事件之后才派发，给一点时间等它到。
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline &&
               !lines.Any(line => line.Contains("hello-from-eml")))
        {
            await Task.Delay(50);
        }

        Assert.Equal(0, exitCode);
        Assert.Contains(lines, line => line.Contains("hello-from-eml"));
    }

    /// <summary>往 zip 里写一个文本条目。</summary>
    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
