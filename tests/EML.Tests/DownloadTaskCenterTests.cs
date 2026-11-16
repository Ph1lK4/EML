using System.Collections.Concurrent;
using System.Diagnostics;
using EML.Core.Downloads;
using EML.Infrastructure.Downloads;

namespace EML.Tests;

/// <summary>用可控的文件传输验证队列顺序、暂停与安装调用生命周期，不写真实实例。</summary>
public sealed class DownloadTaskCenterTests
{
    /// <summary>全局并发约束所有调用，等待任务按优先级及同级提交顺序执行。</summary>
    [Fact]
    public async Task Queue_RespectsConcurrencyPriorityAndFifo()
    {
        var transport = new ControlledDownloader();
        var center = Center(transport, 1);
        var first = center.DownloadAsync(Request("first"));
        await WaitUntil(() => transport.Calls.Count == 1);
        var low = center.DownloadAsync(Request("low", DownloadPriority.Low));
        var highA = center.DownloadAsync(Request("high-a", DownloadPriority.High));
        var highB = center.DownloadAsync(Request("high-b", DownloadPriority.High));
        Assert.Single(transport.Calls);
        transport.Release("first"); await first;
        await WaitUntil(() => transport.Calls.Count == 2);
        Assert.Equal("high-a", transport.Calls.Last());
        transport.Release("high-a"); await highA;
        await WaitUntil(() => transport.Calls.Count == 3);
        Assert.Equal("high-b", transport.Calls.Last());
        transport.Release("high-b"); await highB;
        await WaitUntil(() => transport.Calls.Count == 4);
        transport.Release("low"); await low;
        Assert.Equal(1, transport.MaximumActive);
        await center.StopAsync();
    }

    /// <summary>单文件暂停释放名额但不完成原调用，恢复后还是同一个任务记录。</summary>
    [Fact]
    public async Task Pause_ReleasesSlotAndResumeKeepsOriginalTask()
    {
        var transport = new ControlledDownloader();
        var center = Center(transport, 1);
        var first = center.DownloadAsync(Request("first"));
        await WaitUntil(() => transport.Calls.Count == 1);
        var firstId = center.GetSnapshot().Single().Id;
        Assert.True(center.Pause(firstId));
        await WaitUntil(() => transport.Active == 0);
        Assert.False(first.IsCompleted);
        var second = center.DownloadAsync(Request("second"));
        await WaitUntil(() => transport.Calls.Count == 2);
        transport.Release("second"); await second;
        Assert.True(center.Resume(firstId));
        await WaitUntil(() => transport.Calls.Count == 3);
        transport.Release("first"); await first;
        Assert.Equal(2, center.GetSnapshot().Count);
        Assert.All(center.GetSnapshot(), row => Assert.Equal(DownloadTaskState.Completed, row.State));
        await center.StopAsync();
    }

    /// <summary>快速暂停再继续时，旧取消令牌完成只会重新排队，不会将任务变为失败。</summary>
    [Fact]
    public async Task RapidPauseResume_RequeuesCanceledAttempt()
    {
        var transport = new ControlledDownloader();
        var center = Center(transport, 1);
        var task = center.DownloadAsync(Request("first"));
        await WaitUntil(() => transport.Calls.Count == 1);
        var id = center.GetSnapshot().Single().Id;
        center.Pause(id); center.Resume(id);
        await WaitUntil(() => transport.Calls.Count == 2);
        transport.Release("first"); await task;
        Assert.Equal(DownloadTaskState.Completed, center.GetSnapshot().Single().State);
        await center.StopAsync();
    }

    /// <summary>全局暂停影响后续到达的文件，恢复之前不会偷偷发起网络请求。</summary>
    [Fact]
    public async Task PauseAll_AlsoHoldsNewFiles()
    {
        var transport = new ControlledDownloader();
        var center = Center(transport, 2);
        center.PauseAll();
        var first = center.DownloadAsync(Request("first"));
        var second = center.DownloadAsync(Request("second"));
        Assert.Empty(transport.Calls);
        Assert.All(center.GetSnapshot(), row => Assert.Equal(DownloadTaskState.Paused, row.State));
        center.ResumeAll();
        await WaitUntil(() => transport.Calls.Count == 2);
        transport.Release("first"); transport.Release("second");
        await Task.WhenAll(first, second);
        Assert.Equal(2, transport.MaximumActive);
        await center.StopAsync();
    }

