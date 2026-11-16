using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
using EML.Core.Downloads;
using EML.Core.Minecraft.Gtnh;

namespace EML.App.ViewModels;

/// <summary>独立展示镜像站的 Prism 客户端；无需已有 GTNH，安装仍由宿主统一导入和补全。</summary>
public sealed partial class GtnhPackDownloadViewModel : ObservableObject, IDisposable
{
    private readonly IGtnhPackService _service;
    private readonly Func<GtnhPackRelease, IProgress<FileDownloadProgress>, CancellationToken, Task<string>> _install;
    private readonly Func<bool> _canInstall;
    private readonly Action<Uri>? _openWebsite;
    private readonly IOperationNotifications _notifications;
    private readonly object _pendingGate = new();
    private readonly HashSet<Task> _pending = [];
    private IReadOnlyList<GtnhPackRelease> _allReleases = [];
    private CancellationTokenSource? _queryCancellation;
    private int _queryRevision, _installRevision, _javaFilterIndex;
    private bool _active, _closing, _dirty = true, _includePrereleases;
    private GtnhPackRelease? _selectedRelease;

    /// <summary>构造仅接线；联网查询要等页面打开，写入互斥由现有实例服务取得。</summary>
    public GtnhPackDownloadViewModel(IGtnhPackService service,
        Func<GtnhPackRelease, IProgress<FileDownloadProgress>, CancellationToken, Task<string>> install,
        Func<bool> canInstall, Action<Uri>? openWebsite = null, IOperationNotifications? notifications = null)
    {
        _service = service; _install = install; _canInstall = canInstall; _openWebsite = openWebsite;
        _notifications = notifications ?? NullOperationNotifications.Instance;
    }

