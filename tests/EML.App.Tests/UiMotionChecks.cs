using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;

namespace EML.App.Tests;

/// <summary>复用烟雾测试的 STA，检查动效仅改变展示，不改绑定、选择或命令。</summary>
internal static class UiMotionChecks
{
    /// <summary>使用内存控件验证快速切换和卸载后的状态，不创建窗口或访问实例。</summary>
    internal static void Run(Border host)
    {
        VerifyEntranceCleanup(host);
        VerifyHoverTargetPersistence(host);
        VerifyButtonCleanup(host);
        VerifyArrowCleanup(host);
        VerifySelectionCleanup(host);
        VerifyNavigationCleanup(host);
        host.Child = null;
    }

    /// <summary>悬停终点必须成为持久基值；过渡时钟结束或移除后仍保持，而不是退回模板初值。</summary>
    private static void VerifyHoverTargetPersistence(Border host)
    {
        var button = new Button { Content = "启动游戏" };
        var toggle = new ToggleButton { Style = (Style)Application.Current.FindResource("DropDownToggleStyle") };
        var list = new ListBox { ItemsSource = new[] { "GTNH 2.9.0" } };
        var panel = new StackPanel();
        panel.Children.Add(button);
        panel.Children.Add(toggle);
        panel.Children.Add(list);
        host.Child = panel;
        Layout(host);
        var item = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        var scale = Assert.IsType<ScaleTransform>(button.Template.FindName("InteractionScale", button));
        var lift = Assert.IsType<TranslateTransform>(button.Template.FindName("InteractionTranslation", button));
        var shift = Assert.IsType<TranslateTransform>(item.Template.FindName("ItemTranslation", item));
        var arrow = Assert.IsAssignableFrom<FrameworkElement>(toggle.Template.FindName("Arrow", toggle));
        var rotation = Assert.IsType<RotateTransform>(arrow.RenderTransform);

        // 调用生产过渡函数，不伪造 IsMouseOver；验证的是时钟移除后使用的底层终点。
        foreach (var (target, property, value) in new (Animatable, DependencyProperty, double)[]
        {
            (scale, ScaleTransform.ScaleXProperty, 1.018),
            (scale, ScaleTransform.ScaleYProperty, 1.018),
            (lift, TranslateTransform.YProperty, -2),
            (shift, TranslateTransform.XProperty, 3),
            (rotation, RotateTransform.AngleProperty, 180)
        })
        {
            var from = (double)target.GetValue(property);
            UiMotion.DoubleTo(target, property, value, true, 160);
            Assert.Equal(value, (double)target.GetAnimationBaseValue(property));
            var clock = UiMotion.Animation(from, value, 160).CreateClock();
            target.ApplyAnimationClock(property, clock);
            FinishAnimation(clock);
            Assert.Equal(value, (double)target.GetValue(property));
            target.BeginAnimation(property, null);
            Assert.Equal(value, (double)target.GetValue(property));
            // 离开、快速反向和再次悬停均应以最新状态为准，不能叠加旧时钟。
            var rest = target is ScaleTransform ? 1d : 0d;
            foreach (var next in new[] { rest, value, rest })
            {
                var previous = (double)target.GetValue(property);
                UiMotion.DoubleTo(target, property, next, true, 120);
                var nextClock = UiMotion.Animation(previous, next, 120).CreateClock();
                target.ApplyAnimationClock(property, nextClock);
                FinishAnimation(nextClock);
                Assert.Equal(next, (double)target.GetValue(property));
                target.BeginAnimation(property, null);
                Assert.Equal(next, (double)target.GetValue(property));
                Assert.False(target.HasAnimatedProperties);
            }
        }

        foreach (var (control, name) in new (Control, string)[]
        {
            (button, "HoverTint"), (button, "HoverOutline"),
            (item, "ItemHover"), (item, "ItemHoverOutline"), (item, "ItemSelection")
        })
        {
            var layer = Assert.IsType<Border>(control.Template.FindName(name, control));
            foreach (var opacity in new[] { 1d, 0d, 1d, 0d })
            {
                var previous = layer.Opacity;
                UiMotion.OpacityTo(layer, opacity, true);
                Assert.Equal(opacity, (double)layer.GetAnimationBaseValue(UIElement.OpacityProperty));
                var clock = UiMotion.Animation(previous, opacity, 140).CreateClock();
                layer.ApplyAnimationClock(UIElement.OpacityProperty, clock);
                FinishAnimation(clock);
                Assert.Equal(opacity, layer.Opacity);
                layer.BeginAnimation(UIElement.OpacityProperty, null);
                Assert.Equal(opacity, layer.Opacity);
                Assert.False(layer.HasAnimatedProperties);
            }
        }
    }

