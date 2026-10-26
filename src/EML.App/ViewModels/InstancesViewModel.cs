using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
using EML.Core.Minecraft;

namespace EML.App.ViewModels;

/// <summary>负责列表和命令状态；扫描、磁盘写入和窗口交互分别交给对应接口。</summary>
public partial class InstancesViewModel : ObservableObject
{
    private readonly IMinecraftInstanceLocator _locator;
    private readonly IMinecraftInstanceManager _manager;
    private readonly IInstanceDialogs _dialogs;
    public string InstancesRoot { get; }
    public ObservableCollection<MinecraftInstance> Instances { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(DeleteCommand), nameof(ExportCommand))]
    private MinecraftInstance? selectedInstance;
    [ObservableProperty] private string statusText = "正在准备检测本地实例。";
    [ObservableProperty] private string newName = "";
    [ObservableProperty] private double exportPercent;
    [ObservableProperty] private string exportStatus = "";
    [ObservableProperty] private bool isExporting;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(RenameCommand), nameof(DeleteCommand), nameof(ExportCommand))]
    private bool isBusy;
    public bool CanInteract => !IsBusy;

    /// <summary>接收依赖，构造时不扫描磁盘；首次扫描由窗口显示事件触发。</summary>
    public InstancesViewModel(IMinecraftInstanceLocator locator, IMinecraftInstanceManager manager,
        IInstanceDialogs dialogs, string instancesRoot)
    {
        _locator = locator;
        _manager = manager;
        _dialogs = dialogs;
        InstancesRoot = instancesRoot;
    }

    /// <summary>切换实例后，将当前显示名称填入重命名输入框。</summary>
    partial void OnSelectedInstanceChanged(MinecraftInstance? value) => NewName = value?.Name ?? "";
    /// <summary>写入操作要求已选择实例且没有其他操作正在执行。</summary>
    private bool CanManage() => !IsBusy && SelectedInstance is not null;
    /// <summary>扫描期间和写入期间禁止重复刷新。</summary>
    private bool CanRefresh() => !IsBusy;

    /// <summary>刷新列表并保留选择；异常显示在状态栏。</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在扫描实例……";
        try
        {
            await ReloadAsync(SelectedInstance?.InstancePath);
            StatusText = $"找到 {Instances.Count} 个实例。识别结果不代表文件完整或可直接启动。";
        }
        catch (Exception ex) { StatusText = "扫描失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>修改目录和 Prism 名称，成功后按新路径重新选择实例。</summary>
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RenameAsync()
    {
        if (!CanManage()) return;
        var instance = SelectedInstance!;
        var name = NewName;
        IsBusy = true;
        try
        {
            var path = await _manager.RenameAsync(instance.InstancePath, name);
            await ReloadAfterChangeAsync(path, "已重命名为：" + name);
        }
        catch (Exception ex) { StatusText = "重命名失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>先显示确认窗口，再请求系统回收选中实例；取消不会执行删除。</summary>
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task DeleteAsync()
    {
        if (!CanManage()) return;
        var instance = SelectedInstance!;
        IsBusy = true;
        try
        {
            if (!_dialogs.ConfirmDelete(instance)) return;
            await _manager.DeleteAsync(instance.InstancePath);
            await ReloadAfterChangeAsync(null, "实例已删除，回收结果以 Windows 系统处理为准。");
        }
        catch (OperationCanceledException) { StatusText = "已取消删除。"; }
        catch (Exception ex) { StatusText = "删除失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>选择目标并导出完整 ZIP，异步命令生成取消按钮，进度回到 UI 线程更新。</summary>
    [RelayCommand(CanExecute = nameof(CanManage), IncludeCancelCommand = true)]
    private async Task ExportAsync(CancellationToken token)
    {
        if (!CanManage()) return;
        var instance = SelectedInstance!;
        IsBusy = true;
        try
        {
            var path = _dialogs.ChooseExportPath(instance);
            if (path is null) return;
            IsExporting = true;
            ExportPercent = 0;
            ExportStatus = "正在整理实例文件……";
            StatusText = "正在导出，请勿修改实例文件。";
            var progress = new Progress<InstanceExportProgress>(p =>
            {
                // 忽略任务结束后才到达 UI 队列的进度事件。
                if (!IsExporting) return;
                ExportPercent = p.Percent;
                ExportStatus = $"{p.Percent:F1}%  {p.FileName}";
            });
            await _manager.ExportAsync(instance.InstancePath, path, progress, token);
            ExportPercent = 100;
            ExportStatus = "导出完成";
            StatusText = $"已导出：{path}。仅包含实例内文件，不包含外部共享依赖。";
        }
        catch (OperationCanceledException) { ExportStatus = "已取消导出"; StatusText = "未生成目标 ZIP。"; }
        catch (Exception ex) { ExportStatus = "导出失败"; StatusText = ex.Message; }
        finally { IsExporting = false; IsBusy = false; }
    }

    /// <summary>扫描完成后一次性替换集合，避免扫描失败时破坏当前列表。</summary>
    private async Task ReloadAsync(string? selectedPath)
    {
        var result = await _locator.FindAllAsync();
        Instances.Clear();
        foreach (var instance in result) Instances.Add(instance);
        SelectedInstance = Instances.FirstOrDefault(i => string.Equals(i.InstancePath, selectedPath,
            StringComparison.OrdinalIgnoreCase)) ?? Instances.FirstOrDefault();
    }

    /// <summary>区分磁盘修改成功和后续刷新失败，避免误导用户重复执行写入操作。</summary>
    private async Task ReloadAfterChangeAsync(string? path, string success)
    {
        try { await ReloadAsync(path); StatusText = success; }
        catch (Exception ex) { StatusText = success + "；但刷新失败，请手动刷新：" + ex.Message; }
    }
}
