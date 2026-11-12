using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Launching;

namespace EML.App.ViewModels;

/// <summary>每实例启动偏好、准备取消和游戏生命周期；运行期间与实例管理共用忙碌状态。</summary>
public partial class InstancesViewModel
{
    private const int MaximumLogLines = 1000;
    private readonly IMinecraftGameLauncher? _launcher;
    private readonly IMinecraftLaunchSettingsStore? _launchSettingsStore;
    private CancellationTokenSource? _settingsLoadCancellation;
    private CancellationTokenSource? _launchCancellation;
    private int _settingsRevision;
    private int _launchRevision;
    private string? _loadedSettingsPath;

    /// <summary>复用 Java 页面已检测的结果，空路径仍由启动服务自动匹配版本。</summary>
    public JavaSettingsViewModel? Java { get; }
    /// <summary>保留当前载入任务，便于调用方等待设置就绪。</summary>
    public Task LaunchSettingsLoadTask { get; private set; } = Task.CompletedTask;
    /// <summary>界面只保留最近一千行，完整输出由启动服务写入本次日志文件。</summary>
    public ObservableCollection<string> LaunchLogLines { get; } = [];

    [ObservableProperty] private string playerName = "Steve";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditOfflinePlayerName), nameof(SelectedLaunchMode))]
    private bool useMicrosoftAccount;
    /// <summary>启动模式显示文本；设置文件仍使用旧的布尔字段，保持已有实例兼容。</summary>
    public IReadOnlyList<string> LaunchModes { get; } = ["离线模式", "正版模式"];
    /// <summary>将界面选择映射到现有正版开关，避免改变旧设置文件格式。</summary>
    public string SelectedLaunchMode
    {
        get => UseMicrosoftAccount ? "正版模式" : "离线模式";
        set
        {
            // 绑定暂时清空选择时保留原模式，避免误切换到另一个 UUID。
            if (value is "正版模式" or "离线模式") UseMicrosoftAccount = value == "正版模式";
        }
    }
    /// <summary>只有当前实例设置已读取且选择离线模式时，才允许编辑离线玩家名。</summary>
    public bool CanEditOfflinePlayerName => CanEditLaunchSettings && !UseMicrosoftAccount;
    [ObservableProperty] private string selectedJavaPath = "";
    [ObservableProperty] private string minMemoryMbText = "1024";
    [ObservableProperty] private string maxMemoryMbText = "8192";
    [ObservableProperty] private string additionalJvmArguments = "";
    [ObservableProperty] private string launchSettingsStatus = "请选择实例后设置启动参数。";
    [ObservableProperty] private string launchStatus = "尚未启动游戏。";
    [ObservableProperty] private string launchLogPath = "";
    [ObservableProperty] private int? lastExitCode;
    [ObservableProperty] private string runningInstanceName = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditLaunchSettings), nameof(CanEditOfflinePlayerName))]
    [NotifyCanExecuteChangedFor(nameof(LaunchGameCommand), nameof(SaveLaunchSettingsCommand))]
    private bool isLaunchSettingsLoaded;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadLaunchSettingsCommand))]
    private bool isLaunchSettingsLoading;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelLaunchPreparationCommand))]
    private bool isPreparingLaunch;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopGameCommand))]
    private bool isGameRunning;

    /// <summary>设置未读取或正在执行实例操作时禁止编辑，避免迟到的磁盘结果覆盖输入。</summary>
    public bool CanEditLaunchSettings => !IsBusy && IsLaunchSettingsLoaded && SelectedInstance is not null;

    /// <summary>切换目标立即取消旧读取并恢复默认显示，旧任务无论何时返回都不能覆盖新实例。</summary>
    private void BeginLoadLaunchSettings(MinecraftInstance? instance, bool notifySuccess = false)
    {
        _settingsLoadCancellation?.Cancel();
        _settingsLoadCancellation = null;
        var revision = ++_settingsRevision;
        _loadedSettingsPath = null;
        IsLaunchSettingsLoaded = false;
        IsLaunchSettingsLoading = false;
        ApplyLaunchSettings(new MinecraftLaunchSettings());
        if (instance is null || _launchSettingsStore is null)
        {
            LaunchSettingsStatus = instance is null ? "请选择实例后设置启动参数。" : "此页面未配置启动设置服务。";
            LaunchSettingsLoadTask = Task.CompletedTask;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _settingsLoadCancellation = cancellation;
        IsLaunchSettingsLoading = true;
        LaunchSettingsStatus = "正在读取此实例的启动设置……";
        LaunchSettingsLoadTask = LoadLaunchSettingsAsync(instance.InstancePath, revision, cancellation, notifySuccess);
    }

    /// <summary>读取并校验持久化设置；失败时保留错误，不使用默认值偷偷覆盖损坏配置。</summary>
    private async Task LoadLaunchSettingsAsync(string instancePath, int revision, CancellationTokenSource cancellation, bool notifySuccess)
    {
        try
        {
            var settings = await _launchSettingsStore!.LoadAsync(instancePath, cancellation.Token);
            if (revision != _settingsRevision || cancellation.IsCancellationRequested) return;
            MinecraftLaunchValidation.Validate(settings);
            ApplyLaunchSettings(settings);
            _loadedSettingsPath = instancePath;
            IsLaunchSettingsLoaded = true;
            LaunchSettingsStatus = "设置已加载；修改后点击“保存设置”，或直接使用当前输入启动。";
            if (notifySuccess) _notifications.Success("启动设置读取完成", LaunchSettingsStatus, instancePath);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (revision == _settingsRevision)
            {
                LaunchSettingsStatus = "启动设置读取失败：" + ex.Message + "。修复后可点击“重新读取”。";
                _notifications.Failure("启动设置读取失败", ex, instancePath);
            }
        }
        finally
        {
            if (revision == _settingsRevision)
            {
                IsLaunchSettingsLoading = false;
                _settingsLoadCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    /// <summary>将当前实例的已验证设置显示到可编辑字段。</summary>
    private void ApplyLaunchSettings(MinecraftLaunchSettings settings)
    {
        PlayerName = settings.PlayerName;
        UseMicrosoftAccount = settings.UseMicrosoftAccount;
        SelectedJavaPath = settings.JavaPath ?? "";
        MinMemoryMbText = settings.MinMemoryMb.ToString(CultureInfo.InvariantCulture);
        MaxMemoryMbText = settings.MaxMemoryMb.ToString(CultureInfo.InvariantCulture);
        AdditionalJvmArguments = settings.AdditionalJvmArguments;
    }

    /// <summary>从文本输入创建独立快照，输入错误必须报错，不能沿用上一次有效数值。</summary>
    private MinecraftLaunchSettings ReadLaunchSettings()
    {
        if (!int.TryParse(MinMemoryMbText, NumberStyles.None, CultureInfo.InvariantCulture, out var minimum) ||
            !int.TryParse(MaxMemoryMbText, NumberStyles.None, CultureInfo.InvariantCulture, out var maximum))
            throw new ArgumentException("初始内存和最大内存必须填写以 MB 为单位的整数。");
        var settings = new MinecraftLaunchSettings
        {
            PlayerName = PlayerName.Trim(),
            UseMicrosoftAccount = UseMicrosoftAccount,
            JavaPath = string.IsNullOrWhiteSpace(SelectedJavaPath) ? null : SelectedJavaPath.Trim(),
            MinMemoryMb = minimum,
            MaxMemoryMb = maximum,
            AdditionalJvmArguments = AdditionalJvmArguments
        };
        MinecraftLaunchValidation.Validate(settings);
        return settings;
    }

    /// <summary>只有对应实例的设置已载入且当前空闲时，才允许保存或启动。</summary>
    private bool HasCurrentLaunchSettings() => CanEditLaunchSettings && string.Equals(
        SelectedInstance?.InstancePath, _loadedSettingsPath, StringComparison.OrdinalIgnoreCase);
    /// <summary>没有启动实现的旧调用方仍可使用实例管理，但不显示可执行的启动命令。</summary>
    private bool CanLaunchGame() => _launcher is not null && HasCurrentLaunchSettings();
    /// <summary>保存必须有持久化实现和已载入的对应实例，避免读写互相覆盖。</summary>
    private bool CanSaveLaunchSettings() => _launchSettingsStore is not null && HasCurrentLaunchSettings();
    /// <summary>读取失败后可显式重试，忙碌时禁止重新加载。</summary>
    private bool CanReloadLaunchSettings() => !IsBusy && !IsLaunchSettingsLoading &&
        _launchSettingsStore is not null && SelectedInstance is not null;

    /// <summary>显式重读选中实例的设置，并等待这一轮读取完成。</summary>
    [RelayCommand(CanExecute = nameof(CanReloadLaunchSettings))]
    private Task ReloadLaunchSettingsAsync()
    {
        if (!CanReloadLaunchSettings()) return Task.CompletedTask;
        BeginLoadLaunchSettings(SelectedInstance, notifySuccess: true);
        return LaunchSettingsLoadTask;
    }

    /// <summary>只保存启动偏好，不触碰导入清单、文件来源或完整性基线。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveLaunchSettings))]
    private async Task SaveLaunchSettingsAsync()
    {
        if (!CanSaveLaunchSettings()) return;
        var instancePath = SelectedInstance!.InstancePath;
        var revision = _settingsRevision;
        IsBusy = true;
        try
        {
            var settings = ReadLaunchSettings();
            await _launchSettingsStore!.SaveAsync(instancePath, settings);
            if (revision == _settingsRevision) LaunchSettingsStatus = "已保存此实例的启动设置。";
            _notifications.Success("启动设置已保存", "已保存此实例的玩家、内存、Java 和 JVM 设置。", instancePath);
        }
        catch (Exception ex)
        {
            if (revision == _settingsRevision) LaunchSettingsStatus = "保存失败：" + ex.Message;
            _notifications.Failure("启动设置保存失败", ex, instancePath);
        }
        finally { IsBusy = false; }
    }

    /// <summary>快速准备并等待游戏退出；整个期间禁止其他实例写入与重复启动。</summary>
    [RelayCommand(CanExecute = nameof(CanLaunchGame), IncludeCancelCommand = true)]
    private async Task LaunchGameAsync(CancellationToken operationToken)
    {
        if (!CanLaunchGame()) return;
        var instance = SelectedInstance!;
        IsBusy = true;
        IsPreparingLaunch = true;
        IsGameRunning = false;
        RunningInstanceName = instance.Name;
        LastExitCode = null;
        LaunchLogPath = "";
        LaunchLogLines.Clear();
        SelectedDetailTab = 4;
        var revision = ++_launchRevision;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        _launchCancellation = cancellation;
        CancelLaunchPreparationCommand.NotifyCanExecuteChanged();
        LaunchStatus = "正在准备启动配置与 Java……";
        StatusText = LaunchStatus;
        try
        {
            var settings = ReadLaunchSettings();
            using var progress = new BoundedLaunchProgress(p => ApplyLaunchProgress(revision, p));
            MinecraftLaunchResult result;
            try { result = await _launcher!.RunAsync(instance.InstancePath, settings, progress, cancellation.Token); }
            finally { progress.Complete(); }
            LastExitCode = result.ExitCode;
            LaunchLogPath = result.LogPath ?? LaunchLogPath;
            LaunchStatus = result.IsCanceled
                ? result.Started ? "游戏进程已停止。" : "已取消启动准备。"
                : result.Started ? $"游戏进程已退出，退出码：{result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "未知"}。"
                : "未创建游戏进程，请查看日志中的原因。";
            AppendLaunchLog(LaunchStatus);
            StatusText = LaunchStatus;
            if (result.IsCanceled) _notifications.Cancelled(result.Started ? "游戏已停止" : "启动已取消", LaunchStatus, instance.InstancePath, LaunchLogPath);
            else if (result.Started && result.ExitCode == 0)
                _notifications.Success("游戏已正常退出", LaunchStatus, instance.InstancePath, LaunchLogPath);
            else _notifications.Failure(result.Started ? "游戏异常退出" : "游戏启动失败",
                LaunchStatus + "\n请查看本次运行日志中的错误信息。", instance.InstancePath, LaunchLogPath);
        }
        catch (OperationCanceledException)
        {
            LaunchStatus = IsGameRunning ? "游戏停止操作已结束。" : "已取消启动准备。";
            AppendLaunchLog(LaunchStatus);
            StatusText = LaunchStatus;
            _notifications.Cancelled("启动或运行已取消", LaunchStatus, instance.InstancePath, LaunchLogPath);
        }
        catch (Exception ex)
        {
            LaunchStatus = "启动或运行失败：" + ex.Message;
            AppendLaunchLog(LaunchStatus);
            StatusText = LaunchStatus;
            _notifications.Failure("启动或运行失败", ex, instance.InstancePath, LaunchLogPath);
        }
        finally
        {
            ++_launchRevision;
            _launchCancellation = null;
            IsPreparingLaunch = false;
            IsGameRunning = false;
            IsBusy = false;
            CancelLaunchPreparationCommand.NotifyCanExecuteChanged();
            StopGameCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>只接收当前进程的进度，退出后的迟到事件不能重新将界面标记为运行中。</summary>
    private void ApplyLaunchProgress(int revision, MinecraftLaunchProgress progress)
    {
        if (revision != _launchRevision || _launchCancellation is null) return;
        if (progress.LogPath is { } path) LaunchLogPath = path;
        if (progress.Stage == MinecraftLaunchStage.Running)
        {
            var firstRunning = !IsGameRunning;
            IsPreparingLaunch = false;
            IsGameRunning = true;
            LaunchStatus = "游戏进程已启动，正在接收输出；请在游戏窗口确认加载结果。";
            StatusText = LaunchStatus;
            if (firstRunning) _notifications.Success("游戏进程已启动", LaunchStatus, SelectedInstance?.InstancePath, LaunchLogPath);
        }
        else if (progress.Stage == MinecraftLaunchStage.Preparing)
        {
            LaunchStatus = progress.Message;
            StatusText = LaunchStatus;
        }
        AppendLaunchLog((progress.StandardError ? "[stderr] " : "") + progress.Message);
    }

    /// <summary>限制界面日志行数及单行长度，完整输出保留在磁盘日志中。</summary>
    private void AppendLaunchLog(string message)
    {
        foreach (var line in message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            LaunchLogLines.Add(line.Length > 16384 ? line[..16384] + "…（完整内容见日志文件）" : line);
            while (LaunchLogLines.Count > MaximumLogLines) LaunchLogLines.RemoveAt(0);
        }
    }

    /// <summary>准备阶段取消仍有效时启用取消按钮。</summary>
    private bool CanCancelLaunchPreparation() => IsPreparingLaunch && _launchCancellation is { IsCancellationRequested: false };
    /// <summary>只有当前 EML 启动的游戏仍运行且尚未请求停止时启用停止按钮。</summary>
    private bool CanStopGame() => IsGameRunning && _launchCancellation is { IsCancellationRequested: false };

    /// <summary>取消启动前检查或参数准备，不启动新的游戏进程。</summary>
    [RelayCommand(CanExecute = nameof(CanCancelLaunchPreparation))]
    private void CancelLaunchPreparation() => CancelLaunchOperation();

    /// <summary>显式请求停止当前游戏，由启动服务等待它及子进程退出。</summary>
    [RelayCommand(CanExecute = nameof(CanStopGame))]
    private void StopGame() => CancelLaunchOperation();

    /// <summary>两个取消入口共用同一个令牌，忙碌状态保持到启动服务确认结束。</summary>
    private void CancelLaunchOperation()
    {
        _launchCancellation?.Cancel();
        LaunchStatus = IsGameRunning ? "正在停止游戏进程，请稍候……" : "正在取消启动准备……";
        CancelLaunchPreparationCommand.NotifyCanExecuteChanged();
        StopGameCommand.NotifyCanExecuteChanged();
    }
}
