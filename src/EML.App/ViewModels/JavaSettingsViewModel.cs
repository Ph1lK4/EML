using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
using EML.Core.Java;

namespace EML.App.ViewModels;

/// <summary>提供 Java 列表、当前选择和检测命令，供 WPF 页面绑定。</summary>
public partial class JavaSettingsViewModel : ObservableObject
{
    private readonly IJavaLocator _javaLocator;
    private readonly IOperationNotifications _notifications;

    [ObservableProperty]
    private ObservableCollection<JavaInstallation> installations = [];

    [ObservableProperty]
    private JavaInstallation? selectedInstallation;

    [ObservableProperty]
    private string statusText = "尚未检测 Java";

    /// <summary>接收 Java 检测接口，让界面无需了解注册表或磁盘搜索的具体实现。</summary>
    public JavaSettingsViewModel(IJavaLocator javaLocator, IOperationNotifications? notifications = null)
    {
        _javaLocator = javaLocator;
        _notifications = notifications ?? NullOperationNotifications.Instance;
    }

    /// <summary>检测 Java 并更新列表，默认选中首项；失败时显示原因。</summary>
    // 生成供按钮和启动扫描调用的 DetectJavaCommand。
    [RelayCommand]
    private async Task DetectJavaAsync()
    {
        try
        {
            StatusText = "正在检测 Java……";
            Installations.Clear();

            var results = await _javaLocator.FindAllAsync();

            foreach (var installation in results)
            {
                Installations.Add(installation);
            }

            SelectedInstallation = Installations.FirstOrDefault();

            StatusText = Installations.Count == 0
                ? "没有找到可用的 Java"
                : $"找到 {Installations.Count} 个 Java 环境";
            if (Installations.Count == 0) _notifications.Warning("Java 检测完成", StatusText + "，请安装适合整合包的 Java 后重新检测。");
            else _notifications.Success("Java 检测完成", StatusText, Installations.Select(item => item.ExecutablePath).ToArray());
        }
        catch (Exception exception)
        {
            StatusText = $"检测失败：{exception.Message}";
            _notifications.Failure("Java 检测失败", exception);
        }
    }
}