    /// <summary>同步把真实 WPF 时钟推进到结束后，避免固定休眠或依赖屏幕鼠标位置。</summary>
    private static void FinishAnimation(AnimationClock clock)
    {
        var controller = Assert.IsType<ClockController>(clock.Controller);
        controller.Begin();
        controller.SeekAlignedToLastTick(clock.Timeline.Duration.TimeSpan + TimeSpan.FromMilliseconds(1), TimeSeekOrigin.BeginTime);
        Assert.Equal(ClockState.Stopped, clock.CurrentState);
    }

    /// <summary>入场动画停止后恢复调用方的不透明度与位移，保留实时绑定。</summary>
    private static void VerifyEntranceCleanup(Border host)
    {
        var model = new MotionModel();
        var translation = new TranslateTransform(3, 4);
        var panel = new Border { DataContext = model, RenderTransform = translation };
        panel.SetBinding(UIElement.OpacityProperty, new Binding(nameof(MotionModel.Opacity)));
        panel.SetBinding(UiMotion.EnterKeyProperty, new Binding(nameof(MotionModel.Key)));
        UiMotion.SetEnterDistance(panel, 8);
        UiMotion.SetEnter(panel, true);
        host.Child = panel;
        Layout(host);
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

        foreach (var key in new[] { 1, 2, 3, 2, 1 }) model.Key = key;
        panel.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        UiMotion.SetEnter(panel, false);

        Assert.Equal(model.Opacity, panel.Opacity);
        Assert.False(panel.HasAnimatedProperties);
        Assert.Same(translation, panel.RenderTransform);
        Assert.Equal(3, translation.X);
        Assert.Equal(4, translation.Y);
        Assert.False(translation.HasAnimatedProperties);
        Assert.NotNull(BindingOperations.GetBindingExpression(panel, UIElement.OpacityProperty));
        Assert.NotNull(BindingOperations.GetBindingExpression(panel, UiMotion.EnterKeyProperty));
        model.Opacity = 0.84;
        model.Key = 9;
        Assert.Equal(0.84, panel.Opacity);
        Assert.Equal(9, UiMotion.GetEnterKey(panel));
        Assert.False(panel.HasAnimatedProperties);
    }

