using System.Text;
using EML.Core.Minecraft.Launching;

namespace EML.Infrastructure.Minecraft.Launching;

/// <summary>只收集本次运行的明确致命错误；普通 ERROR、异常堆栈或 stderr 本身不代表游戏失败。</summary>
internal sealed class LaunchFailureDetector
{
    internal const int MaximumLinePrefix = 2048;
    private const int MaximumEvidenceLength = 1024;
    private readonly object _gate = new();
    private readonly StringBuilder[] _lines = [new(), new()];
    private MinecraftLaunchFailure? _failure;

    /// <summary>两条管道均结束后读取结果；证据不会被后续普通输出或界面日志上限覆盖。</summary>
    internal MinecraftLaunchFailure? Failure { get { lock (_gate) return _failure; } }

    /// <summary>读取层先脱敏再提交；按管道累计有限行首，识别跨读取块的标记且不拼接两路输出。</summary>
    internal void Append(string safeText, bool standardError)
    {
        lock (_gate)
        {
            if (_failure is not null) return;
            var line = _lines[standardError ? 1 : 0];
            foreach (var value in safeText)
            {
                if (value is '\r' or '\n')
                {
                    Inspect(line);
                    line.Clear();
                    if (_failure is not null) return;
                }
                else if (line.Length < MaximumLinePrefix) line.Append(value);
            }
        }
    }

    /// <summary>管道结束时也检查没有换行符的尾行；不会读取历史日志或上一次运行的崩溃报告。</summary>
    internal void Complete(bool standardError)
    {
        lock (_gate)
        {
            var line = _lines[standardError ? 1 : 0];
            if (_failure is null) Inspect(line);
            line.Clear();
        }
    }

    /// <summary>保留第一条具体错误的有限证据，避免后续概括错误掩盖最初的初始化失败。</summary>
    private void Inspect(StringBuilder line)
    {
        if (line.Length == 0) return;
        var evidence = line.ToString().Trim();
        var content = ErrorContent(evidence);
        if (content is null) return;
        if (content.StartsWith("Caused by: ", StringComparison.Ordinal)) content = content[11..];
        var diagnostic = Classify(content);
        if (diagnostic is null) return;
        _failure = new(diagnostic.Value.Code, diagnostic.Value.Summary,
            evidence.Length > MaximumEvidenceLength ? evidence[..MaximumEvidenceLength] + "…（完整内容见日志）" : evidence);
    }

    /// <summary>展开错误级别或明确的标准流重定向包装；普通 INFO 和聊天示例不作为错误。</summary>
    private static string? ErrorContent(string line)
    {
        var content = line;
        var prefixed = false;
        var errorLevel = false;
        var logLevel = false;
        var redirectedStream = false;
        for (var count = 0; count < 6 && content.StartsWith('['); count++)
        {
            var end = content.IndexOf(']');
            if (end is < 1 or > 160) return null;
            var prefix = content[1..end];
            if (prefix.Contains("CHAT", StringComparison.OrdinalIgnoreCase)) return null;
            errorLevel |= prefix.EndsWith("/ERROR", StringComparison.Ordinal) || prefix.EndsWith("/FATAL", StringComparison.Ordinal)
                || prefix is "ERROR" or "FATAL";
            logLevel |= errorLevel || prefix.EndsWith("/INFO", StringComparison.Ordinal)
                || prefix.EndsWith("/WARN", StringComparison.Ordinal) || prefix is "INFO" or "WARN";
            // Forge 可用 INFO/WARN 重发标准流，级别并不改变其中具体致命签名的含义。
            redirectedStream |= logLevel && (prefix is "STDOUT" or "STDERR");
            prefixed = true;
            content = content[(end + 1)..].TrimStart();
            if (content.StartsWith(':')) content = content[1..].TrimStart();
        }
        return prefixed && !errorLevel && !redirectedStream ? null : content;
    }

    /// <summary>采用少量确定的失败签名，不以一般异常、日志级别、运行时长或成功阶段文字判定游戏结果。</summary>
    private static (string Code, string Summary)? Classify(string content)
    {
        if (content.StartsWith("org.lwjglx.SDLException: Could not initialize SDL.", StringComparison.Ordinal))
            return ("SdlInitialization", "游戏的 SDL 初始化失败。日志中已有明确错误；仅凭此信息不能确定是驱动、环境还是启动配置导致。");
        if (content == "Error occurred during initialization of VM"
            || content.StartsWith("Error: Could not create the Java Virtual Machine.", StringComparison.Ordinal))
            return ("JvmInitialization", "Java 虚拟机初始化失败，请查看本次日志中的具体原因和 JVM 参数。");
        if (content.StartsWith("# A fatal error has been detected by the Java Runtime Environment:", StringComparison.Ordinal))
            return ("JvmCrash", "Java 虚拟机报告了致命错误，游戏进程未能正常运行或结束。");
        // 单独的报告标题也可能是加载画面打印硬件信息，不能据此判定崩溃。
        if (content.StartsWith("#@!@# Game crashed! Crash report saved to:", StringComparison.Ordinal))
            return ("MinecraftCrash", "Minecraft 报告了游戏崩溃，请查看本次日志及其中记录的崩溃报告位置。");
        if (content.StartsWith("Exception in thread \"main\" ", StringComparison.Ordinal)
            || content.StartsWith("Exception in thread \"Render thread\" ", StringComparison.Ordinal)
            || content.StartsWith("Exception in thread \"Client thread\" ", StringComparison.Ordinal))
            return ("UncaughtGameThreadException", "游戏主线程或渲染线程发生未捕获异常，请查看错误摘要与本次运行日志。");
        return null;
    }
}
