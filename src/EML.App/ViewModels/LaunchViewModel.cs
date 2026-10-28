// ════════════════════════════════════════════════════════════════════
//  文件：LaunchViewModel.cs
//  位置：src\EML.App\ViewModels
//  作用：启动页的 ViewModel —— 窗口上「启动 1.7.10」那一块的所有逻辑。
//
//  现在它做四件事：
//    ① 生成命令：拿到「选中的 Java + 游戏目录 + 玩家名 + 内存 + 额外参数」
//       → 调 ILaunchPlanner → 把命令行放到界面上（方便复制/核对）；
//    ② 启动游戏：把命令里再补上 -Djava.library.path（先解压 natives）
//       → 调 IGameLauncher 真跑起来 → 日志实时收进界面；
//    ③ 停止游戏：结束整个进程树；
//    ④ 运行状态：每秒刷新「PID + 已运行多久」，退出时给出退出码和运行时长。
//
//  它不负责：检测 Java（用 JavaSettingsViewModel）、拼命令行（ILaunchPlanner）、
//            解压 natives 的细节（INativesExtractor）、进程细节（IGameLauncher）。
// ════════════════════════════════════════════════════════════════════

using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.Core.Java;
using EML.Core.Launch;

namespace EML.App.ViewModels;

/// <summary>
/// 启动页 ViewModel。
///
/// 分区：
///   一、依赖     —— 由窗口注入 Core 接口，这里不认识任何 Infrastructure 类型；
///   二、输入属性 —— 界面上能改的四个值；
///   三、输出属性 —— 命令、日志、状态、是否在运行；
///   四、动作     —— Generate / Start / Stop；
///   五、辅助     —— 拼请求、收日志、默认游戏目录。
/// </summary>
public partial class LaunchViewModel : ObservableObject
{
    /// <summary>这一步只支持 1.7.10 原版；以后支持别的版本时改成参数。</summary>
    private const string VersionName = "1.7.10";

    /// <summary>日志最多保留多少字符，超出就丢掉最旧的，防止界面越用越卡。</summary>
    private const int MaxLogLength = 400_000;

    /// <summary>日志回退时保留的字符数（比上限少一截，避免每行都裁剪一次）。</summary>
    private const int TrimmedLogLength = 300_000;

    // ────────────────────────────────────────────────────────────────
    // 一、依赖
    //     窗口只传接口进来，方便以后换 Forge/Prism 实现，也方便单测。
    // ────────────────────────────────────────────────────────────────

    /// <summary>拼命令用的接口。</summary>
    private readonly ILaunchPlanner _launchPlanner;

    /// <summary>真正拉起进程用的接口。</summary>
    private readonly IGameLauncher _gameLauncher;

    /// <summary>解压本机库（.dll）用的接口。</summary>
    private readonly INativesExtractor _nativesExtractor;

    /// <summary>当前正在跑的游戏进程；没在跑时是 null。</summary>
    private IGameProcess? _process;

    /// <summary>
    /// 这次退出是不是用户自己点了「停止」。
    /// 手动结束时退出码也是非 0，不能当成崩溃来提示。
    /// </summary>
    private bool _stopRequested;

    /// <summary>日志缓冲。界面只是把它整串显示出来。</summary>
    private readonly StringBuilder _log = new();

    /// <summary>
    /// 每秒刷新一次「运行中（PID / 已运行多久）」。
    /// 用 DispatcherTimer 而不是后台循环：它本来就在 UI 线程回调，改属性不用切线程。
    /// </summary>
    private readonly DispatcherTimer _statusTimer;

    /// <summary>本次启动的时刻，用来算运行时长。</summary>
    private DateTime _startedAt;

    /// <summary>本次启动的进程号，启动后记下来（退出后 Process.Id 就读不到了）。</summary>
    private int _processId;

    // ────────────────────────────────────────────────────────────────
    // 二、输入属性
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

    /// <summary>
    /// 额外 JVM 参数（可空），例如 <c>-XX:+UseG1GC</c>。
    /// 原样交给 ILaunchPlanner 拼进 JVM 部分；填错了由 Java 自己报错。
    /// </summary>
    [ObservableProperty]
    private string extraJvmArguments = string.Empty;

    /// <summary>
    /// 额外游戏参数（可空），例如 <c>--width 1280 --height 720</c>。
    /// 追加在 --username 这些游戏参数的最后。
    /// </summary>
    [ObservableProperty]
    private string extraGameArguments = string.Empty;

    // ────────────────────────────────────────────────────────────────
    // 三、输出属性
    // ────────────────────────────────────────────────────────────────

