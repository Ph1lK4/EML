using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using EML.App.ViewModels;
using EML.App.Services;
using EML.Core.Minecraft;
using EML.Infrastructure.Java;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Launching;
using EML.Infrastructure.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Mods.Online;
using EML.Infrastructure.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Catalog;
using EML.Infrastructure.Minecraft.Modrinth.Api;
using EML.Infrastructure.Minecraft.CurseForge.Api;
using EML.Infrastructure.Downloads;
using EML.Infrastructure.Minecraft.Accounts;

namespace EML.App;

/// <summary>组装页面依赖，并在窗口首次显示时自动检测 Java 和实例。</summary>
public partial class MainWindow : Window
{
    private readonly HttpFileDownloader _fileDownloader;
    private readonly DownloadTaskCenter _downloadCenter;
    private readonly MinecraftAccountService _accountService = new(MicrosoftLoginOptions.Load(AppContext.BaseDirectory));
    private readonly ModrinthApiClient _modrinth = new();
    private readonly CurseForgeApiClient _curseForge = new();
    private readonly ModIconCache _modIcons;
    private readonly GtnhAddonService _gtnhAddons;
    private readonly GtnhMirrorPackService _gtnhPacks;
    private readonly InstanceCatalog _instanceCatalog;
    private readonly WpfOperationNotifications _notifications;
    private bool _waitingForCleanup;
    private bool _closingAfterCleanup;
    /// <summary>创建页面依赖、设置绑定对象，并注册首次显示事件。</summary>
    public MainWindow()
    {
        InitializeComponent();
        _notifications = new WpfOperationNotifications(this);
        var launcherRoot = GetLauncherRoot();
        var bandwidth = new DownloadBandwidthLimiter();
        _fileDownloader = new HttpFileDownloader(new FileDownloadOptions
        {
            CacheDirectory = Path.Combine(launcherRoot, "cache", "downloads")
        }, bandwidthLimiter: bandwidth);
        _downloadCenter = new DownloadTaskCenter(_fileDownloader, bandwidth,
            Path.Combine(launcherRoot, "settings", "downloads.json"));
        var locator = new LauncherRootMinecraftInstanceLocator(launcherRoot);
        var checker = new MinecraftInstanceChecker(launcherRoot);
        var javaLocator = new WindowsJavaLocator();
        var java = new JavaSettingsViewModel(javaLocator, notifications: _notifications);
        var localMods = new LocalModManager(launcherRoot);
        var onlineCatalog = new OnlineModCatalog(_modrinth, _curseForge);
        _gtnhAddons = new GtnhAddonService(launcherRoot, _downloadCenter, localMods, onlineCatalog: onlineCatalog,
            modrinth: _modrinth, curseForge: _curseForge, contentRegistrar: new GtnhContentRegistrar(launcherRoot));
        _gtnhPacks = new GtnhMirrorPackService(launcherRoot, _downloadCenter);
        var onlineInstaller = new OnlineModInstaller(launcherRoot, onlineCatalog, _downloadCenter, localMods);
        _modIcons = new ModIconCache(launcherRoot, WpfModIconDecoder.Decode);
        var accounts = new MinecraftAccountViewModel(_accountService, uri =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }),
            notifications: _notifications);
        var instances = new InstancesViewModel(locator, new MinecraftInstanceManager(launcherRoot),
                new MinecraftInstanceImporter(launcherRoot), new InstanceDialogs(this), locator.InstancesRoot,
                checker, new MinecraftInstanceDownloader(launcherRoot, checker, _downloadCenter),
                new MinecraftGameLauncher(launcherRoot, checker, javaLocator, _accountService),
                new MinecraftLaunchSettingsStore(launcherRoot), java,
                new GtnhInstanceMigrationService(launcherRoot), new InstanceMigrationDialogs(this),
                localMods, new ModManagementDialogs(this), onlineCatalog, onlineInstaller, _modIcons,
                uri => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }),
                ChineseModCatalog.Default, _gtnhAddons, notifications: _notifications, accounts: accounts, gtnhPacks: _gtnhPacks);
        _instanceCatalog = new InstanceCatalog(_modrinth, _curseForge);
        var archives = new InstanceArchiveService(launcherRoot, _instanceCatalog, _downloadCenter);
        var downloads = new InstanceDownloadsViewModel(_instanceCatalog,
            (release, progress, token) => instances.DownloadInstanceAsync(archives, release.Provider, release.ProjectId, release.Id, progress, token),
            () => !instances.IsBusy, _modIcons,
            uri => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }),
            notifications: _notifications);
        var transferCenter = new DownloadCenterViewModel(_downloadCenter,
            new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher), notifications: _notifications,
            settingsPath: Path.Combine(launcherRoot, "settings", "downloads.json"));
        DataContext = new MainViewModel(java, instances, accounts, downloads, transferCenter);
        ContentRendered += OnFirstContentRendered;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    /// <summary>游戏仍在启动或运行时先明确确认；随后取消并等待活动任务清理完成再关闭。</summary>
    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closingAfterCleanup) return;
        if (_waitingForCleanup) { e.Cancel = true; return; }
        if (DataContext is not MainViewModel viewModel) return;
        if (viewModel.Instances.LaunchGameCommand.IsRunning && !DialogPrompt.Confirm(this, "退出 EML",
            viewModel.Instances.IsGameRunning
                ? "游戏仍在运行。是否停止游戏并退出 EML？\n建议先在游戏内正常退出，以保存进度。"
                : "游戏启动尚未结束。是否取消启动并退出 EML？\n如果游戏进程刚刚创建，也会停止本次进程。",
            "停止游戏并退出", DialogTone.Warning))
        {
            e.Cancel = true;
            return;
        }
        IAsyncRelayCommand[] operations =
        [
            viewModel.Instances.QuickCheckCommand, viewModel.Instances.FullCheckCommand,
            viewModel.Instances.DownloadMissingCommand, viewModel.Instances.ImportCommand,
            viewModel.Instances.ExportCommand, viewModel.Instances.RenameCommand, viewModel.Instances.DeleteCommand,
            viewModel.Instances.LaunchGameCommand, viewModel.Instances.SaveLaunchSettingsCommand,
            viewModel.Instances.MigratePlayerDataCommand, viewModel.Instances.RestoreMigrationCommand,
            viewModel.Instances.RefreshModsCommand, viewModel.Instances.AddLocalModsCommand,
            viewModel.Instances.AdoptModCommand, viewModel.Instances.EnableModCommand, viewModel.Instances.DisableModCommand,
            viewModel.Instances.RemoveModCommand, viewModel.Instances.RestoreModCommand,
            viewModel.Instances.RecoverModOperationsCommand
        ];
        if (viewModel.Accounts is { } accounts)
            operations = [.. operations, accounts.LoginCommand, accounts.LoadCommand, accounts.LogoutCommand];
        // 自动读取可能仍在排队，因此除了命令还要停止并等待页面自己的任务。
        var modListCleanup = viewModel.Instances.StopModListLoadingAsync();
        var onlineCleanup = viewModel.Instances.OnlineMods?.StopAsync() ?? Task.CompletedTask;
        var gtnhCleanup = viewModel.Gtnh?.StopAsync() ?? Task.CompletedTask;
        var downloadCleanup = viewModel.Downloads?.StopAsync() ?? Task.CompletedTask;
        var queueCleanup = _downloadCenter.StopAsync();
        var pending = operations.Where(command => command.IsRunning)
            .Select(command => command.ExecutionTask).OfType<Task>().Append(modListCleanup).Append(onlineCleanup).Append(gtnhCleanup).Append(downloadCleanup).Append(queueCleanup)
            .Where(task => !task.IsCompleted).ToArray();
        if (pending.Length == 0) return;
        e.Cancel = true;
        _waitingForCleanup = true;
        IsEnabled = false;
        viewModel.Instances.StatusText = "正在结束游戏或文件操作并清理临时文件，随后关闭窗口……";
        foreach (var command in operations) command.Cancel();
        try { await Task.WhenAll(pending); }
        catch (Exception ex)
        {
            // 命令通常自行报告错误；关闭期间的意外异常保留诊断，避免 async void 崩溃。
            System.Diagnostics.Debug.WriteLine("关闭前结束文件操作：" + ex);
        }
        finally
        {
            _closingAfterCleanup = true;
            // 任务可能在 Cancel 时同步结束；排队关闭，避免在当前 Closing 回调内再次调用 Close。
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    /// <summary>后台文件操作结束后释放窗口持有的下载器资源。</summary>
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _notifications.Dispose();
        if (DataContext is MainViewModel viewModel) viewModel.Dispose();
        _instanceCatalog.Dispose(); _gtnhPacks.Dispose(); _gtnhAddons.Dispose(); _fileDownloader.Dispose(); _accountService.Dispose(); _modrinth.Dispose(); _curseForge.Dispose(); _modIcons.Dispose();
    }

    /// <summary>鼠标右键先更新实例选择，菜单命令始终作用于点击的那一项。</summary>
    private void OnInstanceRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem row && DataContext is MainViewModel viewModel && viewModel.Instances.CanInteract)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }

    /// <summary>键盘菜单键同样核对当前行，忙碌时拒绝切换到另一个操作目标。</summary>
    private void OnInstanceMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem row || row.DataContext is not MinecraftInstance instance || DataContext is not MainViewModel viewModel) return;
        if (!viewModel.Instances.CanInteract && !ReferenceEquals(viewModel.Instances.SelectedInstance, instance))
        {
            e.Handled = true;
            return;
        }
        viewModel.Instances.SelectedInstance = instance;
        row.IsSelected = true;
    }

    /// <summary>更多按钮提供与右键相同的入口，方便触控和首次使用。</summary>
    private void OnOpenInstanceMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button || DataContext is not MainViewModel { Instances.SelectedInstance: not null }) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>点击遮罩收起面板；面板内的点击不会被当成关闭操作。</summary>
    private void OnWorkspaceBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender) && DataContext is MainViewModel viewModel)
            viewModel.CloseInstanceWorkspaceCommand.Execute(null);
    }

    /// <summary>打开后把焦点移入面板，关闭后返回实例列表，避免键盘操作到遮罩背后的实例。</summary>
    private void OnWorkspaceVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (WorkspaceOverlay.IsVisible) CloseWorkspaceButton.Focus();
            else if (DataContext is MainViewModel { IsInstancePage: true }) InstanceList.Focus();
        }));
    }

    /// <summary>Escape 提供键盘返回入口，收起面板不会取消下载或关闭游戏。</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainViewModel { IsWorkspaceVisible: true } viewModel)
        {
            viewModel.CloseInstanceWorkspaceCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>首次显示时并行检测 Java 和实例；已有检测任务则等待它完成。</summary>
    private async void OnFirstContentRendered(object? sender, EventArgs e)
    {
        // 先显示窗口，再自动检测；取消订阅确保切换标签、重新显示窗口不会重复触发。
        ContentRendered -= OnFirstContentRendered;
        if (DataContext is not MainViewModel viewModel) return;

        // 复用现有命令，保留状态提示、异常处理和执行期间禁用按钮的行为。
        // 若用户已手动触发检测，等待现有任务，避免重复扫描。
        var javaTask = viewModel.Java.DetectJavaCommand.IsRunning
            ? viewModel.Java.DetectJavaCommand.ExecutionTask ?? Task.CompletedTask
            : viewModel.Java.DetectJavaCommand.ExecuteAsync(null);
        var instancesTask = viewModel.Instances.RefreshCommand.IsRunning
            ? viewModel.Instances.RefreshCommand.ExecutionTask ?? Task.CompletedTask
            : viewModel.Instances.RefreshCommand.ExecuteAsync(null);
        var accountTask = viewModel.Accounts is { } accounts
            ? accounts.IsBusy ? accounts.LoadCommand.ExecutionTask ?? accounts.LoginCommand.ExecutionTask ?? accounts.LogoutCommand.ExecutionTask ?? Task.CompletedTask
                : accounts.LoadCommand.ExecuteAsync(null)
            : Task.CompletedTask;
        await Task.WhenAll(javaTask, instancesTask, accountTask);
    }

    /// <summary>Debug 开发时向上查找 EML.slnx 以读取项目测试包；Release 或找不到解决方案时使用 EXE 目录。</summary>
    private static string GetLauncherRoot()
    {
#if DEBUG
        // 开发时读取解决方案旁的测试包；发布版本始终读取 EXE 旁的 instances。
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "EML.slnx"))) return directory.FullName;
#endif
        return AppContext.BaseDirectory;
    }
}
