using System.Windows.Controls;

namespace EML.App.Views;

/// <summary>统一展示离线与正版启动身份，两个入口的选择与名称均由共享模型维护。</summary>
public partial class LaunchIdentitySelector : UserControl
{
    /// <summary>加载绑定和主题，不保存设置、不登录账号或创建游戏进程。</summary>
    public LaunchIdentitySelector() => InitializeComponent();
}
