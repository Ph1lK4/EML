using System.Collections.Concurrent;
using EML.App.Services;
using EML.Core.Minecraft.Launching;

namespace EML.Tests;

/// <summary>暂停真实排队上下文验证 UI 背压，避免同步 Post 替身掩盖无限排队问题。</summary>
public sealed class BoundedLaunchProgressTests
{
    /// <summary>十万条日志只保留有限尾部且只排一个 UI 回调，运行阶段不会被日志覆盖。</summary>
    [Fact]
    public async Task PausedUiKeepsOneCallbackAndBoundedOutputWithoutDroppingRunning()
    {
        var context = new PausedContext();
        var delivered = new List<MinecraftLaunchProgress>();
        using var progress = new BoundedLaunchProgress(delivered.Add, context);
        progress.Report(new(MinecraftLaunchStage.Preparing, "prepare"));
        await context.WaitForPostAsync();
        for (var index = 0; index < 100_000; index++)
            progress.Report(new(MinecraftLaunchStage.Output, "line-" + index));
        progress.Report(new(MinecraftLaunchStage.Running, "running", LogPath: "game.log"));
        progress.Report(new(MinecraftLaunchStage.Preparing, "stale preparing"));
        await Task.Delay(150);

        Assert.Empty(delivered);
        Assert.Equal(1, context.PendingCount);
        context.RunOne();
        Assert.Equal(MinecraftLaunchStage.Running, delivered[0].Stage);
        Assert.Equal("game.log", delivered[0].LogPath);
        Assert.Equal(100, delivered.Count(value => value.Stage == MinecraftLaunchStage.Output));
        Assert.DoesNotContain(delivered, value => value.Stage == MinecraftLaunchStage.Preparing);
        progress.Complete();
        var output = delivered.Where(value => value.Stage == MinecraftLaunchStage.Output).ToArray();
        Assert.Equal(BoundedLaunchProgress.MaximumPendingOutput, output.Length);
        Assert.Equal("line-99000", output[0].Message);
        Assert.Equal("line-99999", output[^1].Message);
        Assert.Single(delivered, value => value.Stage == MinecraftLaunchStage.Running);
    }

    /// <summary>进程结束同步显示有限尾部后，早已排队或随后到来的状态均不能再次更新界面。</summary>
    [Fact]
    public async Task CompleteFlushesTailAndInvalidatesQueuedAndLateCallbacks()
    {
        var context = new PausedContext();
        var delivered = new List<MinecraftLaunchProgress>();
        using var progress = new BoundedLaunchProgress(delivered.Add, context);
        progress.Report(new(MinecraftLaunchStage.Running, "running"));
        progress.Report(new(MinecraftLaunchStage.Output, "last failure", true));
        await context.WaitForPostAsync();

        progress.Complete();

        Assert.Equal(2, delivered.Count);
        Assert.Equal("last failure", delivered[^1].Message);
        progress.Report(new(MinecraftLaunchStage.Running, "late running"));
        progress.Report(new(MinecraftLaunchStage.Output, "late output"));
        context.RunOne();
        await Task.Delay(100);
        Assert.Equal(2, delivered.Count);
        Assert.Equal(0, context.PendingCount);
    }

    /// <summary>释放窗口的缓冲后，队列中的回调不会访问旧界面，定时器也停止投递。</summary>
    [Fact]
    public async Task DisposeDropsPendingEventsWithoutTouchingReceiver()
    {
        var context = new PausedContext();
        var delivered = new List<MinecraftLaunchProgress>();
        var progress = new BoundedLaunchProgress(delivered.Add, context);
        progress.Report(new(MinecraftLaunchStage.Output, "pending"));
        await context.WaitForPostAsync();

        progress.Dispose();
        context.RunOne();
        progress.Report(new(MinecraftLaunchStage.Output, "late"));
        await Task.Delay(100);

        Assert.Empty(delivered);
        Assert.Equal(0, context.PendingCount);
    }

    /// <summary>单条输出也有上限，显示接收器出错不会中止剩余有限日志的处理。</summary>
    [Fact]
    public void LongMessagesAndReceiverFailuresRemainBounded()
    {
        var delivered = new List<MinecraftLaunchProgress>();
        using var progress = new BoundedLaunchProgress(value =>
        {
            if (value.Message == "fail") throw new InvalidOperationException("display failure");
            delivered.Add(value);
        }, new PausedContext());
        progress.Report(new(MinecraftLaunchStage.Output, "fail"));
        progress.Report(new(MinecraftLaunchStage.Output, new string('x', 1_000_000)));
        progress.Complete();
        var result = Assert.Single(delivered);
        Assert.InRange(result.Message.Length, 16384, 16450);
        Assert.Contains("完整内容见日志文件", result.Message);
    }

    /// <summary>模拟尚未处理消息的 UI，仅由测试明确调用 RunOne 执行调度项。</summary>
    private sealed class PausedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private readonly TaskCompletionSource _firstPost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PendingCount => _callbacks.Count;

        /// <summary>只排队而不立即执行，准确暴露生产 Dispatcher 的积压行为。</summary>
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callbacks.Enqueue((callback, state));
            _firstPost.TrySetResult();
        }

        /// <summary>等待真实定时调度，无需任意长休眠或调用私有实现。</summary>
        internal Task WaitForPostAsync() => _firstPost.Task.WaitAsync(TimeSpan.FromSeconds(5));

        /// <summary>模拟 UI 消费一个回调，不自动清空后续批次。</summary>
        internal void RunOne()
        {
            Assert.True(_callbacks.TryDequeue(out var item));
            item.Callback(item.State);
        }
    }
}
