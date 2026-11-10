using System.Windows.Controls;

namespace EML.App.Views;

/// <summary>展示 Java 检测结果与详情，不改变实例启动配置。</summary>
public partial class JavaEnvironmentView : UserControl
{
    /// <summary>初始化页面布局，检测和登录流程由 ViewModel 管理。</summary>
    public JavaEnvironmentView() => InitializeComponent();
}
