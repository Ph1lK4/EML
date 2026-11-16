using System.Windows;
using System.Windows.Controls;

namespace EML.App.Views;

/// <summary>确认框和向导共用的内容外壳；可离屏验证，不包含文件或业务操作。</summary>
public partial class DialogSurface : UserControl
{
    /// <summary>加载标题、滚动正文和固定操作栏。</summary>
    public DialogSurface() => InitializeComponent();

    public event EventHandler? CloseRequested;
    /// <summary>将关闭请求交给所属窗口，关闭始终不代表确认。</summary>
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
