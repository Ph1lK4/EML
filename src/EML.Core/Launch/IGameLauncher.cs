// ════════════════════════════════════════════════════════════════════
//  文件：IGameLauncher.cs
//  位置：src\EML.Core\Launch
//  作用：「用一条启动计划，把游戏进程拉起来」的接口。
//
//  为什么要有这一层？
//    LaunchPlan 只描述「要跑什么」，不负责「怎么跑」。
//    真正启动要用 Process，属于 Windows/Infrastructure 的事，
//    所以 Core 只留接口，实现放在 EML.Infrastructure.Launch.GameLauncher。
// ════════════════════════════════════════════════════════════════════

namespace EML.Core.Launch;

/// <summary>
/// 启动器：把 <see cref="LaunchPlan"/> 变成真正在跑的进程。
/// </summary>
public interface IGameLauncher
{
    /// <summary>
    /// 启动游戏。返回时进程已经起来了，调用方可以开始收日志。
    /// </summary>
    /// <param name="plan">完整的启动计划（Java 路径 + 参数 + 工作目录）。</param>
    /// <returns>进程句柄，调用方用完要 Dispose。</returns>
    /// <exception cref="InvalidOperationException">进程没能启动时抛出。</exception>
    IGameProcess Start(LaunchPlan plan);
}
