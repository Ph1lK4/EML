using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace EML.App.Services;

/// <summary>完整操作的结果弹窗：成功与失败有明确标识，说明和路径均可复制。</summary>
internal sealed class OperationResultDialog : DialogWindow
{
    private readonly Action<string> _copy;
    private readonly TextBlock _copyStatus;

    /// <summary>构造可离屏测试的结果内容；剪贴板仅在用户点击复制时写入。</summary>
    internal OperationResultDialog(OperationNotice notice, Action<string>? copy = null)
        : base(notice.Title, ResultLabel(notice.Kind), width: 660, height: notice.Paths.Count > 0 ? 560 : 420,
            tone: ResultTone(notice.Kind))
    {
        _copy = copy ?? Clipboard.SetText;
        var message = SelectableText(notice.Message, notice.Kind == OperationNoticeKind.Failure ? "失败原因，可选中复制" : "操作结果，可选中复制", 220);
        Body.Children.Add(message);
        // 批量校验可能产生上万条路径；限制可视控件数量，全部内容仍可一键复制。
        foreach (var path in notice.Paths.Take(20)) Body.Children.Add(PathCard(path));
        if (notice.Paths.Count > 20)
            Body.Children.Add(DialogPrompt.Text($"共有 {notice.Paths.Count} 条相关路径，当前展示前 20 条。点击“复制全部详情”可获取全部路径。"));
        _copyStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
        AutomationProperties.SetLiveSetting(_copyStatus, AutomationLiveSetting.Polite);
        Body.Children.Add(_copyStatus);

        CancelButton.Content = "知道了";
        AutomationProperties.SetName(CancelButton, "关闭操作结果");
        var copyAll = new Button { Content = "复制全部详情", MinWidth = 112, Margin = new(0, 0, 8, 0) };
        copyAll.Click += (_, _) => Copy(FormatDetails(notice));
        Actions.Children.Insert(0, copyAll);
    }

    /// <summary>保留完整说明和精确路径，便于用户粘贴诊断信息。</summary>
    internal static string FormatDetails(OperationNotice notice) =>
        $"{notice.Title}（{ResultLabel(notice.Kind)}）\n\n{notice.Message}" +
        (notice.Paths.Count == 0 ? "" : "\n\n相关路径：\n" + string.Join("\n", notice.Paths));

    /// <summary>一条路径配一个独立复制按钮，长中文路径在卡片内换行。</summary>
    private Border PathCard(string path)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "相关路径", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 6) });
        panel.Children.Add(SelectableText(path, "相关路径，可选中复制", 120));
        var button = new Button { Content = "复制路径", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 90 };
        AutomationProperties.SetName(button, "复制路径：" + path);
        button.Click += (_, _) => Copy(path);
        panel.Children.Add(button);
        var card = new Border { Child = panel, Padding = new(14), CornerRadius = new(10), Margin = new(0, 12, 0, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "WindowBackgroundBrush");
        return card;
    }

    /// <summary>说明和路径使用只读文本框，支持 Ctrl+C，保留滚动和选中能力。</summary>
    private static TextBox SelectableText(string value, string accessibleName, double maxHeight)
    {
        var text = new TextBox
        {
            Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new(0),
            Background = System.Windows.Media.Brushes.Transparent, Padding = new(4), MinHeight = 30,
            MaxHeight = maxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 0, 0, 8)
        };
        AutomationProperties.SetName(text, accessibleName);
        return text;
    }

    /// <summary>剪贴板被占用时保留当前弹窗并提示手动复制，不递归弹出错误窗口。</summary>
    private void Copy(string text)
    {
        try { _copy(text); _copyStatus.Text = "已复制。"; }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or SecurityException)
        { _copyStatus.Text = "暂时无法写入剪贴板，请选中文字后按 Ctrl+C，或稍后重试。"; }
    }

    /// <summary>以文字明确区分最终状态，避免只靠图标颜色传达结果。</summary>
    private static string ResultLabel(OperationNoticeKind kind) => kind switch
    {
        OperationNoticeKind.Success => "操作成功", OperationNoticeKind.Failure => "操作失败",
        OperationNoticeKind.Warning => "请查看以下待处理事项", _ => "操作已取消"
    };

    /// <summary>将业务结果映射到已有主题弹窗的强调色和图标。</summary>
    private static DialogTone ResultTone(OperationNoticeKind kind) => kind switch
    {
        OperationNoticeKind.Success => DialogTone.Success, OperationNoticeKind.Failure => DialogTone.Danger,
        OperationNoticeKind.Warning => DialogTone.Warning, _ => DialogTone.Information
    };
}
