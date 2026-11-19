using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Media;
using EML.App.Views;

namespace EML.App.Services;

internal enum DialogTone { Information, Success, Warning, Danger }

/// <summary>统一模态窗口外观；系统仍负责移动、缩放和模态所有权，按钮只返回用户选择。</summary>
internal class DialogWindow : Window
{
    private readonly ResourceDictionary _highContrastResources = new();
    private bool _watchingSystemColors;
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
        RefreshHighContrastResources();
        ConfigureTone(tone);
        Content = Surface;
        Surface.CloseRequested += (_, _) => Close();
        CancelButton = new Button { Content = "取消", IsCancel = true, IsDefault = true, MinWidth = 88, Margin = new(0, 0, 8, 0) };
        Actions.Children.Add(CancelButton);
        FocusTarget = CancelButton;
        // 位移仅属于对话框内容；系统窗口、标题栏和模态确认不等待动画。
        Surface.RenderTransform = new TranslateTransform();
        UiMotion.SetEnterDistance(Surface, 8);
        UiMotion.SetEnter(Surface, true);
        ContentRendered += OnContentRendered;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Closed += OnClosed;
    }

    /// <summary>设置文字和图标的强调色；危险状态同时用明确按钮文案表达，不只依赖颜色。</summary>
    private void ConfigureTone(DialogTone tone)
    {
        Surface.ToneIcon.Text = tone switch
        {
            DialogTone.Success => "\uE73E", DialogTone.Danger => "\uEA39", DialogTone.Warning => "\uE7BA", _ => "\uE946"
        };
        // 使用共享语义色，成功、警告和失败不再与主操作共用金色或写死浅色底。
        var (background, foreground) = tone switch
        {
            DialogTone.Success => ("SuccessSoftBrush", "SuccessBrush"),
            DialogTone.Warning => ("WarningSoftBrush", "WarningBrush"),
            DialogTone.Danger => ("DangerSoftBrush", "DangerBrush"),
            _ => ("InfoSoftBrush", "SecondaryAccentBrush")
        };
        Surface.ToneTile.SetResourceReference(Border.BackgroundProperty, background);
        Surface.ToneIcon.SetResourceReference(TextBlock.ForegroundProperty, foreground);
    }

    /// <summary>高对比度仅临时覆盖独立资源字典，退出后重新继承当前的全局主题。</summary>
    private void RefreshHighContrastResources()
    {
        if (!SystemParameters.HighContrast)
        {
            Surface.Resources.MergedDictionaries.Remove(_highContrastResources);
            _highContrastResources.Clear();
            return;
        }
        foreach (var (key, color) in AppearancePalette.Create(false, highContrast: true))
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _highContrastResources[key] = brush;
        }
        _highContrastResources["WallpaperPanelBrush"] = SystemColors.WindowBrush;
        if (!Surface.Resources.MergedDictionaries.Contains(_highContrastResources))
            Surface.Resources.MergedDictionaries.Add(_highContrastResources);
    }

    /// <summary>只在弹窗显示期间监听静态系统事件，未显示的临时弹窗不会被事件长期引用。</summary>
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!_watchingSystemColors)
        {
            SystemParameters.StaticPropertyChanged += OnSystemColorsChanged;
            _watchingSystemColors = true;
        }
        RefreshHighContrastResources();
    }

    /// <summary>卸载或关闭时对称解除静态监听，重新显示时由 Loaded 恢复。</summary>
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (!_watchingSystemColors) return;
        SystemParameters.StaticPropertyChanged -= OnSystemColorsChanged;
        _watchingSystemColors = false;
    }

    /// <summary>系统通知可能来自其他线程，已开弹窗在自己的 UI 线程切换高对比度。</summary>
    private void OnSystemColorsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SystemParameters.HighContrast) || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() => { if (_watchingSystemColors) RefreshHighContrastResources(); }));
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
        OnUnloaded(this, new RoutedEventArgs());
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ContentRendered -= OnContentRendered;
        UiMotion.SetEnter(Surface, false);
    }
}
