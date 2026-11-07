using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Launching;

namespace EML.Tests;

/// <summary>通过生产启动命令验证设置隔离和进程生命周期，所有依赖使用内存替身。</summary>
public sealed class InstancesViewModelLaunchingTests
{
    /// <summary>选中实例后显示默认离线名称、内存和自动 Java，不选实例时不能启动。</summary>
    [Fact]
    public async Task SelectionLoadsDefaultsAndEnablesLaunch()
    {
        var fixture = new Fixture();
        Assert.False(fixture.ViewModel.LaunchGameCommand.CanExecute(null));

        await fixture.SelectAsync();

        Assert.Equal("Steve", fixture.ViewModel.PlayerName);
        Assert.Equal("1024", fixture.ViewModel.MinMemoryMbText);
        Assert.Equal("8192", fixture.ViewModel.MaxMemoryMbText);
        Assert.Equal("", fixture.ViewModel.SelectedJavaPath);
        Assert.True(fixture.ViewModel.CanEditLaunchSettings);
        Assert.True(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.SaveLaunchSettingsCommand.CanExecute(null));
    }

    /// <summary>读取尚未结束时，即使直接执行命令也不能保存或使用上一实例的设置启动。</summary>
    [Fact]
    public async Task LoadingSettingsBlocksLaunchAndSave()
    {
        var fixture = new Fixture();
        var pending = NewSignal<MinecraftLaunchSettings>();
        fixture.Store.Load = (_, _) => pending.Task;
        fixture.ViewModel.SelectedInstance = fixture.Instance;

        Assert.True(fixture.ViewModel.IsLaunchSettingsLoading);
        Assert.False(fixture.ViewModel.CanEditLaunchSettings);
        Assert.False(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
        await StartLaunch(fixture.ViewModel);
        await fixture.ViewModel.SaveLaunchSettingsCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Launcher.Calls);
        Assert.Empty(fixture.Store.Saved);

        pending.SetResult(new MinecraftLaunchSettings { PlayerName = "Loaded" });
        await fixture.ViewModel.LaunchSettingsLoadTask;
        Assert.Equal("Loaded", fixture.ViewModel.PlayerName);
        Assert.True(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
    }

    /// <summary>快速切换实例时，忽略取消的旧读取即使迟到返回也不能覆盖新设置。</summary>
    [Fact]
    public async Task LateSettingsFromPreviousSelectionCannotOverwriteCurrentInput()
    {
        var fixture = new Fixture();
        var first = NewSignal<MinecraftLaunchSettings>();
        var second = NewSignal<MinecraftLaunchSettings>();
        var other = fixture.Instance with { Name = "Other", InstancePath = fixture.Instance.InstancePath + "2" };
        fixture.Store.Load = (path, _) => path == fixture.Instance.InstancePath ? first.Task : second.Task;
        fixture.ViewModel.SelectedInstance = fixture.Instance;
        var oldLoad = fixture.ViewModel.LaunchSettingsLoadTask;
        fixture.ViewModel.SelectedInstance = other;
        second.SetResult(new MinecraftLaunchSettings { PlayerName = "OtherPlayer", MaxMemoryMb = 4096 });
        await fixture.ViewModel.LaunchSettingsLoadTask;
        fixture.ViewModel.PlayerName = "UserEditing";

        first.SetResult(new MinecraftLaunchSettings { PlayerName = "StalePlayer" });
        await oldLoad;
        await fixture.ViewModel.SaveLaunchSettingsCommand.ExecuteAsync(null);

        Assert.Equal("UserEditing", fixture.ViewModel.PlayerName);
        Assert.Equal("4096", fixture.ViewModel.MaxMemoryMbText);
        Assert.Equal(other.InstancePath, Assert.Single(fixture.Store.Saved).Path);
    }

    /// <summary>清空选择会让尚未结束的读取失效，不重新启用已经没有目标的启动按钮。</summary>
    [Fact]
    public async Task ClearingSelectionInvalidatesPendingSettings()
    {
        var fixture = new Fixture();
        var pending = NewSignal<MinecraftLaunchSettings>();
        fixture.Store.Load = (_, _) => pending.Task;
        fixture.ViewModel.SelectedInstance = fixture.Instance;
        var load = fixture.ViewModel.LaunchSettingsLoadTask;
        fixture.ViewModel.SelectedInstance = null;
        pending.SetResult(new MinecraftLaunchSettings { PlayerName = "OldPlayer" });
        await load;

        Assert.False(fixture.ViewModel.IsLaunchSettingsLoaded);
        Assert.False(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
        Assert.Equal("Steve", fixture.ViewModel.PlayerName);
    }

    /// <summary>读取失败明确显示原因并阻止覆盖，修复后用户可以显式重读。</summary>
    [Fact]
    public async Task ReadFailureBlocksLaunchUntilSuccessfulReload()
    {
        var fixture = new Fixture();
        fixture.Store.Load = (_, _) => Task.FromException<MinecraftLaunchSettings>(new IOException("设置无法读取"));
        await fixture.SelectAsync();

        Assert.Contains("设置无法读取", fixture.ViewModel.LaunchSettingsStatus);
        Assert.False(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.SaveLaunchSettingsCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.ReloadLaunchSettingsCommand.CanExecute(null));

        fixture.Store.Load = (_, _) => Task.FromResult(new MinecraftLaunchSettings { PlayerName = "Recovered" });
        await fixture.ViewModel.ReloadLaunchSettingsCommand.ExecuteAsync(null);
        Assert.Equal("Recovered", fixture.ViewModel.PlayerName);
        Assert.True(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
    }

    /// <summary>界面将本次实例和用户输入作为独立设置传入启动服务，不依赖上次保存。</summary>
    [Fact]
    public async Task LaunchPassesPlayerJavaMemoryAndJvmArguments()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.ViewModel.PlayerName = "Custom_Player";
        fixture.ViewModel.SelectedJavaPath = "C:\\Java 21\\bin\\java.exe";
        fixture.ViewModel.MinMemoryMbText = "2048";
        fixture.ViewModel.MaxMemoryMbText = "6144";
        fixture.ViewModel.AdditionalJvmArguments = "-XX:+UseG1GC -Dexample=\"hello world\"";

        await StartLaunch(fixture.ViewModel);

        var call = Assert.Single(fixture.Launcher.Calls);
        Assert.Equal(fixture.Instance.InstancePath, call.Path);
        Assert.Equal("Custom_Player", call.Settings.PlayerName);
        Assert.Equal("C:\\Java 21\\bin\\java.exe", call.Settings.JavaPath);
        Assert.Equal(2048, call.Settings.MinMemoryMb);
        Assert.Equal(6144, call.Settings.MaxMemoryMb);
        Assert.Equal(fixture.ViewModel.AdditionalJvmArguments, call.Settings.AdditionalJvmArguments);
        Assert.Empty(fixture.Store.Saved);
        Assert.Equal(4, fixture.ViewModel.SelectedDetailTab);
    }

    /// <summary>空 Java 文本传入 null，使各实例由共享启动服务选择匹配版本。</summary>
    [Fact]
    public async Task EmptyJavaPathRequestsAutomaticSelection()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.ViewModel.SelectedJavaPath = "  ";
        await StartLaunch(fixture.ViewModel);
        Assert.Null(Assert.Single(fixture.Launcher.Calls).Settings.JavaPath);
    }

    /// <summary>非法文本不会退回旧值或交给 Java，错误发生后仍可修正并重试。</summary>
    [Theory]
    [InlineData("memory-text")]
    [InlineData("memory-range")]
    [InlineData("name")]
    [InlineData("jvm")]
    public async Task InvalidSettingsDoNotStartOrSave(string kind)
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        switch (kind)
        {
            case "memory-text": fixture.ViewModel.MaxMemoryMbText = "8192abc"; break;
            case "memory-range": fixture.ViewModel.MinMemoryMbText = "16384"; break;
            case "name": fixture.ViewModel.PlayerName = "bad name!"; break;
            case "jvm": fixture.ViewModel.AdditionalJvmArguments = "-Xmx2G"; break;
        }

        await fixture.ViewModel.SaveLaunchSettingsCommand.ExecuteAsync(null);
        await StartLaunch(fixture.ViewModel);

        Assert.Empty(fixture.Store.Saved);
        Assert.Empty(fixture.Launcher.Calls);
        Assert.Contains("保存失败", fixture.ViewModel.LaunchSettingsStatus);
        Assert.Contains("失败", fixture.ViewModel.LaunchStatus);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.False(fixture.ViewModel.IsPreparingLaunch);
        Assert.True(fixture.ViewModel.CanEditLaunchSettings);
    }

