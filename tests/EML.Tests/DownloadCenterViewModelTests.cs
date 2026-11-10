using System.Collections.Concurrent;
using EML.App.ViewModels;
using EML.Core.Downloads;

namespace EML.Tests;

public sealed class DownloadCenterViewModelTests
{
    /// <summary>页面读取共享设置和已有任务，不因打开页面而启动或暂停下载。</summary>
    [Fact]
    public void OpeningPageReadsCurrentQueueAndSettingsWithoutChangingService()
    {
        var center = new Center { Settings = new() { MaxConcurrentDownloads = 5, BytesPerSecond = 1536 } };
        center.Snapshots = [Snapshot(DownloadTaskState.Downloading) with { BytesPerSecond = 2048 }];
        using var model = new DownloadCenterViewModel(center, new UiQueue());

        Assert.Equal("5", model.MaxConcurrentText);
        Assert.Equal("1.5", model.RateLimitKiBText);
        Assert.True(model.HasTasks);
        Assert.Single(model.Tasks);
        Assert.Contains("下载中 1", model.Summary);
        Assert.Contains("KiB/s", model.TotalSpeed);
        Assert.Empty(center.Calls);
        Assert.Equal(1, center.Subscribers);
    }

    /// <summary>连续后台事件合并为一次界面回调，并在界面处理时采用最新进度。</summary>
    [Fact]
    public async Task WorkerNotificationsCoalesceAndReuseRowsOnlyWhenUiQueueRuns()
    {
        var queue = new UiQueue();
        var first = Snapshot(DownloadTaskState.Downloading);
        var center = new Center { Snapshots = [first] };
        using var model = new DownloadCenterViewModel(center, queue);
        var row = Assert.Single(model.Tasks);
        var changes = 0;
        row.PropertyChanged += (_, _) => changes++;

        await Task.Run(() =>
        {
            for (var index = 1; index <= 25; index++)
            {
                center.Snapshots = [first with { BytesReceived = index * 1024 }];
                center.Publish();
            }
        });
        Assert.Equal(1, queue.PostCount);
        Assert.Equal(0, changes);
        Assert.Equal(0, row.ProgressValue);
        queue.Drain();
        Assert.Same(row, Assert.Single(model.Tasks));
        Assert.Equal(25, row.ProgressValue);
        Assert.True(changes > 0);
        Assert.Equal(2, center.Reads);
    }

    /// <summary>选中身份和未应用的优先级不被进度刷新覆盖，实际优先级改变时再同步。</summary>
    [Fact]
    public void SelectionAndPriorityDraftSurviveProgressAndDisappearWithRemovedTask()
    {
        var queue = new UiQueue();
        var snapshot = Snapshot(DownloadTaskState.Queued);
        var center = new Center { Snapshots = [snapshot] };
        using var model = new DownloadCenterViewModel(center, queue);
        var selected = model.SelectedTask = Assert.Single(model.Tasks);
        model.SelectedPriorityIndex = 0;
        center.Snapshots = [snapshot with { BytesReceived = 1024 }];
        center.Publish(); queue.Drain();
        Assert.Same(selected, model.SelectedTask);
        Assert.Equal(0, model.SelectedPriorityIndex);

        center.Snapshots = [snapshot with { Priority = DownloadPriority.High }];
        center.Publish(); queue.Drain();
        Assert.Same(selected, model.SelectedTask);
        Assert.Equal(2, model.SelectedPriorityIndex);
        center.Snapshots = [];
        center.Publish(); queue.Drain();
        Assert.Empty(model.Tasks);
        Assert.Null(model.SelectedTask);
        Assert.False(model.HasTasks);
        Assert.False(model.CancelCommand.CanExecute(null));
    }

