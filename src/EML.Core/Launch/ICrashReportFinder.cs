// ════════════════════════════════════════════════════════════════════
//  文件：ICrashReportFinder.cs
//  位置：src\EML.Core\Launch
//  作用：定义「找最新的崩溃报告」的接口。
//
//  背景：
//    1.7.10 崩溃时，Minecraft 自己会在 <游戏目录>\crash-reports\ 下写
//    一份 crash-<时间>-client.txt，里面有完整堆栈。
//    这份文件比界面上的日志更有用（日志会被后续输出覆盖，崩溃报告是快照）。
//
//  为什么单独一个接口：
//    找文件是「文件系统的事」，归 Infrastructure；界面只负责展示结果。
// ════════════════════════════════════════════════════════════════════

namespace EML.Core.Launch;

/// <summary>
/// 在游戏目录里找最新的崩溃报告。
/// </summary>
public interface ICrashReportFinder
{
    /// <summary>
    /// 找 <c>crash-reports\crash-*.txt</c> 里最近修改的那一份。
    /// 找不到返回 null（不是所有崩溃都会生成崩溃报告，例如启动器层面的失败）。
    /// </summary>
    string? FindLatest(string gameDirectory);
}