    /// <summary>修改等待任务优先级即时改变后续执行顺序，不中断当前文件。</summary>
    [Fact]
    public async Task PriorityChange_ReordersOnlyQueuedFiles()
    {
        var transport = new ControlledDownloader();
        var center = Center(transport, 1);
        var first = center.DownloadAsync(Request("first"));
        await WaitUntil(() => transport.Calls.Count == 1);
        var second = center.DownloadAsync(Request("second"));
        var third = center.DownloadAsync(Request("third"));
        var thirdId = center.GetSnapshot().Single(row => row.DisplayName == "third").Id;
        Assert.True(center.SetPriority(thirdId, DownloadPriority.High));
        Assert.Single(transport.Calls);
        transport.Release("first"); await first;
        await WaitUntil(() => transport.Calls.Count == 2);
        Assert.Equal("third", transport.Calls.Last());
        transport.Release("third"); await third;
        await WaitUntil(() => transport.Calls.Count == 3);
        transport.Release("second"); await second;
        await center.StopAsync();
    }

    /// <summary>队列取消会取消原安装调用，清理历史不会删除目标或缓存文件。</summary>
    [Fact]
    public async Task CancelQueued_DoesNotStartTransportAndCanClearHistory()
    {
        var transport = new ControlledDownloader();
        var center = Center(transport, 1);
        center.PauseAll();
        var task = center.DownloadAsync(Request("first"));
        Assert.True(center.Cancel(center.GetSnapshot().Single().Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(transport.Calls);
        center.ClearFinished();
        Assert.Empty(center.GetSnapshot());
        await center.StopAsync();
    }

    /// <summary>调用方取消暂停任务也能退出，避免关窗等待永久挂起。</summary>
    [Fact]
    public async Task CallerCancellation_EndsPausedTask()
    {
        var transport = new ControlledDownloader();
        var center = Center(transport, 1);
        center.PauseAll();
        using var cancellation = new CancellationTokenSource();
        var task = center.DownloadAsync(Request("first"), cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(DownloadTaskState.Canceled, center.GetSnapshot().Single().State);
        await center.StopAsync();
    }

    /// <summary>关闭中心取消队列与活动请求，并等待活动传输关闭后才返回。</summary>
    [Fact]
    public async Task Stop_CancelsRunningAndQueuedAndRejectsNewTasks()
    {
        var transport = new ControlledDownloader();
        var center = Center(transport, 1);
        var running = center.DownloadAsync(Request("first"));
        var queued = center.DownloadAsync(Request("second"));
        await WaitUntil(() => transport.Calls.Count == 1);
        await center.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(0, transport.Active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => center.DownloadAsync(Request("third")));
    }

    /// <summary>失败不会自动盲目重放原请求，任务列表也不会泄漏异常中的签名 URL。</summary>
    [Fact]
    public async Task Failure_IsTerminalAndRedactsRemoteError()
    {
        var transport = new DelegateDownloader((_, _, _) => throw new IOException("https://example.test/?secret=hidden"));
        var center = Center(transport, 1);
        await Assert.ThrowsAsync<IOException>(() => center.DownloadAsync(Request("first")));
        var row = center.GetSnapshot().Single();
        Assert.Equal(DownloadTaskState.Failed, row.State);
        Assert.DoesNotContain("secret", row.Error);
        Assert.False(center.Resume(row.Id));
        await center.StopAsync();
    }

    /// <summary>已完成的缓存命中与续传字节原样传递，文件完成不改写任何实例就绪状态。</summary>
    [Fact]
    public async Task Progress_ReportsVerificationCacheAndResumedBytes()
    {
        var states = new ConcurrentQueue<DownloadTaskState>();
        var transport = new DelegateDownloader((request, progress, _) =>
        {
            progress?.Report(new(5, 10, 1) { Phase = FileDownloadPhase.Verifying, FromCache = true, ResumedBytes = 5 });
            return Task.FromResult(new FileDownloadResult(request.DestinationPath, 10, true) { CacheHit = true, ResumedBytes = 5 });
        });
        var center = Center(transport, 1);
        center.Changed += (_, _) => { foreach (var row in center.GetSnapshot()) states.Enqueue(row.State); };
        var result = await center.DownloadAsync(Request("first"));
        var snapshot = center.GetSnapshot().Single();
        Assert.True(result.CacheHit);
        Assert.Contains(DownloadTaskState.Verifying, states);
        Assert.True(snapshot.FromCache);
        Assert.Equal(5, snapshot.ResumedBytes);
        Assert.Equal(0, snapshot.BytesPerSecond);
        await center.StopAsync();
    }

    /// <summary>一次提交后外部修改 URL 或摘要集合不会改变队列中的请求。</summary>
    [Fact]
    public async Task Queue_SnapshotsMutableRequestCollections()
    {
        var urls = new List<string> { "https://example.test/original" };
        var hashes = new Dictionary<string, string> { ["sha1"] = new string('a', 40) };
        FileDownloadRequest? observed = null;
        var center = Center(new DelegateDownloader((request, _, _) =>
        {
            observed = request;
            return Task.FromResult(new FileDownloadResult(request.DestinationPath, 1, true));
        }), 1);
        center.PauseAll();
        var task = center.DownloadAsync(Request("first") with { Urls = urls, Hashes = hashes });
        urls[0] = "https://example.test/changed"; hashes["sha1"] = new string('b', 40);
        center.ResumeAll(); await task;
        Assert.Equal("https://example.test/original", observed!.Urls.Single());
        Assert.Equal(new string('a', 40), observed.Hashes["sha1"]);
        await center.StopAsync();
    }

    /// <summary>设置跨中心重建保留，非法值不会污染当前限制。</summary>
    [Fact]
    public async Task Settings_PersistAndRejectInvalidValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eml-center-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var limiter = new DownloadBandwidthLimiter();
            var center = new DownloadTaskCenter(new ControlledDownloader(), limiter, path);
            center.UpdateSettings(new() { MaxConcurrentDownloads = 5, BytesPerSecond = 65536 });
            Assert.Equal(65536, limiter.BytesPerSecond);
            Assert.Throws<ArgumentOutOfRangeException>(() => center.UpdateSettings(new() { MaxConcurrentDownloads = 0 }));
            var next = new DownloadTaskCenter(new ControlledDownloader(), new(), path);
            Assert.Equal(center.Settings, next.Settings);
            await center.StopAsync(); await next.StopAsync();
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>全局桶同时限制多条连接，等待期间取消立即生效，调成不限速无需重启下载。</summary>
    [Fact]
    public async Task BandwidthLimit_IsSharedAndCanBeChangedOrCanceled()
    {
        var limiter = new DownloadBandwidthLimiter();
        limiter.SetLimit(10_000);
        var timer = Stopwatch.StartNew();
        await Task.WhenAll(limiter.WaitAsync(2000).AsTask(), limiter.WaitAsync(2000).AsTask());
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(220));
        using var cancellation = new CancellationTokenSource();
        var canceled = limiter.WaitAsync(1_000_000, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        var changed = limiter.WaitAsync(1_000_000).AsTask();
        limiter.SetLimit(0);
        await changed.WaitAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>即使界面监听器抛异常也不影响下载文件的真实结果。</summary>
    [Fact]
    public async Task ObserverFailure_DoesNotFailDownload()
    {
        var center = Center(new DelegateDownloader((request, _, _) => Task.FromResult(new FileDownloadResult(request.DestinationPath, 1, true))), 1);
        center.Changed += (_, _) => throw new InvalidOperationException("UI error");
        Assert.True((await center.DownloadAsync(Request("first"))).HashVerified);
        await center.StopAsync();
    }

    /// <summary>设置单个测试所需的全局并发度。</summary>
    private static DownloadTaskCenter Center(IFileDownloader downloader, int concurrent)
    {
        var center = new DownloadTaskCenter(downloader, new());
        center.UpdateSettings(new() { MaxConcurrentDownloads = concurrent });
        return center;
    }

    /// <summary>测试下载目标只有字符串身份，假传输器不会访问该路径。</summary>
    private static FileDownloadRequest Request(string name, DownloadPriority priority = DownloadPriority.Normal) => new()
    {
        DestinationPath = Path.Combine(Path.GetTempPath(), name + ".jar"), DisplayName = name,
        Urls = ["https://example.test/file"], Priority = priority
    };

    /// <summary>有界等待受控异步操作开始，不靠固定长延迟猜测状态。</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    /// <summary>测试可决定每个文件何时完成，取消时会在返回前释放活动名额。</summary>
    private sealed class ControlledDownloader : IFileDownloader
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _releases = new();
        private int _active;
        private int _maximum;
        public ConcurrentQueue<string> Calls { get; } = new();
        public int Active => Volatile.Read(ref _active);
        public int MaximumActive => Volatile.Read(ref _maximum);

        public async Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var name = request.DisplayName!;
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _releases[name] = signal;
            var active = Interlocked.Increment(ref _active);
            int old;
            do { old = _maximum; if (old >= active) break; } while (Interlocked.CompareExchange(ref _maximum, active, old) != old);
            Calls.Enqueue(name);
            try
            {
                await signal.Task.WaitAsync(cancellationToken);
                return new(request.DestinationPath, 1, true);
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public void Release(string name) => _releases[name].TrySetResult();
    }

    /// <summary>为单场景注入传输行为。</summary>
    private sealed class DelegateDownloader(Func<FileDownloadRequest, IProgress<FileDownloadProgress>?, CancellationToken, Task<FileDownloadResult>> action) : IFileDownloader
    {
        public Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) => action(request, progress, cancellationToken);
    }
}
