using System.Diagnostics;
using EML.Core.Downloads;

namespace EML.Infrastructure.Downloads;

/// <summary>全部文件共享的令牌桶；速率修改立即生效，暂停或取消不会留下长时间的排队配额。</summary>
public sealed class DownloadBandwidthLimiter : IDownloadBandwidthLimiter
{
    private readonly object _gate = new();
    private long _bytesPerSecond;
    private double _tokens;
    private long _lastRefill = Stopwatch.GetTimestamp();

    public long BytesPerSecond { get { lock (_gate) return _bytesPerSecond; } }

    /// <summary>设置总速率，零表示不限速；最多允许短暂积攒 32 KiB 的传输额度。</summary>
    public void SetLimit(long bytesPerSecond)
    {
        if (bytesPerSecond is < 0 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        lock (_gate)
        {
            _bytesPerSecond = bytesPerSecond;
            _tokens = Capacity(bytesPerSecond);
            _lastRefill = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>分批申请字节配额，等待最多一百毫秒后重读限制，避免限速修改或取消迟迟不生效。</summary>
    public async ValueTask WaitAsync(int byteCount, CancellationToken cancellationToken = default)
    {
        if (byteCount < 0) throw new ArgumentOutOfRangeException(nameof(byteCount));
        var remaining = (double)byteCount;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double delay;
            lock (_gate)
            {
                if (_bytesPerSecond == 0) return;
                var now = Stopwatch.GetTimestamp();
                _tokens = Math.Min(Capacity(_bytesPerSecond),
                    _tokens + Stopwatch.GetElapsedTime(_lastRefill, now).TotalSeconds * _bytesPerSecond);
                _lastRefill = now;
                var available = Math.Min(remaining, _tokens);
                _tokens -= available;
                remaining -= available;
                if (remaining <= 0) return;
                delay = Math.Clamp(remaining / _bytesPerSecond * 1000, 1, 100);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>允许十分之一秒的小突发，大文件与多个连接仍共用同一总预算。</summary>
    private static double Capacity(long rate) => rate == 0 ? 0 : Math.Clamp(rate / 10d, 1, 32 * 1024);
}
