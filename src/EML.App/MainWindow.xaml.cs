using System.IO;
using System.Windows;
using EML.App.ViewModels;
using EML.App.Services;
using EML.Infrastructure.Java;
using EML.Infrastructure.Minecraft;

namespace EML.App;

public partial class MainWindow : Window
{
    /// <summary>初始化窗口，组装 Java 与实例检测服务并设置数据来源；注册首次显示事件，稍后自动扫描。</summary>
    public MainWindow()
    {
        InitializeComponent();
        var launcherRoot = GetLauncherRoot();
        var locator = new LauncherRootMinecraftInstanceLocator(launcherRoot);
        DataContext = new MainViewModel(
            new JavaSettingsViewModel(new WindowsJavaLocator()),
            new InstancesViewModel(locator, new MinecraftInstanceManager(launcherRoot),
                new InstanceDialogs(this), locator.InstancesRoot));
        ContentRendered += OnFirstContentRendered;
    }

    /// <summary>窗口内容首次显示后并行触发两项扫描；先取消订阅确保只运行一次。async void 仅用于这个事件处理器。</summary>
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
