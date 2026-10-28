// ════════════════════════════════════════════════════════════════════
//  文件：CrashReportFinderTests.cs
//  位置：tests\EML.Tests
//  作用：验证「找最新崩溃报告」的逻辑。
//
//  测试策略：
//    自己造 crash-reports 目录和两份假报告，
//    用「修改时间」控制新旧，确认返回的是新的那份。
// ════════════════════════════════════════════════════════════════════

using EML.Infrastructure.Launch;

namespace EML.Tests;

/// <summary>
/// 崩溃报告查找的测试。
/// </summary>
public sealed class CrashReportFinderTests : IDisposable
{
    /// <summary>每个测试独享的临时游戏目录。</summary>
    private readonly string gameDirectory = Path.Combine(
        Path.GetTempPath(), "eml-crash-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>测试结束删掉临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(gameDirectory))
        {
            Directory.Delete(gameDirectory, true);
        }
    }

    /// <summary>没有 crash-reports 目录时应该返回 null，而不是抛异常。</summary>
    [Fact]
    public void FindLatest_没有崩溃目录时返回null()
    {
        var finder = new CrashReportFinder();

        Assert.Null(finder.FindLatest(gameDirectory));
    }

    /// <summary>有两份报告时，返回最近修改的那一份。</summary>
    [Fact]
    public void FindLatest_返回最近修改的那一份()
    {
        var directory = Path.Combine(gameDirectory, "crash-reports");
        Directory.CreateDirectory(directory);

        var older = Path.Combine(directory, "crash-2026-10-23_10.00.00-client.txt");
        var newer = Path.Combine(directory, "crash-2026-10-24_09.00.00-client.txt");

        File.WriteAllText(older, "old");
        File.WriteAllText(newer, "new");

        // 明确把修改时间拉开，避免同一秒内写文件导致顺序不稳
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var finder = new CrashReportFinder();

        Assert.Equal(newer, finder.FindLatest(gameDirectory));
    }
}
