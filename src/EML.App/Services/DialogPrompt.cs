using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace EML.App.Services;

/// <summary>应用内提示和确认统一使用主题窗口，系统文件选择器仍使用原生对话框。</summary>
internal static class DialogPrompt
{
    /// <summary>仅明确按确认返回 true；关闭、Esc、默认 Enter 和取消均不会授权操作。</summary>
    internal static bool Confirm(Window owner, string title, string message, string confirmText,
        DialogTone tone = DialogTone.Warning, string? subject = null, string? detail = null)
    {
        var window = CreateConfirmation(title, message, confirmText, tone, subject, detail);
        window.Owner = owner;
        return window.ShowDialog() == true;
    }

    /// <summary>构造可测试的确认内容，不显示窗口，也不访问实例或账号。</summary>
    internal static DialogWindow CreateConfirmation(string title, string message, string confirmText,
        DialogTone tone = DialogTone.Warning, string? subject = null, string? detail = null)
    {
        var window = new DialogWindow(title, "请确认操作对象和影响范围", height: 450, tone: tone);
        if (subject is not null) window.Body.Children.Add(DetailCard(subject, detail ?? ""));
        else if (detail is not null) window.Body.Children.Add(DetailCard("操作位置", detail));
        window.Body.Children.Add(Text(message));
        var accept = window.AddAccept(confirmText);
        if (tone == DialogTone.Danger) accept.SetResourceReference(FrameworkElement.StyleProperty, "DangerButton");
        return window;
    }

    /// <summary>信息提示只有关闭动作，Enter 和 Esc 均可返回原窗口。</summary>
    internal static void Inform(Window owner, string title, string message)
    {
        var window = new DialogWindow(title, "", height: 340) { Owner = owner };
        window.Body.Children.Add(Text(message));
        window.CancelButton.Content = "知道了";
        window.ShowDialog();
    }

    /// <summary>统一正文行距；长路径和提示均按可用宽度换行。</summary>
    internal static TextBlock Text(string value) => new()
    {
        Text = value, TextWrapping = TextWrapping.Wrap, LineHeight = 24, Margin = new(0, 0, 0, 10)
    };

    /// <summary>用独立卡片显示对象与只读可复制路径，避免路径混在警告段落中。</summary>
    internal static Border DetailCard(string label, string value)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6) });
        if (value.Length > 0)
        {
            var path = new TextBox
            {
                Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new(0),
                Padding = new(0), MinHeight = 0, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 120
            };
            path.SetResourceReference(Control.ForegroundProperty, "MutedTextBrush");
            AutomationProperties.SetName(path, label + "，可复制详情");
            content.Children.Add(path);
        }
        var card = new Border { Child = content, Padding = new(16), CornerRadius = new(10), Margin = new(0, 0, 0, 18) };
        card.SetResourceReference(Border.BackgroundProperty, "WindowBackgroundBrush");
        return card;
    }
}
