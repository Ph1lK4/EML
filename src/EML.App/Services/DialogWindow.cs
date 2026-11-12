using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using EML.App.Views;

namespace EML.App.Services;

internal enum DialogTone { Information, Success, Warning, Danger }

/// <summary>统一模态窗口外观；系统仍负责移动、缩放和模态所有权，按钮只返回用户选择。</summary>
internal class DialogWindow : Window
{
    internal DialogSurface Surface { get; }
    internal StackPanel Body => Surface.BodyPanel;
    internal WrapPanel Actions => Surface.FooterActions;
    internal Button CancelButton { get; }
    internal FrameworkElement? FocusTarget { get; set; }

    /// <summary>在工作区内限制尺寸，长正文单独滚动，避免底部确认按钮移出屏幕。</summary>
    internal DialogWindow(string title, string subtitle = "", double width = 600, double height = 460,
        DialogTone tone = DialogTone.Information)
    {
        Title = title;
        SetResourceReference(StyleProperty, typeof(Window));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        var area = SystemParameters.WorkArea;
        MaxWidth = Math.Max(1, area.Width - 24); MaxHeight = Math.Max(1, area.Height - 24);
        Width = Math.Min(width, MaxWidth); Height = Math.Min(height, MaxHeight);
        MinWidth = Math.Min(440, Width); MinHeight = Math.Min(300, Height);
        // 保留系统非客户区行为，无透明分层窗口；标题区域可拖动，关闭按钮明确参与命中测试。
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 44, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(12), UseAeroCaptionButtons = false
        });
        Surface = new DialogSurface();
        Surface.HeadingText.Text = title;
        Surface.SubtitleText.Text = subtitle;
        Surface.SubtitleText.Visibility = subtitle.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ConfigureTone(tone);
        Content = Surface;
        Surface.CloseRequested += (_, _) => Close();
        CancelButton = new Button { Content = "取消", IsCancel = true, IsDefault = true, MinWidth = 88, Margin = new(0, 0, 8, 0) };
        Actions.Children.Add(CancelButton);
        FocusTarget = CancelButton;
        UiMotion.SetEnter(Surface, true);
        ContentRendered += OnContentRendered;
        Closed += OnClosed;
    }

    /// <summary>设置文字和图标的强调色；危险状态同时用明确按钮文案表达，不只依赖颜色。</summary>
    private void ConfigureTone(DialogTone tone)
    {
        if (SystemParameters.HighContrast)
        {
            Surface.Resources["SurfaceBrush"] = SystemColors.WindowBrush;
            Surface.Resources["WindowBackgroundBrush"] = SystemColors.WindowBrush;
            Surface.Resources["TextBrush"] = SystemColors.WindowTextBrush;
            Surface.Resources["MutedTextBrush"] = SystemColors.WindowTextBrush;
            Surface.Resources["BorderBrush"] = SystemColors.WindowTextBrush;
            Surface.Resources["AccentBrush"] = SystemColors.HighlightBrush;
            Surface.Resources["AccentSoftBrush"] = SystemColors.WindowBrush;
            Surface.Resources["DangerBrush"] = SystemColors.WindowTextBrush;
        }
        Surface.ToneIcon.Text = tone switch
        {
            DialogTone.Success => "\uE73E", DialogTone.Danger => "\uEA39", DialogTone.Warning => "\uE7BA", _ => "\uE946"
        };
        if (tone == DialogTone.Success) return;
        if (tone == DialogTone.Information || SystemParameters.HighContrast) return;
        Surface.ToneTile.Background = new SolidColorBrush(tone == DialogTone.Danger ? Color.FromRgb(255, 240, 241) : Color.FromRgb(255, 246, 229));
        Surface.ToneIcon.Foreground = new SolidColorBrush(tone == DialogTone.Danger ? Color.FromRgb(174, 59, 66) : Color.FromRgb(143, 91, 20));
    }

    /// <summary>仅输入类确认显式使用 Enter；其余确认保留取消作为默认操作。</summary>
    internal Button AddAccept(string caption, bool isDefault = false)
    {
        var button = new Button { Content = caption, MinWidth = 104, IsDefault = isDefault };
        button.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetName(button, caption);
        if (isDefault) CancelButton.IsDefault = false;
        button.Click += (_, _) => DialogResult = true;
        Actions.Children.Add(button);
        return button;
    }

    /// <summary>窗口首次显示时聚焦安全默认项或输入框；不抢夺用户后续选择。</summary>
    private void OnContentRendered(object? sender, EventArgs args)
    {
        ContentRendered -= OnContentRendered;
        if (FocusTarget is { IsEnabled: true, IsVisible: true } target) Keyboard.Focus(target);
    }

    /// <summary>关闭时停止短动画，确认结果不依赖动画完成事件。</summary>
    private void OnClosed(object? sender, EventArgs args)
    {
        ContentRendered -= OnContentRendered;
        UiMotion.SetEnter(Surface, false);
    }
}
