using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.Core.Downloads;

namespace EML.App.ViewModels;

/// <summary>把后台文件队列发布到界面，任务行按身份复用，操作不重放实例安装事务。</summary>
public sealed partial class DownloadCenterViewModel : ObservableObject, IDisposable
{
    private const int MaximumVisibleTasks = 500;
    private readonly IDownloadTaskCenter _center;
    private readonly SynchronizationContext _context;
    private readonly Dictionary<Guid, DownloadTaskRow> _rows = [];
    private int _refreshQueued;
    private int _disposed;
    private bool _hasFinishedTasks;

    [ObservableProperty] private string maxConcurrentText;
    [ObservableProperty] private string rateLimitKiBText;
    [ObservableProperty] private string settingsStatus = "设置应用于全部下载；0 KiB/s 表示不限速。";
    [ObservableProperty] private string actionStatus = "支持续传的文件会保留进度；失败或取消后，请回原页面重新补全或下载。";
    [ObservableProperty] private string summary = "暂无文件下载任务。";
    [ObservableProperty] private string totalSpeed = "0 B/s";
    [ObservableProperty] private DownloadTaskRow? selectedTask;
    [ObservableProperty] private int selectedPriorityIndex = 1;

    public ObservableCollection<DownloadTaskRow> Tasks { get; } = [];
    public IReadOnlyList<string> Priorities { get; } = ["低", "普通", "高"];
    public bool HasTasks => Tasks.Count > 0;
    public bool CanInteract => Volatile.Read(ref _disposed) == 0;

