using System.Windows.Controls;

namespace EML.App.Views;

/// <summary>展示共享文件下载队列，操作与线程切换交由页面模型管理。</summary>
public partial class DownloadCenterView : UserControl
{
    /// <summary>加载主题和任务列表，不在构造时启动或取消文件下载。</summary>
    public DownloadCenterView() => InitializeComponent();
}
