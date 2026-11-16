using System.Windows.Controls;

namespace EML.App.Views;

/// <summary>国内镜像本体下载页，联网与安装均由绑定的页面模型处理。</summary>
public partial class GtnhPackDownloadView : UserControl
{
    /// <summary>只加载页面与主题，不读取目录或启动后台任务。</summary>
    public GtnhPackDownloadView() => InitializeComponent();
}