    /// <summary>按钮严格按当前任务阶段开放，失败文件不提供脱离原事务的继续入口。</summary>
    [Theory]
    [InlineData(DownloadTaskState.Queued, true, false, true, true)]
    [InlineData(DownloadTaskState.Downloading, true, false, true, false)]
    [InlineData(DownloadTaskState.Paused, false, true, true, true)]
    [InlineData(DownloadTaskState.Verifying, false, false, true, false)]
    [InlineData(DownloadTaskState.Completed, false, false, false, false)]
    [InlineData(DownloadTaskState.Failed, false, false, false, false)]
    [InlineData(DownloadTaskState.Canceled, false, false, false, false)]
    public void ControlsFollowTaskState(DownloadTaskState state, bool pause, bool resume, bool cancel, bool priority)
    {
        var center = new Center { Snapshots = [Snapshot(state)] };
        using var model = new DownloadCenterViewModel(center, new UiQueue());
        model.SelectedTask = Assert.Single(model.Tasks);
        Assert.Equal(pause, model.PauseCommand.CanExecute(null));
        Assert.Equal(resume, model.ResumeCommand.CanExecute(null));
        Assert.Equal(cancel, model.CancelCommand.CanExecute(null));
        Assert.Equal(priority, model.ApplyPriorityCommand.CanExecute(null));
        if (state is DownloadTaskState.Failed or DownloadTaskState.Canceled)
        {
            model.ResumeCommand.Execute(null);
            Assert.Empty(center.Calls);
            Assert.Contains("原页面", model.ActionStatus);
        }
    }

    /// <summary>各命令携带选中的固定文件身份，状态转换后无需重新选择即可继续操作。</summary>
    [Fact]
    public void CommandsTargetSelectedFileAndRefreshTheirAvailability()
    {
        var selected = Snapshot(DownloadTaskState.Queued);
        var other = Snapshot(DownloadTaskState.Queued);
        var center = new Center { Snapshots = [selected, other] };
        using var model = new DownloadCenterViewModel(center, new UiQueue());
        model.SelectedTask = model.Tasks.Single(task => task.Id == selected.Id);
        model.SelectedPriorityIndex = 2;
        model.ApplyPriorityCommand.Execute(null);
        model.PauseCommand.Execute(null);
        Assert.True(model.ResumeCommand.CanExecute(null));
        Assert.False(model.PauseCommand.CanExecute(null));
        model.ResumeCommand.Execute(null);
        Assert.False(model.ApplyPriorityCommand.CanExecute(null));
        model.CancelCommand.Execute(null);

        Assert.Equal(new[] { "priority", "pause", "resume", "cancel" }, center.Calls.Select(call => call.Action));
        Assert.All(center.Calls, call => Assert.Equal(selected.Id, call.Id));
        Assert.Equal(DownloadPriority.High, center.Calls[0].Priority);
        Assert.Equal(DownloadTaskState.Queued, center.Snapshots.Single(task => task.Id == other.Id).State);
        Assert.False(model.CancelCommand.CanExecute(null));
        Assert.Contains("原页面", model.ActionStatus);
    }

    /// <summary>任务可能在点击前结束，拒绝操作和服务异常都转成可见提示。</summary>
    [Fact]
    public void StateRaceAndOperationFailureStayVisibleWithoutFalseSuccess()
    {
        var center = new Center { Snapshots = [Snapshot(DownloadTaskState.Downloading)], AcceptOperations = false };
        using var model = new DownloadCenterViewModel(center, new UiQueue());
        model.SelectedTask = Assert.Single(model.Tasks);
        model.PauseCommand.Execute(null);
        Assert.Contains("状态已改变", model.ActionStatus);
        center.OperationError = new IOException("测试服务拒绝操作");
        model.CancelCommand.Execute(null);
        Assert.Contains("测试服务拒绝操作", model.ActionStatus);
        Assert.Equal(DownloadTaskState.Downloading, model.SelectedTask.State);
    }

