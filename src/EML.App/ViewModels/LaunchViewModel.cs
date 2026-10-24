// ════════════════════════════════════════════════════════════════════
//  文件：LaunchViewModel.cs
//  位置：src\EML.App\ViewModels
//  作用：启动页的 ViewModel —— 窗口上「启动 1.7.10」那一块的所有逻辑。
//
//  这一步只做一件事：
//    拿到「选中的 Java + 游戏目录 + 玩家名 + 内存」→ 调 ILaunchPlanner
//    → 把生成出来的命令行放到界面上。
//
//  它不负责：检测 Java（用 JavaSettingsViewModel）、启动进程（下一步）。
// ════════════════════════════════════════════════════════════════════

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.Core.Java;
using EML.Core.Launch;

namespace EML.App.ViewModels;

/// <summary>
/// 启动页 ViewModel。
///
/// 分区：
///   一、输入属性 —— 界面上能改的四个值；
///   二、输出属性 —— 生成出来的命令和状态文字；
///   三、动作     —— Generate 命令（按钮点下去执行的）。
/// </summary>
public partial class LaunchViewModel : ObservableObject
{
    /// <summary>拼命令用的接口。窗口只传接口进来，方便以后换 Forge/Prism 实现。</summary>
    private readonly ILaunchPlanner _launchPlanner;

    // ────────────────────────────────────────────────────────────────
    // 一、输入属性
    //     这些字段带 [ObservableProperty]，CommunityToolkit 会自动生成
    //     同名首字母大写的属性（gameDirectory → GameDirectory），
    //     XAML 里绑的就是生成出来的那个属性。
    // ────────────────────────────────────────────────────────────────

    /// <summary>游戏目录，默认取系统的 <c>%APPDATA%\.minecraft</c>。</summary>
    [ObservableProperty]
    private string gameDirectory = DefaultGameDirectory();

    /// <summary>玩家名，离线模式用；默认 Steve。</summary>
    [ObservableProperty]
    private string playerName = "Steve";

    /// <summary>最大内存（MB），默认 2048；会变成命令里的 <c>-Xmx2048M</c>。</summary>
    [ObservableProperty]
    private int maxMemoryMb = 2048;

    // ────────────────────────────────────────────────────────────────
    // 二、输出属性
    // ────────────────────────────────────────────────────────────────

    /// <summary>生成出来的完整命令；还没生成时是空串。</summary>
    [ObservableProperty]
    private string commandLine = string.Empty;

    /// <summary>状态栏文字：未生成 / 成功 / 失败原因。</summary>
    [ObservableProperty]
    private string statusText = "选一个 Java，然后点「生成启动命令」";

    /// <summary>
    /// 构造：只保存依赖，不做任何业务动作。
    /// </summary>
    /// <param name="launchPlanner">拼命令用的实现（窗口里传 VanillaLaunchPlanner）。</param>
    public LaunchViewModel(ILaunchPlanner launchPlanner)
    {
        _launchPlanner = launchPlanner;
    }

    // ────────────────────────────────────────────────────────────────
    // 三、动作
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 生成启动命令。
    ///
    /// 参数 <paramref name="java"/> 不是从属性里拿的，而是按钮的
    /// CommandParameter 从左边列表（JavaList）直接带过来的：
    /// 这样「选中的 Java」只有一份数据来源，不会出现两边不同步。
    ///
    /// [RelayCommand] 会生成一个 ICommand 属性，名字是方法名 + Command，
    /// 所以 XAML 里绑的是 GenerateCommand。
    /// </summary>
    [RelayCommand]
    private void Generate(JavaInstallation? java)
    {
        // 没选 Java 时不要静默失败，明确告诉用户先选。
        if (java is null)
        {
            StatusText = "请先在左边选一个 Java";
            return;
        }

        try
        {
            var request = new LaunchRequest(
                java.ExecutablePath,
                GameDirectory,
                "1.7.10",
                string.IsNullOrWhiteSpace(PlayerName) ? "Steve" : PlayerName.Trim(),
                MaxMemoryMb);

            // 这一步只拿 CommandLine 显示；真正启动时用计划里的 JavaPath/Arguments。
            CommandLine = _launchPlanner.Build(request).CommandLine;
            StatusText = "命令已生成（这一步只显示，不启动游戏）";
        }
        catch (Exception exception)
        {
            // 缺版本 JSON 之类的问题，把消息原样显示出来；
            // 消息本身是给人看的（例如「缺 versions\1.7.10\1.7.10.json」）。
            StatusText = "生成失败：" + exception.Message;
        }
    }

    /// <summary>
    /// 默认游戏目录：<c>%APPDATA%\.minecraft</c>。
    /// 用户可以在界面上改成别的目录（比如 Prism 的实例目录）。
    /// </summary>
    private static string DefaultGameDirectory()
    {
        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);

        return Path.Combine(appData, ".minecraft");
    }
}
