using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Launching;
using EML.Infrastructure.Minecraft.Launching;

namespace EML.Tests;

/// <summary>通过真实受控进程及实例界面验证失败判定；不启动游戏或访问用户实例。</summary>
public sealed class LaunchFailureReportingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-launch-failure-" + Guid.NewGuid().ToString("N"));
    private const string FatalOutput = "org.lwjglx.SDLException: Could not initialize SDL. SDL error: Haptic error CoCreateInstance";

    /// <summary>两条输出管道中的明确致命信息都优先于零退出码，且弹窗保留真实日志路径。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FatalOutputWithZeroExitIsReportedAsFailure(bool standardError, bool wrapped)
    {
        var stream = standardError ? "Error" : "Out";
        var output = (wrapped ? $"[Client thread/INFO] [{(standardError ? "STDERR" : "STDOUT")}]: " : "") + FatalOutput;
        // 尾行没有换行且紧接退出，结果仍必须等待输出排空后再判定。
        var plan = CreatePlan($"[Console]::{stream}.Write('{output}'); exit 0");
        var (model, launcher, notifications) = await CreateModelAsync(plan);

        await StartLaunch(model);

        var result = Assert.IsType<MinecraftLaunchResult>(launcher.LastResult);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(MinecraftLaunchOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Failure);
        Assert.Contains(FatalOutput, result.Failure.Evidence);
        var notice = Assert.Single(notifications.Notices);
        Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
        Assert.Contains(FatalOutput, notice.Message);
        Assert.Contains("0", notice.Message);
        Assert.NotNull(result.LogPath);
        Assert.Equal(result.LogPath, model.LaunchLogPath);
        Assert.True(File.Exists(result.LogPath));
        Assert.Contains(result.LogPath, notice.Paths);
        var diskLog = await File.ReadAllTextAsync(result.LogPath);
        Assert.Contains($"[{(standardError ? "stderr" : "stdout")}] " + output, diskLog);
        Assert.Contains(model.LaunchLogLines, line => line.Contains(FatalOutput, StringComparison.Ordinal));
        Assert.False(model.IsGameRunning);
        Assert.False(model.IsBusy);
    }

    /// <summary>没有已知致命文本时，非零退出码仍作为失败处理，不能被新检测器放行。</summary>
    [Fact]
    public async Task NonzeroExitWithoutRecognizedDiagnosticStillFails()
    {
        var (model, launcher, notifications) = await CreateModelAsync(
            CreatePlan("[Console]::Out.WriteLine('fixture process exiting'); exit 7"));

        await StartLaunch(model);

        var result = Assert.IsType<MinecraftLaunchResult>(launcher.LastResult);
        Assert.Null(result.Failure);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(MinecraftLaunchOutcome.Failed, result.Outcome);
        var notice = Assert.Single(notifications.Notices);
        Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
        Assert.Contains("7", notice.Message);
        Assert.Contains(result.LogPath!, notice.Paths);
        Assert.True(File.Exists(result.LogPath));
    }

    /// <summary>诊断、界面和落盘日志共用已脱敏输出，不允许新增失败证据泄露登录令牌。</summary>
    [Fact]
    public async Task FatalEvidenceIsRedactedAcrossResultNoticeUiAndDisk()
    {
        const string secret = "fixture-fake-access-token-never-a-real-credential";
        var plan = CreatePlan($"[Console]::Out.WriteLine('{FatalOutput} {secret}'); " +
            $"[Console]::Error.WriteLine('{FatalOutput} {secret}'); exit 0") with { SensitiveValues = [secret] };
        var (model, launcher, notifications) = await CreateModelAsync(plan);

        await StartLaunch(model);

        var result = Assert.IsType<MinecraftLaunchResult>(launcher.LastResult);
        var failure = Assert.IsType<MinecraftLaunchFailure>(result.Failure);
        Assert.Equal(MinecraftLaunchOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(secret, failure.Evidence);
        Assert.Contains("[登录令牌已隐藏]", failure.Evidence);
        var notice = Assert.Single(notifications.Notices);
        Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
        Assert.DoesNotContain(secret, notice.Title + notice.Message);
        Assert.Contains("[登录令牌已隐藏]", notice.Message);
        Assert.DoesNotContain(secret, model.LaunchStatus);
        Assert.All(model.LaunchLogLines, line => Assert.DoesNotContain(secret, line));
        var diskLog = await File.ReadAllTextAsync(result.LogPath!);
        Assert.DoesNotContain(secret, diskLog);
        Assert.Contains("[登录令牌已隐藏]", diskLog);
    }

    /// <summary>致命信息后超过界面容量的普通日志不能抹除最终失败证据。</summary>
    [Fact]
    public async Task FatalEvidenceSurvivesTheBoundedUiLogTail()
    {
        var plan = CreatePlan($"[Console]::Out.WriteLine('{FatalOutput}'); " +
            "for ($i = 0; $i -lt 3000; $i++) { [Console]::Out.WriteLine('ordinary-noise-' + $i) }; exit 0");
        var (model, launcher, notifications) = await CreateModelAsync(plan);

        await StartLaunch(model);

        var result = Assert.IsType<MinecraftLaunchResult>(launcher.LastResult);
        Assert.Equal(MinecraftLaunchOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Failure);
        Assert.Contains(FatalOutput, result.Failure.Evidence);
        Assert.InRange(model.LaunchLogLines.Count, 1, 1000);
        var notice = Assert.Single(notifications.Notices);
        Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
        Assert.Contains(FatalOutput, notice.Message);
        var diskLog = await File.ReadAllTextAsync(result.LogPath!);
        Assert.Contains(FatalOutput, diskLog);
        Assert.Contains("ordinary-noise-2999", diskLog);
    }

    /// <summary>可恢复的警告、错误和捕获异常不等于启动失败；零退出只报告进程结束。</summary>
    [Fact]
    public async Task NonfatalChatterDoesNotBecomeFailureOrGameSuccess()
    {
        var plan = CreatePlan("[Console]::Out.WriteLine('[WARN] optional texture unavailable; using fallback'); " +
            "[Console]::Error.WriteLine('[ERROR] Failed to load guide page; retry later'); " +
            "[Console]::Error.WriteLine('Caught java.io.IOException: optional cache unavailable'); " +
            "[Console]::Error.WriteLine('    at example.OptionalCache.read(OptionalCache.java:42)'); exit 0");
        var (model, launcher, notifications) = await CreateModelAsync(plan);

        await StartLaunch(model);

        var result = Assert.IsType<MinecraftLaunchResult>(launcher.LastResult);
        Assert.Null(result.Failure);
        Assert.Equal(MinecraftLaunchOutcome.ProcessExited, result.Outcome);
        var notice = Assert.Single(notifications.Notices);
        Assert.Equal(OperationNoticeKind.Warning, notice.Kind);
        Assert.Equal("游戏进程已退出", notice.Title);
        Assert.DoesNotContain("正常退出", notice.Title + notice.Message);
        Assert.Contains(result.LogPath!, notice.Paths);
        Assert.False(model.IsBusy);
    }

    /// <summary>收到致命输出后显式停止该受控进程，最终仍优先报告取消而非失败。</summary>
    [Fact]
    public async Task CancellationAfterFatalOutputRemainsCanceled()
    {
        var plan = CreatePlan($"[Console]::Out.WriteLine('{FatalOutput}'); " +
            "[Console]::Out.Flush(); Start-Sleep -Seconds 120");
        var (model, launcher, notifications) = await CreateModelAsync(plan);
        launcher.CancelWhen = value => value.Stage == MinecraftLaunchStage.Output &&
            value.Message.Contains(FatalOutput, StringComparison.Ordinal);

        await StartLaunch(model);

        Assert.True(launcher.CanceledFromOutput);
        var result = Assert.IsType<MinecraftLaunchResult>(launcher.LastResult);
        Assert.True(result.IsCanceled);
        Assert.NotNull(result.Failure);
        Assert.Equal(MinecraftLaunchOutcome.Canceled, result.Outcome);
        var notice = Assert.Single(notifications.Notices);
        Assert.Equal(OperationNoticeKind.Cancelled, notice.Kind);
        Assert.Contains(result.LogPath!, notice.Paths);
        Assert.Contains(FatalOutput, await File.ReadAllTextAsync(result.LogPath!));
        Assert.False(model.IsGameRunning);
        Assert.False(model.IsBusy);
    }

    /// <summary>同一界面及启动服务再次运行时，上一轮失败证据不能污染新结果。</summary>
    [Fact]
    public async Task CleanRunAfterFatalRunDoesNotReuseFailureEvidence()
    {
        var (model, launcher, notifications) = await CreateModelAsync(
            CreatePlan($"[Console]::Out.WriteLine('{FatalOutput}'); exit 0"));
        await StartLaunch(model);
        Assert.Equal(MinecraftLaunchOutcome.Failed, launcher.LastResult!.Outcome);
        Assert.Equal(OperationNoticeKind.Failure, Assert.Single(notifications.Notices).Kind);

        launcher.Plan = CreatePlan("[Console]::Out.WriteLine('clean second execution'); exit 0");
        notifications.Notices.Clear();
        await StartLaunch(model);

        var result = Assert.IsType<MinecraftLaunchResult>(launcher.LastResult);
        Assert.Null(result.Failure);
        Assert.Equal(MinecraftLaunchOutcome.ProcessExited, result.Outcome);
        var notice = Assert.Single(notifications.Notices);
        Assert.Equal(OperationNoticeKind.Warning, notice.Kind);
        Assert.DoesNotContain(FatalOutput, notice.Message);
        Assert.All(model.LaunchLogLines, line => Assert.DoesNotContain(FatalOutput, line));
        var diskLog = await File.ReadAllTextAsync(result.LogPath!);
        Assert.Contains("clean second execution", diskLog);
        Assert.DoesNotContain(FatalOutput, diskLog);
    }

    /// <summary>使用内存设置及禁止副作用的替身，仅让启动执行层走真实实现。</summary>
    private async Task<(InstancesViewModel Model, PreparedProcessLauncher Launcher, RecordingNotifications Notifications)>
        CreateModelAsync(MinecraftLaunchPlan plan)
    {
        var unused = new UnusedServices();
        var notifications = new RecordingNotifications();
        var launcher = new PreparedProcessLauncher(_root, plan);
        var model = new InstancesViewModel(unused, unused, unused, unused, _root, unused, unused,
            launcher, new MemorySettings(), notifications: notifications);
        model.SelectedInstance = new("Failure reporting fixture", plan.InstancePath, plan.GameDirectory, []);
        await model.LaunchSettingsLoadTask;
        Assert.True(model.LaunchGameCommand.CanExecute(null));
        return (model, launcher, notifications);
    }

    /// <summary>只执行系统 PowerShell 的固定测试脚本，不调用 Java 或任意整合包脚本。</summary>
    private MinecraftLaunchPlan CreatePlan(string script)
    {
        var instance = Path.Combine(_root, "instances", "fixture");
        var game = Path.Combine(instance, ".minecraft");
        Directory.CreateDirectory(game);
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return new(instance, game, executable, 8, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script])
        {
            NativesDirectory = Path.Combine(game, "natives")
        };
    }

    /// <summary>同步交付界面进度，让最终断言不依赖线程池回调到达时间。</summary>
    private static Task StartLaunch(InstancesViewModel model)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new ImmediateProgressContext());
        try { return model.LaunchGameCommand.ExecuteAsync(null); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    /// <summary>只清理由当前测试创建的随机临时根目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>即时执行投递的回调，测试不依赖 WPF Dispatcher。</summary>
    private sealed class ImmediateProgressContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    /// <summary>同步观察进程进度，支持在确定收到输出后触发取消。</summary>
    private sealed class InlineProgress(Action<MinecraftLaunchProgress> report) : IProgress<MinecraftLaunchProgress>
    {
        public void Report(MinecraftLaunchProgress value) => report(value);
    }

    /// <summary>绕过实例准备但复用同一真实进程服务，验证管道、日志和多次运行的状态隔离。</summary>
    private sealed class PreparedProcessLauncher(string root, MinecraftLaunchPlan plan) : IMinecraftGameLauncher
    {
        private readonly MinecraftGameLauncher _launcher = new(root);
        public MinecraftLaunchPlan Plan { get; set; } = plan;
        public MinecraftLaunchResult? LastResult { get; private set; }
        public Func<MinecraftLaunchProgress, bool>? CancelWhen { get; set; }
        public bool CanceledFromOutput { get; private set; }

        /// <summary>测试不执行真实实例准备，误调用时立即失败。</summary>
        public Task<MinecraftLaunchPlan> PrepareAsync(string instancePath, MinecraftLaunchSettings settings,
            IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>限定测试进程寿命并转发生产进度，返回真实执行结果而非模拟结果。</summary>
        public async Task<MinecraftLaunchResult> RunAsync(string instancePath, MinecraftLaunchSettings settings,
            IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            LastResult = null;
            CanceledFromOutput = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var observedProgress = new InlineProgress(value =>
            {
                progress?.Report(value);
                if (CancelWhen?.Invoke(value) == true)
                {
                    CanceledFromOutput = true;
                    timeout.Cancel();
                }
            });
            LastResult = await _launcher.RunPreparedAsync(Plan, observedProgress, timeout.Token);
            return LastResult;
        }
    }

    /// <summary>收集每轮通知，保证不会先弹成功再弹失败。</summary>
    private sealed class RecordingNotifications : IOperationNotifications
    {
        public List<OperationNotice> Notices { get; } = [];
        public void Report(OperationNotice notice) => Notices.Add(notice);
    }

    /// <summary>仅返回默认离线设置，不读写磁盘配置或账号凭证。</summary>
    private sealed class MemorySettings : IMinecraftLaunchSettingsStore
    {
        public Task<MinecraftLaunchSettings> LoadAsync(string instancePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MinecraftLaunchSettings());
        public Task SaveAsync(string instancePath, MinecraftLaunchSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>禁止测试启动之外的扫描、导入、下载和实例变更。</summary>
    private sealed class UnusedServices : IMinecraftInstanceLocator, IMinecraftInstanceManager, IMinecraftInstanceImporter,
        IMinecraftInstanceChecker, IMinecraftInstanceDownloader, IInstanceDialogs
    {
        public Task<IReadOnlyList<MinecraftInstance>> FindAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string instancePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RenameAsync(string instancePath, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportAsync(string instancePath, string destinationZip, IProgress<InstanceExportProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstanceImportPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstanceImportResult> ImportAsync(string archivePath, string instanceName,
            IProgress<InstanceImportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstanceCheckReport> CheckAsync(string instancePath, InstanceCheckMode mode = InstanceCheckMode.Full,
            IProgress<InstanceCheckProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstanceDownloadReport> DownloadMissingAsync(string instancePath,
            IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public bool ConfirmDelete(MinecraftInstance instance) => throw new NotSupportedException();
        public string? ChooseExportPath(MinecraftInstance instance) => throw new NotSupportedException();
        public string? ChooseImportPath() => throw new NotSupportedException();
        public string? ConfirmImport(InstanceImportPreview preview) => throw new NotSupportedException();
    }
}
