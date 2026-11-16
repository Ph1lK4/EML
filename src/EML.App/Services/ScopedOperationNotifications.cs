namespace EML.App.Services;

/// <summary>初始化只保留页面状态；按异步调用链屏蔽结果弹窗，不影响同时进行的手动操作。</summary>
public sealed class ScopedOperationNotifications(IOperationNotifications inner) : IOperationNotifications
{
    private readonly AsyncLocal<int> _initializationDepth = new();

    /// <summary>在提交时判断来源，初始化通知不入队，也不会在初始化结束后补弹。</summary>
    public void Report(OperationNotice notice)
    {
        if (_initializationDepth.Value == 0) inner.Report(notice);
    }

    /// <summary>同步初始化也可以静默，例如下载中心构造时读取现有队列。</summary>
    public T RunInitialization<T>(Func<T> initialize)
    {
        var previous = _initializationDepth.Value;
        _initializationDepth.Value = previous + 1;
        try { return initialize(); }
        finally { _initializationDepth.Value = previous; }
    }

    /// <summary>静默范围随 await 和派生读取任务传播，异常时恢复；不吞掉错误或取消。</summary>
    public async Task RunInitializationAsync(Func<Task> initialize)
    {
        var previous = _initializationDepth.Value;
        _initializationDepth.Value = previous + 1;
        try { await initialize(); }
        finally { _initializationDepth.Value = previous; }
    }
}
