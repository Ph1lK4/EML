using System.IO;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using EML.App.ViewModels;
using EML.App.Services;
using EML.Infrastructure.Java;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Downloads;

namespace EML.App;

/// <summary>组装页面依赖，并在窗口首次显示时自动检测 Java 和实例。</summary>
public partial class MainWindow : Window
{
    private readonly HttpFileDownloader _fileDownloader = new();
    private bool _waitingForCleanup;
    private bool _closingAfterCleanup;
    /// <summary>创建页面依赖、设置绑定对象，并注册首次显示事件。</summary>
    public MainWindow()
    {
        InitializeComponent();
        var launcherRoot = GetLauncherRoot();
        var locator = new LauncherRootMinecraftInstanceLocator(launcherRoot);
        var checker = new MinecraftInstanceChecker(launcherRoot);
        DataContext = new MainViewModel(
            new JavaSettingsViewModel(new WindowsJavaLocator()),
            new InstancesViewModel(locator, new MinecraftInstanceManager(launcherRoot),
                new MinecraftInstanceImporter(launcherRoot), new InstanceDialogs(this), locator.InstancesRoot,
                checker, new MinecraftInstanceDownloader(launcherRoot, checker, _fileDownloader)));
        ContentRendered += OnFirstContentRendered;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    /// <summary>关闭前取消并等待正在执行的文件操作，确保下载和导入有机会清理临时文件。</summary>
    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closingAfterCleanup) return;
        if (_waitingForCleanup) { e.Cancel = true; return; }
        if (DataContext is not MainViewModel viewModel) return;
        IAsyncRelayCommand[] operations =
        [
            viewModel.Instances.QuickCheckCommand, viewModel.Instances.FullCheckCommand,
            viewModel.Instances.DownloadMissingCommand, viewModel.Instances.ImportCommand,
            viewModel.Instances.ExportCommand, viewModel.Instances.RenameCommand, viewModel.Instances.DeleteCommand
        ];
        var pending = operations.Where(command => command.IsRunning)
            .Select(command => command.ExecutionTask).OfType<Task>().ToArray();
        if (pending.Length == 0) return;
        e.Cancel = true;
        _waitingForCleanup = true;
        IsEnabled = false;
        viewModel.Instances.StatusText = "正在结束文件操作并清理临时文件，随后关闭窗口……";
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
    private void OnWindowClosed(object? sender, EventArgs e) => _fileDownloader.Dispose();

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
        await Task.WhenAll(javaTask, instancesTask);
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
