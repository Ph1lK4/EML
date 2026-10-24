using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Launcher.Contracts;
using Launcher.Download;
using Launcher.Forge;
using Launcher.Metadata;
using Launcher.Prism;
using Launcher.Runtime;
using Microsoft.Win32;

namespace Launcher.Gui;

/// <summary>
/// WPF 外壳：只做三件事——收集用户输入、调核心、把结果吐到日志框。
/// 所有启动逻辑都在 Launcher.Runtime / Download / Forge 里，这里不碰文件格式。
/// </summary>
public partial class MainWindow : Window
{
    private readonly LaunchService _service = LaunchService.CreateDefault();
    private IReadOnlyList<JavaCandidate> _javas = Array.Empty<JavaCandidate>();
    private CancellationTokenSource? _cts;
    /// <summary>当前实例如果是 Prism / MultiMC 实例，就只读地复用它的库与资源，不下载。</summary>
    private PrismInstance? _prism;

    public MainWindow()
    {
        InitializeComponent();
        InstanceBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "GTNH");
        DetectJava(this, new RoutedEventArgs());
        RefreshVersions();
    }

    // ---------------------------------------------------------------- 日志

    private void Log(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Log(text));
            return;
        }

        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = !busy;
        ForgeButton.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
    }

    private static RuleContext Platform() => RuleContext.WindowsX64();

    // ---------------------------------------------------------------- 实例 / 版本

    private void BrowseInstance(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择实例目录（里面应有 .minecraft）" };
        if (dialog.ShowDialog() != true) return;

        InstanceBox.Text = dialog.FolderName;
        RefreshVersions();
    }

    private void RefreshVersionsClick(object sender, RoutedEventArgs e) => RefreshVersions();

    private void RefreshVersions(string? select = null)
    {
        VersionBox.Items.Clear();
        _prism = null;

        var root = InstanceBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            Status.Text = "实例目录不存在";
            return;
        }

        // Prism / MultiMC 实例：目录里有 instance.cfg + patches/，版本信息不在 .minecraft/versions 里。
        if (IsPrismInstance(root))
        {
            LoadPrismInstance(root);
            return;
        }

        var versionsDirectory = Path.Combine(root, ".minecraft", "versions");
        if (!Directory.Exists(versionsDirectory))
        {
            Status.Text = "这个目录下没有 .minecraft/versions";
            return;
        }

        var ids = Directory.GetDirectories(versionsDirectory)
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrEmpty(id)
                         && File.Exists(Path.Combine(versionsDirectory, id!, id! + ".json")))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var id in ids) VersionBox.Items.Add(id);

        var pick = select
                   ?? ids.FirstOrDefault(id => id.StartsWith("1.7.10-Forge", StringComparison.OrdinalIgnoreCase))
                   ?? ids.FirstOrDefault();
        if (pick is not null) VersionBox.SelectedItem = pick;

        Status.Text = ids.Count == 0
            ? "还没装任何版本：先「安装 Forge…」或下载 1.7.10"
            : $"共 {ids.Count} 个版本";
    }

    private static bool IsPrismInstance(string directory) =>
        File.Exists(Path.Combine(directory, "instance.cfg"))
        && Directory.Exists(Path.Combine(directory, "patches"));

    /// <summary>Prism 实例：版本不从这里选，只读 patches 合并出启动参数。</summary>
    private void LoadPrismInstance(string directory)
    {
        try
        {
            var prism = PrismInstance.Open(directory);
            _prism = prism;

            VersionBox.Items.Add($"[Prism] {prism.Name}");
            VersionBox.SelectedIndex = 0;

            if (prism.ResolveConsoleJava() is { } java) JavaPathBox.Text = java;
            MemoryBox.Text = prism.MaxMemoryMb.ToString();

            Status.Text = $"Prism 实例：{prism.Name}（Java {prism.JavaMajor}，{prism.MinMemoryMb}-{prism.MaxMemoryMb} MB，库 {prism.Libraries.Count} 个）";
            Log($"识别到 Prism 实例：{prism.Name}");
            Log($"  Prism 根目录：{prism.PrismRoot}");
            Log($"  游戏目录：{prism.GameDirectory}");
            Log($"  主类：{prism.MainClass ?? "(未声明)"}，库 {prism.Libraries.Count} 个，JVM 参数 {prism.ExtraJvmArgs.Count} 项");
        }
        catch (Exception ex)
        {
            Status.Text = "这是 Prism 实例，但读取失败：" + ex.Message;
            Log("读 Prism 实例失败：" + ex.Message);
        }
    }

    /// <summary>把选中的版本（含 inheritsFrom 父版本）合并成一个可启动的版本。</summary>
    private (InstanceLayout Layout, ResolvedVersion Version, string JsonPath)? ResolveSelected()
    {
        var root = InstanceBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            Log("实例目录不存在：" + root);
            return null;
        }

        if (VersionBox.SelectedItem is not string versionId)
        {
            Log("先选一个版本。");
            return null;
        }

        var layout = new InstanceLayout(root);
        var jsonPath = layout.VersionJsonPath(versionId);
        if (!File.Exists(jsonPath))
        {
            Log("找不到版本 JSON：" + jsonPath);
            return null;
        }

        var child = MetadataJson.Load(jsonPath);
        VersionJson? parent = null;
        if (child.InheritsFrom is { } parentId)
        {
            var parentPath = layout.VersionJsonPath(parentId);
            if (File.Exists(parentPath)) parent = MetadataJson.Load(parentPath);
            else Log($"[警告] 缺少父版本 {parentId}.json，库列表可能不完整");
        }

        return (layout, VersionMerger.Merge(child, parent), jsonPath);
    }

    // ---------------------------------------------------------------- Java

    private void DetectJava(object sender, RoutedEventArgs e)
    {
        _javas = JavaLocator.FindAll();
        JavaBox.Items.Clear();
        foreach (var java in _javas) JavaBox.Items.Add(java);

        if (_javas.Count == 0)
        {
            Status.Text = "没找到 Java：手动填 javaw.exe 路径，或先装 Java 8 / 17";
            return;
        }

        var preferred = _javas.FirstOrDefault(java => java.Major == 8) ?? _javas[0];
        JavaBox.SelectedItem = preferred;
    }

    private void JavaChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JavaBox.SelectedItem is JavaCandidate java) JavaPathBox.Text = java.Path;
    }

    // ---------------------------------------------------------------- 操作

    private async void InstallForge(object sender, RoutedEventArgs e)
    {
        if (_prism is not null)
        {
            Log("Prism 实例不用装 Forge：Forge / lwjgl3ify 都已在 Prism 的 patches 里。");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择 Forge installer jar",
            Filter = "Forge 安装包|*.jar|所有文件|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        var root = InstanceBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            Log("实例目录不存在：" + root);
            return;
        }

        SetBusy(true);
        try
        {
            Log("解包 Forge 安装器：" + Path.GetFileName(dialog.FileName));
            var baseJson = Path.Combine(AppContext.BaseDirectory, "data", "1.7.10.json");
            var result = await new ForgeInstaller().InstallAsync(
                dialog.FileName, root, baseVersionJsonPath: File.Exists(baseJson) ? baseJson : null);

            Log($"装好版本 {result.VersionId}");
            Log($"  universal jar：{result.UniversalJarPath}");
            if (result.BaseVersionJsonCopied is { } copiedJson) Log("  顺带补了基础版本：" + copiedJson);
            RefreshVersions(result.VersionId);
        }
        catch (Exception ex)
        {
            Log("安装失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DownloadFiles(object sender, RoutedEventArgs e)
    {
        if (_prism is not null)
        {
            Log("Prism 实例是只读复用：库和资源都在 Prism 根目录，这里不下载。");
            return;
        }

        var selected = ResolveSelected();
        if (selected is null) return;

        var (layout, version, _) = selected.Value;
        SetBusy(true);
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                ProgressBarControl.Value = p.Total == 0 ? 0 : p.Completed * 100.0 / p.Total;
                Status.Text = $"下载 {p.Completed}/{p.Total}：{p.Label}";
            });

            Log($"开始补齐 {version.Id}（缺什么下什么）…");
            var count = await new InstanceDownloader().RunAsync(version, layout.GameDirectory, Platform(), progress);
            Log(count == 0 ? "文件齐全，没有要下的。" : $"下载完成，本次补了 {count} 个文件。");
            Status.Text = "下载完成";
        }
        catch (Exception ex)
        {
            Log("下载失败：" + ex.Message);
        }
        finally
        {
            ProgressBarControl.Value = 0;
            SetBusy(false);
        }
    }

    private async void StartGame(object sender, RoutedEventArgs e)
    {
        if (_prism is not null)
        {
            await StartPrismAsync();
            return;
        }

        var selected = ResolveSelected();
        if (selected is null) return;

        var javaPath = JavaPathBox.Text.Trim();
        if (javaPath.Length == 0)
        {
            Log("先选或填一个 Java 路径。");
            return;
        }

        if (!int.TryParse(MemoryBox.Text.Trim(), out var memory) || memory < 512)
        {
            Log("内存至少要 512 MB。");
            return;
        }

        var player = PlayerBox.Text.Trim();
        if (player.Length == 0)
        {
            Log("玩家名不能为空。");
            return;
        }

        var (layout, version, jsonPath) = selected.Value;
        var request = new LaunchRequest
        {
            Java = new JavaRuntime(javaPath, "local", 8, Source: "gui"),
            GameDirectory = layout.GameDirectory,
            VersionName = version.Id,
            Auth = new AuthInfo(player, OfflineUuid.ForName(player)),
            MaxMemoryMb = memory,
        };

        var preparation = _service.Prepare(layout, version, request, Platform(), jsonPath);
        if (!preparation.Ready)
        {
            Log($"预检没通过：缺 {preparation.MissingFiles.Count} 个文件，先点「下载 / 修复文件」。");
            foreach (var file in preparation.MissingFiles.Take(5)) Log("  缺 " + file);
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        Log($"启动 {version.Id}（Java：{javaPath}，内存 {memory} MB，玩家：{player}）");

        try
        {
            var outcome = await _service.RunAsync(
                preparation, line => Log($"[{line.Stream}] {line.Text}"), _cts.Token);

            if (outcome.Process.Cancelled) Log("已手动停止。");
            else Log($"进程退出，退出码 {outcome.Process.ExitCode}，跑了 {outcome.Process.Duration.TotalSeconds:F1} 秒。");

            if (outcome.Diagnosis is { } diagnosis)
            {
                Log($"诊断 {diagnosis.Code}：{diagnosis.Title}");
                Log("  " + diagnosis.Detail);
                foreach (var suggestion in diagnosis.Suggestions) Log("  建议：" + suggestion);
                if (diagnosis.MatchedLine is { } matched) Log("  命中日志：" + matched);
            }

            if (outcome.LogPath is { } logPath) Status.Text = "本次日志：" + logPath;
        }
        catch (OperationCanceledException)
        {
            Log("已取消。");
        }
        catch (Exception ex)
        {
            Log("启动出错：" + ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    /// <summary>Prism 模式：只读复用实例，直接拼命令行启动，不碰下载与安装。</summary>
    private async Task StartPrismAsync()
    {
        var prism = _prism;
        if (prism is null) return;

        var javaPath = JavaPathBox.Text.Trim();
        if (javaPath.Length == 0 && prism.ResolveConsoleJava() is { } consoleJava) javaPath = consoleJava;
        if (javaPath.Length == 0)
        {
            Log("Prism 实例没有可用的 Java：手动填 java.exe 的路径。");
            return;
        }

        if (!int.TryParse(MemoryBox.Text.Trim(), out var memory) || memory < 512)
        {
            Log("内存至少要 512 MB。");
            return;
        }

        var player = PlayerBox.Text.Trim();
        if (player.Length == 0)
        {
            Log("玩家名不能为空。");
            return;
        }

        var request = new LaunchRequest
        {
            Java = new JavaRuntime(javaPath, "prism", prism.JavaMajor, Source: "prism"),
            GameDirectory = prism.GameDirectory,
            VersionName = prism.Name,
            Auth = new AuthInfo(player, OfflineUuid.ForName(player)),
            MinMemoryMb = prism.MinMemoryMb,
            MaxMemoryMb = memory,
        };

        LaunchPlan plan;
        try
        {
            plan = PrismPlanner.Build(prism, request, Platform());
        }
        catch (Exception ex)
        {
            Log("拼启动参数失败：" + ex.Message);
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var classpathCount = plan.Classpath.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
        Log($"Prism 启动 {prism.Name}（Java {javaPath}，内存 {memory} MB，classpath {classpathCount} 项，玩家 {player}）");

        try
        {
            var result = await new JavaProcessRunner().RunAsync(
                plan.JavaPath, plan.EnumerateArgs().ToList(), plan.WorkingDirectory,
                line => Log($"[{line.Stream}] {line.Text}"), _cts.Token);

            if (result.Cancelled) Log("已手动停止。");
            else Log($"进程退出，退出码 {result.ExitCode}，跑了 {result.Duration.TotalSeconds:F1} 秒。");

            var latestLog = Path.Combine(prism.GameDirectory, "logs", "latest.log");
            if (File.Exists(latestLog))
            {
                var lines = File.ReadAllLines(latestLog).TakeLast(400).ToList();
                var diagnosis = new CrashDiagnoser().Diagnose(lines, result.ExitCode);
                if (diagnosis is not null)
                {
                    Log($"诊断 {diagnosis.Code}：{diagnosis.Title}");
                    Log("  " + diagnosis.Detail);
                    foreach (var suggestion in diagnosis.Suggestions) Log("  建议：" + suggestion);
                    if (diagnosis.MatchedLine is { } matched) Log("  命中日志：" + matched);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("已取消。");
        }
        catch (Exception ex)
        {
            Log("启动出错：" + ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void StopGame(object sender, RoutedEventArgs e)
    {
        if (_cts is null)
        {
            Log("当前没有在跑的游戏进程。");
            return;
        }

        Log("正在停止游戏…");
        _cts.Cancel();
    }

    private void ClearLog(object sender, RoutedEventArgs e) => LogBox.Clear();
}
