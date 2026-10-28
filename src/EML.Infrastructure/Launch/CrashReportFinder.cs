// ════════════════════════════════════════════════════════════════════
//  文件：CrashReportFinder.cs
//  位置：src\EML.Infrastructure\Launch
//  作用：在 <游戏目录>\crash-reports 里找最新的崩溃报告文件。
//
//  规则：
//    文件名形如 crash-2026-10-24_09.47.50-client.txt；
//    按「最后修改时间」取最新的一份，不靠文件名排序
//    （时间格式一旦变，字符串排序就不可靠了）。
// ════════════════════════════════════════════════════════════════════

using EML.Core.Launch;

namespace EML.Infrastructure.Launch;

/// <summary>
/// 崩溃报告查找：只看文件系统，不读内容。
/// </summary>
public sealed class CrashReportFinder : ICrashReportFinder
{
    /// <summary>崩溃报告所在目录（相对于游戏目录）。</summary>
    private const string CrashReportsFolder = "crash-reports";

    /// <summary>崩溃报告的文件名通配符。</summary>
    private const string CrashReportPattern = "crash-*.txt";

    /// <summary>
    /// 找最近修改的一份崩溃报告；没有就返回 null。
    /// 读目录失败（权限、路径不存在）也返回 null：
    /// 界面只需要知道「有没有」，不该因为找文件失败再抛一次异常。
    /// </summary>
    public string? FindLatest(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return null;
        }

        var directory = Path.Combine(gameDirectory, CrashReportsFolder);

        if (!Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            return Directory
                .EnumerateFiles(directory, CrashReportPattern)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