    /// <summary>生成出来的完整命令；还没生成时是空串。</summary>
    [ObservableProperty]
    private string commandLine = string.Empty;

    /// <summary>状态栏文字：未生成 / 已生成 / 运行中 / 失败原因。</summary>
    [ObservableProperty]
    private string statusText = "选一个 Java，然后点「生成启动命令」或直接「启动游戏」";

    /// <summary>游戏日志（stdout + stderr 逐行追加）。</summary>
    [ObservableProperty]
    private string logText = string.Empty;

    /// <summary>
    /// 只看错误和警告：勾上后日志框只显示含 ERROR / WARN / Exception 的行。
    /// 日志本身不丢，随时可以取消勾选看全部。
    /// </summary>
    [ObservableProperty]
    private bool showErrorsOnly;

    /// <summary>游戏是否正在运行；控制「启动/停止」按钮的状态文字。</summary>
    [ObservableProperty]
    private bool isRunning;

    /// <summary>
    /// 构造：只保存依赖，不做任何业务动作。
    /// </summary>
    /// <param name="launchPlanner">拼命令用的实现（窗口里传 VanillaLaunchPlanner）。</param>
    /// <param name="gameLauncher">启动进程用的实现（窗口里传 GameLauncher）。</param>
    /// <param name="nativesExtractor">解压本机库用的实现（窗口里传 NativesExtractor）。</param>
    public LaunchViewModel(
        ILaunchPlanner launchPlanner,
        IGameLauncher gameLauncher,
        INativesExtractor nativesExtractor)
    {
        _launchPlanner = launchPlanner;
        _gameLauncher = gameLauncher;
        _nativesExtractor = nativesExtractor;

        // 状态计时器：只在游戏运行期间启动，退出时停掉
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateRunningStatus();
    }

    // ────────────────────────────────────────────────────────────────
    // 四、动作
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 生成启动命令（只显示，不启动）。
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
            var plan = _launchPlanner.Build(CreateRequest(java));

