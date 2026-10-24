// ════════════════════════════════════════════════════════════════════
//  文件：LaunchPlannerTests.cs
//  位置：tests\EML.Tests
//  作用：验证「启动命令拼得对不对」。
//
//  测试策略：
//    不依赖真实的 .minecraft，也不内置任何 Mojang 资产。
//    每个测试自己造一个临时目录，写一份最小的版本 JSON，
//    测完在 Dispose 里删掉。这样在任何机器上都能跑。
// ════════════════════════════════════════════════════════════════════

using EML.Core.Launch;
using EML.Infrastructure.Launch;

namespace EML.Tests;

/// <summary>
/// 启动命令拼接的测试。
/// 用临时目录造一份最小的版本 JSON，不依赖任何外部资产。
/// </summary>
public sealed class LaunchPlannerTests : IDisposable
{
    /// <summary>每个测试独享一个临时游戏目录，避免互相污染。</summary>
    private readonly string gameDirectory = Path.Combine(
        Path.GetTempPath(), "eml-launch-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>测试结束删掉临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(gameDirectory))
        {
            Directory.Delete(gameDirectory, true);
        }
    }

    /// <summary>
    /// 正常路径：版本 JSON 齐全时，生成的命令应该包含
    /// 主类、玩家名、资源索引、离线 UUID、所有库和客户端 jar。
    /// </summary>
    [Fact]
    public void Build_生成包含主类与离线UUID的启动命令()
    {
        // ① 造一份最小版本 JSON：只要计划器真正用到的字段。
        var versionDirectory = Path.Combine(gameDirectory, "versions", "1.7.10");
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(
            Path.Combine(versionDirectory, "1.7.10.json"),
            """
            {
              "mainClass": "net.minecraft.client.main.Main",
              "minecraftArguments": "--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory} --assetsDir ${assets_root} --assetIndex ${assets_index_name} --uuid ${auth_uuid} --userType ${user_type}",
              "assetIndex": { "id": "1.7.10" },
              "libraries": [
                {
                  "name": "a:b:1",
                  "downloads": { "artifact": { "path": "a/b/1/b-1.jar" } }
                },
                {
                  "name": "c:d:2",
                  "downloads": { "artifact": { "path": "c/d/2/d-2.jar" } }
                }
              ]
            }
            """);

        var planner = new VanillaLaunchPlanner();
        var request = new LaunchRequest(
            @"C:\Java\jre8\bin\java.exe",
            gameDirectory,
            "1.7.10",
            "Steve");

        // ② 生成。
        var plan = planner.Build(request);

        // ③ 逐项核对：路径、工作目录、主类、各个参数、库、客户端 jar。
        Assert.Equal(request.JavaPath, plan.JavaPath);
        Assert.Equal(gameDirectory, plan.WorkingDirectory);
        Assert.Contains("net.minecraft.client.main.Main", plan.Arguments);
        Assert.Contains("--username Steve", plan.Arguments);
        Assert.Contains("--assetIndex 1.7.10", plan.Arguments);

        // 离线 UUID 必须是 Java 的 nameUUIDFromBytes 规则（Steve 是固定值）。
        Assert.Contains("--uuid 5627dd98-e6be-3c21-b8a8-e92344183641", plan.Arguments);

        // 两个库都要进 classpath，客户端 jar 必须最后一项。
        Assert.Contains("b-1.jar", plan.Arguments);
        Assert.Contains("d-2.jar", plan.Arguments);
        Assert.Contains(
            Path.Combine("versions", "1.7.10", "1.7.10.jar"),
            plan.Arguments);
    }

    /// <summary>
    /// 异常路径：游戏目录里没有版本 JSON 时，
    /// 要抛一个消息明确、能直接显示给用户的异常，而不是静默失败。
    /// </summary>
    [Fact]
    public void Build_缺版本JSON时给出明确提示()
    {
        var planner = new VanillaLaunchPlanner();
        var request = new LaunchRequest(
            @"C:\Java\jre8\bin\java.exe",
            gameDirectory,
            "1.7.10",
            "Steve");

        var exception = Assert.Throws<FileNotFoundException>(
            () => planner.Build(request));

        // 消息里必须写清楚缺的是哪个文件，用户才知道去补什么。
        Assert.Contains("versions\\1.7.10\\1.7.10.json", exception.Message);
    }
}
