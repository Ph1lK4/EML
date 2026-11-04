namespace EML.Infrastructure.Downloads;

/// <summary>限制单次下载的资源、重试和等待时间，避免无限重试或无限写入。</summary>
public sealed record FileDownloadOptions
{
    public int MaxAttemptsPerUrl { get; init; } = 3;
    public int MaxRedirects { get; init; } = 5;
    public long MaximumFileBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(10);
}
