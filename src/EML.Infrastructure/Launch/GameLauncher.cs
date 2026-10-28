// ════════════════════════════════════════════════════════════════════
//  文件：GameLauncher.cs
//  位置：src\EML.Infrastructure\Launch
//  作用：用 System.Diagnostics.Process 实现 IGameLauncher。
//
//  这一段做三件事：
//    1) 按启动计划启动 java.exe（参数原样传，不经过 shell）；
//    2) 把 stdout / stderr 读出来，逐行通过事件抛给上层；
//    3) 提供「等退出」和「结束进程树」。
//
//  为什么不用 UseShellExecute = true？
//    那样拿不到输出流，也没法精确地只结束这个进程树。
// ════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Globalization;
using System.Text;
using EML.Core.Launch;

namespace EML.Infrastructure.Launch;

/// <summary>
/// 游戏启动器：把一条命令真正跑起来。
/// </summary>
public sealed class GameLauncher : IGameLauncher
{
    /// <summary>
    /// 读游戏输出用的编码。
    ///
    /// 为什么不是固定 UTF-8：
    ///   Java 8 在中文 Windows 上往控制台写的是系统 OEM 代码页（简体中文是 GBK / 936），
    ///   按 UTF-8 硬解会把 Java 自己的中文报错解成「����」。
    ///   日志里大多数行是英文，GBK 与 UTF-8 解码结果一样，只有中文行会不同。
    ///
    /// 取不到 OEM 代码页时（比如系统不支持），退回 UTF-8，至少不影响英文日志。
    /// </summary>
    private static readonly Encoding ConsoleEncoding = CreateConsoleEncoding();

    private static Encoding CreateConsoleEncoding()
    {
        try
        {
            // .NET 默认只带 UTF-8/UTF-16 等，GBK 这类代码页要注册扩展提供程序
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// 启动进程。
    ///
    /// 参数直接给 ProcessStartInfo.Arguments（整串），
    /// 因为 <see cref="LaunchPlan.Arguments"/> 里的路径已经加好引号了；
    /// 不在这里二次拆分，避免把带空格的路径拆坏。
    /// </summary>
    public IGameProcess Start(LaunchPlan plan)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = plan.JavaPath,
            Arguments = plan.Arguments,
            WorkingDirectory = plan.WorkingDirectory,

            // 不用 shell，才能拿到输出、才能精确结束这个进程
            UseShellExecute = false,

            // 收日志：stdout 和 stderr 都接
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Java 在 Windows 控制台按系统 OEM 代码页输出（中文系统是 GBK），
            // 用系统码页解码，Java 的中文报错才不会乱码
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding
        };

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException(
                "进程没能启动：" + plan.JavaPath);
        }

        // 先包一层（订阅输出事件），再开始异步读，避免第一批日志丢掉
        var gameProcess = new GameProcess(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return gameProcess;
    }

    /// <summary>
    /// IGameProcess 的 Process 实现。
    ///
    /// 单独一个私有类，是为了不让 Process 泄漏到 Core / ViewModel，
    /// 也方便以后换实现（比如加一个「启动后延迟 200ms 再读日志」的变体）。
    /// </summary>
    private sealed class GameProcess : IGameProcess
    {
        private readonly Process _process;

        /// <summary>
        /// 进程号在构造时就记下来：Process.Id 在进程退出后再读会抛异常，
        /// 而界面可能要在游戏退出后还显示一次「刚才跑的是哪个进程」。
        /// </summary>
        private readonly int _id;

        public GameProcess(Process process)
        {
            _process = process;
            _id = process.Id;

            // stdout 和 stderr 都当成「一行日志」往上抛。
            // 1.7.10 的崩溃栈在 stderr，必须接。
            _process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    OutputReceived?.Invoke(this, args.Data);
                }
            };

            _process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    OutputReceived?.Invoke(this, args.Data);
                }
            };
        }

        /// <inheritdoc />
        public event EventHandler<string>? OutputReceived;

        /// <inheritdoc />
        public bool HasExited => _process.HasExited;

        /// <inheritdoc />
        public int Id => _id;

        /// <inheritdoc />
        public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

        /// <summary>
        /// 等退出并返回退出码。
        ///
        /// 为什么不用同步 WaitForExit()？
        ///   那会卡住 UI 线程，窗口在游戏退出前都动不了。
        /// </summary>
        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            await _process.WaitForExitAsync(cancellationToken);

            // 有异步读流的进程，WaitForExitAsync 返回时最后几行可能还没读完；
            // 这里再同步等一次，保证退出后日志是完整的。
            _process.WaitForExit();

            return _process.ExitCode;
        }

        /// <summary>
        /// 结束进程树。
        ///
        /// 参数 true 很重要：Java 进程可能拉起子进程，
        /// 只杀父进程会留下孤儿进程继续占着内存。
        /// </summary>
        public void Kill()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        /// <summary>释放 Process 句柄（不结束进程）。</summary>
        public void Dispose() => _process.Dispose();
    }
}
