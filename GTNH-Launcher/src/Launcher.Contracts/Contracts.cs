namespace Launcher.Contracts;

// ---------------------------------------------------------------------------
// 这个文件是「组队契约」：负责 Java / 下载 / 启动核心 / 界面的人各自实现一部分，
// 最后靠这些类型拼起来。改这里要全组先说一声。
// ---------------------------------------------------------------------------

/// <summary>版本 JSON 里 javaVersion 字段映射过来的 Java 需求。</summary>
/// <param name="Component">Mojang 组件名，例如 jre-legacy / java-runtime-gamma。</param>
/// <param name="MajorVersion">大版本号，例如 8 / 17 / 21。</param>
public sealed record JavaRequirement(string Component, int MajorVersion);

/// <summary>一个已经落地的 Java 运行时（朋友写的 Java 检测模块输出这个）。</summary>
public sealed record JavaRuntime(
    string Path,
    string Version,
    int Major,
    string? Vendor = null,
    bool? Is64Bit = null,
    string Source = "system");

/// <summary>Java 解析入口：先找已装，再谈下载。</summary>
public interface IJavaProvider
{
    Task<JavaRuntime?> ResolveAsync(JavaRequirement requirement, CancellationToken ct = default);
}

/// <summary>一条下载任务：目标路径 + 校验信息，地址可以是官方也可以是镜像。</summary>
public sealed record DownloadTask(
    string Url,
    string Destination,
    string? Sha1,
    long? Size,
    string Label,
    string Kind = "library");

public sealed record DownloadProgress(int Completed, int Total, long BytesDone, long? BytesTotal, string Label);

/// <summary>下载器契约：并发、重试、镜像切换都由实现方负责，调用方只给任务表。</summary>
public interface IDownloadService
{
    Task DownloadAsync(
        IReadOnlyList<DownloadTask> tasks,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>登录信息。离线模式也要给 UUID（Java 版按名字算，见 OfflineUuid）。</summary>
public sealed record AuthInfo(string PlayerName, string Uuid, string AccessToken = "0", string UserType = "legacy");

/// <summary>启动一次游戏需要的东西。GameDirectory 是实例的 .minecraft 目录。</summary>
public sealed record LaunchRequest
{
    public required JavaRuntime Java { get; init; }
    public required string GameDirectory { get; init; }
    public required string VersionName { get; init; }
    public AuthInfo Auth { get; init; } = new("Player", "");
    public int MinMemoryMb { get; init; } = 1024;
    public int MaxMemoryMb { get; init; } = 6144;
    public IReadOnlyList<string> ExtraJvmArgs { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExtraGameArgs { get; init; } = Array.Empty<string>();
}

/// <summary>需要解压到 natives 目录的 jar。</summary>
public sealed record NativeJar(string LibraryName, string JarPath, IReadOnlyList<string> Excludes);

/// <summary>启动核心的最终产物：一条可以直接交给 Process 的命令行。</summary>
public sealed record LaunchPlan
{
    public required string JavaPath { get; init; }
    public required string MainClass { get; init; }
    public required IReadOnlyList<string> JvmArgs { get; init; }
    public required IReadOnlyList<string> GameArgs { get; init; }
    public required string Classpath { get; init; }
    public required string WorkingDirectory { get; init; }
    public IReadOnlyList<NativeJar> Natives { get; init; } = Array.Empty<NativeJar>();

    public IEnumerable<string> EnumerateArgs() => JvmArgs.Append(MainClass).Concat(GameArgs);

    /// <summary>给人看的命令行（含引号）；真正启动请用 ProcessStartInfo.ArgumentList。</summary>
    public string ToDisplayCommandLine()
    {
        var parts = new List<string> { Quote(JavaPath) };
        parts.AddRange(EnumerateArgs().Select(Quote));
        return string.Join(' ', parts);
    }

    private static string Quote(string value) =>
        value.Contains(' ') || value.Contains('"') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
}

/// <summary>规则判定用的平台上下文。1.7.10 只用到 os.name，arch 给老 natives 的 ${arch} 用。</summary>
public sealed record RuleContext(string OsName, string Arch, string OsVersion, IReadOnlyDictionary<string, bool> Features)
{
    public static RuleContext WindowsX64(IReadOnlyDictionary<string, bool>? features = null) =>
        new("windows", "x86_64", "10.0", features ?? Empty);

    public static RuleContext LinuxX64(IReadOnlyDictionary<string, bool>? features = null) =>
        new("linux", "x86_64", "6.0", features ?? Empty);

    private static readonly IReadOnlyDictionary<string, bool> Empty = new Dictionary<string, bool>();
}

// ---------------------------------------------------------------------------
// 进程层与诊断：启动核心把 LaunchPlan 交给 IProcessRunner，跑完把日志尾部交给诊断器。
// 实现方（Launcher.Runtime）负责真正起进程；契约层不碰 System.Diagnostics。
// ---------------------------------------------------------------------------

/// <summary>进程输出的一行。Stream 是 stdout / stderr。</summary>
public sealed record ProcessLine(string Stream, string Text);

/// <summary>
/// 一次进程运行的结果。Error 非空表示进程根本没启动起来（不是退出码）；
/// Cancelled 表示调用方取消、进程被我们杀掉了。
/// </summary>
public sealed record ProcessRunResult(int ExitCode, TimeSpan Duration, bool Cancelled, string? Error = null);

/// <summary>起一个子进程并等它结束。参数一律按 ArgumentList 传，不拼字符串。</summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<ProcessLine>? onLine = null,
        CancellationToken ct = default);
}

/// <summary>崩溃诊断：给玩家看的结论 + 可执行建议。MatchedLine 是命中的那行日志，方便 UI 高亮。</summary>
public sealed record CrashDiagnosis(
    string Code,
    string Title,
    string Detail,
    IReadOnlyList<string> Suggestions,
    string? MatchedLine = null);

/// <summary>
/// 按日志尾部 + 退出码判断「为什么崩」。
/// 正常退出返回 null；识别不了返回 Code=UNKNOWN 的诊断，别让调用方拿到空指针。
/// </summary>
public interface ICrashDiagnoser
{
    CrashDiagnosis? Diagnose(IReadOnlyList<string> logLines, int? exitCode);
}
