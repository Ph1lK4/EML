using System.Windows.Controls;

namespace EML.App.Views;

/// <summary>独立展示当前实例的启动设置，继续使用原有加载、校验和保存命令。</summary>
public partial class LaunchSettingsView : UserControl
{
    /// <summary>初始化页面绑定，不自动写入设置或启动游戏。</summary>
    public LaunchSettingsView() => InitializeComponent();
}
