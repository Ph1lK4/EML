using System.Diagnostics;
using System.Text;
using Launcher.Contracts;

namespace Launcher.Runtime;

/// <summary>
/// 用 ArgumentList 启动子进程（绝不拼命令行字符串），逐行把 stdout/stderr 回调出去。
/// 取消时杀整棵进程树——Java 会再拉子进程，只杀父进程会留孤儿。
/// </summary>
public sealed class JavaProcessRunner : IProcessRunner
{
    private readonly Encoding _outputEncoding;

    /// <param name="outputEncoding">
    /// 读日志用的编码。默认 UTF-8；中文 Windows 上 Java 8 的控制台输出可能是 GBK，
    /// 真遇到乱码再传 <c>Encoding.GetEncoding(936)</c>（需要先注册 CodePagesEncodingProvider）。
    /// </param>
    public JavaProcessRunner(Encoding? outputEncoding = null) => _outputEncoding = outputEncoding ?? Encoding.UTF8;

    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<ProcessLine>? onLine = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var elapsed = Stopwatch.StartNew();

        if (ct.IsCancellationRequested)
            return new ProcessRunResult(-1, elapsed.Elapsed, true, "已取消");

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = _outputEncoding,
            StandardErrorEncoding = _outputEncoding,
        };
        foreach (var argument in arguments ?? Array.Empty<string>())
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new ProcessRunResult(-1, elapsed.Elapsed, false, "进程没有启动");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessRunResult(-1, elapsed.Elapsed, false, $"启动失败：{ex.Message}");
        }

        var cancelled = false;
        await using var registration = ct.Register(() =>
        {
            cancelled = true;
            TryKill(process);
        });

        var stdout = PumpAsync(process.StandardOutput, "stdout", onLine);
        var stderr = PumpAsync(process.StandardError, "stderr", onLine);

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }

        return new ProcessRunResult(process.ExitCode, elapsed.Elapsed, cancelled, null);
    }

    private static async Task PumpAsync(StreamReader reader, string stream, Action<ProcessLine>? onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            try
            {
                onLine?.Invoke(new ProcessLine(stream, line));
            }
            catch
            {
                // 回调方（UI）自己抛异常不该把读流线程搞挂。
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程刚好退出，忽略。
        }
    }
}
