using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace EML.App.Services;

/// <summary>仅调整系统标题栏颜色，保留 Windows 原生移动、缩放、贴靠与窗口按钮。</summary>
public static class WindowAppearance
{
    private static readonly ConditionalWeakTable<Window, CaptionSubscription> Subscriptions = new();

    public static readonly DependencyProperty DarkCaptionProperty = DependencyProperty.RegisterAttached(
        "DarkCaption", typeof(bool), typeof(WindowAppearance), new PropertyMetadata(false, OnDarkCaptionChanged));

    /// <summary>读取窗口是否使用与启动器一致的标题栏。</summary>
    public static bool GetDarkCaption(DependencyObject element) => (bool)element.GetValue(DarkCaptionProperty);
    /// <summary>启用或关闭标题栏外观，不改变窗口的其他行为。</summary>
    public static void SetDarkCaption(DependencyObject element, bool value) => element.SetValue(DarkCaptionProperty, value);

    /// <summary>主题切换后重新取共享颜色；首次为亮色也注册句柄初始化和高对比度监听。</summary>
    public static void Refresh(Window window)
    {
        _ = Subscriptions.GetValue(window, static target => new CaptionSubscription(target));
        Apply(window);
    }

    /// <summary>亮暗反复切换复用同一订阅，不重复注册窗口事件或覆盖动态资源绑定。</summary>
    private static void OnDarkCaptionChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is Window window) Refresh(window);
    }

    /// <summary>使用官方 DWM 窗口属性；不支持的系统保留原生外观，不影响窗口打开。</summary>
    private static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var highContrast = SystemParameters.HighContrast;
        var dark = GetDarkCaption(window) && !highContrast ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var caption = highContrast ? -1 : ColorRef(window, "WindowBackgroundBrush");
        var text = highContrast ? -1 : ColorRef(window, "TextBrush");
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

    /// <summary>一扇窗口只保留一组系统设置监听，并在关闭时显式解除静态事件引用。</summary>
    private sealed class CaptionSubscription
    {
        private readonly Window _window;

        /// <summary>监听窗口句柄建立、关闭以及系统高对比度设置，不读取或修改系统主题偏好。</summary>
        internal CaptionSubscription(Window window)
        {
            _window = window;
            window.SourceInitialized += OnSourceInitialized;
            window.Closed += OnClosed;
            SystemParameters.StaticPropertyChanged += OnSettingsChanged;
        }

        /// <summary>句柄可用后应用当前主题，覆盖先选择亮色再显示窗口的情况。</summary>
        private void OnSourceInitialized(object? sender, EventArgs args) => Apply(_window);

        /// <summary>高对比度变化后在所属窗口线程刷新，不跨线程操作 WPF 资源。</summary>
        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(SystemParameters.HighContrast) || _window.Dispatcher.HasShutdownStarted) return;
            _ = _window.Dispatcher.BeginInvoke(new Action(() => Apply(_window)));
        }

        /// <summary>关闭窗口时移除静态监听，使重复打开弹窗不会积累窗口引用。</summary>
        private void OnClosed(object? sender, EventArgs args)
        {
            SystemParameters.StaticPropertyChanged -= OnSettingsChanged;
            _window.SourceInitialized -= OnSourceInitialized;
            _window.Closed -= OnClosed;
            Subscriptions.Remove(_window);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
