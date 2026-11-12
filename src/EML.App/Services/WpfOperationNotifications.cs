using System.Windows;
using System.Windows.Threading;

namespace EML.App.Services;

/// <summary>在界面线程串行显示操作结果，避免并发任务叠出多个模态窗口。</summary>
internal sealed class WpfOperationNotifications : IOperationNotifications, IDisposable
{
    private readonly Window _owner;
    private readonly Func<bool> _canPresent;
    private readonly Action<OperationNotice> _present;
    private readonly Queue<OperationNotice> _pending = new();
    private bool _scheduled;
    private bool _presenting;
    private int _disposed;

    /// <summary>主窗口恢复可交互时继续显示排队结果，业务命令无需等待用户关闭提示。</summary>
    internal WpfOperationNotifications(Window owner, Func<bool>? canPresent = null, Action<OperationNotice>? present = null)
    {
        _owner = owner;
        _canPresent = canPresent ?? (() => _owner.IsVisible && _owner.IsEnabled && _owner.IsActive &&
            !_owner.OwnedWindows.Cast<Window>().Any(window => window.IsVisible));
        _present = present ?? (notice => new OperationResultDialog(notice) { Owner = _owner }.ShowDialog());
        _owner.Activated += OnOwnerActivated;
        _owner.IsEnabledChanged += OnOwnerEnabledChanged;
        _owner.Closed += OnOwnerClosed;
    }

    /// <summary>跨线程提交结果；窗口关闭后直接忽略，不让通知失败改变文件操作结果。</summary>
    public void Report(OperationNotice notice)
    {
        if (Volatile.Read(ref _disposed) != 0 || _owner.Dispatcher.HasShutdownStarted) return;
        try
        {
            _owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_disposed != 0) return;
                _pending.Enqueue(notice);
                ScheduleNext();
            }));
        }
        catch (InvalidOperationException) when (_owner.Dispatcher.HasShutdownStarted) { }
    }

    /// <summary>只排队一次展示动作，让当前命令先完成状态清理。</summary>
    private void ScheduleNext()
    {
        if (_disposed != 0 || _scheduled || _presenting || _pending.Count == 0 || _owner.Dispatcher.HasShutdownStarted) return;
        _scheduled = true;
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowNext));
    }

    /// <summary>已有确认框时保留结果，待其关闭后再展示；通知之间保持先后顺序。</summary>
    private void ShowNext()
    {
        _scheduled = false;
        if (_disposed != 0 || _presenting || _pending.Count == 0 || !_canPresent()) return;
        _presenting = true;
        try
        {
            _present(_pending.Dequeue());
        }
        finally { _presenting = false; ScheduleNext(); }
    }

    /// <summary>文件选择或确认结束后，主窗口重新激活时处理积压结果。</summary>
    private void OnOwnerActivated(object? sender, EventArgs args) => ScheduleNext();
    /// <summary>界面从忙碌关闭状态恢复时重新判断是否可展示。</summary>
    private void OnOwnerEnabledChanged(object sender, DependencyPropertyChangedEventArgs args) => ScheduleNext();
    /// <summary>主窗口关闭后不再创建结果弹窗。</summary>
    private void OnOwnerClosed(object? sender, EventArgs args) => Dispose();

    /// <summary>解除窗口事件并丢弃关闭期间的待显示结果。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _owner.Activated -= OnOwnerActivated;
        _owner.IsEnabledChanged -= OnOwnerEnabledChanged;
        _owner.Closed -= OnOwnerClosed;
        _pending.Clear();
    }
}
