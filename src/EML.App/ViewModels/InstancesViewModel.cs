using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Launching;
using EML.Core.Minecraft.Migration;
using EML.Core.Minecraft.Mods;
using EML.Core.Minecraft.Mods.Online;
using EML.Core.Minecraft.Gtnh;
using EML.Core.Minecraft.Catalog;
using EML.Core.Downloads;

namespace EML.App.ViewModels;

/// <summary>负责列表和命令状态；扫描、磁盘写入和窗口交互分别交给对应接口。</summary>
public partial class InstancesViewModel : ObservableObject
{
    private readonly IMinecraftInstanceLocator _locator;
    private readonly IMinecraftInstanceManager _manager;
    private readonly IMinecraftInstanceImporter _importer;
    private readonly IInstanceDialogs _dialogs;
    private readonly IOperationNotifications _notifications;
    // 区分本次和上一次任务，避免迟到的进度事件改写当前界面。
    private int _transferRevision;
    /// <summary>页面上显示的实例扫描根目录。</summary>
    public string InstancesRoot { get; }
    /// <summary>供列表绑定的扫描结果；增删会通知界面刷新。</summary>
    public ObservableCollection<MinecraftInstance> Instances { get; } = [];
    /// <summary>独立在线目录，不在启动检测时查询远程项目。</summary>
    public OnlineModsViewModel? OnlineMods { get; }
    /// <summary>GTNH 专区把推荐模组与迁移组织在同一目标实例下。</summary>
    public GtnhHubViewModel? Gtnh { get; }
    /// <summary>两个启动入口共用账号页的公开档案，登录、更换或退出后玩家名自动更新。</summary>
    public MinecraftAccountViewModel? Accounts { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(DeleteCommand), nameof(ExportCommand),
        nameof(QuickCheckCommand), nameof(FullCheckCommand), nameof(DownloadMissingCommand),
        nameof(LaunchGameCommand), nameof(SaveLaunchSettingsCommand), nameof(ReloadLaunchSettingsCommand),
        nameof(MigratePlayerDataCommand), nameof(RestoreMigrationCommand))]
    private MinecraftInstance? selectedInstance;
    [ObservableProperty] private string statusText = "正在准备检测本地实例。";
    [ObservableProperty] private string newName = "";
    [ObservableProperty] private double transferPercent;
    [ObservableProperty] private string transferStatus = "";
    [ObservableProperty] private bool isExporting;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract), nameof(CanEditLaunchSettings), nameof(CanEditOfflinePlayerName))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(RenameCommand), nameof(DeleteCommand), nameof(ExportCommand), nameof(ImportCommand),
        nameof(QuickCheckCommand), nameof(FullCheckCommand), nameof(DownloadMissingCommand),
        nameof(LaunchGameCommand), nameof(SaveLaunchSettingsCommand), nameof(ReloadLaunchSettingsCommand),
        nameof(MigratePlayerDataCommand), nameof(RestoreMigrationCommand))]
    private bool isBusy;
    /// <summary>没有实例操作正在执行时，允许修改输入和列表选择。</summary>
    public bool CanInteract => !IsBusy;

    /// <summary>接收依赖，构造时不扫描磁盘；首次扫描由窗口显示事件触发。</summary>
    public InstancesViewModel(IMinecraftInstanceLocator locator, IMinecraftInstanceManager manager,
        IMinecraftInstanceImporter importer, IInstanceDialogs dialogs, string instancesRoot,
        IMinecraftInstanceChecker checker, IMinecraftInstanceDownloader instanceDownloader,
        IMinecraftGameLauncher? launcher = null, IMinecraftLaunchSettingsStore? launchSettingsStore = null,
        JavaSettingsViewModel? java = null, IMinecraftInstanceMigrationService? migration = null,
        IInstanceMigrationDialogs? migrationDialogs = null,
        ILocalModManager? localMods = null, IModManagementDialogs? modDialogs = null,
        IOnlineModCatalog? onlineCatalog = null, IOnlineModInstaller? onlineInstaller = null,
        IModIconCache? modIcons = null, Action<Uri>? openModWebsite = null,
        IOnlineModLocalization? modLocalization = null, IGtnhAddonService? gtnhAddons = null,
        IOperationNotifications? notifications = null, MinecraftAccountViewModel? accounts = null,
        IGtnhPackService? gtnhPacks = null)
    {
        _locator = locator;
        _manager = manager;
        _importer = importer;
        _dialogs = dialogs;
        _notifications = notifications ?? NullOperationNotifications.Instance;
        InstancesRoot = instancesRoot;
        _checker = checker;
        _instanceDownloader = instanceDownloader;
        _launcher = launcher;
        _launchSettingsStore = launchSettingsStore;
        Java = java;
        Accounts = accounts;
        _migration = migration; _migrationDialogs = migrationDialogs;
        _localMods = localMods; _modDialogs = modDialogs;
        if (onlineCatalog is not null && onlineInstaller is not null)
            OnlineMods = new OnlineModsViewModel(onlineCatalog, onlineInstaller, modIcons,
                () => !IsBusy, busy => IsBusy = busy,
                () => { ClearCheckResults(); ResetModSelection(); }, openModWebsite, modLocalization, notifications: _notifications);
        if (migration is not null && gtnhAddons is not null)
            Gtnh = new GtnhHubViewModel(this, migration, gtnhAddons,
                () => { ClearCheckResults(); ResetModSelection(); }, openModWebsite, notifications: _notifications, packs: gtnhPacks);
    }

    /// <summary>切换实例后更新名称，并清除属于上一个实例的检查和下载记录。</summary>
    partial void OnSelectedInstanceChanged(MinecraftInstance? value)
    {
        NewName = value?.Name ?? "";
        ClearCheckResults();
        BeginLoadLaunchSettings(value);
        MigrationJournalPath = "";
        MigrationStateLoadTask = LoadMigrationStateAsync(value);
        ResetModSelection();
        OnlineMods?.SetInstance(value);
    }
    /// <summary>判断当前是否允许重命名、删除或导出选中的实例。</summary>
    private bool CanManage() => !IsBusy && SelectedInstance is not null;
    /// <summary>判断是否空闲，供刷新和导入命令决定能否执行。</summary>
    private bool CanRefresh() => !IsBusy;

    /// <summary>刷新列表并保留选择；异常显示在状态栏。</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        ClearCheckResults();
        IsBusy = true;
        StatusText = "正在扫描实例……";
        try
        {
            await ReloadAsync(SelectedInstance?.InstancePath);
            StatusText = $"找到 {Instances.Count} 个实例。识别结果不代表文件完整或可直接启动。";
            _notifications.Success("实例扫描完成", StatusText, InstancesRoot);
        }
        catch (Exception ex) { StatusText = "扫描失败：" + ex.Message; _notifications.Failure("实例扫描失败", ex, InstancesRoot); }
        finally { IsBusy = false; }
    }

    /// <summary>修改目录和 Prism 名称，成功后按新路径重新选择实例。</summary>
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RenameAsync()
    {
        if (!CanManage()) return;
        var instance = SelectedInstance!;
        var name = NewName;
        ClearCheckResults();
        IsBusy = true;
        try
        {
            var path = await _manager.RenameAsync(instance.InstancePath, name);
            var refreshed = await ReloadAfterChangeAsync(path, "已重命名为：" + name);
            _notifications.Report(CreateOperationNotice(refreshed ? OperationNoticeKind.Success : OperationNoticeKind.Warning,
                refreshed ? "重命名完成" : "已重命名，列表刷新未完成", StatusText, [instance.InstancePath, path]));
        }
        catch (Exception ex) { StatusText = "重命名失败：" + ex.Message; _notifications.Failure("重命名失败", ex, instance.InstancePath); }
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
            ClearCheckResults();
            await _manager.DeleteAsync(instance.InstancePath);
            var refreshed = await ReloadAfterChangeAsync(null, "实例已删除，回收结果以 Windows 系统处理为准。");
            _notifications.Report(CreateOperationNotice(refreshed ? OperationNoticeKind.Success : OperationNoticeKind.Warning,
                refreshed ? "删除操作完成" : "已删除，列表刷新未完成", StatusText, [instance.InstancePath]));
        }
        catch (OperationCanceledException) { StatusText = "已取消删除。"; _notifications.Cancelled("删除已取消", StatusText, instance.InstancePath); }
        catch (Exception ex) { StatusText = "删除失败：" + ex.Message; _notifications.Failure("删除失败", ex, instance.InstancePath); }
        finally { IsBusy = false; }
    }

    /// <summary>选择保存位置并导出 ZIP，更新进度并处理取消或失败。</summary>
    [RelayCommand(CanExecute = nameof(CanManage), IncludeCancelCommand = true)]
    private async Task ExportAsync(CancellationToken token)
    {
        if (!CanManage()) return;
        var instance = SelectedInstance!;
        string? path = null;
        IsBusy = true;
        try
        {
            path = _dialogs.ChooseExportPath(instance);
            if (path is null) return;
            IsExporting = true;
            TransferPercent = 0;
            TransferStatus = "正在整理实例文件……";
            var revision = ++_transferRevision;
            StatusText = "正在导出，请勿修改实例文件。";
            var progress = new Progress<InstanceExportProgress>(p =>
            {
                // 忽略任务结束后才到达 UI 队列的进度事件。
                if (!IsExporting || revision != _transferRevision) return;
                TransferPercent = p.Percent;
                TransferStatus = $"{p.Percent:F1}%  {p.FileName}";
            });
            await _manager.ExportAsync(instance.InstancePath, path, progress, token);
            TransferPercent = 100;
            TransferStatus = "导出完成";
            StatusText = $"已导出：{path}。仅包含实例内文件，不包含外部共享依赖。";
            _notifications.Success("实例导出完成", StatusText, path, instance.InstancePath);
        }
        catch (OperationCanceledException) { TransferStatus = "已取消导出"; StatusText = "未生成目标 ZIP。"; _notifications.Cancelled("导出已取消", StatusText, path, instance.InstancePath); }
        catch (Exception ex) { TransferStatus = "导出失败"; StatusText = ex.Message; _notifications.Failure("导出失败", ex, path, instance.InstancePath); }
        finally { ++_transferRevision; IsExporting = false; IsBusy = false; }
    }

    /// <summary>预览并导入新目录，再自动补全支持格式的运行依赖；下载失败保留已导入实例。</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh), IncludeCancelCommand = true)]
    private async Task ImportAsync(CancellationToken token)
    {
        if (IsBusy) return;
        IsBusy = true;
        string? archive = null;
        try
        {
            archive = _dialogs.ChooseImportPath();
            if (archive is not null) await ImportArchiveCoreAsync(archive, token);
        }
        catch (OperationCanceledException) { TransferStatus = "已取消导入"; StatusText = "没有新增实例。"; _notifications.Cancelled("导入已取消", StatusText, archive, InstancesRoot); }
        catch (Exception ex) { TransferStatus = "导入失败"; StatusText = ex.Message; _notifications.Failure("导入失败", ex, archive, InstancesRoot); }
        finally { IsBusy = false; }
    }

    /// <summary>在线下载、导入和补全共用一段忙碌期间；任何阶段取消都保留已提交实例。</summary>
    public Task<string> DownloadInstanceAsync(IInstanceArchiveService archives, InstanceCatalogProvider provider,
        string projectId, string releaseId, IProgress<FileDownloadProgress>? progress, CancellationToken token) =>
        DownloadInstanceAsync((downloadProgress, cancellation) =>
            archives.PrepareAsync(provider, projectId, releaseId, downloadProgress, cancellation), progress, token);

    /// <summary>平台下载和 GTNH 镜像共用导入及补全流程，从原包准备到最终结果持续持有实例操作锁。</summary>
    public async Task<string> DownloadInstanceAsync(
        Func<IProgress<FileDownloadProgress>?, CancellationToken, Task<PreparedInstanceArchive>> prepare,
        IProgress<FileDownloadProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        if (IsBusy) throw new InvalidOperationException("正在执行实例操作，请等待完成。");
        IsBusy = true;
        ClearCheckResults();
        TransferPercent = 0;
        TransferStatus = "正在下载已选版本……";
        try
        {
            var prepared = await prepare(progress, token);
            token.ThrowIfCancellationRequested();
            var imported = await ImportArchiveCoreAsync(prepared.ArchivePath, token, prepared.ExpectedFormat, prepared.SuggestedName);
            return TransferStatus + "\n" + StatusText + (imported ? "\n" + DownloadSummary : "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            TransferStatus = "已取消实例下载";
            StatusText = "尚未导入新实例；已经校验的原包会保留在缓存中。";
            _notifications.Cancelled("实例下载已取消", StatusText, InstancesRoot);
            return StatusText;
        }
        catch (Exception ex)
        {
            TransferStatus = "实例下载未完成";
            _notifications.Failure("实例下载未完成", ex, InstancesRoot);
            return StatusText = ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>本地导入与在线下载共用预览、命名、提交和自动补全；调用方持续持有忙碌状态。</summary>
    private async Task<bool> ImportArchiveCoreAsync(string archive, CancellationToken token, string? expectedFormat = null, string? suggestedName = null)
    {
        var revision = ++_transferRevision;
        InstanceImportResult? imported = null;
        try
        {
            TransferPercent = 0;
            TransferStatus = "正在分析整合包……";
            var preview = await _importer.InspectAsync(archive, token);
            if (expectedFormat is not null && !preview.Format.Equals(expectedFormat, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"下载来源与压缩包格式不符：预期 {expectedFormat}，实际 {preview.Format}。");
            if (!string.IsNullOrWhiteSpace(suggestedName)) preview = preview with { SuggestedName = suggestedName };
            token.ThrowIfCancellationRequested();
            var name = _dialogs.ConfirmImport(preview);
            if (name is null)
            {
                TransferStatus = "已取消导入"; StatusText = "没有新增实例。";
                _notifications.Cancelled("导入已取消", StatusText, archive);
                return false;
            }
            StatusText = "正在导入包内文件……";
            var progress = new Progress<InstanceImportProgress>(p =>
            {
                // 只接收当前任务的进度；已结束任务排队中的回调直接忽略。
                if (revision != _transferRevision || !IsBusy) return;
                TransferPercent = p.Percent;
                TransferStatus = $"{p.Percent:F1}%  {p.FileName}";
            });
            imported = await _importer.ImportAsync(archive, name, progress, token);
            ++_transferRevision;
            TransferPercent = 100;
            TransferStatus = "导入完成";
            var refreshed = await ReloadAfterChangeAsync(imported.InstancePath, "实例文件已导入。");
            var refreshMessage = refreshed ? null : StatusText;
            SelectImportedInstance(imported);
            SelectedDetailTab = 2;
            var sourceFormat = imported.Manifest.SourceFormat;
            if (sourceFormat.Equals("Prism", StringComparison.OrdinalIgnoreCase) ||
                 sourceFormat.Equals("Modrinth", StringComparison.OrdinalIgnoreCase) ||
                 sourceFormat.Equals("CurseForge", StringComparison.OrdinalIgnoreCase) ||
                 sourceFormat.Equals("Zip", StringComparison.OrdinalIgnoreCase))
            {
                // 补全始终使用导入结果中的路径，不依赖扫描成功，也不在两阶段之间释放忙碌状态。
                var notice = await RunDownloadAsync(imported.InstancePath, token, afterImport: true);
                if (refreshMessage is not null)
                    notice = notice with { Kind = notice.Kind == OperationNoticeKind.Success ? OperationNoticeKind.Warning : notice.Kind,
                        Message = notice.Message + "\n" + refreshMessage };
                _notifications.Report(notice with { Paths = notice.Paths.Append(archive).ToArray() });
            }
            else
            {
                StatusText += " 当前格式已保存包内文件；可点击“补全实例依赖”继续检查。";
                _notifications.Warning("实例已导入，运行依赖尚未检查", StatusText, archive, imported.InstancePath);
            }
        }
        catch (OperationCanceledException)
        {
            TransferStatus = imported is null ? "已取消导入" : "实例已导入，后续操作已取消";
            StatusText = imported is null ? "没有新增实例。" : "已导入实例保留，可点击“补全实例依赖”继续。";
            _notifications.Cancelled(TransferStatus, StatusText, archive, imported?.InstancePath ?? InstancesRoot);
        }
        catch (Exception ex)
        {
            TransferStatus = imported is null ? "导入失败" : "实例已导入，后续操作未完成";
            StatusText = (imported is null ? "" : "已导入实例保留，可点击“补全实例依赖”继续。") + ex.Message;
            if (imported is null) _notifications.Failure(TransferStatus, ex, archive, InstancesRoot);
            else _notifications.Warning(TransferStatus, StatusText, archive, imported.InstancePath, NoticeExceptionPath(ex));
        }
        finally { ++_transferRevision; }
        return imported is not null;
    }

    /// <summary>刷新失败或扫描暂未发现时仍展示已提交实例，保证自动补全结果属于正确条目。</summary>
    private void SelectImportedInstance(InstanceImportResult imported)
    {
        var instance = Instances.FirstOrDefault(item => string.Equals(item.InstancePath, imported.InstancePath,
            StringComparison.OrdinalIgnoreCase));
        if (instance is null)
        {
            var manifest = imported.Manifest;
            instance = new MinecraftInstance(manifest.Name, imported.InstancePath, imported.GameDirectory,
                manifest.MinecraftVersion is { } version ? [version] : [])
            {
                Format = manifest.SourceFormat,
                Warnings = ["根据导入结果显示；完整扫描信息可稍后刷新。"]
            };
            Instances.Add(instance);
        }
        SelectedInstance = instance;
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
    private async Task<bool> ReloadAfterChangeAsync(string? path, string success)
    {
        try { await ReloadAsync(path); StatusText = success; return true; }
        catch (Exception ex) { StatusText = success + "；但刷新失败，请手动刷新：" + ex.Message; return false; }
    }

    /// <summary>汇总一次用户操作的结果，保留路径原文并移除空值及重复项。</summary>
    private static OperationNotice CreateOperationNotice(OperationNoticeKind kind, string title, string message, IEnumerable<string?> paths) =>
        new(kind, title, message, paths.Where(path => !string.IsNullOrEmpty(path)).Select(path => path!).Distinct(StringComparer.Ordinal).ToArray());
}
