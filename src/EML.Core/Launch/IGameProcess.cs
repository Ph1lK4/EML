// ════════════════════════════════════════════════════════════════════
//  文件：IGameProcess.cs
//  位置：src\EML.Core\Launch
//  作用：一个「正在运行的 Minecraft 进程」的最小抽象。
//
//  为什么单独抽出来？
//    窗口只关心三件事：有没有新日志、退没退出、能不能关掉。
//    把这三件事定成接口，ViewModel 就不需要认识 System.Diagnostics.Process，
//    以后想换成别的启动方式（比如先经过一个启动包装器）也不用改窗口代码。
// ════════════════════════════════════════════════════════════════════

namespace EML.Core.Launch;

/// <summary>
/// 游戏进程句柄。
///
/// 生命周期：由 <see cref="IGameLauncher.Start"/> 创建，
/// 调用方负责在不再使用后 <see cref="IDisposable.Dispose"/>。
/// </summary>
public interface IGameProcess : IDisposable
{
    /// <summary>
    /// 进程输出了一行（stdout 和 stderr 都走这里）。
    ///
    /// 注意：这个事件可能在后台线程触发，界面层要自己切回 UI 线程。
    /// </summary>
    event EventHandler<string>? OutputReceived;

    /// <summary>进程是否已经退出。</summary>
    bool HasExited { get; }

    /// <summary>
    /// 退出码；进程还没退出时是 <c>null</c>。
    /// 0 表示正常退出，其它值多半是崩溃（例如 -1073741819）。
    /// </summary>
    int? ExitCode { get; }

    /// <summary>
    /// 等进程退出，返回退出码。可以传 <paramref name="cancellationToken"/> 取消等待
    /// （取消只是「不等了」，不会杀进程）。
    /// </summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 结束进程。会连同子进程一起结束（Minecraft 会拉起 Java 子进程）。
    /// </summary>
    void Kill();
}
