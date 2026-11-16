using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EML.App.Views;

/// <summary>保留工作区的页面索引绑定，但把无标签栏的当前页面作为无障碍分组呈现。</summary>
public sealed class WorkspaceTabControl : TabControl
{
    /// <summary>普通 TabControl 的 peer 依赖标签容器；无标签模板应直接公开当前可视内容。</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new WorkspaceAutomationPeer(this);

    /// <summary>切换工作区后刷新已存在的自动化树，不让辅助工具继续使用上一页的控件缓存。</summary>
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (ReferenceEquals(e.OriginalSource, this) && UIElementAutomationPeer.FromElement(this) is { } peer)
        {
            // SelectedContent 先变化，可视子树在随后的绑定/布局阶段才换页。
            // InvalidatePeer 主要刷新属性，不能独自清掉已经查询过的子节点缓存。
            _ = Dispatcher.InvokeAsync(() =>
            {
                peer.ResetChildrenCache();
                peer.InvalidatePeer();
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>复用 WPF 的可视子树遍历；不公开不可见标签或额外的标签选择操作。</summary>
    private sealed class WorkspaceAutomationPeer(WorkspaceTabControl owner) : FrameworkElementAutomationPeer(owner)
    {
        /// <summary>工作区是当前功能的容器，而不是用户可操作的标签栏。</summary>
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        /// <summary>提供稳定的控件类型名称，便于辅助工具识别工作区。</summary>
        protected override string GetClassNameCore() => nameof(WorkspaceTabControl);

        /// <summary>以当前功能名称标识分组；外部显式设置的无障碍名称仍然优先。</summary>
        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return !string.IsNullOrWhiteSpace(name) ? name
                : owner.SelectedItem is TabItem { Header: string header } ? header : "实例工作区";
        }
    }
}