    /// <summary>用户设置以 KiB/s 输入并精确转换为字节，不把共享限速误当成单文件限速。</summary>
    [Theory]
    [InlineData("1", "0", 1, 0L)]
    [InlineData("8", "1048576", 8, 1073741824L)]
    [InlineData("4", "1.5", 4, 1536L)]
    [InlineData("3", "0.0009765625", 3, 1L)]
    public void SettingsConvertKiBExactly(string count, string rate, int expectedCount, long expectedBytes)
    {
        var center = new Center();
        using var model = new DownloadCenterViewModel(center, new UiQueue());
        model.MaxConcurrentText = count;
        model.RateLimitKiBText = rate;
        model.SaveSettingsCommand.Execute(null);
        Assert.Equal(expectedCount, center.Settings.MaxConcurrentDownloads);
        Assert.Equal(expectedBytes, center.Settings.BytesPerSecond);
        Assert.Equal(1, center.SettingsWrites);
        Assert.Contains("已保存", model.SettingsStatus);
    }

    /// <summary>非法并发、负速率、溢出和不足一字节的精度都保持原下载设置。</summary>
    [Theory]
    [InlineData("0", "0")]
    [InlineData("9", "0")]
    [InlineData("1.5", "0")]
    [InlineData("", "0")]
    [InlineData("3", "-1")]
    [InlineData("3", "1048577")]
    [InlineData("3", "0.0001")]
    [InlineData("3", "999999999999999999999999999999999")]
    [InlineData("3", "abc")]
    public void InvalidSettingsDoNotWrite(string count, string rate)
    {
        var center = new Center();
        var original = center.Settings;
        using var model = new DownloadCenterViewModel(center, new UiQueue());
        model.MaxConcurrentText = count; model.RateLimitKiBText = rate;
        model.SaveSettingsCommand.Execute(null);
        Assert.Same(original, center.Settings);
        Assert.Equal(0, center.SettingsWrites);
        Assert.Contains("请填写", model.SettingsStatus);
    }

    /// <summary>保存失败保留用户草稿，进度事件也不会覆盖尚未保存的设置。</summary>
    [Fact]
    public void SettingsFailureAndProgressPreserveInput()
    {
        var queue = new UiQueue();
        var center = new Center { OperationError = new IOException("设置文件只读") };
        using var model = new DownloadCenterViewModel(center, queue);
        model.MaxConcurrentText = "7"; model.RateLimitKiBText = "256";
        model.SaveSettingsCommand.Execute(null);
        Assert.Contains("设置文件只读", model.SettingsStatus);
        Assert.Equal(0, center.SettingsWrites);
        center.Publish(); queue.Drain();
        Assert.Equal("7", model.MaxConcurrentText);
        Assert.Equal("256", model.RateLimitKiBText);
    }

    /// <summary>全局操作只转发控制和清理记录，说明后续任务也受全局暂停影响。</summary>
    [Fact]
    public void GlobalControlsExplainPersistentPauseAndNeverReplayOrStopTransactions()
    {
        var center = new Center { Snapshots = [Snapshot(DownloadTaskState.Completed), Snapshot(DownloadTaskState.Failed)] };
        using var model = new DownloadCenterViewModel(center, new UiQueue());
        model.PauseAllCommand.Execute(null);
        Assert.Contains("新加入", model.ActionStatus);
        model.ResumeAllCommand.Execute(null);
        model.ClearFinishedCommand.Execute(null);
        Assert.Equal(new[] { "pause-all", "resume-all", "clear" }, center.Calls.Select(call => call.Action));
        Assert.Empty(model.Tasks);
        Assert.False(model.ClearFinishedCommand.CanExecute(null));
        Assert.Contains("文件和缓存保留", model.ActionStatus);
        Assert.Equal(0, center.Stops);
    }