    /// <summary>悬停清理只影响模板内部动画，不执行命令、移动命中区域或覆盖外部绑定。</summary>
    private static void VerifyButtonCleanup(Border host)
    {
        var executions = 0;
        var model = new MotionModel();
        var external = new TranslateTransform(7, 9);
        var button = new Button { Content = "启动游戏", Command = new RelayCommand(() => executions++), DataContext = model };
        button.SetBinding(UIElement.RenderTransformProperty, new Binding { Source = external });
        button.SetBinding(UIElement.OpacityProperty, new Binding(nameof(MotionModel.Opacity)));
        var peer = new Button { Content = "实例详情" };
        var panel = new StackPanel();
        panel.Children.Add(button);
        panel.Children.Add(peer);
        host.Child = panel;
        Layout(host);
        var scale = Assert.IsType<ScaleTransform>(button.Template.FindName("InteractionScale", button));
        var translation = Assert.IsType<TranslateTransform>(button.Template.FindName("InteractionTranslation", button));
        Assert.False(scale.IsFrozen);
        Assert.False(translation.IsFrozen);
        Assert.NotSame(scale, peer.Template.FindName("InteractionScale", peer));
        Assert.NotSame(translation, peer.Template.FindName("InteractionTranslation", peer));
        AssertFixedHitArea(button, "ButtonHitArea");
        UiMotion.SetPress(button, true);
        // 合成事件仅覆盖事件接线；它们不会改变 IsMouseOver，不用于证明真实悬停效果。
        foreach (var enabled in new[] { true, false, true })
        {
            button.IsEnabled = enabled;
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
        }
        SeedButtonMotion(button);
        button.IsEnabled = false;
        AssertButtonAtRest(button);
        button.IsEnabled = true;
        SeedButtonMotion(button);
        button.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        AssertButtonAtRest(button);
        SeedButtonMotion(button);
        button.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, button, host)
        { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        AssertButtonAtRest(button);
        SeedButtonMotion(button);
        UiMotion.SetPress(button, false);
        AssertButtonAtRest(button);
        Assert.Equal(0, executions);
        Assert.Same(external, button.RenderTransform);
        Assert.NotNull(BindingOperations.GetBindingExpression(button, UIElement.RenderTransformProperty));
        Assert.NotNull(BindingOperations.GetBindingExpression(button, UIElement.OpacityProperty));
        Assert.Equal(model.Opacity, button.Opacity);
        model.Opacity = 0.91;
        Assert.Equal(0.91, button.Opacity);
        AssertFixedHitArea(button, "ButtonHitArea");
    }

