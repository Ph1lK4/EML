using System.Windows.Controls;

namespace EML.App.Views;

/// <summary>独立新实例下载页面，业务状态与请求由 ViewModel 管理。</summary>
public partial class InstanceDownloadsView : UserControl
{
    /// <summary>加载共享主题下的目录与版本控件，不在构造时发起网络请求。</summary>
    public InstanceDownloadsView() => InitializeComponent();
}
