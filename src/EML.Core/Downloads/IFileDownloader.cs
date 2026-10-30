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
}

/// <summary>成功落盘的文件及实际写入量；未提供哈希时不会声称已通过内容校验。</summary>
public sealed record FileDownloadResult(string DestinationPath, long BytesWritten, bool HashVerified);

/// <summary>当前尝试的下载进度；切换镜像或重试时已接收字节数从零开始。</summary>
public sealed record FileDownloadProgress(long BytesReceived, long? TotalBytes, int Attempt)
{
    public double? Percent => TotalBytes is > 0 ? Math.Min(100, BytesReceived * 100d / TotalBytes.Value) : null;
}
