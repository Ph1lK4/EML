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
    public static readonly DependencyProperty EnterKeyProperty = DependencyProperty.RegisterAttached(
        "EnterKey", typeof(object), typeof(UiMotion), new PropertyMetadata(null, OnEnterKeyChanged));
    public static readonly DependencyProperty EnterDistanceProperty = DependencyProperty.RegisterAttached(
        "EnterDistance", typeof(double), typeof(UiMotion), new PropertyMetadata(0d));
    public static readonly DependencyProperty TurnProperty = DependencyProperty.RegisterAttached(
        "Turn", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnTurnChanged));

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
    /// <summary>读取触发内容更新反馈的绑定值，不修改此值或业务选择。</summary>
    public static object? GetEnterKey(DependencyObject element) => element.GetValue(EnterKeyProperty);
    /// <summary>内容变化时重播短过渡，适用于导航页面和当前实例信息。</summary>
    public static void SetEnterKey(DependencyObject element, object? value) => element.SetValue(EnterKeyProperty, value);
    /// <summary>读取显式入场容器的垂直位移距离。</summary>
    public static double GetEnterDistance(DependencyObject element) => (double)element.GetValue(EnterDistanceProperty);
    /// <summary>只为显式提供 TranslateTransform 的容器启用位移，不替换外部变换。</summary>
    public static void SetEnterDistance(DependencyObject element, double value) => element.SetValue(EnterDistanceProperty, value);
    /// <summary>读取下拉按钮是否启用箭头旋转。</summary>
    public static bool GetTurn(DependencyObject element) => (bool)element.GetValue(TurnProperty);
    /// <summary>为下拉按钮启用短时旋转，不参与展开或收起逻辑。</summary>
    public static void SetTurn(DependencyObject element, bool value) => element.SetValue(TurnProperty, value);

    /// <summary>选中反馈仅操作模板内部表面，取消选择或卸载时释放动画。</summary>
    private static void OnSelectionChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not ListBoxItem item) return;
        item.Selected -= OnItemSelected;
        item.Unselected -= OnItemSelectionEnded;
        item.Unloaded -= OnItemSelectionEnded;
        item.Loaded -= OnItemLoaded;
        item.MouseEnter -= OnItemHover;
        item.MouseLeave -= OnItemHover;
        item.IsEnabledChanged -= OnItemEnabledChanged;
        ResetSelection(item);
        if (change.NewValue is not true) return;
        item.Selected += OnItemSelected;
        item.Unselected += OnItemSelectionEnded;
        item.Unloaded += OnItemSelectionEnded;
        item.Loaded += OnItemLoaded;
        item.MouseEnter += OnItemHover;
        item.MouseLeave += OnItemHover;
        item.IsEnabledChanged += OnItemEnabledChanged;
        if (item.IsLoaded) UpdateSelection(item, false);
    }

    /// <summary>切换时更新装饰层，首次布局和减少动态效果时直接显示。</summary>
    private static void OnItemSelected(object sender, RoutedEventArgs args)
    {
        if (sender is ListBoxItem item && ReferenceEquals(args.OriginalSource, item)) UpdateSelection(item, true);
    }

    /// <summary>快速往返切换或卸载时不残留半透明的导航项。</summary>
    private static void OnItemSelectionEnded(object sender, RoutedEventArgs args)
    {
        if (sender is not ListBoxItem item || !ReferenceEquals(args.OriginalSource, item)) return;
        if (args.RoutedEvent == FrameworkElement.UnloadedEvent) ResetSelection(item);
        else UpdateSelection(item, true);
    }

    /// <summary>回收的实例行重新加载时直接同步状态，不播放过期选择动画。</summary>
    private static void OnItemLoaded(object sender, RoutedEventArgs args) => UpdateSelection((ListBoxItem)sender, false);

    /// <summary>悬停平移内部内容并淡入描边，外层布局、焦点和命中范围保持不变。</summary>
    private static void OnItemHover(object sender, MouseEventArgs args) => UpdateSelection((ListBoxItem)sender, true);

    /// <summary>禁用列表时立即稳定装饰层，保留真实的选中项。</summary>
    private static void OnItemEnabledChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateSelection((ListBoxItem)sender, false);

    /// <summary>选中和悬停各自使用独立表面，快速切换从当前进度继续。</summary>
    private static void UpdateSelection(ListBoxItem item, bool animate)
    {
        var enabled = animate && item.IsLoaded && item.IsVisible && item.IsEnabled;
        var hovered = item.IsEnabled && item.IsLoaded && item.IsVisible && item.IsMouseOver;
        if (item.Template?.FindName("ItemHover", item) is Border hover)
            OpacityTo(hover, hovered && !item.IsSelected ? 1 : 0, enabled);
        if (item.Template?.FindName("ItemHoverOutline", item) is Border outline)
            OpacityTo(outline, hovered ? 1 : 0, enabled);
        if (item.Template?.FindName("ItemSelection", item) is Border selected)
            OpacityTo(selected, item.IsSelected ? 1 : 0, enabled);
        if (item.Template?.FindName("ItemTranslation", item) is TranslateTransform translation)
            DoubleTo(translation, TranslateTransform.XProperty, hovered && CanAnimate ? 3 : 0, enabled, 160);
    }

    /// <summary>清理模板动画，不改动外部绑定和选中状态。</summary>
    private static void ResetSelection(ListBoxItem item)
    {
        // 卸载和关闭行为时不可再次读取 IsMouseOver，回收的行可能仍保留旧的鼠标状态。
        foreach (var name in new[] { "ItemHover", "ItemHoverOutline" })
            if (item.Template?.FindName(name, item) is Border hover) OpacityTo(hover, 0, false);
        if (item.Template?.FindName("ItemSelection", item) is Border selected)
            OpacityTo(selected, item.IsSelected ? 1 : 0, false);
        if (item.Template?.FindName("ItemTranslation", item) is TranslateTransform translation)
            DoubleTo(translation, TranslateTransform.XProperty, 0, false, 0);
    }

    /// <summary>只在系统允许时播放过渡，关闭动画后控件立即进入目标状态。</summary>
    private static bool CanAnimate => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    /// <summary>装饰层以持久局部值保存状态，动画结束后保持终点，直到下一次交互改变。</summary>
    internal static void OpacityTo(UIElement element, double target, bool animate)
    {
        var from = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        // 这些是主题专用装饰层，不是外部控件的透明度；SetCurrentValue 会在应用动画时被重新求值。
        element.SetValue(UIElement.OpacityProperty, target);
        if (animate && CanAnimate && Math.Abs(from - target) > 0.001)
            element.BeginAnimation(UIElement.OpacityProperty, Animation(from, target, 140));
    }

    /// <summary>绑定的内容更新后只刷新视觉，不等待动画完成再切换业务状态。</summary>
    private static void OnEnterKeyChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is FrameworkElement { IsLoaded: true, IsVisible: true } element && GetEnter(element)) FadeIn(element);
    }

    /// <summary>订阅页面可见性；关闭行为时清理事件和动画，避免留下透明页面。</summary>
    private static void OnEnterChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not FrameworkElement element) return;
        element.Loaded -= OnEntered;
        element.Unloaded -= OnPageUnloaded;
        element.IsVisibleChanged -= OnPageVisibilityChanged;
        ResetEntrance(element);
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
        if (element.IsLoaded && element.IsVisible) FadeIn(element);
        else ResetEntrance(element);
    }

    /// <summary>离开可视树时清理动画时钟，保留页面原本的不透明度。</summary>
    private static void OnPageUnloaded(object sender, RoutedEventArgs args) =>
        ResetEntrance((FrameworkElement)sender);

    /// <summary>仅清理入场行为拥有的动画，恢复原始透明度和位移绑定。</summary>
    private static void ResetEntrance(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (GetEnterDistance(element) != 0 && element.RenderTransform is TranslateTransform { IsFrozen: false } translation)
            translation.BeginAnimation(TranslateTransform.YProperty, null);
    }

    /// <summary>使用 FillBehavior.Stop 自动返回原始属性，避免取消动画后残留透明状态。</summary>
    private static void FadeIn(FrameworkElement element)
    {
        if (!CanAnimate || !element.IsVisible) { ResetEntrance(element); return; }
        var opacity = (double)element.GetAnimationBaseValue(UIElement.OpacityProperty);
        if (opacity <= 0) { ResetEntrance(element); return; }
        // Stop 时钟可能仍附着在对象上，因此按当前值是否偏离基线判断动画是否仍在途中。
        var fromOpacity = Math.Abs(element.Opacity - opacity) > 0.001 ? element.Opacity : opacity * 0.78;
        element.BeginAnimation(UIElement.OpacityProperty, Animation(fromOpacity, opacity, 180));
        if (GetEnterDistance(element) is var distance && distance != 0 && element.RenderTransform is TranslateTransform translation && !translation.IsFrozen)
        {
            var target = (double)translation.GetAnimationBaseValue(TranslateTransform.YProperty);
            var from = Math.Abs(translation.Y - target) > 0.001 ? translation.Y : target + distance;
            translation.BeginAnimation(TranslateTransform.YProperty, Animation(from, target, 200));
        }
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
        button.LostKeyboardFocus -= OnButtonFocusLost;
        button.IsEnabledChanged -= OnButtonEnabledChanged;
        button.Unloaded -= OnButtonUnloaded;
        button.Loaded -= OnButtonLoaded;
        ResetButton(button);
        if (change.NewValue is not true) return;
        button.MouseEnter += OnButtonEnter;
        button.MouseLeave += OnButtonLeave;
        button.PreviewMouseLeftButtonDown += OnButtonDown;
        button.PreviewMouseLeftButtonUp += OnButtonUp;
        button.PreviewKeyDown += OnButtonKeyDown;
        button.PreviewKeyUp += OnButtonKeyUp;
        button.LostMouseCapture += OnCaptureLost;
        button.LostKeyboardFocus += OnButtonFocusLost;
        button.IsEnabledChanged += OnButtonEnabledChanged;
        button.Unloaded += OnButtonUnloaded;
        button.Loaded += OnButtonLoaded;
        if (button.IsLoaded) RestoreHover(button, false);
    }

    /// <summary>悬停时轻微上浮、放大并渐亮，不等待动画完成才允许点击。</summary>
    private static void OnButtonEnter(object sender, MouseEventArgs args) => RestoreHover((ButtonBase)sender);
    /// <summary>离开后从当前动画进度回到原位，不突然跳回起点。</summary>
    private static void OnButtonLeave(object sender, MouseEventArgs args)
    {
        PoseButton((ButtonBase)sender, 1, 0, true, 120);
        UpdateButtonTint((ButtonBase)sender, false, true);
    }
    /// <summary>按下时短暂收缩，不改变按钮的命中范围。</summary>
    private static void OnButtonDown(object sender, MouseButtonEventArgs args) => PoseButton((ButtonBase)sender, 0.98, 0.5, true, 80);
    /// <summary>松开后根据指针位置恢复悬停状态。</summary>
    private static void OnButtonUp(object sender, MouseButtonEventArgs args) => RestoreHover((ButtonBase)sender);
    /// <summary>键盘激活与鼠标按压采用一致反馈，不拦截按键。</summary>
    private static void OnButtonKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key is Key.Space or Key.Enter && !args.IsRepeat) PoseButton((ButtonBase)sender, 0.98, 0.5, true, 80);
    }
    /// <summary>键盘松开后恢复按钮尺寸。</summary>
    private static void OnButtonKeyUp(object sender, KeyEventArgs args)
    {
        if (args.Key is Key.Space or Key.Enter) RestoreHover((ButtonBase)sender);
    }
    /// <summary>鼠标捕获被其他控件取消时不保留按下状态。</summary>
    private static void OnCaptureLost(object sender, MouseEventArgs args) => RestoreHover((ButtonBase)sender);
    /// <summary>Enter 激活后面板可能立即收起；即使 KeyUp 到达别处，也不能残留按下缩放。</summary>
    private static void OnButtonFocusLost(object sender, KeyboardFocusChangedEventArgs args) => ResetButton((ButtonBase)sender);
    /// <summary>按钮进入禁用状态时停止反馈，保证禁用外观稳定。</summary>
    private static void OnButtonEnabledChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        ResetButton((ButtonBase)sender);
        if (args.NewValue is true) RestoreHover((ButtonBase)sender, false);
    }
    /// <summary>按钮移出可视树时释放动画时钟。</summary>
    private static void OnButtonUnloaded(object sender, RoutedEventArgs args)
    {
        ResetButton((ButtonBase)sender);
    }

    /// <summary>重新进入可视树时同步真实悬停状态，不重播离开前的动画。</summary>
    private static void OnButtonLoaded(object sender, RoutedEventArgs args) => RestoreHover((ButtonBase)sender, false);

    /// <summary>底色和描边使用独立装饰层，不改变页面设定的背景色或绑定。</summary>
    private static void UpdateButtonTint(ButtonBase button, bool hovered, bool animate)
    {
        foreach (var name in new[] { "HoverTint", "HoverOutline" })
            if (button.Template?.FindName(name, button) is Border surface)
                OpacityTo(surface, hovered ? 1 : 0, animate);
    }

    /// <summary>根据真实指针位置恢复上浮与高亮，隐藏或禁用按钮不保留悬停状态。</summary>
    private static void RestoreHover(ButtonBase button, bool animate = true)
    {
        var hovered = button.IsEnabled && button.IsLoaded && button.IsVisible && button.IsMouseOver;
        PoseButton(button, hovered ? 1.018 : 1, hovered ? -2 : 0, animate, 160);
        UpdateButtonTint(button, hovered, animate);
    }

    /// <summary>只操作本主题模板的局部变换，不覆盖页面为控件设置的变换或绑定。</summary>
    private static void PoseButton(ButtonBase button, double targetScale, double targetY, bool animate, int milliseconds)
    {
        if (!button.IsEnabled || !CanAnimate) { targetScale = 1; targetY = 0; }
        if (button.Template?.FindName("InteractionScale", button) is ScaleTransform scale)
        {
            DoubleTo(scale, ScaleTransform.ScaleXProperty, targetScale, animate, milliseconds);
            DoubleTo(scale, ScaleTransform.ScaleYProperty, targetScale, animate, milliseconds);
        }
        if (button.Template?.FindName("InteractionTranslation", button) is TranslateTransform translation)
            DoubleTo(translation, TranslateTransform.YProperty, targetY, animate, milliseconds);
    }

    /// <summary>专用模板变换持久保存终点，快速进出从当前值接续；不操作控件外部的变换。</summary>
    internal static void DoubleTo(Animatable target, DependencyProperty property, double value, bool animate, int milliseconds)
    {
        if (target.IsFrozen) return;
        var from = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        // FillBehavior.Stop 结束时回到基值，所以必须保存真实基值，而不是临时的 current value。
        target.SetValue(property, value);
        if (animate && CanAnimate && Math.Abs(from - value) > 0.001)
            target.BeginAnimation(property, Animation(from, value, milliseconds));
    }

    /// <summary>禁用、失焦、卸载和关闭行为都清理专用变换与高亮，不读取陈旧鼠标状态。</summary>
    private static void ResetButton(ButtonBase button)
    {
        PoseButton(button, 1, 0, false, 0);
        UpdateButtonTint(button, false, false);
    }

    /// <summary>箭头仍由原生 IsChecked 驱动，不接管 ToggleButton 的鼠标或键盘事件。</summary>
    private static void OnTurnChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not ToggleButton toggle) return;
        toggle.Checked -= OnTurnStateChanged;
        toggle.Unchecked -= OnTurnStateChanged;
        toggle.Loaded -= OnTurnLoaded;
        toggle.Unloaded -= OnTurnUnloaded;
        TurnArrow(toggle, false);
        if (change.NewValue is not true) return;
        toggle.Checked += OnTurnStateChanged;
        toggle.Unchecked += OnTurnStateChanged;
        toggle.Loaded += OnTurnLoaded;
        toggle.Unloaded += OnTurnUnloaded;
    }

    /// <summary>打开和收起下拉框时平滑转动箭头。</summary>
    private static void OnTurnStateChanged(object sender, RoutedEventArgs args) => TurnArrow((ToggleButton)sender, true);
    /// <summary>首次加载直接同步角度，避免打开的下拉框显示反向箭头。</summary>
    private static void OnTurnLoaded(object sender, RoutedEventArgs args) => TurnArrow((ToggleButton)sender, false);
    /// <summary>下拉按钮卸载时释放箭头动画。</summary>
    private static void OnTurnUnloaded(object sender, RoutedEventArgs args) => TurnArrow((ToggleButton)sender, false);

    /// <summary>只修改模板中专用的旋转变换，不覆盖控件外部变换。</summary>
    private static void TurnArrow(ToggleButton toggle, bool animate)
    {
        if (toggle.Template?.FindName("Arrow", toggle) is not FrameworkElement { RenderTransform: RotateTransform rotation } || rotation.IsFrozen) return;
        var target = toggle.IsChecked is true ? 180d : 0d;
        DoubleTo(rotation, RotateTransform.AngleProperty, target, animate && toggle.IsLoaded && toggle.IsVisible, 160);
    }

    /// <summary>统一轻量缓出曲线，结束时回到绑定或静态值，不持有最终画面。</summary>
    internal static DoubleAnimation Animation(double from, double to, int milliseconds) => new(from, to, TimeSpan.FromMilliseconds(milliseconds))
    { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
}