            // 这一步只拿 CommandLine 显示；真正启动时会另外补 java.library.path。
            CommandLine = plan.CommandLine;
            StatusText = "命令已生成（只显示，不启动；要进游戏点「启动游戏」）";
        }
        catch (Exception exception)
        {
            // 缺版本 JSON 之类的问题，把消息原样显示出来；
            // 消息本身是给人看的（例如「缺 versions\1.7.10\1.7.10.json」）。
            StatusText = "生成失败：" + exception.Message;
        }
    }

    /// <summary>
    /// 启动游戏：拼命令 → 解压 natives → 补 java.library.path → 跑进程 → 收日志。
    ///
    /// 为什么 java.library.path 在这里补，而不是在 LaunchPlanner 里？
    ///   因为解压 dll 是「启动时的一次性准备动作」，会写磁盘；
    ///   拼命令应该是纯计算，随时可以预览。两者分开，职责更清楚。
    /// </summary>
    [RelayCommand]
    private async Task StartAsync(JavaInstallation? java)
    {
        if (IsRunning)
        {
            StatusText = "游戏已经在运行了";
            return;
        }

        if (java is null)
        {
            StatusText = "请先在左边选一个 Java";
            return;
        }

        IGameProcess? process = null;

        try
        {
            var request = CreateRequest(java);
            var plan = _launchPlanner.Build(request);

            // ① 解压本机库；没有本机库时返回空串
            var nativesDirectory = _nativesExtractor.Extract(
                request.GameDirectory, request.VersionName);

            // ② 把 -Djava.library.path 放到参数最前面：
            //    JVM 参数必须在主类之前，放最前面一定安全。
            var arguments = string.IsNullOrEmpty(nativesDirectory)
                ? plan.Arguments
                : $"-Djava.library.path=\"{nativesDirectory}\" {plan.Arguments}";

            var finalPlan = new LaunchPlan(
                plan.JavaPath, arguments, plan.WorkingDirectory);

            // ③ 启动
            ResetLog();
            _stopRequested = false;
            IsRunning = true;
            StatusText = "正在启动游戏…";

            process = _gameLauncher.Start(finalPlan);
            process.OutputReceived += (_, line) => AppendLog(line);
            _process = process;
            _processId = process.Id;
            _startedAt = DateTime.Now;

            CommandLine = finalPlan.CommandLine;
            UpdateRunningStatus();
            _statusTimer.Start();

            // ④ 等退出（不阻塞界面）
            var exitCode = await process.WaitForExitAsync();

            _statusTimer.Stop();
            IsRunning = false;

            var elapsed = FormatDuration(DateTime.Now - _startedAt);
            var pid = _processId;

            StatusText = _stopRequested
                ? $"游戏已停止（PID {pid}，运行了 {elapsed}）"
                : exitCode == 0
                    ? $"游戏已正常退出（PID {pid}，运行了 {elapsed}）"
                    : $"游戏异常退出：退出码 {exitCode}，运行了 {elapsed}（日志最后几行是线索）";
        }
        catch (Exception exception)
        {
            _statusTimer.Stop();
            IsRunning = false;
            StatusText = "启动失败：" + exception.Message;
        }
        finally
        {
            if (process is not null)
            {
                process.Dispose();

                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }
            }
        }
    }

    /// <summary>
    /// 停止游戏：结束整个进程树。
    /// </summary>
    [RelayCommand]
    private void Stop()
    {
        var process = _process;

        if (process is null || !IsRunning)
        {
            StatusText = "当前没有正在运行的游戏";
            return;
        }

        try
        {
            _stopRequested = true;
            process.Kill();
            StatusText = "已结束游戏进程";
        }
        catch (Exception exception)
        {
            StatusText = "结束进程失败：" + exception.Message;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 五、辅助
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把界面上的四个输入拼成一次启动请求。
    /// 内存做个范围夹取：太小会起不来，太大可能拖垮系统。
    /// </summary>
    private LaunchRequest CreateRequest(JavaInstallation java)
    {
        return new LaunchRequest(
            java.ExecutablePath,
            GameDirectory,
            VersionName,
            string.IsNullOrWhiteSpace(PlayerName) ? "Steve" : PlayerName.Trim(),
            Math.Clamp(MaxMemoryMb, 512, 32768),
            string.IsNullOrWhiteSpace(ExtraJvmArguments) ? null : ExtraJvmArguments.Trim(),
            string.IsNullOrWhiteSpace(ExtraGameArguments) ? null : ExtraGameArguments.Trim());
    }

    /// <summary>清空上一次的日志。</summary>
    private void ResetLog()
    {
        _log.Clear();
        LogText = string.Empty;
    }

    /// <summary>
    /// 追加一行日志。
    ///
    /// 进程的输出事件在后台线程触发，这里必须切回 UI 线程再改属性，
    /// 否则 WPF 会抛「调用线程无法访问此对象」。
    /// </summary>
    private void AppendLog(string line)
    {
        var application = Application.Current;

        if (application is not null && !application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.BeginInvoke(() => AppendLog(line));
            return;
        }

        _log.AppendLine(line);

        // 日志太长时丢掉最旧的，避免界面越跑越卡
        if (_log.Length > MaxLogLength)
        {
            _log.Remove(0, _log.Length - TrimmedLogLength);
        }

        // 开着「只看错误」时，无关行不用重画（重画会把整串日志过滤一遍，尽量少做）
        if (!ShowErrorsOnly || IsLogLineImportant(line))
        {
            RenderLog();
        }
    }

    /// <summary>按当前过滤开关刷新日志框。</summary>
    private void RenderLog()
    {
        LogText = ShowErrorsOnly ? FilterLog(_log.ToString()) : _log.ToString();
    }

    /// <summary>
    /// 切换「只看错误和警告」时立即按新条件重画，不用等下一行日志。
    /// CommunityToolkit 会在属性变化后自动调用这个 partial 方法。
    /// </summary>
    partial void OnShowErrorsOnlyChanged(bool value) => RenderLog();

    /// <summary>
    /// 只保留含 ERROR / WARN / FATAL / Exception / Caused by 的行。
    /// 1.7.10 的日志格式是 <c>[时间] [线程/级别]: 内容</c>，按关键字过滤足够用。
    /// </summary>
    private static string FilterLog(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (IsLogLineImportant(line))
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString();
    }

    /// <summary>这一行是不是错误/警告级别（大小写不敏感）。</summary>
    private static bool IsLogLineImportant(string line)
    {
        return line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
               || line.Contains("WARN", StringComparison.OrdinalIgnoreCase)
               || line.Contains("FATAL", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Exception", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Caused by", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>刷新「运行中（PID / 已运行多久）」这一行状态。</summary>
    private void UpdateRunningStatus()
    {
        if (!IsRunning)
        {
            return;
        }

        StatusText =
            $"游戏运行中（PID {_processId}，已运行 {FormatDuration(DateTime.Now - _startedAt)}）";
    }

    /// <summary>把时长格式化成 mm:ss；超过一小时就变成 hh:mm:ss。</summary>
    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";
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
