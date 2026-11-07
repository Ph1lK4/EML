using System.Diagnostics;
using EML.Core.Minecraft.Launching;

namespace EML.App.Services;

/// <summary>将进程日志转换为有界的 UI 批次；磁盘完整日志由启动服务独立保存。</summary>
internal sealed class BoundedLaunchProgress : IProgress<MinecraftLaunchProgress>, IDisposable
{
    internal const int MaximumPendingOutput = 1000;
    private const int MaximumMessageLength = 16384;
    private const int BatchSize = 100;
    private readonly object _gate = new();
    private readonly object _deliveryGate = new();
    private readonly Queue<MinecraftLaunchProgress> _output = new();
    private readonly SynchronizationContext _context;
    private readonly Action<MinecraftLaunchProgress> _receive;
    private readonly Timer _timer;
    private MinecraftLaunchProgress? _preparing;
    private MinecraftLaunchProgress? _running;
    private bool _sawRunning;
    private bool _posted;
    private bool _disposed;
    private long _nextPostAt;

    /// <summary>捕获 UI 上下文，固定频率投递，避免日志长期占用 Dispatcher 的普通优先级队列。</summary>
    internal BoundedLaunchProgress(Action<MinecraftLaunchProgress> receive, SynchronizationContext? context = null)
    {
        _receive = receive;
        _context = context ?? SynchronizationContext.Current ?? new SynchronizationContext();
        _timer = new Timer(_ => Schedule(), null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
    }

    /// <summary>只缓冲最新输出；运行状态独立保留，不能被大量日志挤掉。</summary>
    public void Report(MinecraftLaunchProgress value)
    {
        if (value.Message.Length > MaximumMessageLength)
            value = value with { Message = value.Message[..MaximumMessageLength] + "…（完整内容见日志文件）" };
        lock (_gate)
        {
            if (_disposed) return;
            if (value.Stage == MinecraftLaunchStage.Running)
            {
                _running = value;
                _preparing = null;
                _sawRunning = true;
            }
            else if (value.Stage == MinecraftLaunchStage.Preparing)
            {
                if (!_sawRunning) _preparing = value;
            }
            else
            {
                while (_output.Count >= MaximumPendingOutput) _output.Dequeue();
                _output.Enqueue(value);
            }
        }
    }

    /// <summary>即使 UI 暂停，也只允许一个待处理回调；下一批由后续定时 tick 调度。</summary>
    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed || _posted || Environment.TickCount64 < _nextPostAt ||
                _output.Count == 0 && _preparing is null && _running is null) return;
            _posted = true;
        }
        try { _context.Post(_ => DrainBatch(), null); }
        catch (Exception ex)
        {
            lock (_gate) _posted = false;
            Trace.TraceWarning("无法调度游戏日志显示：{0}", ex.Message);
        }
    }

    /// <summary>每次只更新有限行数，处理期间仍视为已投递，防止定时器继续排队。</summary>
    private void DrainBatch()
    {
        try { DeliverPending(BatchSize, complete: false); }
        finally
        {
            lock (_gate)
            {
                _posted = false;
                // 即便某批 UI 更新较慢，也至少留出 50ms 处理输入、取消和窗口关闭。
                _nextPostAt = Environment.TickCount64 + 50;
            }
        }
    }

    /// <summary>调用方在 UI 线程结束启动任务时排空有限尾部日志，并让所有迟到消息失效。</summary>
    internal void Complete() => DeliverPending(MaximumPendingOutput, complete: true);

    /// <summary>供受控 UI 测试或显式刷新排空当前有限缓冲，不影响后续日志。</summary>
    internal void FlushPending() => DeliverPending(MaximumPendingOutput, complete: false);

    /// <summary>原子取出阶段及有限日志，接收回调失败不会使日志消费者或进程失去管理。</summary>
    private void DeliverPending(int maximum, bool complete)
    {
        lock (_deliveryGate)
        {
            MinecraftLaunchProgress[] pending;
            lock (_gate)
            {
                if (_disposed) return;
                var values = new List<MinecraftLaunchProgress>(Math.Min(maximum, _output.Count) + 2);
                if (_preparing is not null) values.Add(_preparing);
                if (_running is not null) values.Add(_running);
                _preparing = null;
                _running = null;
                while (maximum-- > 0 && _output.TryDequeue(out var value)) values.Add(value);
                pending = values.ToArray();
                if (complete) _disposed = true;
            }
            if (complete) _timer.Dispose();
            foreach (var value in pending)
            {
                try { _receive(value); }
                catch (Exception ex) { Trace.TraceWarning("游戏日志显示失败：{0}", ex.Message); }
            }
        }
    }

    /// <summary>释放调度器并丢弃尚未显示的消息，不向关闭后的窗口派发回调。</summary>
    public void Dispose()
    {
        lock (_deliveryGate)
        {
            lock (_gate)
            {
                _disposed = true;
                _output.Clear();
                _preparing = null;
                _running = null;
            }
            _timer.Dispose();
        }
    }
}
