// ════════════════════════════════════════════════════════════════════
//  文件：MainViewModel.cs
//  位置：src\EML.App\ViewModels
//  作用：主窗口唯一的 DataContext —— 把各块功能的 ViewModel 组合起来。
//
//  为什么要有它？
//    WPF 的窗口只应该有一个 DataContext。
//    如果每个面板各挂一个，窗口里就会出现两套数据来源，
//    接手的人很难判断「这个值到底从哪来」。
//    所以这里统一组合，窗口只挂它，XAML 里用 Java.* / Launch.* 取子块。
// ════════════════════════════════════════════════════════════════════

using EML.Core.Java;
using EML.Core.Launch;

namespace EML.App.ViewModels;

/// <summary>
/// 主窗口的 ViewModel：组合各功能块，本身不写业务逻辑。
///
/// 现在的分区：
///   Java   —— Java 环境检测（原有功能，阿淦写的 IJavaLocator）
///   Launch —— 生成 1.7.10 启动命令（这一步新增）
///
/// 以后加「实例」「下载」「账号」时，在这里各加一个子 VM 即可，
/// 窗口和现有绑定都不用动。
/// </summary>
public sealed class MainViewModel
{
    /// <summary>Java 环境检测块。</summary>
    public JavaSettingsViewModel Java { get; }

    /// <summary>启动命令块。</summary>
    public LaunchViewModel Launch { get; }

    /// <summary>
    /// 构造：把 Core 的接口交给各子块，子块自己创建。
    /// 这样窗口只依赖 MainViewModel，不直接 new 具体实现。
    /// </summary>
    /// <param name="javaLocator">Java 检测实现（Infrastructure 的 WindowsJavaLocator）。</param>
    /// <param name="launchPlanner">启动命令实现（Infrastructure 的 VanillaLaunchPlanner）。</param>
    public MainViewModel(IJavaLocator javaLocator, ILaunchPlanner launchPlanner)
    {
        Java = new JavaSettingsViewModel(javaLocator);
        Launch = new LaunchViewModel(launchPlanner);
    }
}