    /// <summary>必须在界面线程创建，或显式提供界面同步上下文；构造不暂停或启动任务。</summary>
    public DownloadCenterViewModel(IDownloadTaskCenter center, SynchronizationContext? context = null)
    {
        _center = center;
        _context = context ?? SynchronizationContext.Current ??
            throw new InvalidOperationException("下载中心需要界面线程的同步上下文。");
        var settings = center.Settings;
        maxConcurrentText = settings.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture);
        rateLimitKiBText = (settings.BytesPerSecond / 1024m).ToString(CultureInfo.InvariantCulture);
        _center.Changed += OnCenterChanged;
        RefreshSnapshot();
    }

    /// <summary>后台事件只安排一次界面刷新，连续进度事件共用同一个待处理回调。</summary>
    private void OnCenterChanged(object? sender, EventArgs args)
    {
        if (!CanInteract || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _context.Post(_ =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (CanInteract) RefreshSnapshot();
        }, null);
    }

    /// <summary>复用相同任务的行对象，仅增删或移动改变的项目，并限制长期下载后的界面占用。</summary>
    private void RefreshSnapshot()
    {
        try
        {
            var snapshots = _center.GetSnapshot();
            var visible = snapshots.OrderBy(task => DownloadTaskRow.IsFinishedState(task.State))
                .ThenByDescending(task => task.CreatedAt).ThenBy(task => task.Id).Take(MaximumVisibleTasks).ToArray();
            var identities = visible.Select(task => task.Id).ToHashSet();
            var selectedId = SelectedTask?.Id;
            var previousPriority = SelectedTask?.Priority;
            foreach (var id in _rows.Keys.Where(id => !identities.Contains(id)).ToArray())
            {
                Tasks.Remove(_rows[id]); _rows.Remove(id);
            }
            for (var index = 0; index < visible.Length; index++)
            {
                var snapshot = visible[index];
                if (!_rows.TryGetValue(snapshot.Id, out var row))
                {
                    row = new(snapshot); _rows.Add(snapshot.Id, row); Tasks.Insert(index, row);
                }
                else
                {
                    row.Update(snapshot);
                    var oldIndex = Tasks.IndexOf(row);
                    if (oldIndex != index) Tasks.Move(oldIndex, index);
                }
            }
            SelectedTask = selectedId is { } identity && _rows.TryGetValue(identity, out var selected) ? selected : null;
            if (SelectedTask is { } current && previousPriority != current.Priority) SelectedPriorityIndex = (int)current.Priority;
            var downloading = snapshots.Count(task => task.State == DownloadTaskState.Downloading);
            var queued = snapshots.Count(task => task.State == DownloadTaskState.Queued);
            var paused = snapshots.Count(task => task.State == DownloadTaskState.Paused);
            var finished = snapshots.Count(task => DownloadTaskRow.IsFinishedState(task.State));
            _hasFinishedTasks = finished > 0;
            Summary = snapshots.Count == 0 ? "暂无文件下载任务。" :
                $"共 {snapshots.Count} 个文件 · 下载中 {downloading} · 等待 {queued} · 暂停 {paused} · 已结束 {finished}" +
                (snapshots.Count > visible.Length ? $"；显示 {visible.Length} 项，优先保留进行中的任务。" : "");
            TotalSpeed = DownloadTaskRow.FormatBytes(snapshots.Where(task => task.State == DownloadTaskState.Downloading)
                .Sum(task => Math.Max(0, task.BytesPerSecond))) + "/s";
            OnPropertyChanged(nameof(HasTasks));
            NotifyCommands();
        }
        catch (Exception ex) { ActionStatus = "读取下载队列失败：" + ex.Message; }
    }

    /// <summary>切换选中任务时显示其实际优先级，不随每次进度刷新覆盖未应用的选择。</summary>
    partial void OnSelectedTaskChanged(DownloadTaskRow? value)
    {
        SelectedPriorityIndex = (int)(value?.Priority ?? DownloadPriority.Normal);
        NotifyCommands();
    }

    /// <summary>优先级选择只有在范围有效且任务仍等待时才能应用。</summary>
    partial void OnSelectedPriorityIndexChanged(int value) => ApplyPriorityCommand.NotifyCanExecuteChanged();

    /// <summary>校验并发与共享限速后保存，错误输入不会修改当前运行设置。</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void SaveSettings()
    {
        if (!CanInteract) return;
        if (!int.TryParse(MaxConcurrentText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 8)
        { SettingsStatus = "并发下载数请填写 1 到 8 的整数。"; return; }
        if (!decimal.TryParse(RateLimitKiBText.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var limit) ||
            limit is < 0 or > 1048576 || limit * 1024 != decimal.Truncate(limit * 1024))
        { SettingsStatus = "限速请填写 0 到 1048576 KiB/s，最小单位为 1 字节；0 表示不限速。"; return; }
        try
        {
            _center.UpdateSettings(new() { MaxConcurrentDownloads = count, BytesPerSecond = (long)(limit * 1024) });
            SettingsStatus = limit == 0 ? $"已保存：最多同时下载 {count} 个文件，不限速。" :
                $"已保存：最多同时下载 {count} 个文件，共享限速 {limit.ToString(CultureInfo.InvariantCulture)} KiB/s。";
        }
        catch (Exception ex) { SettingsStatus = "保存下载设置失败：" + ex.Message; }
    }

    /// <summary>只暂停正在传输或等待的文件，校验阶段不发起新的暂停请求。</summary>
    private bool CanPause() => CanInteract && SelectedTask?.State is DownloadTaskState.Queued or DownloadTaskState.Downloading;
    /// <summary>已暂停文件允许继续，失败任务不在此处重放。</summary>
    private bool CanResume() => CanInteract && SelectedTask?.State == DownloadTaskState.Paused;
    /// <summary>文件尚未结束时允许取消，取消结果由下载中心发布。</summary>
    private bool CanCancel() => CanInteract && SelectedTask is { IsFinished: false };
    /// <summary>优先级只调整等待队列，避免让用户误以为会抢占正在传输的文件。</summary>
    private bool CanApplyPriority() => CanInteract && SelectedPriorityIndex is >= 0 and <= 2 &&
        SelectedTask?.State is DownloadTaskState.Queued or DownloadTaskState.Paused;
    /// <summary>历史清理只有存在已结束任务时才启用，不删除缓存或实例文件。</summary>
    private bool CanClearFinished() => CanInteract && _hasFinishedTasks;

    /// <summary>暂停选中的固定任务，拒绝结果提示用户以最新队列状态为准。</summary>
    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (CanPause()) Perform(() => _center.Pause(SelectedTask!.Id), "已请求暂停；支持续传的文件会保留已接收进度。");
    }

    /// <summary>继续当前暂停文件；全局暂停时仍需点击全部继续以放行队列。</summary>
    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        if (CanResume()) Perform(() => _center.Resume(SelectedTask!.Id), "已请求继续；若已全部暂停，请先点击“全部继续”。");
    }

    /// <summary>取消单个文件不会自动启动新的安装，后续修复由原页面的流程负责。</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (CanCancel()) Perform(() => _center.Cancel(SelectedTask!.Id), "已请求取消此文件；需要时请回原页面重新下载或补全。");
    }

    /// <summary>把显式选择的优先级应用到当前等待任务。</summary>
    [RelayCommand(CanExecute = nameof(CanApplyPriority))]
    private void ApplyPriority()
    {
        if (CanApplyPriority()) Perform(() => _center.SetPriority(SelectedTask!.Id, (DownloadPriority)SelectedPriorityIndex), "已更新等待队列优先级。");
    }

    /// <summary>全局暂停持续影响后来加入的下载，明确提示如何恢复。</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void PauseAll() => PerformGlobal(_center.PauseAll, "已全部暂停；此后新加入的下载也会等待，点击“全部继续”恢复。");

    /// <summary>解除全局暂停并继续暂停队列，保留原安装调用。</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void ResumeAll() => PerformGlobal(_center.ResumeAll, "已请求全部继续，队列按优先级和并发设置安排下载。");

    /// <summary>只清理已结束的展示记录，不删除已下载文件或恢复缓存。</summary>
    [RelayCommand(CanExecute = nameof(CanClearFinished))]
    private void ClearFinished()
    {
        if (CanClearFinished()) PerformGlobal(_center.ClearFinished, "已清理结束记录，已下载文件和缓存保留。");
    }

    /// <summary>统一处理任务状态竞争和服务错误，命令不假设任务必然接受操作。</summary>
    private void Perform(Func<bool> operation, string success)
    {
        try { ActionStatus = operation() ? success : "任务状态已改变，请根据最新列表重新选择操作。"; }
        catch (Exception ex) { ActionStatus = "操作未完成：" + ex.Message; }
        RefreshSnapshot();
    }

    /// <summary>全局命令也检查生命周期，关闭后排队点击不会再修改下载中心。</summary>
    private void PerformGlobal(Action operation, string success)
    {
        if (!CanInteract) return;
        try { operation(); ActionStatus = success; }
        catch (Exception ex) { ActionStatus = "操作未完成：" + ex.Message; }
        RefreshSnapshot();
    }

    /// <summary>任务状态更新后同步按钮可执行条件，不依赖再次切换选中行。</summary>
    private void NotifyCommands()
    {
        PauseCommand.NotifyCanExecuteChanged(); ResumeCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        ApplyPriorityCommand.NotifyCanExecuteChanged(); ClearFinishedCommand.NotifyCanExecuteChanged();
        PauseAllCommand.NotifyCanExecuteChanged(); ResumeAllCommand.NotifyCanExecuteChanged(); SaveSettingsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>退订后台事件并使排队回调失效；下载停止与等待由主窗口统一负责。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _center.Changed -= OnCenterChanged;
        OnPropertyChanged(nameof(CanInteract));
        NotifyCommands();
    }
}