    /// <summary>模拟尚未结束的主题动画，单独验证清理路径而不伪造鼠标位置。</summary>
    private static void SeedButtonMotion(Button button)
    {
        var scale = Assert.IsType<ScaleTransform>(button.Template.FindName("InteractionScale", button));
        var translation = Assert.IsType<TranslateTransform>(button.Template.FindName("InteractionTranslation", button));
        scale.SetCurrentValue(ScaleTransform.ScaleXProperty, 1.02d);
        scale.SetCurrentValue(ScaleTransform.ScaleYProperty, 1.02d);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.02, 0.98, TimeSpan.FromSeconds(1)));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.02, 0.98, TimeSpan.FromSeconds(1)));
        translation.SetCurrentValue(TranslateTransform.YProperty, -2d);
        translation.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-2, 0, TimeSpan.FromSeconds(1)));
        foreach (var name in new[] { "HoverTint", "HoverOutline" })
        {
            var layer = Assert.IsType<Border>(button.Template.FindName(name, button));
            layer.SetCurrentValue(UIElement.OpacityProperty, 1d);
            layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1)));
        }
    }

    /// <summary>按钮离开交互状态后必须释放全部时钟，恢复原尺寸和位置。</summary>
    private static void AssertButtonAtRest(Button button)
    {
        var scale = Assert.IsType<ScaleTransform>(button.Template.FindName("InteractionScale", button));
        var translation = Assert.IsType<TranslateTransform>(button.Template.FindName("InteractionTranslation", button));
        Assert.Equal(1, scale.ScaleX);
        Assert.Equal(1, scale.ScaleY);
        Assert.Equal(0, translation.X);
        Assert.Equal(0, translation.Y);
        Assert.False(scale.HasAnimatedProperties);
        Assert.False(translation.HasAnimatedProperties);
        foreach (var name in new[] { "HoverTint", "HoverOutline" })
        {
            var layer = Assert.IsType<Border>(button.Template.FindName(name, button));
            Assert.Equal(0, layer.Opacity);
            Assert.False(layer.HasAnimatedProperties);
        }
    }

    /// <summary>下拉箭头使用每个模板自己的可变变换，关闭动效后保留真实展开状态。</summary>
    private static void VerifyArrowCleanup(Border host)
    {
        var toggle = new ToggleButton { Style = (Style)Application.Current.FindResource("DropDownToggleStyle") };
        host.Child = toggle;
        Layout(host);
        var arrow = Assert.IsAssignableFrom<FrameworkElement>(toggle.Template.FindName("Arrow", toggle));
        var rotation = Assert.IsType<RotateTransform>(arrow.RenderTransform);
        Assert.False(rotation.IsFrozen);
        foreach (var value in new[] { true, false, true }) toggle.IsChecked = value;
        toggle.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        UiMotion.SetTurn(toggle, false);
        Assert.True(toggle.IsChecked);
        Assert.Equal(180, rotation.Angle);
        Assert.False(rotation.HasAnimatedProperties);
        Assert.Same(rotation, arrow.RenderTransform);
    }

    /// <summary>实例行反复选中与回收后不残留动画，外部透明度和选择结果保持不变。</summary>
    private static void VerifySelectionCleanup(Border host)
    {
        var model = new MotionModel();
        var list = new ListBox { ItemsSource = new[] { "GTNH 2.8.4", "GTNH 2.9.0", "GTNH 2.9.0 · 中文测试服" } };
        host.Child = list;
        Layout(host);
        var first = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        var second = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(1));
        var translation = Assert.IsType<TranslateTransform>(first.Template.FindName("ItemTranslation", first));
        Assert.False(translation.IsFrozen);
        Assert.NotSame(translation, second.Template.FindName("ItemTranslation", second));
        AssertFixedHitArea(first, "ItemHitArea");
        first.DataContext = model;
        first.SetBinding(UIElement.OpacityProperty, new Binding(nameof(MotionModel.Opacity)));
        var external = new TranslateTransform(2, 3);
        first.SetBinding(UIElement.RenderTransformProperty, new Binding { Source = external });
        Assert.True(UiMotion.GetSelection(first));
        foreach (var index in new[] { 0, 1, 2, 0, 2, 0 })
        {
            list.SelectedIndex = index;
            Layout(host);
        }
        SeedItemMotion(first);
        first.IsEnabled = false;
        AssertItemAtRest(first);
        first.IsEnabled = true;
        SeedItemMotion(first);
        first.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        AssertItemAtRest(first);
        SeedItemMotion(first);
        UiMotion.SetSelection(first, false);
        AssertItemAtRest(first);
        Assert.Equal(0, list.SelectedIndex);
        Assert.True(first.IsSelected);
        Assert.Same(external, first.RenderTransform);
        Assert.Equal(model.Opacity, first.Opacity);
        Assert.NotNull(BindingOperations.GetBindingExpression(first, UIElement.OpacityProperty));
        Assert.NotNull(BindingOperations.GetBindingExpression(first, UIElement.RenderTransformProperty));
        AssertFixedHitArea(first, "ItemHitArea");
    }

    /// <summary>模拟回收之前未完成的列表悬停动画，不修改选中状态。</summary>
    private static void SeedItemMotion(ListBoxItem item)
    {
        var translation = Assert.IsType<TranslateTransform>(item.Template.FindName("ItemTranslation", item));
        translation.SetCurrentValue(TranslateTransform.XProperty, 3d);
        translation.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(3, 0, TimeSpan.FromSeconds(1)));
        foreach (var name in new[] { "ItemHover", "ItemHoverOutline" })
        {
            var layer = Assert.IsType<Border>(item.Template.FindName(name, item));
            layer.SetCurrentValue(UIElement.OpacityProperty, 1d);
            layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1)));
        }
    }

    /// <summary>列表回收后清空悬停反馈，但保留真实选中项的装饰。</summary>
    private static void AssertItemAtRest(ListBoxItem item)
    {
        var translation = Assert.IsType<TranslateTransform>(item.Template.FindName("ItemTranslation", item));
        Assert.Equal(0, translation.X);
        Assert.Equal(0, translation.Y);
        Assert.False(translation.HasAnimatedProperties);
        foreach (var name in new[] { "ItemSurface", "ItemHover", "ItemHoverOutline", "ItemSelection" })
        {
            var surface = Assert.IsType<Border>(item.Template.FindName(name, item));
            Assert.False(surface.HasAnimatedProperties);
        }
        Assert.Equal(0, Assert.IsType<Border>(item.Template.FindName("ItemHover", item)).Opacity);
        Assert.Equal(0, Assert.IsType<Border>(item.Template.FindName("ItemHoverOutline", item)).Opacity);
        Assert.Equal(item.IsSelected ? 1d : 0d, Assert.IsType<Border>(item.Template.FindName("ItemSelection", item)).Opacity);
    }

    /// <summary>固定的模板外壳不参与动画，视觉位移不会改变控件的命中布局。</summary>
    private static void AssertFixedHitArea(Control control, string name)
    {
        var area = Assert.IsAssignableFrom<FrameworkElement>(control.Template.FindName(name, control));
        Assert.True(area.RenderTransform.Value.IsIdentity);
        Assert.True(area.LayoutTransform.Value.IsIdentity);
        Assert.False(area.HasAnimatedProperties);
    }

    /// <summary>导航标记是装饰对象，绑定、无选中状态和卸载不受标记动画影响。</summary>
    private static void VerifyNavigationCleanup(Border host)
    {
        var model = new MotionModel();
        var list = new ListBox { ItemsSource = new[] { "本地实例", "GTNH 专区", "Java 环境" }, DataContext = model };
        list.SetBinding(Selector.SelectedIndexProperty, new Binding(nameof(MotionModel.Key)) { Mode = BindingMode.TwoWay });
        var translation = new TranslateTransform(2, 6);
        var marker = new Border { Width = 3, Height = 20, RenderTransform = translation, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top };
        var panel = new Grid();
        panel.Children.Add(list);
        panel.Children.Add(marker);
        NavigationMotion.SetIndicator(list, marker);
        host.Child = panel;
        Layout(host);
        list.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        foreach (var index in new[] { 0, 2, 1, -1, 0, 2 })
        {
            model.Key = index;
            Layout(host);
            Assert.Equal(index, list.SelectedIndex);
        }
        Assert.Same(marker, NavigationMotion.GetIndicator(list));
        Assert.False(marker.IsHitTestVisible);
        model.Key = -1;
        Assert.Equal(Visibility.Hidden, marker.Visibility);
        list.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        NavigationMotion.SetIndicator(list, null);
        Assert.False(translation.HasAnimatedProperties);
        Assert.Same(translation, marker.RenderTransform);
        Assert.Equal(2, translation.X);
        Assert.Equal(6, translation.Y);
        Assert.Equal(Visibility.Visible, marker.Visibility);
        Assert.NotNull(BindingOperations.GetBindingExpression(list, Selector.SelectedIndexProperty));
        model.Key = 1;
        Assert.Equal(1, list.SelectedIndex);
        list.SelectedIndex = 2;
        Assert.Equal(2, model.Key);
    }

    /// <summary>离屏应用生产模板和布局，避免启动窗口、计时器或业务服务。</summary>
    private static void Layout(Border host)
    {
        host.Measure(new Size(720, 520));
        host.Arrange(new Rect(0, 0, 720, 520));
        host.UpdateLayout();
    }

    /// <summary>仅向绑定发出状态变化，用于检查动效没有替换表达式。</summary>
    private sealed class MotionModel : INotifyPropertyChanged
    {
        private double _opacity = 0.72;
        private int _key;
        public event PropertyChangedEventHandler? PropertyChanged;
        public double Opacity
        {
            get => _opacity;
            set { _opacity = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Opacity))); }
        }
        public int Key
        {
            get => _key;
            set { _key = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Key))); }
        }
    }
}
