using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EML.App.Services;

namespace EML.App.Tests;

/// <summary>验证真实结果弹窗的可复制路径、状态区分和长说明布局，不操作系统剪贴板。</summary>
internal static class OperationResultDialogChecks
{
    /// <summary>在现有 STA 应用中检查四类结果，复制成功、剪贴板占用和小窗口滚动。</summary>
    internal static void Run(Action<DialogWindow, string> render)
    {
        foreach (var kind in Enum.GetValues<OperationNoticeKind>()) VerifyResult(kind, render);
        VerifyClipboardFailure();
        VerifyLargePathList();
        VerifyNotificationQueue();
    }

    /// <summary>大量失败路径保留完整复制内容，避免一次创建成千上万个路径控件。</summary>
    private static void VerifyLargePathList()
    {
        var paths = Enumerable.Range(1, 1000).Select(index => $"C:\\实例\\文件-{index}.jar").ToArray();
        var notice = new OperationNotice(OperationNoticeKind.Warning, "文件检查完成", "有文件待处理。", paths);
        string? copied = null;
        var window = new OperationResultDialog(notice, text => copied = text);
        try
        {
            Assert.Equal(20, Descendants(window.Body).OfType<Button>().Count(button => Equals(button.Content, "复制路径")));
            Assert.Single(window.Actions.Children.OfType<Button>(), button => Equals(button.Content, "复制全部详情"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(OperationResultDialog.FormatDetails(notice), copied);
            Assert.Contains(paths[^1], copied);
        }
        finally { window.Close(); }
    }

    /// <summary>跨线程结果按序展示，其他确认框占用时延期，关闭后不再弹出积压结果。</summary>
    private static void VerifyNotificationQueue()
    {
        var owner = new Window();
        var mayShow = false;
        var shown = new List<string>();
        using var queue = new WpfOperationNotifications(owner, () => mayShow, notice => shown.Add(notice.Title));
        try
        {
            Task.Run(() =>
            {
                queue.Report(new(OperationNoticeKind.Success, "先完成", "完成", []));
                queue.Report(new(OperationNoticeKind.Failure, "后失败", "失败", []));
            }).GetAwaiter().GetResult();
            DrainNotifications(); Assert.Empty(shown);
            mayShow = true; owner.IsEnabled = false; owner.IsEnabled = true;
            DrainNotifications(); Assert.Equal(new[] { "先完成", "后失败" }, shown);
            queue.Report(new(OperationNoticeKind.Success, "关闭前的结果", "完成", []));
            queue.Dispose(); DrainNotifications(); Assert.Equal(2, shown.Count);
            queue.Report(new(OperationNoticeKind.Success, "关闭后的结果", "完成", []));
            DrainNotifications(); Assert.Equal(2, shown.Count);
        }
        finally { owner.Close(); }
    }

    /// <summary>在当前 STA 中处理已排队的结果，不显示真实窗口或等待网络。</summary>
    private static void DrainNotifications()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>路径按原字符复制，长正文不会把底部按钮挤出窗口。</summary>
    private static void VerifyResult(OperationNoticeKind kind, Action<DialogWindow, string> render)
    {
        const string path = "C:\\Users\\玩家\\Desktop\\EML\\instances\\\u3000中文实例（带本地数据）\u3000\\.minecraft\\journeymap\\出生点：矿脉.json\u00a0";
        var notice = new OperationNotice(kind, kind == OperationNoticeKind.Failure ? "导入失败" : "实例操作结果",
            kind == OperationNoticeKind.Failure
                ? "包内路径含非法文件名。文件名“中文坐标:矿脉.json”：含 Windows 不允许的字符“:”（U+003A）。\n原始压缩包保持不变。"
                : "实例文件已处理。请查看操作结果，确认是否仍有需要处理的内容。", [path]);
        var copied = new List<string>();
        var window = new OperationResultDialog(notice, copied.Add);
        try
        {
            render(window, "operation-" + kind.ToString().ToLowerInvariant());
            var text = Descendants(window.Body).OfType<TextBox>().ToArray();
            Assert.All(text, box => Assert.True(box.IsReadOnly));
            var pathText = Assert.Single(text, box => box.Text == path);
            pathText.SelectAll();
            Assert.Equal(path, pathText.SelectedText);
            var copyPath = Assert.Single(Descendants(window.Body).OfType<Button>(), button => Equals(button.Content, "复制路径"));
            copyPath.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(path, Assert.Single(copied));
            var copyAll = Assert.Single(window.Actions.Children.OfType<Button>(), button => Equals(button.Content, "复制全部详情"));
            copyAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(OperationResultDialog.FormatDetails(notice), copied[1]);
            Assert.Contains(notice.Message, copied[1]);
            Assert.Contains(path, copied[1]);
            Assert.Equal("知道了", window.CancelButton.Content);
            Assert.True(window.CancelButton.IsDefault);
            Assert.True(window.CancelButton.IsCancel);
            Assert.Null(window.DialogResult);
            Assert.Equal(kind == OperationNoticeKind.Success, window.Surface.SubtitleText.Text == "操作成功");
            Assert.Equal(kind == OperationNoticeKind.Failure, window.Surface.SubtitleText.Text == "操作失败");

            window.Body.Children.Add(DialogPrompt.Text(string.Join("\n", Enumerable.Repeat(path, 20))));
            var surface = window.Surface;
            window.Content = null;
            var host = new Border { Width = 440, Height = 320, Child = surface };
            try
            {
                host.Measure(new Size(440, 320)); host.Arrange(new Rect(0, 0, 440, 320)); host.UpdateLayout();
                Assert.True(surface.BodyScroll.ScrollableHeight > 0);
                var bounds = surface.FooterActions.TransformToAncestor(surface).TransformBounds(new Rect(surface.FooterActions.RenderSize));
                Assert.InRange(bounds.Bottom, 0, 320);
                Assert.InRange(bounds.Right, 0, 440);
            }
            finally { host.Child = null; window.Content = surface; }
        }
        finally { window.Close(); }
    }

    /// <summary>剪贴板被占用时提示仍可选中复制；再次点击可重试，不丢失原始错误。</summary>
    private static void VerifyClipboardFailure()
    {
        const string path = "C:\\实例\\中文包.zip";
        var attempts = 0;
        string? copied = null;
        var window = new OperationResultDialog(new(OperationNoticeKind.Failure, "导入失败", "文件被其他程序占用。", [path]), text =>
        {
            if (++attempts == 1) throw new COMException("Clipboard occupied");
            copied = text;
        });
        try
        {
            var button = Assert.Single(Descendants(window.Body).OfType<Button>(), item => Equals(item.Content, "复制路径"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(copied);
            Assert.Contains(window.Body.Children.OfType<TextBlock>(), item => item.Text.Contains("Ctrl+C"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(path, copied);
            Assert.Contains(window.Body.Children.OfType<TextBlock>(), item => item.Text == "已复制。");
            Assert.Null(window.DialogResult);
        }
        finally { window.Close(); }
    }

    /// <summary>枚举当前弹窗视觉控件，包含路径卡片和按钮。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is Control control) control.ApplyTemplate();
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
