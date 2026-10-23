using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.Core.Java;

namespace EML.App.ViewModels;

public partial class JavaSettingsViewModel : ObservableObject
{
    private readonly IJavaLocator _javaLocator;

    [ObservableProperty]
    private ObservableCollection<JavaInstallation> installations = [];

    [ObservableProperty]
    private JavaInstallation? selectedInstallation;

    [ObservableProperty]
    private string statusText = "尚未检测 Java";

    public JavaSettingsViewModel(IJavaLocator javaLocator)
    {
        _javaLocator = javaLocator;
    }

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
        }
        catch (Exception exception)
        {
            StatusText = $"检测失败：{exception.Message}";
        }
    }
}