namespace EML.Core.Downloads;

/// <summary>优先级只影响等待队列，不中断已经开始的文件传输。</summary>
public enum DownloadPriority { Low, Normal, High }

/// <summary>文件任务状态独立于实例导入、补全及最终启动状态。</summary>
public enum DownloadTaskState { Queued, Downloading, Paused, Verifying, Completed, Failed, Canceled }

/// <summary>共享下载设置；零速率表示不限速，并发数限制为一至八。</summary>
public sealed record DownloadCenterSettings
{
    public int MaxConcurrentDownloads { get; init; } = 3;
    public long BytesPerSecond { get; init; }
}

/// <summary>线程安全的只读任务快照，错误消息不含下载 URL 或认证凭据。</summary>
public sealed record DownloadTaskSnapshot
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public string GroupName { get; init; } = "文件下载";
    public required string DestinationPath { get; init; }
    public DownloadTaskState State { get; init; }
    public DownloadPriority Priority { get; init; }
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public int Attempt { get; init; }
    public bool FromCache { get; init; }
    public long ResumedBytes { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>统一管理文件队列；暂停保持原调用等待，取消则结束原调用，不单独重放安装事务。</summary>
public interface IDownloadTaskCenter
{
    event EventHandler? Changed;
    DownloadCenterSettings Settings { get; }
    /// <summary>读取线程安全的任务列表副本。</summary>
    IReadOnlyList<DownloadTaskSnapshot> GetSnapshot();
    /// <summary>校验并保存全局并发数与带宽限制。</summary>
    void UpdateSettings(DownloadCenterSettings settings);
    /// <summary>暂停文件而不结束原安装调用。</summary>
    bool Pause(Guid id);
    /// <summary>继续已暂停的原任务。</summary>
    bool Resume(Guid id);
    /// <summary>取消文件并结束对应原调用。</summary>
    bool Cancel(Guid id);
    /// <summary>更新等待队列优先级，不抢占当前传输。</summary>
    bool SetPriority(Guid id, DownloadPriority priority);
    /// <summary>暂停当前队列及随后提交的任务。</summary>
    void PauseAll();
    /// <summary>恢复全局队列并解除单文件暂停。</summary>
    void ResumeAll();
    /// <summary>清理终态展示记录，不删除文件。</summary>
    void ClearFinished();
    /// <summary>拒绝新任务、取消队列并等待活动文件释放。</summary>
    Task StopAsync();
}
