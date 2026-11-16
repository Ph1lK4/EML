using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EML.App.Services;

namespace EML.App.Views;

/// <summary>在线模组页只提供绑定布局，查询与安装状态由独立 ViewModel 管理。</summary>
public partial class OnlineModsView : UserControl
{
    /// <summary>初始化页面，不在构造期间发起网络请求。</summary>
    public OnlineModsView() => InitializeComponent();
}

/// <summary>为版本与加载器选项提供中文展示，不更改绑定对象中的平台值。</summary>
public sealed class OnlineModLabelConverter : IValueConverter
{
    /// <summary>通过参数区分版本通道和加载器，未知转换类型保持原文。</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (parameter as string) switch
    {
        "Channel" => OnlineModDisplayText.Channel(value as string),
        "Loader" => OnlineModDisplayText.Loader(value as string),
        _ => value
    };

    /// <summary>标签仅用于单向展示，不允许把中文显示值回写到筛选模型。</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
