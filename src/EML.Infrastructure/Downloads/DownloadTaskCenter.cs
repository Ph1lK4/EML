using System.Diagnostics;
using System.Text.Json;
using EML.Core.Downloads;

namespace EML.Infrastructure.Downloads;

/// <summary>为现有文件下载接口添加队列与控制，不绕过安装器的事务、来源记录或文件校验。</summary>
public sealed class DownloadTaskCenter : IFileDownloader, IDownloadTaskCenter
{
    private const int HistoryLimit = 500;
    private readonly object _gate = new();
    private readonly IFileDownloader _downloader;
    private readonly DownloadBandwidthLimiter _limiter;
    private readonly string? _settingsPath;
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DownloadCenterSettings _settings;
    private bool _allPaused;
    private bool _stopped;
    private int _running;
    private long _sequence;

    public event EventHandler? Changed;
    public DownloadCenterSettings Settings { get { lock (_gate) return _settings; } }

    /// <summary>复用同一个传输器及总限速器；设置文件只保存并发数与速率，不保存 URL、密钥或实例任务。</summary>
    public DownloadTaskCenter(IFileDownloader downloader, DownloadBandwidthLimiter limiter, string? settingsPath = null)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        _settingsPath = settingsPath is null ? null : Path.GetFullPath(settingsPath);
        _settings = LoadSettings(_settingsPath);
        _limiter.SetLimit(_settings.BytesPerSecond);
    }

    /// <summary>登记不可变请求后等待原安装流程；暂停不会使安装器误认为任务失败或成功。</summary>
    public async Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request,
        IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(request.Priority)) throw new ArgumentOutOfRangeException(nameof(request));
        ArgumentNullException.ThrowIfNull(request.Urls);
        ArgumentNullException.ThrowIfNull(request.Hashes);
        var copy = request with { Urls = request.Urls.ToArray(), Hashes = new Dictionary<string, string>(request.Hashes) };
        Entry entry;
        lock (_gate)
        {
            if (_stopped) throw new OperationCanceledException("下载任务中心已关闭。", new CancellationToken(true));
            entry = new Entry(copy, progress, ++_sequence, _allPaused);
            _entries.Add(entry.Id, entry);
            PumpLocked();
        }
        using var registration = cancellationToken.Register(() => Cancel(entry.Id));
        NotifyChanged();
        return await entry.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>复制所有任务状态，UI 无需持有后台可变对象。</summary>
    public IReadOnlyList<DownloadTaskSnapshot> GetSnapshot()
    {
        lock (_gate) return _entries.Values.OrderByDescending(entry => entry.Sequence).Select(entry => entry.Snapshot).ToArray();
    }

    /// <summary>设置校验和落盘成功后才生效；降低并发时让已有传输完成，不截断文件。</summary>
    public void UpdateSettings(DownloadCenterSettings settings)
    {
        ValidateSettings(settings);
        lock (_gate)
        {
            if (_stopped) throw new InvalidOperationException("下载任务中心已关闭。");
            SaveSettings(settings);
            _settings = settings;
            _limiter.SetLimit(settings.BytesPerSecond);
            PumpLocked();
        }
        NotifyChanged();
    }

    /// <summary>暂停单个文件；运行中的传输先保存可续传片段，再释放并发名额。</summary>
    public bool Pause(Guid id)
    {
        CancellationTokenSource? attempt;
        lock (_gate)
        {
            if (!TryGetPending(id, out var entry)) return false;
            entry.Paused = true;
            entry.Snapshot = entry.Snapshot with { State = DownloadTaskState.Paused, BytesPerSecond = 0 };
            attempt = entry.AttemptCancellation;
        }
        CancelAttempt(attempt);
        NotifyChanged();
        return true;
    }

    /// <summary>继续原任务；全局暂停期间保持暂停，恢复全局后按优先级排队。</summary>
    public bool Resume(Guid id)
    {
        lock (_gate)
        {
            if (!TryGetPending(id, out var entry) || !entry.Paused && entry.Snapshot.State != DownloadTaskState.Paused) return false;
            entry.Paused = false;
            entry.Snapshot = entry.Snapshot with { State = _allPaused ? DownloadTaskState.Paused : DownloadTaskState.Queued };
            PumpLocked();
        }
        NotifyChanged();
        return true;
    }

    /// <summary>取消结束原调用，不能在后台擅自重放已被安装器回收的临时目录。</summary>
    public bool Cancel(Guid id)
    {
        CancellationTokenSource? attempt;
        lock (_gate)
        {
            if (!TryGetPending(id, out var entry)) return false;
            entry.Canceled = true;
            entry.Snapshot = entry.Snapshot with { State = DownloadTaskState.Canceled, BytesPerSecond = 0 };
            attempt = entry.AttemptCancellation;
            if (!entry.Running) entry.Completion.TrySetCanceled(new CancellationToken(true));
            TrimHistoryLocked();
        }
        CancelAttempt(attempt);
        NotifyChanged();
        return true;
    }

    /// <summary>优先级改变队列次序，同优先级先来先下载，不抢占正在写入的文件。</summary>
    public bool SetPriority(Guid id, DownloadPriority priority)
    {
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        lock (_gate)
        {
            if (!TryGetPending(id, out var entry)) return false;
            entry.Snapshot = entry.Snapshot with { Priority = priority };
            PumpLocked();
        }
        NotifyChanged();
        return true;
    }

    /// <summary>暂停当前和后续到来的文件，让整个下载队列保持等待。</summary>
    public void PauseAll()
    {
        CancellationTokenSource[] attempts;
        lock (_gate)
        {
            if (_stopped) return;
            _allPaused = true;
            foreach (var entry in _entries.Values.Where(IsPending))
                entry.Snapshot = entry.Snapshot with { State = DownloadTaskState.Paused, BytesPerSecond = 0 };
            attempts = _entries.Values.Where(IsPending).Select(entry => entry.AttemptCancellation).OfType<CancellationTokenSource>().ToArray();
        }
        foreach (var attempt in attempts) CancelAttempt(attempt);
        NotifyChanged();
    }

    /// <summary>解除全局及单文件暂停，重新按队列优先级安排任务。</summary>
    public void ResumeAll()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _allPaused = false;
            foreach (var entry in _entries.Values.Where(IsPending))
            {
                entry.Paused = false;
                if (!entry.Running || entry.Snapshot.State == DownloadTaskState.Paused)
                    entry.Snapshot = entry.Snapshot with { State = DownloadTaskState.Queued };
            }
            PumpLocked();
        }
        NotifyChanged();
    }

    /// <summary>只清除已结束的内存展示记录，不删除任何缓存、片段或实例文件。</summary>
    public void ClearFinished()
    {
        lock (_gate)
            foreach (var entry in _entries.Values.Where(entry => entry.Completion.Task.IsCompleted && !entry.Running).ToArray())
                _entries.Remove(entry.Id);
        NotifyChanged();
    }

    /// <summary>拒绝新任务并取消等待与运行任务，直到全部传输关闭文件流才完成。</summary>
    public Task StopAsync()
    {
        CancellationTokenSource[] attempts;
        lock (_gate)
        {
            _stopped = true;
            foreach (var entry in _entries.Values.Where(IsPending))
            {
                entry.Canceled = true;
                entry.Snapshot = entry.Snapshot with { State = DownloadTaskState.Canceled, BytesPerSecond = 0 };
                if (!entry.Running) entry.Completion.TrySetCanceled(new CancellationToken(true));
            }
            attempts = _entries.Values.Select(entry => entry.AttemptCancellation).OfType<CancellationTokenSource>().ToArray();
            if (_running == 0) _drained.TrySetResult();
        }
        foreach (var attempt in attempts) CancelAttempt(attempt);
        NotifyChanged();
        return _drained.Task;
    }

    /// <summary>调用时持有状态锁；工作线程不会在队列锁中执行网络或外部进度回调。</summary>
    private void PumpLocked()
    {
        while (!_stopped && !_allPaused && _running < _settings.MaxConcurrentDownloads)
        {
            var next = _entries.Values.Where(entry => IsPending(entry) && !entry.Running && !entry.Paused)
                .OrderByDescending(entry => entry.Snapshot.Priority).ThenBy(entry => entry.Sequence).FirstOrDefault();
            if (next is null) break;
            next.Running = true;
            next.AttemptCancellation = new CancellationTokenSource();
            next.LastProgressAt = Stopwatch.GetTimestamp();
            next.LastNetworkBytes = 0;
            next.Snapshot = next.Snapshot with { State = DownloadTaskState.Downloading, BytesPerSecond = 0 };
            _running++;
            var attempt = next.AttemptCancellation;
            _ = Task.Run(() => ExecuteAsync(next, attempt));
        }
    }

    /// <summary>收集一次传输结果；用户暂停后仍等待原任务，快速暂停再继续不会变成失败。</summary>
    private async Task ExecuteAsync(Entry entry, CancellationTokenSource attempt)
    {
        FileDownloadResult? result = null;
        Exception? error = null;
        try
        {
            result = await _downloader.DownloadAsync(entry.Request,
                new InlineProgress(value => ReportProgress(entry, attempt, value)), attempt.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { error = ex; }
        lock (_gate)
        {
            entry.Running = false;
            entry.AttemptCancellation = null;
            _running--;
            if (result is not null)
            {
                // 发布已经成功时如实报告完成，不能让暂停按钮把已写好的文件误记为失败。
                entry.Snapshot = entry.Snapshot with { State = DownloadTaskState.Completed, BytesReceived = result.BytesWritten,
                    TotalBytes = result.BytesWritten, BytesPerSecond = 0, FromCache = result.CacheHit, ResumedBytes = result.ResumedBytes };
                entry.Completion.TrySetResult(result);
            }
            else if (entry.Canceled || _stopped)
            {
                entry.Snapshot = entry.Snapshot with { State = DownloadTaskState.Canceled, BytesPerSecond = 0 };
                entry.Completion.TrySetCanceled(new CancellationToken(true));
            }
            else if (error is OperationCanceledException && attempt.IsCancellationRequested)
            {
                entry.Snapshot = entry.Snapshot with { State = entry.Paused || _allPaused ? DownloadTaskState.Paused : DownloadTaskState.Queued,
                    BytesPerSecond = 0 };
            }
            else
            {
                entry.Snapshot = entry.Snapshot with { State = DownloadTaskState.Failed, BytesPerSecond = 0, Error = SafeError(error) };
                entry.Completion.TrySetException(error ?? new IOException("下载未返回结果。"));
            }
            TrimHistoryLocked();
            PumpLocked();
            if (_stopped && _running == 0) _drained.TrySetResult();
        }
        attempt.Dispose();
        NotifyChanged();
    }

    /// <summary>在当前尝试内更新状态，缓存与已有片段不计入网络速度，过期回调不覆盖最终结果。</summary>
    private void ReportProgress(Entry entry, CancellationTokenSource attempt, FileDownloadProgress progress)
    {
        bool notify;
        lock (_gate)
        {
            if (!ReferenceEquals(entry.AttemptCancellation, attempt) || !IsPending(entry)) return;
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(entry.LastProgressAt, now).TotalSeconds;
            var networkBytes = Math.Max(0, progress.BytesReceived - progress.ResumedBytes);
            var speed = progress.FromCache || networkBytes < entry.LastNetworkBytes || elapsed <= 0
                ? 0 : (networkBytes - entry.LastNetworkBytes) / elapsed;
            var state = entry.Paused || _allPaused ? DownloadTaskState.Paused :
                progress.Phase is FileDownloadPhase.Verifying or FileDownloadPhase.CopyingFromCache
                    ? DownloadTaskState.Verifying : DownloadTaskState.Downloading;
            entry.Snapshot = entry.Snapshot with { State = state, BytesReceived = progress.BytesReceived,
                TotalBytes = progress.TotalBytes, Attempt = Math.Max(entry.Snapshot.Attempt, progress.Attempt),
                FromCache = progress.FromCache, ResumedBytes = progress.ResumedBytes,
                BytesPerSecond = state is DownloadTaskState.Paused or DownloadTaskState.Verifying ? 0 : speed };
            notify = elapsed >= .15 || progress.Phase != FileDownloadPhase.Downloading || progress.BytesReceived == progress.TotalBytes;
            if (notify) { entry.LastProgressAt = now; entry.LastNetworkBytes = networkBytes; }
        }
        entry.Progress?.Report(progress);
        if (notify) NotifyChanged();
    }

    /// <summary>判定仍属于原安装调用的任务，取消中的任务不接受新的操作。</summary>
    private static bool IsPending(Entry entry) => !entry.Completion.Task.IsCompleted && !entry.Canceled;

    /// <summary>在锁内查找可以控制的任务。</summary>
    private bool TryGetPending(Guid id, out Entry entry) => _entries.TryGetValue(id, out entry!) && IsPending(entry);

    /// <summary>限制已结束历史数量；所有活动或暂停任务继续保留。</summary>
    private void TrimHistoryLocked()
    {
        foreach (var entry in _entries.Values.Where(entry => entry.Completion.Task.IsCompleted && !entry.Running)
                     .OrderByDescending(entry => entry.Sequence).Skip(HistoryLimit).ToArray())
            _entries.Remove(entry.Id);
    }

    /// <summary>取消与传输完成可能同时发生，已释放的尝试无需再次取消。</summary>
    private static void CancelAttempt(CancellationTokenSource? attempt)
    {
        try { attempt?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>UI 订阅失败不能改变文件下载结果，事件在状态锁外派发。</summary>
    private void NotifyChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { Debug.WriteLine("下载任务界面更新失败：" + ex.GetType().Name); }
    }

    /// <summary>任务列表只给出固定诊断，不展示可能含签名 URL 或认证头的外部异常原文。</summary>
    private static string SafeError(Exception? error) => error switch
    {
        InvalidDataException => "文件大小、摘要或服务器响应校验失败，请回原页面重试补全。",
        UnauthorizedAccessException => "无法写入下载目录，请检查权限或文件占用。",
        HttpRequestException { StatusCode: { } status } => $"下载服务返回 HTTP {(int)status}，请回原页面重试。",
        TimeoutException => "下载连接超时，请回原页面重试。",
        OperationCanceledException => "传输意外中止，请回原页面重试。",
        _ => "文件下载失败，请回原页面查看安装结果并重试。"
    };

    /// <summary>从受限的小配置文件读取已保存设置；损坏配置保留原样并使用默认值。</summary>
    private static DownloadCenterSettings LoadSettings(string? path)
    {
        if (path is null) return new();
        try
        {
            EnsureNoLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024) return new();
            var settings = JsonSerializer.Deserialize<DownloadCenterSettings>(File.ReadAllText(path)) ?? new();
            ValidateSettings(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(); }
    }

    /// <summary>只替换自己的设置文件，用同目录临时文件避免中断留下半份 JSON。</summary>
    private void SaveSettings(DownloadCenterSettings settings)
    {
        if (_settingsPath is null) return;
        EnsureNoLinks(_settingsPath);
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        EnsureNoLinks(directory);
        var temporary = Path.Combine(directory, ".eml-download-settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings);
                stream.Flush(true);
            }
            EnsureNoLinks(_settingsPath);
            File.Move(temporary, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) { EnsureNoLinks(temporary); File.Delete(temporary); }
        }
    }

    /// <summary>拒绝设置目录中的符号链接，避免把配置写到其他位置。</summary>
    private static void EnsureNoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("下载设置路径包含符号链接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
    }

    /// <summary>统一 UI 与持久化边界，避免无效并发或负速率造成队列停滞。</summary>
    private static void ValidateSettings(DownloadCenterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaxConcurrentDownloads is < 1 or > 8 || settings.BytesPerSecond is < 0 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(settings), "并发数应为 1–8，限速应为 0–1048576 KiB/s。");
    }

    /// <summary>保存单次请求生命周期中的状态；只在中心状态锁内修改。</summary>
    private sealed class Entry
    {
        public Guid Id { get; } = Guid.NewGuid();
        public FileDownloadRequest Request { get; }
        public IProgress<FileDownloadProgress>? Progress { get; }
        public long Sequence { get; }
        public TaskCompletionSource<FileDownloadResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DownloadTaskSnapshot Snapshot { get; set; }
        public CancellationTokenSource? AttemptCancellation { get; set; }
        public bool Paused { get; set; }
        public bool Canceled { get; set; }
        public bool Running { get; set; }
        public long LastProgressAt { get; set; }
        public long LastNetworkBytes { get; set; }

        /// <summary>展示信息与文件身份分离；没有业务标签时以最终文件名作为任务名。</summary>
        public Entry(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress, long sequence, bool allPaused)
        {
            Request = request; Progress = progress; Sequence = sequence;
            Snapshot = new DownloadTaskSnapshot
            {
                Id = Id, DisplayName = Label(request.DisplayName, Path.GetFileName(request.DestinationPath)),
                GroupName = Label(request.GroupName, "文件下载"), DestinationPath = request.DestinationPath,
                State = allPaused ? DownloadTaskState.Paused : DownloadTaskState.Queued, Priority = request.Priority,
                TotalBytes = request.ExpectedSize, CreatedAt = DateTimeOffset.UtcNow
            };
        }

        /// <summary>压缩显示文本中的控制字符并限制长度，不修改实际目标路径。</summary>
        private static string Label(string? value, string fallback)
        {
            var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            text = new string(text.Where(character => !char.IsControl(character)).Take(200).ToArray());
            return string.IsNullOrWhiteSpace(text) ? "文件下载" : text;
        }
    }

    /// <summary>在工作线程同步接收进度，避免后台回调延迟到文件完成之后。</summary>
    private sealed class InlineProgress(Action<FileDownloadProgress> report) : IProgress<FileDownloadProgress>
    {
        public void Report(FileDownloadProgress value) => report(value);
    }
}