    public const string MirrorAddress = "https://mirror.gaytnh.com/";
    public ObservableCollection<GtnhPackRelease> Releases { get; } = [];
    public IReadOnlyList<string> JavaFilters { get; } = ["全部 Java 版本", "新版 Java", "Java 8"];
    public Task ReleasesLoadTask { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isInstalling;
    [ObservableProperty] private string status = "打开页面后读取镜像版本列表。";
    [ObservableProperty] private string installStatus = "选择 Prism 客户端，下载后确认名称并自动导入、补全依赖。";
    [ObservableProperty] private string archiveProgress = "";
    [ObservableProperty] private double archivePercent;
    public bool CanBrowse => !_closing && !IsInstalling;
    public bool HasReleases => Releases.Count > 0;
    public bool HasSelectedRelease => SelectedRelease is not null;
    public bool IsPrereleaseSelection => SelectedRelease?.IsPrerelease == true;
    public string ReleaseInformation => SelectedRelease is not { } release ? "在左侧选择一个版本及 Java 变体。" :
        $"GTNH {release.Version}\nPrism 客户端 · Java {release.JavaVersion}\n" +
        $"{(release.IsPrerelease ? "测试版本" : "稳定版本")} · {release.Size / 1048576d:F1} MiB\n" + release.FileName;

    /// <summary>测试版本需明确开启；变更后取消旧列表，避免迟到响应带回另一种版本范围。</summary>
    public bool IncludePrereleases
    {
        get => _includePrereleases;
        set
        {
            if (!CanBrowse || !SetProperty(ref _includePrereleases, value)) return;
            CancelQuery(); _allReleases = []; ApplyFilter(); _dirty = true;
            if (_active) StartQuery();
        }
    }

    /// <summary>Java 筛选仅过滤已读取目录，安装中保持所选包与显示信息不变。</summary>
    public int JavaFilterIndex
    {
        get => _javaFilterIndex;
        set
        {
            if (!CanBrowse || value is < 0 or > 2 || !SetProperty(ref _javaFilterIndex, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>只能选当前列表中的版本；过滤或刷新时失效的选择必须重新确认。</summary>
    public GtnhPackRelease? SelectedRelease
    {
        get => _selectedRelease;
        set
        {
            if (!CanBrowse || value is not null && !Releases.Contains(value) || !SetProperty(ref _selectedRelease, value)) return;
            OnPropertyChanged(nameof(HasSelectedRelease)); OnPropertyChanged(nameof(IsPrereleaseSelection));
            OnPropertyChanged(nameof(ReleaseInformation)); InstallCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>离开只取消列表请求；已经确认的下载、导入和补全继续执行。</summary>
    public void SetActive(bool active)
    {
        if (_closing || _active == active) return;
        _active = active;
        if (!active) CancelQuery();
        else if (_dirty && !IsInstalling) StartQuery();
        NotifyCommands();
    }

    /// <summary>宿主正在启动游戏或写入实例时，立即禁用本页安装入口。</summary>
    public void NotifyHostStateChanged() => NotifyCommands();

    /// <summary>状态改变统一更新绑定与命令，保留取消按钮直至服务真正退出。</summary>
    partial void OnIsInstallingChanged(bool value) => NotifyCommands();
    /// <summary>列表读取期间允许重新刷新，但不能用旧选择开始安装。</summary>
    partial void OnIsLoadingChanged(bool value) => NotifyCommands();

    /// <summary>安排可等待的读取任务；调用处无需维护查询内部令牌。</summary>
    private void StartQuery() => ReleasesLoadTask = Track(QueryAsync(CancellationToken.None));

    /// <summary>用户显式刷新时报告结果，自动打开页面不重复弹出成功通知。</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync(CancellationToken token) => ReleasesLoadTask = Track(QueryAsync(token, true));

    /// <summary>隐藏页、安装中和关闭期间不开始新的目录请求。</summary>
    private bool CanRefresh() => _active && CanBrowse;

    /// <summary>只应用当前代次的目录，错误和取消都不会让过期结果改写新页面。</summary>
    private async Task QueryAsync(CancellationToken token, bool notifySuccess = false)
    {
        if (!CanRefresh()) return;
        CancelQuery();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _queryCancellation = cancellation;
        var revision = ++_queryRevision;
        var includePrereleases = IncludePrereleases;
        IsLoading = true; Status = "正在读取国内镜像的 Prism 客户端目录……";
        try
        {
            var releases = await _service.GetReleasesAsync(includePrereleases, cancellation.Token);
            if (_closing || !_active || revision != _queryRevision || cancellation.IsCancellationRequested) return;
            _allReleases = releases.Where(release => includePrereleases || !release.IsPrerelease).ToArray();
            _dirty = false; ApplyFilter();
            if (notifySuccess) _notifications.Success("GTNH 版本列表已刷新", Status);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_closing || !_active || revision != _queryRevision || cancellation.IsCancellationRequested) return;
            _dirty = true; _allReleases = []; ApplyFilter();
            Status = "版本列表读取失败：" + ex.Message;
            _notifications.Failure("GTNH 镜像目录读取失败", ex);
        }
        finally
        {
            if (ReferenceEquals(_queryCancellation, cancellation)) _queryCancellation = null;
            if (revision == _queryRevision) IsLoading = false;
        }
    }

    /// <summary>保留服务的版本排序；Java 8 与新版 Java 包按明确的版本标注区分。</summary>
    private void ApplyFilter()
    {
        var oldId = SelectedRelease?.Id;
        var matches = _allReleases.Where(release => JavaFilterIndex switch
        {
            1 => release.JavaVersion != "8",
            2 => release.JavaVersion == "8",
            _ => true
        }).ToArray();
        Releases.Clear(); foreach (var release in matches) Releases.Add(release);
        SelectedRelease = matches.FirstOrDefault(release => release.Id == oldId);
        OnPropertyChanged(nameof(HasReleases));
        Status = matches.Length == 0 ? "没有符合筛选的 Prism 客户端，请调整 Java 筛选或刷新目录。" :
            $"找到 {matches.Length} 个 Prism 客户端包。请选择 GTNH 版本和对应 Java 变体。";
    }

    /// <summary>固定本次包选择，交给宿主下载、确认名称、导入和补全，不根据文字推断安装成功。</summary>
    [RelayCommand(CanExecute = nameof(CanInstall), IncludeCancelCommand = true)]
    private async Task InstallAsync(CancellationToken token)
    {
        if (!CanInstall()) return;
        var release = SelectedRelease!;
        IsInstalling = true; CancelQuery();
        var revision = ++_installRevision;
        ArchivePercent = 0; ArchiveProgress = "正在核对所选版本与镜像文件……";
        InstallStatus = "正在准备原包，下载后将显示导入预览并继续补全实例依赖。";
        try
        {
            var progress = new Progress<FileDownloadProgress>(value => ApplyProgress(revision, value));
            // 宿主已汇总真实导入结果；返回文字可能表示部分完成，不能再弹成功通知。
            InstallStatus = await _install(release, progress, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            InstallStatus = "已取消本次操作；已导入的实例和已完成文件会保留。";
            _notifications.Cancelled("GTNH 下载与导入已取消", InstallStatus);
        }
        catch (Exception ex)
        {
            InstallStatus = "下载或导入未完成：" + ex.Message;
            _notifications.Failure("GTNH 下载与导入失败", ex);
        }
        finally
        {
            ++_installRevision; IsInstalling = false;
            ArchiveProgress = "本次任务已结束，结果见下方。";
            if (_active && _dirty && !_closing) StartQuery();
        }
    }

    /// <summary>安装需要明确选择、已完成目录读取和宿主空闲；不依赖任何已有 GTNH 实例。</summary>
    private bool CanInstall() => _active && !_closing && !IsInstalling && !IsLoading && _canInstall() &&
        SelectedRelease is not null && Releases.Contains(SelectedRelease);

    /// <summary>原包 100% 仅代表传输完成，后续导入和依赖安装另有结果。</summary>
    private void ApplyProgress(int revision, FileDownloadProgress value)
    {
        if (_closing || revision != _installRevision || !IsInstalling) return;
        ArchivePercent = value.Percent ?? 0;
        ArchiveProgress = value.Phase == FileDownloadPhase.Completed
            ? "原包传输完成，正在核对格式并等待导入、补全结果。"
            : $"原包：{value.BytesReceived / 1048576d:F1} MiB" + (value.Percent is { } percent ? $" · {percent:F1}%" : "");
    }

    /// <summary>只打开预设镜像主页，版本列表的远端文字不能成为任意浏览地址。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenWebsite))]
    private void OpenMirrorWebsite()
    {
        if (!CanOpenWebsite()) return;
        try { _openWebsite!(new Uri(MirrorAddress)); }
        catch (Exception ex) { _notifications.Failure("无法打开镜像网页", ex); }
    }

    /// <summary>没有浏览器回调或正在关闭时不启用外链按钮。</summary>
    private bool CanOpenWebsite() => !_closing && _openWebsite is not null;

    /// <summary>取消列表并使迟到响应失效，返回页面时重新读取尚未完成的目录。</summary>
    private void CancelQuery()
    {
        if (IsLoading) _dirty = true;
        ++_queryRevision; _queryCancellation?.Cancel(); IsLoading = false;
    }

    /// <summary>共用状态通知，供页面切换及全局忙碌状态变化调用。</summary>
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanBrowse));
        RefreshCommand.NotifyCanExecuteChanged(); InstallCommand.NotifyCanExecuteChanged();
        OpenMirrorWebsiteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>在锁内保存读取任务，不用后台回调修改集合，关闭时可安全取得快照。</summary>
    private Task Track(Task task)
    {
        lock (_pendingGate) { _pending.RemoveWhere(item => item.IsCompleted); _pending.Add(task); }
        return task;
    }

    /// <summary>关闭前取消请求及安装，等待已安排的工作结束后再释放下载服务。</summary>
    public async Task StopAsync()
    {
        _closing = true; _active = false; CancelQuery(); InstallCommand.Cancel(); NotifyCommands();
        Task[] pending;
        lock (_pendingGate) pending = [.. _pending, InstallCommand.ExecutionTask ?? Task.CompletedTask];
        await Task.WhenAll(pending);
    }

    /// <summary>同步释放仅禁止新任务并请求取消；宿主应先等待 StopAsync。</summary>
    public void Dispose()
    {
        _closing = true; _active = false; CancelQuery(); InstallCommand.Cancel(); NotifyCommands();
    }
}