    /// <summary>长期任务列表有界并优先保留活动文件，隐藏的历史记录仍允许清理。</summary>
    [Fact]
    public void BoundedListPrioritizesActiveFilesAndCanClearHiddenFinishedRecords()
    {
        var center = new Center
        {
            Snapshots = Enumerable.Range(0, 505).Select(index => Snapshot(DownloadTaskState.Queued) with
                { CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(index) })
                .Concat([Snapshot(DownloadTaskState.Completed) with { CreatedAt = DateTimeOffset.UtcNow }]).ToArray()
        };
        using var model = new DownloadCenterViewModel(center, new UiQueue());
        Assert.Equal(500, model.Tasks.Count);
        Assert.All(model.Tasks, task => Assert.Equal(DownloadTaskState.Queued, task.State));
        Assert.Contains("显示 500 项", model.Summary);
        Assert.True(model.ClearFinishedCommand.CanExecute(null));
        model.ClearFinishedCommand.Execute(null);
        Assert.Equal(505, center.Snapshots.Count);
        Assert.False(model.ClearFinishedCommand.CanExecute(null));
    }

    /// <summary>缓存、续传和未知大小有独立展示，校验阶段也不会伪装成已经完成。</summary>
    [Fact]
    public void RowsDescribeCacheResumeAndVerificationWithoutInventingProgress()
    {
        var cached = new DownloadTaskRow(Snapshot(DownloadTaskState.Completed) with { FromCache = true, ResumedBytes = 4096, Attempt = 2 });
        Assert.Equal(100, cached.ProgressValue);
        Assert.Contains("缓存命中", cached.TransferText);
        Assert.Contains("续传", cached.TransferText);
        Assert.Contains("第 2 次", cached.TransferText);
        var unknown = new DownloadTaskRow(Snapshot(DownloadTaskState.Downloading) with { TotalBytes = null });
        Assert.True(unknown.IsIndeterminate);
        Assert.Equal(0, unknown.ProgressValue);
        Assert.Contains("大小未知", unknown.ProgressText);
        var verifying = new DownloadTaskRow(Snapshot(DownloadTaskState.Verifying));
        Assert.True(verifying.IsIndeterminate);
        Assert.Equal("正在校验", verifying.StateText);
        Assert.False(verifying.IsFinished);
    }

    /// <summary>释放页面退订事件并丢弃尚未执行的刷新，关闭流程由主窗口等待下载中心。</summary>
    [Fact]
    public void DisposeUnsubscribesAndMakesPendingCallbacksAndCommandsInert()
    {
        var queue = new UiQueue();
        var center = new Center { Snapshots = [Snapshot(DownloadTaskState.Queued)] };
        var model = new DownloadCenterViewModel(center, queue);
        model.SelectedTask = Assert.Single(model.Tasks);
        center.Snapshots = [];
        center.Publish();
        model.Dispose(); model.Dispose();
        center.Publish(); queue.Drain();
        model.PauseCommand.Execute(null); model.ResumeCommand.Execute(null); model.CancelCommand.Execute(null);
        model.ApplyPriorityCommand.Execute(null); model.SaveSettingsCommand.Execute(null);
        model.PauseAllCommand.Execute(null); model.ResumeAllCommand.Execute(null); model.ClearFinishedCommand.Execute(null);
        Assert.Equal(0, center.Subscribers);
        Assert.Equal(1, center.Reads);
        Assert.Equal(1, queue.PostCount);
        Assert.Single(model.Tasks);
        Assert.False(model.CanInteract);
        Assert.False(model.PauseAllCommand.CanExecute(null));
        Assert.False(model.SaveSettingsCommand.CanExecute(null));
        Assert.Empty(center.Calls);
        Assert.Equal(0, center.SettingsWrites);
        Assert.Equal(0, center.Stops);
    }

