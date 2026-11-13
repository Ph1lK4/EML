using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EML.App.Services;

/// <summary>页面入场和按钮反馈；只使用短动画，尊重系统关闭动画和高对比度设置。</summary>
public static class UiMotion
{
    public static readonly DependencyProperty EnterProperty = DependencyProperty.RegisterAttached(
        "Enter", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnEnterChanged));
    public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
        "Press", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnPressChanged));
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.RegisterAttached(
        "Selection", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnSelectionChanged));

    /// <summary>读取页面是否启用入场反馈。</summary>
    public static bool GetEnter(DependencyObject element) => (bool)element.GetValue(EnterProperty);
    /// <summary>为页面启用或禁用入场反馈。</summary>
    public static void SetEnter(DependencyObject element, bool value) => element.SetValue(EnterProperty, value);
    /// <summary>读取按钮是否启用悬停与按压反馈。</summary>
    public static bool GetPress(DependencyObject element) => (bool)element.GetValue(PressProperty);
    /// <summary>为按钮启用或禁用悬停与按压反馈。</summary>
    public static void SetPress(DependencyObject element, bool value) => element.SetValue(PressProperty, value);
    /// <summary>读取导航项是否启用短暂选中反馈。</summary>
    public static bool GetSelection(DependencyObject element) => (bool)element.GetValue(SelectionProperty);
    /// <summary>导航状态立即更新，动画只补充视觉反馈。</summary>
    public static void SetSelection(DependencyObject element, bool value) => element.SetValue(SelectionProperty, value);

    /// <summary>选中反馈仅操作模板内部表面，取消选择或卸载时释放动画。</summary>
    private static void OnSelectionChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not ListBoxItem item) return;
        item.Selected -= OnItemSelected;
        item.Unselected -= OnItemSelectionEnded;
        item.Unloaded -= OnItemSelectionEnded;
        ResetSelection(item);
        if (change.NewValue is not true) return;
        item.Selected += OnItemSelected;
        item.Unselected += OnItemSelectionEnded;
        item.Unloaded += OnItemSelectionEnded;
    }

    /// <summary>导航切换使用 150ms 淡入，首次布局和减少动态效果时直接显示。</summary>
    private static void OnItemSelected(object sender, RoutedEventArgs args)
    {
        if (sender is not ListBoxItem item || !ReferenceEquals(args.OriginalSource, item) || !CanAnimate || !item.IsLoaded || !item.IsVisible) return;
        if (item.Template?.FindName("ItemSurface", item) is Border surface)
            surface.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(150))
            { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>快速往返切换或卸载时不残留半透明的导航项。</summary>
    private static void OnItemSelectionEnded(object sender, RoutedEventArgs args) => ResetSelection((ListBoxItem)sender);

    /// <summary>清理模板动画，不改动外部绑定和选中状态。</summary>
    private static void ResetSelection(ListBoxItem item)
    {
        if (item.Template?.FindName("ItemSurface", item) is Border surface)
            surface.BeginAnimation(UIElement.OpacityProperty, null);
    }

    /// <summary>只在系统允许时播放过渡，关闭动画后控件立即进入目标状态。</summary>
    private static bool CanAnimate => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    /// <summary>订阅页面可见性；关闭行为时清理事件和动画，避免留下透明页面。</summary>
    private static void OnEnterChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not FrameworkElement element) return;
        element.Loaded -= OnEntered;
        element.Unloaded -= OnPageUnloaded;
        element.IsVisibleChanged -= OnPageVisibilityChanged;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (change.NewValue is not true) return;
        element.Loaded += OnEntered;
        element.Unloaded += OnPageUnloaded;
        element.IsVisibleChanged += OnPageVisibilityChanged;
        if (element.IsLoaded && element.IsVisible) FadeIn(element);
    }

    /// <summary>首次显示内容时执行一次淡入，不影响内容布局和命令状态。</summary>
    private static void OnEntered(object sender, RoutedEventArgs args) => FadeIn((FrameworkElement)sender);

    /// <summary>页面隐藏时终止动画，重新显示时恢复短过渡。</summary>
    private static void OnPageVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (element.IsLoaded && element.IsVisible) FadeIn(element);
    }

    /// <summary>离开可视树时清理动画时钟，保留页面原本的不透明度。</summary>
    private static void OnPageUnloaded(object sender, RoutedEventArgs args) =>
        ((FrameworkElement)sender).BeginAnimation(UIElement.OpacityProperty, null);

    /// <summary>使用 FillBehavior.Stop 自动返回原始属性，避免取消动画后残留透明状态。</summary>
    private static void FadeIn(FrameworkElement element)
    {
        if (!CanAnimate || !element.IsVisible || element.Opacity <= 0) return;
        var opacity = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacity * 0.45, opacity,
            TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>按钮反馈不处理事件，保留原有鼠标、键盘和命令执行规则。</summary>
    private static void OnPressChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not ButtonBase button) return;
        button.MouseEnter -= OnButtonEnter;
        button.MouseLeave -= OnButtonLeave;
        button.PreviewMouseLeftButtonDown -= OnButtonDown;
        button.PreviewMouseLeftButtonUp -= OnButtonUp;
        button.PreviewKeyDown -= OnButtonKeyDown;
        button.PreviewKeyUp -= OnButtonKeyUp;
        button.LostMouseCapture -= OnCaptureLost;
        button.IsEnabledChanged -= OnButtonEnabledChanged;
        button.Unloaded -= OnButtonUnloaded;
        ResetScale(button);
        if (change.NewValue is not true) return;
        button.MouseEnter += OnButtonEnter;
        button.MouseLeave += OnButtonLeave;
        button.PreviewMouseLeftButtonDown += OnButtonDown;
        button.PreviewMouseLeftButtonUp += OnButtonUp;
        button.PreviewKeyDown += OnButtonKeyDown;
        button.PreviewKeyUp += OnButtonKeyUp;
        button.LostMouseCapture += OnCaptureLost;
        button.IsEnabledChanged += OnButtonEnabledChanged;
        button.Unloaded += OnButtonUnloaded;
    }

    /// <summary>悬停时稍微放大，提供按钮可点击的反馈。</summary>
    private static void OnButtonEnter(object sender, MouseEventArgs args) => ScaleTo((ButtonBase)sender, 1.015);
    /// <summary>离开按钮后恢复原始尺寸。</summary>
    private static void OnButtonLeave(object sender, MouseEventArgs args) => ScaleTo((ButtonBase)sender, 1);
    /// <summary>按下时短暂收缩，不改变按钮的命中范围。</summary>
    private static void OnButtonDown(object sender, MouseButtonEventArgs args) => ScaleTo((ButtonBase)sender, 0.975);
    /// <summary>松开后根据指针位置恢复悬停状态。</summary>
    private static void OnButtonUp(object sender, MouseButtonEventArgs args) => RestoreHover((ButtonBase)sender);
    /// <summary>键盘激活与鼠标按压采用一致反馈，不拦截按键。</summary>
    private static void OnButtonKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key is Key.Space or Key.Enter && !args.IsRepeat) ScaleTo((ButtonBase)sender, 0.975);
    }
    /// <summary>键盘松开后恢复按钮尺寸。</summary>
    private static void OnButtonKeyUp(object sender, KeyEventArgs args)
    {
        if (args.Key is Key.Space or Key.Enter) RestoreHover((ButtonBase)sender);
    }
    /// <summary>鼠标捕获被其他控件取消时不保留按下状态。</summary>
    private static void OnCaptureLost(object sender, MouseEventArgs args) => RestoreHover((ButtonBase)sender);
    /// <summary>按钮进入禁用状态时停止反馈，保证禁用外观稳定。</summary>
    private static void OnButtonEnabledChanged(object sender, DependencyPropertyChangedEventArgs args) => ResetScale((ButtonBase)sender);
    /// <summary>按钮移出可视树时释放动画时钟。</summary>
    private static void OnButtonUnloaded(object sender, RoutedEventArgs args) => ResetScale((ButtonBase)sender);

    /// <summary>根据指针位置决定松开后的目标缩放。</summary>
    private static void RestoreHover(ButtonBase button) => ScaleTo(button, button.IsMouseOver ? 1.015 : 1);

    /// <summary>只操作本主题模板的局部变换，不覆盖页面为控件设置的变换或绑定。</summary>
    private static void ScaleTo(ButtonBase button, double target)
    {
        if (!button.IsEnabled || button.Template?.FindName("InteractionScale", button) is not ScaleTransform scale) return;
        if (!CanAnimate) { ResetScale(button); return; }
        var fromX = scale.ScaleX;
        var fromY = scale.ScaleY;
        scale.SetCurrentValue(ScaleTransform.ScaleXProperty, target);
        scale.SetCurrentValue(ScaleTransform.ScaleYProperty, target);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, ScaleAnimation(fromX, target));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, ScaleAnimation(fromY, target));
    }

    /// <summary>每个坐标轴独立使用短时缓出动画，结束后不保留动画时钟。</summary>
    private static DoubleAnimation ScaleAnimation(double from, double to) => new(from, to, TimeSpan.FromMilliseconds(120))
    { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

    /// <summary>恢复主题模板的原始尺寸，不影响业务控件的外部 RenderTransform。</summary>
    private static void ResetScale(ButtonBase button)
    {
        if (button.Template?.FindName("InteractionScale", button) is not ScaleTransform scale) return;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.SetCurrentValue(ScaleTransform.ScaleXProperty, 1d);
        scale.SetCurrentValue(ScaleTransform.ScaleYProperty, 1d);
    }
}
