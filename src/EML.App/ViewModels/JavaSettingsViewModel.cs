using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
using EML.Core.Java;

namespace EML.App.ViewModels;

/// <summary>管理本机 Java 检测与官方托管下载；不修改系统环境变量或实例保存设置。</summary>
public partial class JavaSettingsViewModel : ObservableObject
{
    private readonly IJavaLocator _javaLocator;
    private readonly IOperationNotifications _notifications;
    private readonly IJavaRuntimeService? _runtimes;
    private readonly Dictionary<string, (JavaInstallation Installation, int Revision)> _registered = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _operationCancellation;
    private TaskCompletionSource? _operationCompletion;
    private int _operationRevision;
    private int _registrationRevision;
    private bool _stopped;

    [ObservableProperty] private ObservableCollection<JavaInstallation> installations = [];
    [ObservableProperty] private JavaInstallation? selectedInstallation;
    [ObservableProperty] private string statusText = "尚未检测 Java";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditDownloadFilters), nameof(IsProgressIndeterminate))]
    [NotifyCanExecuteChangedFor(nameof(DetectJavaCommand), nameof(QueryPackagesCommand), nameof(DownloadJavaCommand), nameof(CancelOperationCommand))]
    private bool isBusy;
    [ObservableProperty] private bool isQuerying;
    [ObservableProperty] private bool isInstalling;
    [ObservableProperty] private int selectedMajorVersion = 21;
    [ObservableProperty] private string selectedArchitecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPackageDetails))]
    [NotifyCanExecuteChangedFor(nameof(DownloadJavaCommand))]
    private JavaRuntimePackage? selectedPackage;
    [ObservableProperty] private string downloadStatus = "选择所需版本与架构，查询官方可用包。";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private double? downloadPercent;

    public IReadOnlyList<int> MajorVersions { get; } = [8, 11, 17, 21, 25];
    public IReadOnlyList<string> Architectures { get; } = ["x64", "x86", "aarch64"];
    public ObservableCollection<JavaRuntimePackage> Packages { get; } = [];
    public string InstallationRoot => _runtimes?.InstallationRoot ?? "此页面未配置 Java 下载服务。";
    public bool CanEditDownloadFilters => !_stopped && !IsBusy && _runtimes is not null;
    public bool IsProgressIndeterminate => IsBusy && DownloadPercent is null;
    public string SelectedPackageDetails => SelectedPackage is { } package
        ? $"{package.DisplayName} · {package.Size / 1024d / 1024d:F1} MB"
        : "尚未选择下载包";

    /// <summary>旧调用方可只使用检测；完整应用注入共享下载服务以复用下载中心和缓存。</summary>
    public JavaSettingsViewModel(IJavaLocator javaLocator, IOperationNotifications? notifications = null,
        IJavaRuntimeService? runtimes = null)
    {
        _javaLocator = javaLocator;
        _notifications = notifications ?? NullOperationNotifications.Instance;
        _runtimes = runtimes;
    }

    /// <summary>筛选变化使旧包失效，迟到的目录查询不能恢复旧筛选结果。</summary>
    partial void OnSelectedMajorVersionChanged(int value) => InvalidatePackages();
    /// <summary>架构变化必须重新查询，不能误装另一位数的已选包。</summary>
    partial void OnSelectedArchitectureChanged(string value) => InvalidatePackages();

    /// <summary>清空过时目录并取消尚在进行的查询；已开始安装使用固定快照。</summary>
    private void InvalidatePackages()
    {
        SelectedPackage = null;
        Packages.Clear();
        if (IsQuerying) _operationCancellation?.Cancel();
        if (!IsInstalling) DownloadStatus = "筛选已变化，请重新查询官方可用包。";
    }

    /// <summary>检测和下载串行执行，窗口关闭后不再接受新的操作。</summary>
    private bool CanDetectJava() => !_stopped && !IsBusy;
    /// <summary>只有配置了服务且空闲时才能访问官方目录。</summary>
    private bool CanQueryPackages() => CanEditDownloadFilters;
    /// <summary>选中的包必须仍符合当前筛选，避免使用过时查询结果。</summary>
    private bool CanDownloadJava() => CanEditDownloadFilters && SelectedPackage is { } package &&
        package.MajorVersion == SelectedMajorVersion && package.Architecture == SelectedArchitecture;
    /// <summary>取消只作用于本页当前操作，不会暂停其他实例的下载。</summary>
    private bool CanCancelOperation() => IsBusy && _operationCancellation is { IsCancellationRequested: false };

    /// <summary>检测成功后整体更新列表；外部刚安装的 Java 不会被较早的扫描结果覆盖。</summary>
    [RelayCommand(CanExecute = nameof(CanDetectJava), IncludeCancelCommand = true)]
    private async Task DetectJavaAsync(CancellationToken token)
    {
        if (!CanDetectJava()) return;
        var registrationRevision = _registrationRevision;
        using var cancellation = BeginOperation(token);
        try
        {
            StatusText = "正在检测 Java……";
            var results = await _javaLocator.FindAllAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var selectedPath = SelectedInstallation?.ExecutablePath;
            var merged = results.Concat(_registered.Values.Where(value => value.Revision > registrationRevision).Select(value => value.Installation))
                .GroupBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last()).ToArray();
            Installations.Clear();
            foreach (var installation in merged) Installations.Add(installation);
            SelectedInstallation = Installations.FirstOrDefault(item => string.Equals(item.ExecutablePath, selectedPath, StringComparison.OrdinalIgnoreCase))
                ?? Installations.FirstOrDefault();
            StatusText = Installations.Count == 0 ? "没有找到可用的 Java" : $"找到 {Installations.Count} 个 Java 环境";
            if (Installations.Count == 0) _notifications.Warning("Java 检测完成", StatusText + "，可在本页下载安装适合整合包的 Java。");
            else _notifications.Success("Java 检测完成", StatusText, Installations.Select(item => item.ExecutablePath).ToArray());
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消 Java 检测。";
            if (!_stopped) _notifications.Cancelled("Java 检测已取消", StatusText);
        }
        catch (Exception exception)
        {
            StatusText = $"检测失败：{exception.Message}";
            if (!_stopped) _notifications.Failure("Java 检测失败", exception);
        }
        finally { EndOperation(); }
    }

    /// <summary>查询所选 Windows 版本的官方发布，不自动下载或执行安装。</summary>
    [RelayCommand(CanExecute = nameof(CanQueryPackages), IncludeCancelCommand = true)]
    private async Task QueryPackagesAsync(CancellationToken token)
    {
        if (!CanQueryPackages()) return;
        var major = SelectedMajorVersion;
        var architecture = SelectedArchitecture;
        using var cancellation = BeginOperation(token);
        IsQuerying = true;
        SelectedPackage = null;
        Packages.Clear();
        DownloadStatus = $"正在查询 Java {major} / {architecture} 官方包……";
        try
        {
            var packages = await _runtimes!.GetPackagesAsync(major, architecture, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (major != SelectedMajorVersion || architecture != SelectedArchitecture) return;
            foreach (var package in packages.Where(package => package.MajorVersion == major && package.Architecture == architecture))
                Packages.Add(package);
            SelectedPackage = Packages.FirstOrDefault();
            DownloadStatus = Packages.Count == 0
                ? $"官方暂未提供 Java {major} / {architecture} 的可用 Windows 包，请选择其他受支持的版本或手动安装。"
                : $"找到 {Packages.Count} 个官方包，确认后点击“下载并安装”。";
            if (Packages.Count == 0) _notifications.Warning("Java 查询完成", DownloadStatus);
            else _notifications.Success("Java 查询完成", DownloadStatus);
        }
        catch (OperationCanceledException)
        {
            DownloadStatus = "已取消 Java 包查询。";
            if (!_stopped) _notifications.Cancelled("Java 查询已取消", DownloadStatus);
        }
        catch (Exception exception)
        {
            DownloadStatus = "查询失败：" + exception.Message;
            if (!_stopped) _notifications.Failure("Java 查询失败", exception);
        }
        finally { IsQuerying = false; EndOperation(); }
    }

    /// <summary>通过共享服务下载、校验和安装，完成后直接登记结果，不重新扫描整个电脑。</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadJava), IncludeCancelCommand = true)]
    private async Task DownloadJavaAsync(CancellationToken token)
    {
        if (!CanDownloadJava()) return;
        var package = SelectedPackage!;
        using var cancellation = BeginOperation(token);
        IsInstalling = true;
        var revision = _operationRevision;
        DownloadStatus = "正在准备下载并安装 " + package.DisplayName + "……";
        var progress = new Progress<JavaRuntimeInstallProgress>(value =>
        {
            if (_stopped || revision != _operationRevision || !IsInstalling) return;
            DownloadStatus = value.Stage;
            DownloadPercent = value.Percent;
        });
        try
        {
            var installation = await _runtimes!.InstallAsync(package, progress, cancellation.Token);
            // 安装服务返回即表示已发布可用环境；晚到的取消不把已完成安装说成失败。
            RegisterInstallation(installation);
            DownloadPercent = 100;
            DownloadStatus = $"Java {installation.Version} 安装完成，已加入本机环境列表。";
            if (!_stopped) _notifications.Success("Java 安装完成", DownloadStatus + "\n没有修改系统环境变量。", installation.ExecutablePath, InstallationRoot);
        }
        catch (OperationCanceledException)
        {
            DownloadStatus = "已取消 Java 下载或安装；已验证的下载缓存可在重试时复用。";
            if (!_stopped) _notifications.Cancelled("Java 安装已取消", DownloadStatus, InstallationRoot);
        }
        catch (Exception exception)
        {
            DownloadStatus = "安装失败：" + exception.Message;
            if (!_stopped) _notifications.Failure("Java 安装失败", exception, InstallationRoot);
        }
        finally { IsInstalling = false; EndOperation(); }
    }

    /// <summary>登记手动安装或启动时自动安装的已验证 Java，按可执行路径去重并选中。</summary>
    public void RegisterInstallation(JavaInstallation installation)
    {
        _registered[installation.ExecutablePath] = (installation, ++_registrationRevision);
        var existing = Installations.FirstOrDefault(item => string.Equals(item.ExecutablePath, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) Installations[Installations.IndexOf(existing)] = installation;
        else Installations.Add(installation);
        SelectedInstallation = installation;
        StatusText = $"找到 {Installations.Count} 个 Java 环境";
    }

    /// <summary>建立单次取消范围并锁定过滤器，避免并发命令互相覆盖界面。</summary>
    private CancellationTokenSource BeginOperation(CancellationToken token)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _operationCancellation = cancellation;
        _operationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ++_operationRevision;
        DownloadPercent = null;
        IsBusy = true;
        return cancellation;
    }

    /// <summary>操作完成后使排队进度失效并恢复按钮状态。</summary>
    private void EndOperation()
    {
        var completion = _operationCompletion;
        _operationCompletion = null;
        ++_operationRevision;
        _operationCancellation = null;
        IsBusy = false;
        completion?.TrySetResult();
    }

    /// <summary>用户取消本页当前检测、查询或安装，等待服务清理后再恢复交互。</summary>
    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void CancelOperation()
    {
        _operationCancellation?.Cancel();
        CancelOperationCommand.NotifyCanExecuteChanged();
    }

    /// <summary>关闭窗口时禁止新任务，取消并等待本页工作结束，不停止共享下载中心。</summary>
    public async Task StopAsync()
    {
        _stopped = true;
        var pending = _operationCompletion?.Task;
        _operationCancellation?.Cancel();
        OnPropertyChanged(nameof(CanEditDownloadFilters));
        DetectJavaCommand.NotifyCanExecuteChanged();
        QueryPackagesCommand.NotifyCanExecuteChanged();
        DownloadJavaCommand.NotifyCanExecuteChanged();
        if (pending is not null) await pending;
    }
}
