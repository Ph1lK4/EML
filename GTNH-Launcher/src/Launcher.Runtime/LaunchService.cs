using System.Text;
using Launcher.Contracts;
using Launcher.Metadata;

namespace Launcher.Runtime;

/// <summary>
/// 预检结果：能不能启动、缺什么。UI 可以直接拿 MissingFiles 显示「修复」入口。
/// </summary>
public sealed class LaunchPreparation
{
    internal LaunchPreparation(
        InstanceLayout layout,
        LaunchPlan plan,
        LaunchRequest request,
        IReadOnlyList<string> missingFiles,
        string? versionJsonPath)
    {
        Layout = layout;
        Plan = plan;
        Request = request;
        MissingFiles = missingFiles;
        VersionJsonPath = versionJsonPath;
    }

    public InstanceLayout Layout { get; }
    public LaunchPlan Plan { get; }
    public LaunchRequest Request { get; }
    public IReadOnlyList<string> MissingFiles { get; }
    public string? VersionJsonPath { get; }
    public bool Ready => MissingFiles.Count == 0;
}

/// <summary>一次启动的完整结果：进程状态 + 日志文件 + 崩溃诊断。</summary>
public sealed record LaunchOutcome(
    LaunchPreparation Preparation,
    ProcessRunResult Process,
    CrashDiagnosis? Diagnosis,
    string? LogPath);

/// <summary>
/// 启动核心的编排层：预检 → 解压 natives → 起进程 → 落日志 → 诊断。
/// 不负责下载和 UI；文件缺了就直接报 MissingFiles，让下载模块去补。
/// </summary>
public sealed class LaunchService
{
    private const int TailCapacity = 2000;

    private readonly IProcessRunner _runner;
    private readonly ICrashDiagnoser _diagnoser;

    public LaunchService(IProcessRunner runner, ICrashDiagnoser diagnoser)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _diagnoser = diagnoser ?? throw new ArgumentNullException(nameof(diagnoser));
    }

    public static LaunchService CreateDefault() => new(new JavaProcessRunner(), new CrashDiagnoser());

    /// <summary>只读预检：拼出计划、逐个检查文件，不写磁盘、不起进程。</summary>
    public LaunchPreparation Prepare(
        InstanceLayout layout,
        ResolvedVersion version,
        LaunchRequest request,
        RuleContext platform,
        string? versionJsonPath = null)
    {
        var plan = LaunchPlanner.Build(version, request, platform);

        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Require(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) return;
            if (!File.Exists(path)) missing.Add(path);
        }

        // Java 是绝对路径才检查；只写 "java" 的靠 PATH 解析，这里查不到不代表有问题。
        if (Path.IsPathRooted(plan.JavaPath)) Require(plan.JavaPath);

        var jsonPath = versionJsonPath ?? layout.VersionJsonPath(version.Id);
        Require(jsonPath);

        Require(layout.ClientJarPath(version.ClientJarVersionId));

        foreach (var entry in plan.Classpath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            Require(entry);

        if (version.AssetIndex?.Id is { } assetIndex) Require(layout.AssetsIndexPath(assetIndex));
        if (version.Logging?.Client?.File?.Id is { } logConfig) Require(layout.LogConfigPath(logConfig));
        foreach (var native in plan.Natives) Require(native.JarPath);

        return new LaunchPreparation(layout, plan, request, missing, jsonPath);
    }

    /// <summary>预检通过后真正启动；不通过则直接返回 MISSING_FILES 诊断，不碰进程。</summary>
    public async Task<LaunchOutcome> RunAsync(
        LaunchPreparation preparation,
        Action<ProcessLine>? onLine = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);

        if (!preparation.Ready)
        {
            var first = preparation.MissingFiles.FirstOrDefault();
            var missingDiagnosis = new CrashDiagnosis(
                "MISSING_FILES",
                "启动文件不完整",
                $"缺少 {preparation.MissingFiles.Count} 个文件{(first is null ? "" : "，比如 " + Path.GetFileName(first))}。",
                new[]
                {
                    "先点启动器的「下载 / 修复」，把客户端 jar、库、资源、natives 补齐",
                    "确认实例目录选对了（.minecraft 里应该有 versions / libraries / assets）",
                });
            return new LaunchOutcome(
                preparation,
                new ProcessRunResult(-1, TimeSpan.Zero, false, "预检未通过"),
                missingDiagnosis,
                null);
        }

        Directory.CreateDirectory(preparation.Layout.NativesDirectory);
        NativeExtractor.Extract(preparation.Plan.Natives, preparation.Layout.NativesDirectory);

        Directory.CreateDirectory(preparation.Layout.LogsDirectory);
        var logPath = Path.Combine(preparation.Layout.LogsDirectory, $"launch-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var tail = new Queue<string>(TailCapacity + 1);
        var gate = new object();
        using var writer = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };

        void OnProcessLine(ProcessLine line)
        {
            lock (gate)
            {
                if (tail.Count >= TailCapacity) tail.Dequeue();
                tail.Enqueue(line.Text);
                writer.WriteLine($"[{line.Stream}] {line.Text}");
            }

            onLine?.Invoke(line);
        }

        var result = await _runner
            .RunAsync(
                preparation.Plan.JavaPath,
                preparation.Plan.EnumerateArgs().ToList(),
                preparation.Plan.WorkingDirectory,
                OnProcessLine,
                ct)
            .ConfigureAwait(false);

        string[] tailSnapshot;
        lock (gate) tailSnapshot = tail.ToArray();

        var diagnosis = result.Error is { } error
            ? new CrashDiagnosis(
                "LAUNCH_FAILED",
                "启动失败",
                error,
                new[] { "检查设置的 Java 路径是否存在、有没有执行权限", "确认实例目录没有被占用或只读" })
            : result.Cancelled
                ? null
                : _diagnoser.Diagnose(tailSnapshot, result.ExitCode);

        return new LaunchOutcome(preparation, result, diagnosis, logPath);
    }
}
