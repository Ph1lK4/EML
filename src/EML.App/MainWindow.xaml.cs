// ════════════════════════════════════════════════════════════════════
//  文件：MainWindow.xaml.cs
//  位置：src\EML.App
//  作用：主窗口的「接线」—— 把 MainViewModel 挂到窗口上，并处理
//        纯界面行为（日志框自动滚到底）。
//
//  这里故意不写业务：
//    窗口不 new 具体实现之外的任何东西，也不读写文件；
//    所有逻辑都在 ViewModel 和 Core/Infrastructure 里。
//  这样以后换界面（WinUI / Avalonia）时，这一层可以整块丢掉。
// ════════════════════════════════════════════════════════════════════

using System.Windows;
using EML.App.ViewModels;
using EML.Infrastructure.Java;
using EML.Infrastructure.Launch;

namespace EML.App;

/// <summary>
/// 主窗口：数据都来自 <see cref="MainViewModel"/>，窗口本身不碰业务。
///
/// 依赖装配放在这里，而不是 ViewModel 里：
///   ViewModel 只依赖 Core 的接口
///   （IJavaLocator / ILaunchPlanner / IGameLauncher / INativesExtractor），
///   具体用哪个实现，由最外层的窗口决定。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 一个窗口只挂一个 DataContext：
        //   Java.*   → Java 环境检测块
        //   Launch.* → 启动块（生成命令 / 启动 / 停止 / 日志）
        DataContext = new MainViewModel(
            new WindowsJavaLocator(),
            new VanillaLaunchPlanner(),
            new GameLauncher(),
            new NativesExtractor(),
            new CrashReportFinder());
        // 日志实时追加时，让滚动条跟着走到底部，
        // 否则用户只能看到第一屏，以为游戏卡住了。
        LogBox.TextChanged += (_, _) => LogBox.ScrollToEnd();
    }
}