    /// <summary>显式保存写入对应实例，失败时保留输入并恢复交互。</summary>
    [Fact]
    public async Task SavePersistsSelectionAndReportsFailureWithoutClearingInput()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.ViewModel.PlayerName = "SavedPlayer";
        await fixture.ViewModel.SaveLaunchSettingsCommand.ExecuteAsync(null);
        var saved = Assert.Single(fixture.Store.Saved);
        Assert.Equal(fixture.Instance.InstancePath, saved.Path);
        Assert.Equal("SavedPlayer", saved.Settings.PlayerName);
        fixture.Store.Save = (_, _, _) => Task.FromException(new IOException("磁盘写入失败"));

        await fixture.ViewModel.SaveLaunchSettingsCommand.ExecuteAsync(null);

        Assert.Contains("磁盘写入失败", fixture.ViewModel.LaunchSettingsStatus);
        Assert.Equal("SavedPlayer", fixture.ViewModel.PlayerName);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    /// <summary>从完整检查到进程结束持续占用界面，停止只请求取消并等待启动服务退出。</summary>
    [Fact]
    public async Task PreparingAndRunningBlockAllConflictingOperationsUntilStopped()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        var report = NewSignal<IProgress<MinecraftLaunchProgress>>();
        var canceled = NewSignal<bool>();
        var finish = NewSignal<MinecraftLaunchResult>();
        fixture.Launcher.Run = async (progress, token) =>
        {
            report.SetResult(progress!);
            using var registration = token.Register(() => canceled.TrySetResult(true));
            return await finish.Task;
        };
        var busy = new List<bool>();
        fixture.ViewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(InstancesViewModel.IsBusy)) busy.Add(fixture.ViewModel.IsBusy);
        };

        var operation = StartLaunch(fixture.ViewModel);
        var progress = await report.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.ViewModel.IsPreparingLaunch);
        Assert.True(fixture.ViewModel.CancelLaunchPreparationCommand.CanExecute(null));
        AssertConflictingCommandsDisabled(fixture.ViewModel);
        progress.Report(new(MinecraftLaunchStage.Running, "Java 已创建", LogPath: "current.log"));
        Assert.IsType<BoundedLaunchProgress>(progress).FlushPending();
        Assert.True(fixture.ViewModel.IsGameRunning);
        Assert.False(fixture.ViewModel.IsPreparingLaunch);
        Assert.True(fixture.ViewModel.StopGameCommand.CanExecute(null));
        AssertConflictingCommandsDisabled(fixture.ViewModel);

        fixture.ViewModel.StopGameCommand.Execute(null);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(operation.IsCompleted);
        Assert.True(fixture.ViewModel.IsBusy);
        Assert.False(fixture.ViewModel.StopGameCommand.CanExecute(null));
        finish.SetResult(new(true, 143, true, "current.log"));
        await operation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([true, false], busy);
        Assert.False(fixture.ViewModel.IsGameRunning);
        Assert.Equal(143, fixture.ViewModel.LastExitCode);
        Assert.Contains("已停止", fixture.ViewModel.LaunchStatus);
        Assert.Equal("current.log", fixture.ViewModel.LaunchLogPath);
        Assert.True(fixture.ViewModel.RenameCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
    }

    /// <summary>准备阶段取消会传递到共享检查流程，最终不会保留运行状态。</summary>
    [Fact]
    public async Task CancelPreparationDoesNotReportRunningGame()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        var preparing = NewSignal<bool>();
        fixture.Launcher.Run = async (_, token) =>
        {
            preparing.SetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return new(false, null, true, null);
        };
        var operation = StartLaunch(fixture.ViewModel);
        await preparing.Task;
        fixture.ViewModel.CancelLaunchPreparationCommand.Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("取消启动准备", fixture.ViewModel.LaunchStatus);
        Assert.False(fixture.ViewModel.IsGameRunning);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.Null(fixture.ViewModel.LastExitCode);
    }

    /// <summary>检查或启动失败清楚报告原因，不把失败结果伪装成已进入游戏。</summary>
    [Fact]
    public async Task LauncherFailureRestoresUiAndPreservesReason()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.Launcher.Run = (_, _) => Task.FromException<MinecraftLaunchResult>(new IOException("必要运行库缺失"));

        await StartLaunch(fixture.ViewModel);

        Assert.Contains("必要运行库缺失", fixture.ViewModel.LaunchStatus);
        Assert.Contains(fixture.ViewModel.LaunchLogLines, line => line.Contains("必要运行库缺失"));
        Assert.False(fixture.ViewModel.IsGameRunning);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.True(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
    }

    /// <summary>异常退出保留退出码与日志路径，不能用成功创建进程推断游戏加载成功。</summary>
    [Fact]
    public async Task NonzeroExitIsVisibleWithLogPath()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.Launcher.Run = (_, _) => Task.FromResult(new MinecraftLaunchResult(true, 1, false, "failure.log"));

        await StartLaunch(fixture.ViewModel);

        Assert.Equal(1, fixture.ViewModel.LastExitCode);
        Assert.Equal("failure.log", fixture.ViewModel.LaunchLogPath);
        Assert.Contains("退出码：1", fixture.ViewModel.LaunchStatus);
    }

    /// <summary>大量输出只留下最近一千行，任务结束后的排队事件也不能改变状态。</summary>
    [Fact]
    public async Task LogsAreBoundedAndLateProgressIsIgnored()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        var reported = NewSignal<IProgress<MinecraftLaunchProgress>>();
        var finish = NewSignal<MinecraftLaunchResult>();
        fixture.Launcher.Run = (progress, _) =>
        {
            reported.SetResult(progress!);
            return finish.Task;
        };
        var operation = StartLaunch(fixture.ViewModel);
        var progress = await reported.Task;
        progress.Report(new(MinecraftLaunchStage.Running, "running"));
        for (var i = 0; i < 1100; i++) progress.Report(new(MinecraftLaunchStage.Output, "line" + i));
        Assert.IsType<BoundedLaunchProgress>(progress).FlushPending();
        Assert.Equal(1000, fixture.ViewModel.LaunchLogLines.Count);
        Assert.Equal("line100", fixture.ViewModel.LaunchLogLines[0]);
        Assert.Equal("line1099", fixture.ViewModel.LaunchLogLines[^1]);
        finish.SetResult(new(true, 0, false, "complete.log"));
        await operation;
        var lastMessage = fixture.ViewModel.LaunchStatus;

        progress.Report(new(MinecraftLaunchStage.Running, "stale", LogPath: "wrong.log"));

        Assert.False(fixture.ViewModel.IsGameRunning);
        Assert.Equal(lastMessage, fixture.ViewModel.LaunchStatus);
        Assert.Equal("complete.log", fixture.ViewModel.LaunchLogPath);
        Assert.Equal(1000, fixture.ViewModel.LaunchLogLines.Count);
        Assert.DoesNotContain("stale", fixture.ViewModel.LaunchLogLines);
    }

    /// <summary>进程很快退出时，尚未来得及调度的最后日志也会显示，随后运行状态保持结束。</summary>
    [Fact]
    public async Task FastExitFlushesBufferedOutputBeforeFinalStatus()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.Launcher.Run = (progress, _) =>
        {
            progress!.Report(new(MinecraftLaunchStage.Running, "running", LogPath: "fast.log"));
            progress.Report(new(MinecraftLaunchStage.Output, "last startup error", true));
            return Task.FromResult(new MinecraftLaunchResult(true, 1, false, "fast.log"));
        };

        await StartLaunch(fixture.ViewModel);

        Assert.False(fixture.ViewModel.IsGameRunning);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.Equal(1, fixture.ViewModel.LastExitCode);
        Assert.Contains(fixture.ViewModel.LaunchLogLines, line => line.Contains("last startup error"));
        Assert.Contains("退出码：1", fixture.ViewModel.LaunchLogLines[^1]);
    }

    /// <summary>验证准备或运行中所有可能改写实例的入口共同受忙碌状态约束。</summary>
    private static void AssertConflictingCommandsDisabled(InstancesViewModel model)
    {
        Assert.True(model.IsBusy);
        Assert.False(model.CanInteract);
        Assert.False(model.CanEditLaunchSettings);
        Assert.False(model.LaunchGameCommand.CanExecute(null));
        Assert.False(model.SaveLaunchSettingsCommand.CanExecute(null));
        Assert.False(model.ReloadLaunchSettingsCommand.CanExecute(null));
        Assert.False(model.RenameCommand.CanExecute(null));
        Assert.False(model.DeleteCommand.CanExecute(null));
        Assert.False(model.ExportCommand.CanExecute(null));
        Assert.False(model.ImportCommand.CanExecute(null));
        Assert.False(model.DownloadMissingCommand.CanExecute(null));
        Assert.False(model.QuickCheckCommand.CanExecute(null));
        Assert.False(model.FullCheckCommand.CanExecute(null));
    }

    /// <summary>测试同步投递 UI 进度，避免断言依赖线程池回调抵达时间。</summary>
    private static Task StartLaunch(InstancesViewModel model)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new ImmediateProgressContext());
        try { return model.LaunchGameCommand.ExecuteAsync(null); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    /// <summary>创建可控异步信号，防止结束任务时重入测试断言。</summary>
    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>只在这些纯内存测试中立即处理回调，生产仍使用 WPF 的 UI 同步上下文。</summary>
    private sealed class ImmediateProgressContext : SynchronizationContext
    {
        /// <summary>立即执行进度回调，让日志和阶段断言可确定地观测状态。</summary>
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    /// <summary>组装生产 ViewModel，不访问用户实例、不启动 Java、不触碰磁盘。</summary>
    private sealed class Fixture
    {
        public MinecraftInstance Instance { get; } = new("Test instance", "C:\\Test root\\instances\\Test instance", "C:\\Test root\\instances\\Test instance\\.minecraft", []);
        public FakeLauncher Launcher { get; } = new();
        public FakeSettingsStore Store { get; } = new();
        public InstancesViewModel ViewModel { get; }
        /// <summary>让所有非启动服务在误调用时直接失败。</summary>
        public Fixture()
        {
            var unused = new UnusedServices();
            ViewModel = new(unused, unused, unused, unused, "C:\\Test root\\instances", unused, unused, Launcher, Store);
        }
        /// <summary>选择测试实例并等待本次启动设置载入。</summary>
        public async Task SelectAsync()
        {
            ViewModel.SelectedInstance = Instance;
            await ViewModel.LaunchSettingsLoadTask;
        }
    }

    /// <summary>记录实际启动目标和设置，由测试控制准备、输出、退出和取消。</summary>
    private sealed class FakeLauncher : IMinecraftGameLauncher
    {
        public List<(string Path, MinecraftLaunchSettings Settings)> Calls { get; } = [];
        public Func<IProgress<MinecraftLaunchProgress>?, CancellationToken, Task<MinecraftLaunchResult>> Run { get; set; } =
            (_, _) => Task.FromResult(new MinecraftLaunchResult(true, 0, false, null));
        /// <summary>界面必须使用统一 Run 流程，不能自行准备后绕过服务执行。</summary>
        public Task<MinecraftLaunchPlan> PrepareAsync(string instancePath, MinecraftLaunchSettings settings,
            IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>捕获不可变输入并执行测试预设的生命周期。</summary>
        public Task<MinecraftLaunchResult> RunAsync(string instancePath, MinecraftLaunchSettings settings,
            IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((instancePath, settings));
            return Run(progress, cancellationToken);
        }
    }

    /// <summary>模拟每实例设置存储，能够制造迟到读取或磁盘失败。</summary>
    private sealed class FakeSettingsStore : IMinecraftLaunchSettingsStore
    {
        public List<(string Path, MinecraftLaunchSettings Settings)> Saved { get; } = [];
        public Func<string, CancellationToken, Task<MinecraftLaunchSettings>> Load { get; set; } =
            (_, _) => Task.FromResult(new MinecraftLaunchSettings());
        public Func<string, MinecraftLaunchSettings, CancellationToken, Task> Save { get; set; } = (_, _, _) => Task.CompletedTask;
        /// <summary>将读取时机和错误交给各测试控制。</summary>
        public Task<MinecraftLaunchSettings> LoadAsync(string instancePath, CancellationToken cancellationToken = default) => Load(instancePath, cancellationToken);
        /// <summary>记录成功保存的路径和输入，失败时不伪造已提交结果。</summary>
        public async Task SaveAsync(string instancePath, MinecraftLaunchSettings settings, CancellationToken cancellationToken = default)
        {
            await Save(instancePath, settings, cancellationToken);
            Saved.Add((instancePath, settings));
        }
    }

    /// <summary>启动测试不应触发扫描、下载或管理操作，任何误调用都会立即失败。</summary>
    private sealed class UnusedServices : IMinecraftInstanceLocator, IMinecraftInstanceManager, IMinecraftInstanceImporter,
        IMinecraftInstanceChecker, IMinecraftInstanceDownloader, IInstanceDialogs
    {
        /// <summary>禁止启动流程偷偷刷新扫描。</summary>
        public Task<IReadOnlyList<MinecraftInstance>> FindAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止启动流程删除实例。</summary>
        public Task DeleteAsync(string instancePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止启动流程改名实例。</summary>
        public Task<string> RenameAsync(string instancePath, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止启动流程导出实例。</summary>
        public Task ExportAsync(string instancePath, string destinationZip, IProgress<InstanceExportProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止启动流程检查新的导入包。</summary>
        public Task<InstanceImportPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止启动流程导入新实例。</summary>
        public Task<InstanceImportResult> ImportAsync(string archivePath, string instanceName,
            IProgress<InstanceImportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>完整启动检查属于启动服务，界面不应另外发起检查。</summary>
        public Task<InstanceCheckReport> CheckAsync(string instancePath, InstanceCheckMode mode = InstanceCheckMode.Full,
            IProgress<InstanceCheckProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>启动按钮不自动下载或覆盖实例文件。</summary>
        public Task<InstanceDownloadReport> DownloadMissingAsync(string instancePath,
            IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>不允许启动测试显示删除确认。</summary>
        public bool ConfirmDelete(MinecraftInstance instance) => throw new NotSupportedException();
        /// <summary>不允许启动测试弹出导出选择。</summary>
        public string? ChooseExportPath(MinecraftInstance instance) => throw new NotSupportedException();
        /// <summary>不允许启动测试弹出导入选择。</summary>
        public string? ChooseImportPath() => throw new NotSupportedException();
        /// <summary>不允许启动测试确认新导入。</summary>
        public string? ConfirmImport(InstanceImportPreview preview) => throw new NotSupportedException();
    }
}
