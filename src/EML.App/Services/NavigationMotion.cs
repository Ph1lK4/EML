using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EML.App.Services;

/// <summary>让导航标记跟随实际选中项移动；只提供视觉反馈，不干预导航和焦点。</summary>
public static class NavigationMotion
{
    public static readonly DependencyProperty IndicatorProperty = DependencyProperty.RegisterAttached(
        "Indicator", typeof(Border), typeof(NavigationMotion), new PropertyMetadata(null, OnIndicatorChanged));

    private static readonly DependencyProperty TrackerProperty = DependencyProperty.RegisterAttached(
        "Tracker", typeof(IndicatorTracker), typeof(NavigationMotion));

    /// <summary>读取与导航列表关联的装饰标记。</summary>
    public static Border? GetIndicator(DependencyObject element) => (Border?)element.GetValue(IndicatorProperty);

    /// <summary>关联同一布局中的标记；标记使用独立的 TranslateTransform，不覆盖列表变换。</summary>
    public static void SetIndicator(DependencyObject element, Border? value) => element.SetValue(IndicatorProperty, value);

    /// <summary>更换关联时释放旧订阅，避免标记和旧页面被事件保留。</summary>
    private static void OnIndicatorChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ListBox list) return;
        (list.GetValue(TrackerProperty) as IndicatorTracker)?.Dispose();
        list.ClearValue(TrackerProperty);
        if (args.NewValue is Border indicator)
            list.SetValue(TrackerProperty, new IndicatorTracker(list, indicator));
    }

    /// <summary>仅在导航处于可视树中时跟踪位置，卸载时停止所有动画与静态事件订阅。</summary>
    private sealed class IndicatorTracker : IDisposable
    {
        private readonly ListBox _list;
        private readonly Border _indicator;
        private readonly TranslateTransform? _translation;
        private readonly double _originalY;
        private readonly Visibility _originalVisibility;
        private DispatcherOperation? _pendingUpdate;
        private bool _connected;
        private bool _positioned;

        /// <summary>保存专用标记的原始属性，并等待列表完成加载。</summary>
        internal IndicatorTracker(ListBox list, Border indicator)
        {
            _list = list;
            _indicator = indicator;
            _translation = indicator.RenderTransform as TranslateTransform;
            _originalY = _translation?.Y ?? 0;
            _originalVisibility = indicator.Visibility;
            list.Loaded += OnLoaded;
            list.Unloaded += OnUnloaded;
            if (list.IsLoaded) Connect();
        }

        /// <summary>首次显示和再次进入可视树时恢复位置跟踪。</summary>
        private void OnLoaded(object sender, RoutedEventArgs args) => Connect();

        /// <summary>卸载后不保留时钟、待执行更新或全局设置事件。</summary>
        private void OnUnloaded(object sender, RoutedEventArgs args) => Disconnect();

        /// <summary>布局完成后定位首项；初次出现不从列表顶部飞入。</summary>
        private void Connect()
        {
            if (_connected) return;
            _connected = true;
            _list.SelectionChanged += OnSelectionChanged;
            _list.SizeChanged += OnSizeChanged;
            _list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
            _list.ItemContainerGenerator.StatusChanged += OnContainersChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            Update(false);
            QueueUpdate();
        }

        /// <summary>移除运行期事件并隐藏标记，保留可重新加载的 Loaded 订阅。</summary>
        private void Disconnect()
        {
            if (!_connected) return;
            _connected = false;
            _list.SelectionChanged -= OnSelectionChanged;
            _list.SizeChanged -= OnSizeChanged;
            _list.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
            _list.ItemContainerGenerator.StatusChanged -= OnContainersChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            _pendingUpdate?.Abort();
            _pendingUpdate = null;
            if (_translation is { IsFrozen: false }) _translation.BeginAnimation(TranslateTransform.YProperty, null);
            _indicator.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
            _positioned = false;
        }

        /// <summary>只响应此列表自身的选择，嵌套控件的路由事件不会挪动标记。</summary>
        private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (!ReferenceEquals(args.OriginalSource, _list)) return;
            Update(true);
        }

        /// <summary>窗口尺寸改变时直接对齐，避免尺寸变化制造额外的移动动画。</summary>
        private void OnSizeChanged(object sender, SizeChangedEventArgs args) => Update(false);

        /// <summary>滚动时跟随选中项；选中项离开列表视口后暂时隐藏标记。</summary>
        private void OnScrollChanged(object sender, ScrollChangedEventArgs args)
        {
            if (args.VerticalChange != 0 || args.HorizontalChange != 0 || args.ViewportHeightChange != 0 ||
                args.ViewportWidthChange != 0 || args.ExtentHeightChange != 0 || args.ExtentWidthChange != 0)
                Update(false);
        }

        /// <summary>容器首次生成后等待布局，以免使用尚未计算的行高。</summary>
        private void OnContainersChanged(object? sender, EventArgs args)
        {
            if (_list.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated) QueueUpdate();
        }

        /// <summary>系统关闭动画或启用高对比度时立即落到目标位置。</summary>
        private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
                Update(false);
        }

        /// <summary>合并同一轮加载通知；不使用持续布局监听或轮询定时器。</summary>
        private void QueueUpdate()
        {
            if (!_connected || _pendingUpdate is { Status: DispatcherOperationStatus.Pending }) return;
            _pendingUpdate = _list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _pendingUpdate = null;
                if (_connected) Update(false);
            }));
        }

        /// <summary>以实际容器中心作为目标；快速连续切换从当前屏幕位置接续动画。</summary>
        private void Update(bool animate)
        {
            if (!_connected || _translation is null || _translation.IsFrozen ||
                _indicator.Parent is not FrameworkElement parent || _list.SelectedIndex < 0 ||
                _list.ItemContainerGenerator.ContainerFromIndex(_list.SelectedIndex) is not ListBoxItem item ||
                item.ActualHeight <= 0 || !item.IsVisible)
            {
                Hide();
                return;
            }

            // 只操作与列表处于同一可视树的装饰层，不为外部控件创建或覆盖变换。
            if (!parent.IsAncestorOf(item)) { Hide(); return; }
            var relativeToList = item.TranslatePoint(new Point(), _list);
            if (relativeToList.Y + item.ActualHeight <= 0 || relativeToList.Y >= _list.ActualHeight)
            {
                Hide();
                return;
            }

            var itemPosition = item.TranslatePoint(new Point(), parent);
            var markerPosition = _indicator.TranslatePoint(new Point(), parent);
            var markerHeight = _indicator.ActualHeight > 0 ? _indicator.ActualHeight : _indicator.DesiredSize.Height;
            var targetY = itemPosition.Y + (item.ActualHeight - markerHeight) / 2 - (markerPosition.Y - _translation.Y);
            var from = _translation.Y;
            _translation.BeginAnimation(TranslateTransform.YProperty, null);
            _translation.SetCurrentValue(TranslateTransform.YProperty, targetY);
            _indicator.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
            if (animate && _positioned && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast &&
                Math.Abs(from - targetY) > 0.1)
            {
                _translation.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(from, targetY, TimeSpan.FromMilliseconds(180))
                    {
                        FillBehavior = FillBehavior.Stop,
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
            }
            _positioned = true;
        }

        /// <summary>无有效目标时停止移动，防止旧选中位置留下误导性的标记。</summary>
        private void Hide()
        {
            if (_translation is { IsFrozen: false }) _translation.BeginAnimation(TranslateTransform.YProperty, null);
            _indicator.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
            _positioned = false;
        }

        /// <summary>行为解除关联时还原专用标记，不保留窗口或导航列表引用。</summary>
        public void Dispose()
        {
            Disconnect();
            _list.Loaded -= OnLoaded;
            _list.Unloaded -= OnUnloaded;
            if (_translation is { IsFrozen: false })
            {
                _translation.BeginAnimation(TranslateTransform.YProperty, null);
                _translation.SetCurrentValue(TranslateTransform.YProperty, _originalY);
            }
            _indicator.SetCurrentValue(UIElement.VisibilityProperty, _originalVisibility);
        }
    }
}
