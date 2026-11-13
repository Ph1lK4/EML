using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace EML.App.Services;

/// <summary>仅调整系统标题栏颜色，保留 Windows 原生移动、缩放、贴靠与窗口按钮。</summary>
public static class WindowAppearance
{
    public static readonly DependencyProperty DarkCaptionProperty = DependencyProperty.RegisterAttached(
        "DarkCaption", typeof(bool), typeof(WindowAppearance), new PropertyMetadata(false, OnDarkCaptionChanged));

    /// <summary>读取窗口是否使用与启动器一致的标题栏。</summary>
    public static bool GetDarkCaption(DependencyObject element) => (bool)element.GetValue(DarkCaptionProperty);
    /// <summary>启用或关闭标题栏外观，不改变窗口的其他行为。</summary>
    public static void SetDarkCaption(DependencyObject element, bool value) => element.SetValue(DarkCaptionProperty, value);

    /// <summary>等窗口句柄建立后再应用颜色；关闭时解除系统设置监听。</summary>
    private static void OnDarkCaptionChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not Window window) return;
        window.SourceInitialized -= OnSourceInitialized;
        if (change.NewValue is true)
        {
            window.SourceInitialized += OnSourceInitialized;
        }
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply(window);
    }

    /// <summary>高对比度设置变化时立即交还系统配色，不监听或修改全局主题偏好。</summary>
    private static void OnSourceInitialized(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        Apply(window);
        PropertyChangedEventHandler settingsChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
                _ = window.Dispatcher.BeginInvoke(new Action(() => Apply(window)));
        };
        SystemParameters.StaticPropertyChanged += settingsChanged;
        window.Closed += (_, _) => SystemParameters.StaticPropertyChanged -= settingsChanged;
    }

    /// <summary>使用官方 DWM 窗口属性；不支持的系统保留原生外观，不影响窗口打开。</summary>
    private static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = GetDarkCaption(window) && !SystemParameters.HighContrast ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var caption = dark == 1 ? ColorRef(window, "WindowBackgroundBrush") : -1;
        var text = dark == 1 ? ColorRef(window, "TextBrush") : -1;
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
        _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int)); // DWMWA_TEXT_COLOR
    }

    /// <summary>从共享主题取色并转换为 DWM 的 COLORREF；缺少主题时使用系统默认值。</summary>
    private static int ColorRef(Window window, string resource)
    {
        if (window.TryFindResource(resource) is not SolidColorBrush brush) return -1;
        var color = brush.Color;
        return color.R | color.G << 8 | color.B << 16;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
