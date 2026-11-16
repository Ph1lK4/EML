using System.Windows.Controls;

namespace EML.App.Views;

/// <summary>承载实例右键菜单打开的工作区，沿用原有实例命令与页面索引。</summary>
public partial class InstanceWorkspaceView : UserControl
{
    /// <summary>初始化绑定布局，不在视图中执行文件操作。</summary>
    public InstanceWorkspaceView() => InitializeComponent();
}
