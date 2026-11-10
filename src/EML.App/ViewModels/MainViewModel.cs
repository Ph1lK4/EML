using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EML.App.ViewModels;

/// <summary>组织启动器主页面与单实例工作区；页面导航不改变运行中的文件任务。</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private int _selectedPage;
    private bool _instanceWorkspaceOpen;
    private bool _disposed;

    /// <summary>组合已有功能，并让实例面板与在线模组主页面分别管理可见状态。</summary>
    public MainViewModel(JavaSettingsViewModel java, InstancesViewModel instances, MinecraftAccountViewModel? accounts = null,
        InstanceDownloadsViewModel? downloads = null, DownloadCenterViewModel? transferCenter = null)
    {
        Java = java; Instances = instances; Accounts = accounts;
        Downloads = downloads;
        TransferCenter = transferCenter;
        Instances.SelectedDetailTab = -1;
        Instances.OnlineMods?.SetActive(false);
        Instances.PropertyChanged += OnInstancesChanged;
    }

    public JavaSettingsViewModel Java { get; }
    public InstancesViewModel Instances { get; }
    public MinecraftAccountViewModel? Accounts { get; }
    public GtnhHubViewModel? Gtnh => Instances.Gtnh;
    public InstanceDownloadsViewModel? Downloads { get; }
    public DownloadCenterViewModel? TransferCenter { get; }

    /// <summary>独立主页面保留既有索引，启动设置使用单独页面而不依赖菜单位置。</summary>
    public int SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (_disposed || value is < 0 or > 7 || !SetProperty(ref _selectedPage, value)) return;
            if (value != 0) CloseInstanceWorkspace();
            Instances.OnlineMods?.SetActive(value == 1);
            Gtnh?.SetActive(value == 4);
            Downloads?.SetActive(value == 6);
            OnPropertyChanged(nameof(IsInstancePage)); OnPropertyChanged(nameof(IsOnlinePage));
            OnPropertyChanged(nameof(IsAccountsPage)); OnPropertyChanged(nameof(IsJavaPage));
            OnPropertyChanged(nameof(IsGtnhPage));
            OnPropertyChanged(nameof(IsLaunchSettingsPage));
            OnPropertyChanged(nameof(IsDownloadsPage));
            OnPropertyChanged(nameof(IsDownloadCenterPage));
            OnPropertyChanged(nameof(IsWorkspaceVisible));
        }
    }

    public bool IsInstancePage => SelectedPage == 0;
    public bool IsOnlinePage => SelectedPage == 1;
    public bool IsAccountsPage => SelectedPage == 2;
    public bool IsJavaPage => SelectedPage == 3;
    public bool IsGtnhPage => SelectedPage == 4;
    public bool IsLaunchSettingsPage => SelectedPage == 5;
    public bool IsDownloadsPage => SelectedPage == 6;
    public bool IsDownloadCenterPage => SelectedPage == 7;

    /// <summary>工作区可以保留后台任务产生的结果，但只在本地实例页面显示。</summary>
    public bool InstanceWorkspaceOpen
    {
        get => _instanceWorkspaceOpen;
        private set
        {
            if (SetProperty(ref _instanceWorkspaceOpen, value)) OnPropertyChanged(nameof(IsWorkspaceVisible));
        }
    }

    public bool IsWorkspaceVisible => InstanceWorkspaceOpen && IsInstancePage;
    public string WorkspaceTitle => Instances.SelectedDetailTab switch
    {
        1 => "文件检查", 2 => "补全实例依赖", 3 => "启动设置", 4 => "运行日志",
        5 => "GTNH 数据迁移", 6 => "模组管理", 7 => "重命名实例", _ => "实例详情"
    };

    /// <summary>由已选中实例的右键菜单打开指定工作区，不直接执行删除或下载等操作。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInstanceSection))]
    private void OpenInstanceSection(string? section)
    {
        if (!CanOpenInstanceSection(section)) return;
        if (section == "launch")
        {
            OpenLaunchSettings();
            return;
        }
        SelectedPage = 0;
        Instances.SelectedDetailTab = SectionIndex(section);
        InstanceWorkspaceOpen = true;
    }

    /// <summary>启动设置使用独立大页面，保留当前实例和未保存输入，不触发实例读写。</summary>
    [RelayCommand]
    private void OpenLaunchSettings()
    {
        if (_disposed) return;
        SelectedPage = 5;
        CloseInstanceWorkspace();
    }

    /// <summary>历史迁移入口转到独立专区，不再打开单实例弹出面板。</summary>
    [RelayCommand]
    private void OpenGtnhMigration()
    {
        if (_disposed) return;
        SelectedPage = 4;
        Gtnh?.ShowMigrationCommand.Execute(null);
    }

    /// <summary>关闭展示面板并停止安排隐藏的模组列表读取，保留已有下载和启动任务。</summary>
    [RelayCommand]
    private void CloseInstanceWorkspace()
    {
        InstanceWorkspaceOpen = false;
        Instances.SelectedDetailTab = -1;
    }

    /// <summary>没有明确实例或入口名称时不能打开工作区，防止操作沿用旧选择。</summary>
    private bool CanOpenInstanceSection(string? section) => !_disposed && Instances.SelectedInstance is not null && SectionIndex(section) >= 0;

    /// <summary>集中维护已有实例功能索引；在线模组不再属于实例详情索引。</summary>
    private static int SectionIndex(string? section) => section switch
    {
        "details" => 0, "check" => 1, "download" => 2, "launch" => 3,
        "logs" => 4, "mods" => 6, "rename" => 7, _ => -1
    };

    /// <summary>检查、导入和启动可打开结果面板，但不会把用户从其他主页面强制切走。</summary>
    private void OnInstancesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName == nameof(InstancesViewModel.IsBusy)) Downloads?.NotifyHostStateChanged();
        if (e.PropertyName == nameof(InstancesViewModel.SelectedDetailTab))
        {
            OnPropertyChanged(nameof(WorkspaceTitle));
            if (Instances.SelectedDetailTab is >= 0 and <= 7 && Instances.SelectedInstance is not null)
                InstanceWorkspaceOpen = true;
        }
        else if (e.PropertyName == nameof(InstancesViewModel.SelectedInstance))
        {
            OpenInstanceSectionCommand.NotifyCanExecuteChanged();
            if (Instances.SelectedInstance is null) CloseInstanceWorkspace();
        }
    }

    /// <summary>窗口关闭后解除实例事件订阅；任务取消和资源释放仍由窗口统一处理。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Instances.PropertyChanged -= OnInstancesChanged;
        Instances.OnlineMods?.SetActive(false);
        Gtnh?.Dispose();
        Downloads?.SetActive(false);
        TransferCenter?.Dispose();
        OpenInstanceSectionCommand.NotifyCanExecuteChanged();
    }
}