    /// <summary>只读快照获取失败以文字反馈，不让后台通知异常击穿 UI 线程。</summary>
    [Fact]
    public void SnapshotReadFailureIsReportedAndLaterRefreshCanRecover()
    {
        var queue = new UiQueue();
        var center = new Center { SnapshotError = new IOException("队列暂不可用") };
        using var model = new DownloadCenterViewModel(center, queue);
        Assert.Contains("队列暂不可用", model.ActionStatus);
        center.SnapshotError = null; center.Snapshots = [Snapshot(DownloadTaskState.Downloading)];
        center.Publish(); queue.Drain();
        Assert.Single(model.Tasks);
    }

    /// <summary>构造固定大小的内存任务，测试无需创建真实文件或访问网络。</summary>
    private static DownloadTaskSnapshot Snapshot(DownloadTaskState state) => new()
    {
        Id = Guid.NewGuid(), DisplayName = "library.jar", GroupName = "GTNH · 运行依赖",
        DestinationPath = @"C:\download-test\libraries\library.jar", State = state,
        Priority = DownloadPriority.Normal, TotalBytes = 102400, CreatedAt = DateTimeOffset.UnixEpoch
    };

    /// <summary>显式排队模拟 UI 消息泵，后台调用不能偷偷在发出事件的线程刷新页面。</summary>
    private sealed class UiQueue : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private int _posts;
        public int PostCount => Volatile.Read(ref _posts);
        public override void Post(SendOrPostCallback callback, object? state)
        { Interlocked.Increment(ref _posts); _callbacks.Enqueue((callback, state)); }
        public void Drain() { while (_callbacks.TryDequeue(out var item)) item.Callback(item.State); }
    }

    /// <summary>受控队列只记录调用及状态变更，可模拟竞争和失败，不执行实际下载。</summary>
    private sealed class Center : IDownloadTaskCenter
    {
        private EventHandler? _changed;
        public event EventHandler? Changed { add => _changed += value; remove => _changed -= value; }
        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;
        public DownloadCenterSettings Settings { get; set; } = new();
        public IReadOnlyList<DownloadTaskSnapshot> Snapshots { get; set; } = [];
        public List<(string Action, Guid Id, DownloadPriority? Priority)> Calls { get; } = [];
        public int SettingsWrites { get; private set; }
        public int Reads { get; private set; }
        public int Stops { get; private set; }
        public bool AcceptOperations { get; set; } = true;
        public Exception? OperationError { get; set; }
        public Exception? SnapshotError { get; set; }
        public IReadOnlyList<DownloadTaskSnapshot> GetSnapshot()
        { Reads++; if (SnapshotError is not null) throw SnapshotError; return Snapshots; }
        public void UpdateSettings(DownloadCenterSettings settings)
        { if (OperationError is not null) throw OperationError; SettingsWrites++; Settings = settings; }
        public bool Pause(Guid id) => Change("pause", id, DownloadTaskState.Paused);
        public bool Resume(Guid id) => Change("resume", id, DownloadTaskState.Downloading);
        public bool Cancel(Guid id) => Change("cancel", id, DownloadTaskState.Canceled);
        public bool SetPriority(Guid id, DownloadPriority priority) => Change("priority", id, priority: priority);
        public void PauseAll() => Global("pause-all");
        public void ResumeAll() => Global("resume-all");
        public void ClearFinished()
        {
            Global("clear");
            Snapshots = Snapshots.Where(task => task.State is not (DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Canceled)).ToArray();
        }
        public Task StopAsync() { Stops++; return Task.CompletedTask; }
        public void Publish() => _changed?.Invoke(this, EventArgs.Empty);
        private void Global(string action)
        { if (OperationError is not null) throw OperationError; Calls.Add((action, Guid.Empty, null)); }
        private bool Change(string action, Guid id, DownloadTaskState? state = null, DownloadPriority? priority = null)
        {
            if (OperationError is not null) throw OperationError;
            Calls.Add((action, id, priority));
            if (!AcceptOperations) return false;
            Snapshots = Snapshots.Select(task => task.Id == id ? task with { State = state ?? task.State, Priority = priority ?? task.Priority } : task).ToArray();
            return true;
        }
    }
}
