using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
using EML.Core.Java;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Launching;

namespace EML.App.ViewModels;

/// <summary>每实例启动偏好、准备取消和游戏生命周期；运行期间与实例管理共用忙碌状态。</summary>
public partial class InstancesViewModel
{
    private const int MaximumLogLines = 1000;
    private readonly IMinecraftGameLauncher? _launcher;
    private readonly IMinecraftLaunchSettingsStore? _launchSettingsStore;
    private readonly IJavaRuntimeService? _javaRuntimes;
    private readonly IJavaDownloadDialogs? _javaDownloadDialogs;
    private CancellationTokenSource? _settingsLoadCancellation;
    private CancellationTokenSource? _launchCancellation;
    private int _settingsRevision;
    private int _launchRevision;
    private string? _loadedSettingsPath;
    private readonly Dictionary<string, LaunchSettingsDraft> _launchSettingsDrafts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>只在当前窗口保留每实例的编辑文本；不存账号令牌，也不将未完成输入强制解析为设置。</summary>
    private sealed record LaunchSettingsDraft(string PlayerName, bool UseMicrosoftAccount, string JavaPath,
        string MinMemoryMb, string MaxMemoryMb, string JvmArguments);

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

    /// <summary>切换目标时保留各实例草稿，只有首次选择或显式重读才读取磁盘；旧异步结果不能覆盖新实例。</summary>
    private void BeginLoadLaunchSettings(MinecraftInstance? instance, bool notifySuccess = false, bool discardDraft = false)
    {
        if (IsLaunchSettingsLoaded && _loadedSettingsPath is { } previousPath)
            _launchSettingsDrafts[previousPath] = CaptureLaunchSettingsDraft();
        if (discardDraft && instance is not null) _launchSettingsDrafts.Remove(instance.InstancePath);
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

        if (_launchSettingsDrafts.TryGetValue(instance.InstancePath, out var draft))
        {
            PlayerName = draft.PlayerName;
            UseMicrosoftAccount = draft.UseMicrosoftAccount;
            SelectedJavaPath = draft.JavaPath;
            MinMemoryMbText = draft.MinMemoryMb;
            MaxMemoryMbText = draft.MaxMemoryMb;
            AdditionalJvmArguments = draft.JvmArguments;
            _loadedSettingsPath = instance.InstancePath;
            IsLaunchSettingsLoaded = true;
            LaunchSettingsStatus = "已恢复本次编辑；两处启动模式同步，以最后选择为准。重启 EML 后保留偏好需点击“保存设置”。";
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

    /// <summary>在发送界面状态通知前冻结原始输入，保留尚未填完的文本和当前身份。</summary>
    private LaunchSettingsDraft CaptureLaunchSettingsDraft() => new(PlayerName, UseMicrosoftAccount, SelectedJavaPath,
        MinMemoryMbText, MaxMemoryMbText, AdditionalJvmArguments);

    /// <summary>实例删除或消失后同时作废读取与草稿，防止同名新实例继承旧身份。</summary>
    private void ForgetLaunchSettingsDraft(string instancePath)
    {
        if (string.Equals(SelectedInstance?.InstancePath, instancePath, StringComparison.OrdinalIgnoreCase))
            BeginLoadLaunchSettings(null);
        _launchSettingsDrafts.Remove(instancePath);
    }

    /// <summary>目录改名成功后让草稿跟随实例迁移，不覆盖其他实例的输入。</summary>
    private void MoveLaunchSettingsDraft(string oldPath, string newPath)
    {
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;
        var draft = IsLaunchSettingsLoaded && string.Equals(_loadedSettingsPath, oldPath, StringComparison.OrdinalIgnoreCase)
            ? CaptureLaunchSettingsDraft() : _launchSettingsDrafts.GetValueOrDefault(oldPath);
        ForgetLaunchSettingsDraft(oldPath);
        _launchSettingsDrafts.Remove(newPath);
        if (draft is not null) _launchSettingsDrafts[newPath] = draft;
    }

    /// <summary>成功扫描后丢弃已不存在的实例草稿；扫描失败不影响当前编辑。</summary>
    private void PruneLaunchSettingsDrafts(IReadOnlyList<MinecraftInstance> instances)
    {
        var paths = instances.Select(instance => instance.InstancePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (SelectedInstance is { } selected && !paths.Contains(selected.InstancePath))
            ForgetLaunchSettingsDraft(selected.InstancePath);
        foreach (var path in _launchSettingsDrafts.Keys.Where(path => !paths.Contains(path)).ToArray())
            _launchSettingsDrafts.Remove(path);
    }

    /// <summary>验证当前输入或已冻结的草稿，输入错误必须报错，不能沿用上一次有效数值。</summary>
    private MinecraftLaunchSettings ReadLaunchSettings(LaunchSettingsDraft? input = null)
    {
        input ??= CaptureLaunchSettingsDraft();
        if (!int.TryParse(input.MinMemoryMb, NumberStyles.None, CultureInfo.InvariantCulture, out var minimum) ||
            !int.TryParse(input.MaxMemoryMb, NumberStyles.None, CultureInfo.InvariantCulture, out var maximum))
            throw new ArgumentException("初始内存和最大内存必须填写以 MB 为单位的整数。");
        var settings = new MinecraftLaunchSettings
        {
            PlayerName = input.PlayerName.Trim(),
            UseMicrosoftAccount = input.UseMicrosoftAccount,
            JavaPath = string.IsNullOrWhiteSpace(input.JavaPath) ? null : input.JavaPath.Trim(),
            MinMemoryMb = minimum,
            MaxMemoryMb = maximum,
            AdditionalJvmArguments = input.JvmArguments
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
        BeginLoadLaunchSettings(SelectedInstance, notifySuccess: true, discardDraft: true);
        return LaunchSettingsLoadTask;
    }

    /// <summary>只保存启动偏好，不触碰导入清单、文件来源或完整性基线。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveLaunchSettings))]
    private async Task SaveLaunchSettingsAsync()
    {
        if (!CanSaveLaunchSettings()) return;
        var instancePath = SelectedInstance!.InstancePath;
        var revision = _settingsRevision;
        try
        {
            var settings = ReadLaunchSettings();
            IsBusy = true;
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
        // 先冻结点击启动时的输入，再触发禁用控件与打开日志等界面通知。
        var input = CaptureLaunchSettingsDraft();
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
            var settings = ReadLaunchSettings(input);
            using var progress = new BoundedLaunchProgress(p => ApplyLaunchProgress(revision, p));
            MinecraftLaunchResult result;
            try { result = await RunWithJavaRecoveryAsync(instance, settings, progress, cancellation.Token); }
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

    /// <summary>仅在启动前明确缺少兼容 Java 时询问；安装后使用原身份和参数重试一次，其他错误不触发下载。</summary>
    private async Task<MinecraftLaunchResult> RunWithJavaRecoveryAsync(MinecraftInstance instance,
        MinecraftLaunchSettings settings, BoundedLaunchProgress progress, CancellationToken token)
    {
        JavaRuntimeRequiredException requirement;
        try { return await _launcher!.RunAsync(instance.InstancePath, settings, progress, token); }
        catch (JavaRuntimeRequiredException ex) when (_javaRuntimes is not null && _javaDownloadDialogs is not null)
        {
            progress.FlushPending();
            token.ThrowIfCancellationRequested();
            if (IsGameRunning) throw;
            requirement = ex;
        }

        var versions = requirement.CompatibleMajorVersions.Where(version => version is 8 or 11 or 17 or 21 or 25)
            .Distinct().OrderDescending().ToArray();
        var architecture = requirement.Architecture.ToLowerInvariant() switch
        {
            "x64" => "x64", "x86" => "x86", "arm64" or "aarch64" => "aarch64", _ => null
        };
        if (versions.Length == 0 || architecture is null)
            throw new InvalidOperationException(requirement.Message + "\n此版本或架构暂不支持自动下载，请手动安装符合实例要求的 Java。", requirement);
        if (!_javaDownloadDialogs!.ConfirmDownload(instance.Name, requirement, _javaRuntimes!.InstallationRoot))
            return new(false, null, true, null);
        token.ThrowIfCancellationRequested();
        progress.Report(new(MinecraftLaunchStage.Preparing, "已确认下载，正在查询实例所需的官方 Java……"));
        progress.FlushPending();

        JavaRuntimePackage? package = null;
        foreach (var version in versions)
        {
            var packages = await _javaRuntimes.GetPackagesAsync(version, architecture, token);
            token.ThrowIfCancellationRequested();
            package = packages.FirstOrDefault(candidate => candidate.MajorVersion == version &&
                candidate.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase));
            if (package is not null) break;
        }
        if (package is null)
            throw new InvalidOperationException($"官方暂无可下载的 Java {string.Join('/', versions)}（{architecture}）Windows 包，请手动安装兼容 Java。");

        JavaInstallation installation;
        using (var installProgress = new JavaInstallLaunchProgress(progress))
            installation = await _javaRuntimes.InstallAsync(package, installProgress, token);
        token.ThrowIfCancellationRequested();
        Java?.RegisterInstallation(installation);
        if (string.Equals(SelectedInstance?.InstancePath, instance.InstancePath, StringComparison.OrdinalIgnoreCase))
        {
            SelectedJavaPath = installation.ExecutablePath;
            LaunchSettingsStatus = "已为本次启动选择新安装的 Java；需要记住此实例偏好时请点击“保存设置”。";
        }
        _notifications.Success("Java 安装完成", "已验证 Java 环境，将继续启动原实例。玩家身份和其他启动参数保持不变。",
            installation.ExecutablePath);
        progress.Report(new(MinecraftLaunchStage.Preparing, "Java 已安装，正在使用原身份和设置重新准备启动……"));
        progress.FlushPending();
        // 重试不再进入恢复分支，防止环境仍不兼容时循环下载或反复弹窗。
        return await _launcher!.RunAsync(instance.InstancePath, settings with { JavaPath = installation.ExecutablePath }, progress, token);
    }

    /// <summary>将后台安装阶段送入有界日志，结束后丢弃迟到进度，避免覆盖后续游戏状态。</summary>
    private sealed class JavaInstallLaunchProgress(BoundedLaunchProgress target) : IProgress<JavaRuntimeInstallProgress>, IDisposable
    {
        private int _active = 1;
        /// <summary>仅显示安装阶段和百分比，不输出下载凭证或完整请求地址。</summary>
        public void Report(JavaRuntimeInstallProgress value)
        {
            if (Volatile.Read(ref _active) == 0) return;
            var suffix = value.Percent is { } percent ? $"（{percent:F1}%）" : "";
            target.Report(new(MinecraftLaunchStage.Preparing, "Java 安装：" + value.Stage + suffix));
        }
        /// <summary>安装结束后停止接收本次进度。</summary>
        public void Dispose() => Interlocked.Exchange(ref _active, 0);
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