/// <summary>一个文件的稳定显示对象，进度刷新只替换只读快照并通知已改变的展示字段。</summary>
public sealed class DownloadTaskRow : ObservableObject
{
    private DownloadTaskSnapshot _snapshot;
    public Guid Id => _snapshot.Id;
    public string DisplayName => _snapshot.DisplayName;
    public string GroupName => _snapshot.GroupName;
    public string DestinationPath => _snapshot.DestinationPath;
    public DownloadTaskState State => _snapshot.State;
    public DownloadPriority Priority => _snapshot.Priority;
    public string? Error => _snapshot.Error;
    public bool IsFinished => IsFinishedState(State);
    public string StateText => State switch
    {
        DownloadTaskState.Queued => "等待下载", DownloadTaskState.Downloading => "下载中", DownloadTaskState.Paused => "已暂停",
        DownloadTaskState.Verifying => "正在校验", DownloadTaskState.Completed => "已完成", DownloadTaskState.Failed => "失败",
        DownloadTaskState.Canceled => "已取消", _ => "未知状态"
    };
    public string PriorityText => Priority switch { DownloadPriority.High => "高", DownloadPriority.Low => "低", _ => "普通" };
    public double ProgressValue => State == DownloadTaskState.Completed ? 100 :
        _snapshot.TotalBytes is > 0 ? Math.Clamp(_snapshot.BytesReceived * 100d / _snapshot.TotalBytes.Value, 0, 100) : 0;
    public bool IsIndeterminate => State == DownloadTaskState.Verifying || State == DownloadTaskState.Downloading && _snapshot.TotalBytes is not > 0;
    public string ProgressText => FormatBytes(_snapshot.BytesReceived) + (_snapshot.TotalBytes is { } total ? " / " + FormatBytes(total) : " / 大小未知");
    public string SpeedText => FormatBytes(State == DownloadTaskState.Downloading ? _snapshot.BytesPerSecond : 0) + "/s";
    public string TransferText => (_snapshot.FromCache ? "缓存命中" : "网络下载") +
        (_snapshot.ResumedBytes > 0 ? " · 续传 " + FormatBytes(_snapshot.ResumedBytes) : "") +
        (_snapshot.Attempt > 0 ? $" · 第 {_snapshot.Attempt} 次尝试" : "");

    /// <summary>从服务快照创建显示行，不访问文件或下载地址。</summary>
    public DownloadTaskRow(DownloadTaskSnapshot snapshot) => _snapshot = snapshot;

    /// <summary>相同快照不通知，内容变化保持行对象和当前选中身份。</summary>
    internal void Update(DownloadTaskSnapshot snapshot)
    {
        if (_snapshot == snapshot) return;
        _snapshot = snapshot;
        OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(GroupName)); OnPropertyChanged(nameof(DestinationPath));
        OnPropertyChanged(nameof(State)); OnPropertyChanged(nameof(Priority)); OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(PriorityText)); OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(IsIndeterminate)); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(SpeedText)); OnPropertyChanged(nameof(TransferText));
    }

    /// <summary>文件终态可清理展示记录，其他状态继续保留控制入口。</summary>
    internal static bool IsFinishedState(DownloadTaskState state) => state is DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Canceled;

    /// <summary>统一用二进制单位显示文件大小与速度，异常数值按零处理。</summary>
    internal static string FormatBytes(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0) bytes = 0;
        return bytes >= 1073741824 ? $"{bytes / 1073741824:F1} GiB" : bytes >= 1048576 ? $"{bytes / 1048576:F1} MiB" :
            bytes >= 1024 ? $"{bytes / 1024:F1} KiB" : $"{bytes:F0} B";
    }
}
