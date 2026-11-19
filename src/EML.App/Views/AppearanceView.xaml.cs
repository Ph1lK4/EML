using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using EML.App.ViewModels;

namespace EML.App.Views;

/// <summary>外观页只将滑块操作结束事件交给模型，业务和持久化留在服务层。</summary>
public partial class AppearanceView : UserControl
{
    /// <summary>加载共享主题下的外观设置控件。</summary>
    public AppearanceView() => InitializeComponent();

    /// <summary>拖动结束立即保存，不在每一帧写入磁盘。</summary>
    private void OnSliderCommitted(object sender, DragCompletedEventArgs e) => SavePreview();

    /// <summary>键盘方向键、Home 和 End 调整同样持久化。</summary>
    private void OnSliderKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageDown or Key.PageUp) SavePreview();
    }

    /// <summary>点击轨道或离开滑块时补存，覆盖未产生拖动事件的调整。</summary>
    private void OnSliderLostFocus(object sender, KeyboardFocusChangedEventArgs e) => SavePreview();

    /// <summary>没有模型时不执行保存，兼容设计器和只读预览。</summary>
    private void SavePreview()
    {
        if (DataContext is AppearanceViewModel model) model.SavePreviewCommand.Execute(null);
    }
}
