using System.Windows.Controls;

namespace EML.App.Views;

/// <summary>展示账号与网页授权信息，使用现有登录命令。</summary>
public partial class AccountsView : UserControl
{
    /// <summary>初始化页面布局，检测和登录流程由 ViewModel 管理。</summary>
    public AccountsView() => InitializeComponent();
}
