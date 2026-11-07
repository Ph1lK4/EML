using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using EML.Core.Java;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Launching;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Launching;

/// <summary>执行统一启动计划并管理自己创建的 Java 进程；不执行整合包附带脚本。</summary>
public sealed class MinecraftGameLauncher : IMinecraftGameLauncher
{
    private const int OutputFragmentLength = 8192;
    private readonly string _launcherRoot;
    private readonly MinecraftLaunchPlanner _planner;

    /// <summary>复用 Java 检测；保留 checker 参数兼容旧调用方，启动不进行完整检查。</summary>
    public MinecraftGameLauncher(string launcherRoot, IMinecraftInstanceChecker? checker = null, IJavaLocator? javaLocator = null)
    {
        _launcherRoot = Path.GetFullPath(launcherRoot);
        _planner = new MinecraftLaunchPlanner(_launcherRoot, checker, javaLocator);
    }

    /// <summary>只读验证启动前提，不创建进程、日志或临时文件。</summary>
    public Task<MinecraftLaunchPlan> PrepareAsync(string instancePath, MinecraftLaunchSettings settings,
        IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken cancellationToken = default) =>
        _planner.PrepareAsync(instancePath, settings, progress, cancellationToken);

    /// <summary>每次读取当前启动配置；旧 ZIP 可首次本地准备，不触发整包扫描或网络下载。</summary>
    public async Task<MinecraftLaunchResult> RunAsync(string instancePath, MinecraftLaunchSettings settings,
        IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var plan = await _planner.PrepareForRunAsync(instancePath, settings, progress, cancellationToken).ConfigureAwait(false);
            return await RunPreparedAsync(plan, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, null, true, null);
        }
    }

    /// <summary>只供执行层测试复用；公开入口必须先完成完整准备，不允许外部传入任意可执行文件。</summary>
    internal async Task<MinecraftLaunchResult> RunPreparedAsync(MinecraftLaunchPlan plan,
        IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var root = EmbeddedFileBaseline.ValidateInstance(_launcherRoot, plan.InstancePath);
        ValidateDirectoryWithin(root, plan.GameDirectory, mustExist: true);
        var natives = ValidateDirectoryWithin(root, plan.NativesDirectory, mustExist: false);
        foreach (var child in new[] { "", "java", "jna", "lwjgl", "netty" })
        {
            var directory = Path.Combine(natives, child);
            InstanceImportPaths.EnsureNoLinks(directory);
            Directory.CreateDirectory(directory);
        }
        var launchDirectory = InstanceImportPaths.Within(root, ".eml-runtime/launch");
        var logDirectory = InstanceImportPaths.Within(root, ".eml-runtime/launch/logs");
        InstanceImportPaths.EnsureNoLinks(logDirectory);
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, Guid.NewGuid().ToString("N") + ".log");
        string? argumentFile = null;
        using var process = new Process();
        var started = false;
        var canceled = false;
        try
        {
            process.StartInfo = CreateStartInfo(plan);
            if (plan.JavaMajorVersion >= 9)
            {
                var argumentFilePath = Path.Combine(launchDirectory, Guid.NewGuid().ToString("N") + ".args");
                var encoding = NativeArgumentEncoding();
                var reference = "@" + Path.GetRelativePath(plan.GameDirectory, argumentFilePath);
                // Java 启动器按本机编码解释原生参数，不以 Java 18 的默认文件编码代替它。
                _ = encoding.GetBytes(reference);
                var bytes = EncodeArgumentFile(plan.Arguments, encoding);
                InstanceImportPaths.EnsureNoLinks(argumentFilePath);
                await using (var output = new FileStream(argumentFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    argumentFile = argumentFilePath;
                    await output.WriteAsync(bytes, token).ConfigureAwait(false);
                }
                process.StartInfo.ArgumentList.Add(reference);
            }
            else
            {
                ValidateJava8CommandLength(plan);
                foreach (var argument in plan.Arguments) process.StartInfo.ArgumentList.Add(argument);
            }

            // 先打开日志；启动后日志写入失败也必须继续消费管道并管理子进程。
            await using var log = new ManagedLogWriter(new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 16 * 1024, useAsync: true), new UTF8Encoding(false)));
            var channel = Channel.CreateBounded<MinecraftLaunchProgress>(new BoundedChannelOptions(128)
            {
                SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
            });
            var pump = PumpOutputAsync(channel.Reader, log, progress, logPath);
            using var readCancellation = new CancellationTokenSource();
            Task readers = Task.CompletedTask;
            try
            {
                token.ThrowIfCancellationRequested();
                started = process.Start();
                if (!started) throw new IOException("未能创建 Java 进程。");
                // 两条管道同时读取，不串行 ReadToEnd；长行分段，避免无换行输出无限增长。
                readers = Task.WhenAll(ReadOutputAsync(process.StandardOutput, false, channel.Writer, readCancellation.Token),
                    ReadOutputAsync(process.StandardError, true, channel.Writer, readCancellation.Token));
                Report(progress, new(MinecraftLaunchStage.Running, "Java 已启动，正在等待游戏初始化。", LogPath: logPath));
                try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    canceled = true;
                    await StopOwnedProcessAsync(process).ConfigureAwait(false);
                }
            }
            finally
            {
                // 任何后续异常都不能留下失去管理的游戏进程。
                try
                {
                    if (started && !process.HasExited) await StopOwnedProcessAsync(process).ConfigureAwait(false);
                }
                finally
                {
                    try { await readers.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        readCancellation.Cancel();
                        await readers.ConfigureAwait(false);
                        channel.Writer.TryWrite(new(MinecraftLaunchStage.Output, "进程已退出，输出管道仍被占用，已停止读取。", true));
                    }
                    finally
                    {
                        channel.Writer.TryComplete();
                        await pump.ConfigureAwait(false);
                    }
                }
            }
            return new(started, started ? process.ExitCode : null, canceled, logPath);
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidOperationException("当前 Java 的本机字符编码无法表示启动参数。请移除该字符或将实例移到当前系统编码支持的路径。", ex);
        }
        finally
        {
            if (argumentFile is not null) DeleteOwnedArgumentFile(argumentFile);
        }
    }

    /// <summary>所有参数都以 ArgumentList 传递；不经 cmd/PowerShell 展开重定向或命令替换。</summary>
    internal static ProcessStartInfo CreateStartInfo(MinecraftLaunchPlan plan) => new()
    {
        FileName = plan.JavaPath, WorkingDirectory = plan.GameDirectory, UseShellExecute = false,
        CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
    };

    /// <summary>以本机编码写 Java 9+ 参数文件；按字节转义，兼容多字节编码中出现的反斜杠字节。</summary>
    internal static byte[] EncodeArgumentFile(IReadOnlyList<string> arguments, Encoding encoding)
    {
        using var output = new MemoryStream();
        foreach (var argument in arguments)
        {
            if (argument.IndexOf('\0') >= 0) throw new InvalidDataException("启动参数不能包含空字符。");
            output.WriteByte((byte)'"');
            foreach (var value in encoding.GetBytes(argument))
            {
                var escaped = value switch { (byte)'\n' => (byte)'n', (byte)'\r' => (byte)'r', (byte)'\t' => (byte)'t', (byte)'\f' => (byte)'f', _ => value };
                if (escaped != value || value is (byte)'\\' or (byte)'"') output.WriteByte((byte)'\\');
                output.WriteByte(escaped);
            }
            output.WriteByte((byte)'"');
            output.WriteByte((byte)'\n');
        }
        return output.ToArray();
    }

    /// <summary>Windows Java 的原生启动器使用系统代码页；其他平台使用 UTF-8，无法编码时明确失败。</summary>
    private static Encoding NativeArgumentEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(OperatingSystem.IsWindows() ? (int)GetACP() : 65001,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    /// <summary>Java 8 没有通用参数文件支持，保守检查 Windows 命令行上限并给出可操作提示。</summary>
    internal static void ValidateJava8CommandLength(MinecraftLaunchPlan plan)
    {
        var upperBound = (long)plan.JavaPath.Length + 3;
        foreach (var argument in plan.Arguments) upperBound += (long)argument.Length * 2 + 3;
        if (OperatingSystem.IsWindows() && upperBound >= 32767)
            throw new InvalidOperationException("Java 8 启动参数超过 Windows 安全长度限制。请缩短实例路径，或为支持新版 Java 的整合包选择 Java 9 及以上版本。");
    }

    /// <summary>验证游戏和 natives 目录仍在本实例内部，拒绝目录联接和路径逃逸。</summary>
    private static string ValidateDirectoryWithin(string root, string value, bool mustExist)
    {
        var path = Path.GetFullPath(value);
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("启动目录不在选定实例内。");
        InstanceImportPaths.EnsureNoLinks(path);
        if (mustExist && !Directory.Exists(path)) throw new DirectoryNotFoundException("游戏目录不存在。");
        return path;
    }

    /// <summary>并发消费单条输出管道；固定缓冲及分段上限避免大日志耗尽内存。</summary>
    private static async Task ReadOutputAsync(StreamReader reader, bool standardError,
        ChannelWriter<MinecraftLaunchProgress> output, CancellationToken token)
    {
        var buffer = new char[4096];
        var line = new StringBuilder(OutputFragmentLength);
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            {
                for (var index = 0; index < count; index++)
                {
                    var value = buffer[index];
                    if (value == '\n')
                    {
                        if (line.Length > 0 && line[^1] == '\r') line.Length--;
                        await EmitAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        line.Append(value);
                        if (line.Length >= OutputFragmentLength && !char.IsHighSurrogate(value))
                            await EmitAsync().ConfigureAwait(false);
                    }
                }
            }
            if (line.Length > 0) await EmitAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException ex) { output.TryWrite(new(MinecraftLaunchStage.Output, "读取进程输出失败：" + ex.Message, true)); }

        /// <summary>提交一个有限长度的日志片段；队列满时等待消费，保持内存有界。</summary>
        async Task EmitAsync()
        {
            await output.WriteAsync(new(MinecraftLaunchStage.Output, line.ToString(), standardError), token).ConfigureAwait(false);
            line.Clear();
        }
    }

    /// <summary>串行写日志并通知界面；日志或界面失败不影响双管道消费和进程退出处理。</summary>
    internal static async Task PumpOutputAsync(ChannelReader<MinecraftLaunchProgress> source, TextWriter log,
        IProgress<MinecraftLaunchProgress>? progress, string logPath)
    {
        var writable = true;
        await foreach (var item in source.ReadAllAsync().ConfigureAwait(false))
        {
            if (writable)
            {
                try
                {
                    await log.WriteLineAsync((item.StandardError ? "[stderr] " : "[stdout] ") + item.Message).ConfigureAwait(false);
                    await log.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    writable = false;
                    Report(progress, new(MinecraftLaunchStage.Output, "日志写入失败，仍继续管理游戏进程：" + ex.Message, true, logPath));
                }
            }
            Report(progress, item with { LogPath = logPath });
        }
    }

    /// <summary>只停止本次创建的进程及其子树；失败时明确报告，不按进程名终止其他 Java。</summary>
    private static async Task StopOwnedProcessAsync(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { return; }
        catch (Win32Exception) when (process.HasExited) { return; }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException ex) { throw new IOException("无法确认本次启动的 Java 进程已停止，进程 ID：" + process.Id, ex); }
    }

    /// <summary>隔离界面进度回调异常，避免显示故障阻断输出管道或遗留运行进程。</summary>
    private static void Report(IProgress<MinecraftLaunchProgress>? progress, MinecraftLaunchProgress value)
    {
        try { progress?.Report(value); }
        catch (Exception ex) { Trace.TraceWarning("启动进度通知失败：{0}", ex.Message); }
    }

    /// <summary>仅删除本次用 CreateNew 创建的参数文件；日志及用户内容均保留。</summary>
    private static void DeleteOwnedArgumentFile(string path)
    {
        try
        {
            InstanceImportPaths.EnsureNoLinks(path);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("无法清理启动参数文件 {0}：{1}", path, ex.Message); }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    /// <summary>写入错误由日志消费者报告，关闭时的再次刷新失败不能掩盖已取得的进程退出结果。</summary>
    private sealed class ManagedLogWriter(StreamWriter writer) : TextWriter
    {
        public override Encoding Encoding => writer.Encoding;
        public override Task WriteLineAsync(string? value) => writer.WriteLineAsync(value);
        public override Task FlushAsync() => writer.FlushAsync();

        /// <summary>最终关闭只处理日志介质错误，进程已退出的结果仍正常返回。</summary>
        public override async ValueTask DisposeAsync()
        {
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            { Trace.TraceWarning("关闭游戏日志失败：{0}", ex.Message); }
        }
    }
}
