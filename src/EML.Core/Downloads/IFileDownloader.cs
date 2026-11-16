namespace EML.Core.Downloads;

/// <summary>通用文件下载接口，不依赖 Minecraft 或实例清单。</summary>
public interface IFileDownloader
{
    /// <summary>下载到新文件，校验成功后才发布目标；已有文件永远不会被覆盖。</summary>
    Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request,
        IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>一次下载的目标、候选地址和可选校验依据。</summary>
public sealed record FileDownloadRequest
{
    public required string DestinationPath { get; init; }
    public required IReadOnlyList<string> Urls { get; init; }
    public long? ExpectedSize { get; init; }
    /// <summary>本次请求允许的最大字节数；未设置时采用下载器默认上限，设置后不能放宽全局上限。</summary>
    public long? MaximumBytes { get; init; }
    public IReadOnlyDictionary<string, string> Hashes { get; init; } = new Dictionary<string, string>();
    /// <summary>任务中心使用的展示信息，不参与下载身份或文件完整性判断。</summary>
    public string? DisplayName { get; init; }
    public string? GroupName { get; init; }
    public DownloadPriority Priority { get; init; } = DownloadPriority.Normal;
}

/// <summary>成功落盘的文件及实际写入量；未提供哈希时不会声称已通过内容校验。</summary>
public sealed record FileDownloadResult(string DestinationPath, long BytesWritten, bool HashVerified)
{
    public bool CacheHit { get; init; }
    public long ResumedBytes { get; init; }
}

/// <summary>当前文件的完成字节数包含可复用片段；实际续传量与缓存命中另行标注。</summary>
public sealed record FileDownloadProgress(long BytesReceived, long? TotalBytes, int Attempt)
{
    public double? Percent => TotalBytes is > 0 ? Math.Min(100, BytesReceived * 100d / TotalBytes.Value) : null;
    public FileDownloadPhase Phase { get; init; } = FileDownloadPhase.Downloading;
    public bool FromCache { get; init; }
    public long ResumedBytes { get; init; }
}

/// <summary>传输阶段与安装结果分离；文件完成不代表实例已安装或可启动。</summary>
public enum FileDownloadPhase { Downloading, Verifying, CopyingFromCache, Completed }

/// <summary>供所有文件传输共享的速率控制器，等待必须响应取消。</summary>
public interface IDownloadBandwidthLimiter
{
    /// <summary>等待共享带宽预算，令牌取消时立即停止等待。</summary>
    ValueTask WaitAsync(int byteCount, CancellationToken cancellationToken = default);
}
