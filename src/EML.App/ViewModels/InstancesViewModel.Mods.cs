using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods;

namespace EML.App.ViewModels;

/// <summary>本地模组按文件展示和操作；进入页面后才读取当前实例，与其他实例操作共用忙碌状态。</summary>
public partial class InstancesViewModel
{
    private readonly ILocalModManager? _localMods;
    private readonly IModManagementDialogs? _modDialogs;
    private IReadOnlyList<LocalModEntry> _allModFiles = [];
    private int _modSelectionRevision;
    private int _modScheduleRevision;
    private bool _modsNeedReload;
    private bool _modPageClosing;
    private bool _automaticModRefresh;

    [ObservableProperty] private IReadOnlyList<LocalModEntry> modFiles = [];
    [ObservableProperty] private LocalModEntry? selectedMod;
    [ObservableProperty] private string modSearchText = "";
    [ObservableProperty] private int modFilterIndex;
    [ObservableProperty] private string modListStatus = "进入此页面后读取当前实例的模组。";
    [ObservableProperty] private string modCatalogNotes = "";
    [ObservableProperty] private string modCountText = "尚未读取";
    [ObservableProperty] private string modStatus = "原包声明的模组受到保护；可以管理自己添加、接管或迁移的额外模组。";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelModOperationCommand))]
    private bool isManagingMods;

    /// <summary>筛选只作用于已读到内存的列表，不重新扫描或计算文件哈希。</summary>
    public IReadOnlyList<string> ModFilters { get; } = ["全部", "已启用", "已禁用", "已移除", "有问题"];
    /// <summary>保留当前自动读取任务，供窗口关闭和测试等待。</summary>
    public Task ModListLoadTask { get; private set; } = Task.CompletedTask;
    /// <summary>展示选中文件的完整位置、多模组标识和无法操作的原因。</summary>
    public string ModSelectionDetails => SelectedMod is not { } mod ? "选择模组可查看详情。" :
        $"{mod.Path}\n来源：{mod.SourceDescription}" +
        (mod.Identities.Count == 0 ? "" : "\n包含：" + string.Join("、", mod.Identities.Select(item => $"{item.Id} {item.Version}"))) +
        (string.IsNullOrWhiteSpace(mod.Problem) ? "" : "\n" + mod.Problem) +
        (mod.CanManage || mod.CanAdopt || mod.CanRestore ? "" : "\n此文件当前仅供查看；原包声明的文件不能在此禁用或移除。");

    /// <summary>切换实例时使旧扫描失效，且不在未打开模组页面时扫描磁盘。</summary>
    private void ResetModSelection()
    {
        ++_modSelectionRevision;
        RefreshModsCommand.Cancel();
        _allModFiles = []; SelectedMod = null;
        _modsNeedReload = SelectedInstance is not null;
        ModListStatus = SelectedInstance is null ? "请先选择实例。" : "尚未读取此实例的模组。";
        ModCatalogNotes = "";
        ModStatus = "原包声明的模组受到保护；可以管理自己添加、接管或迁移的额外模组。";
        ApplyModFilter(); NotifyModCommands(); QueueModListLoad();
    }

    /// <summary>只有进入模组页才安排读取，保留原有详情页索引。</summary>
    partial void OnSelectedDetailTabChanged(int value)
    {
        if (value == 6) QueueModListLoad();
    }

    /// <summary>其他文件操作或游戏结束后使模组快照重新读取，操作期间禁止并发变更。</summary>
    partial void OnIsBusyChanged(bool value)
    {
        if (value) _modsNeedReload = true;
        NotifyModCommands();
        OnlineMods?.NotifyHostStateChanged();
        if (!value) QueueModListLoad();
    }

    /// <summary>选中文件变化后更新操作权限与详情。</summary>
    partial void OnSelectedModChanged(LocalModEntry? value)
    {
        OnPropertyChanged(nameof(ModSelectionDetails)); NotifyModCommands();
    }

    /// <summary>搜索名称、文件名、版本和模组 ID，不重新读取磁盘。</summary>
    partial void OnModSearchTextChanged(string value) => ApplyModFilter();
    /// <summary>切换状态筛选时重建可见集合并保留仍可见的选择。</summary>
    partial void OnModFilterIndexChanged(int value) => ApplyModFilter();

    /// <summary>按状态和关键词筛选；一个 JAR 包含多个模组时仍只有一个文件条目。</summary>
    private void ApplyModFilter()
    {
        var query = ModSearchText.Trim();
        var path = SelectedMod?.Path;
        ModFiles = _allModFiles.Where(mod => ModFilterIndex switch
        {
            1 => mod.State == LocalModState.Enabled,
            2 => mod.State == LocalModState.Disabled,
            3 => mod.State == LocalModState.Removed,
            4 => !string.IsNullOrWhiteSpace(mod.Problem),
            _ => true
        }).Where(mod => query.Length == 0 ||
            mod.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            mod.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            mod.Version.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            mod.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            mod.Identities.Any(item => item.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        SelectedMod = ModFiles.FirstOrDefault(mod => string.Equals(mod.Path, path, StringComparison.OrdinalIgnoreCase));
        ModCountText = $"显示 {ModFiles.Count} / {_allModFiles.Count} 个文件";
    }

    /// <summary>把自动读取排到当前命令结束之后，避免忙碌状态回调重入同一命令。</summary>
    private void QueueModListLoad()
    {
        if (_modPageClosing || !_modsNeedReload || SelectedDetailTab != 6 || !CanReadMods()) return;
        ModListLoadTask = LoadModsWhenIdleAsync(++_modScheduleRevision);
    }

    /// <summary>等待旧读取结束，再检查实例与页面状态；已过期的排队任务直接退出。</summary>
    private async Task LoadModsWhenIdleAsync(int revision)
    {
        await Task.Yield();
        if (RefreshModsCommand.ExecutionTask is { IsCompleted: false } previous)
            await previous;
        if (revision != _modScheduleRevision || _modPageClosing || !_modsNeedReload ||
            SelectedDetailTab != 6 || !CanReadMods()) return;
        _automaticModRefresh = true;
        try { await RefreshModsCommand.ExecuteAsync(null); }
        finally { _automaticModRefresh = false; }
    }

    /// <summary>关闭窗口后不再启动排队扫描，并等待当前读取任务安全结束。</summary>
    public Task StopModListLoadingAsync()
    {
        _modPageClosing = true; ++_modScheduleRevision;
        RefreshModsCommand.Cancel();
        return Task.WhenAll(ModListLoadTask, RefreshModsCommand.ExecutionTask ?? Task.CompletedTask);
    }

    /// <summary>读取和写入都需要选中实例、已接入服务且当前无其他实例操作。</summary>
    private bool CanReadMods() => !_modPageClosing && !IsBusy && SelectedInstance is not null && _localMods is not null;
    /// <summary>添加模组还需要文件选择与风险确认界面。</summary>
    private bool CanAddLocalMods() => CanReadMods() && _modDialogs is not null;
    /// <summary>只有已登记且实际存在的禁用文件可以启用。</summary>
    private bool CanEnableMod() => CanReadMods() && SelectedMod is { CanManage: true, IsPresent: true, State: LocalModState.Disabled };
    /// <summary>只有已登记且实际存在的启用文件可以禁用。</summary>
    private bool CanDisableMod() => CanReadMods() && SelectedMod is { CanManage: true, IsPresent: true, State: LocalModState.Enabled };
    /// <summary>服务判定为可接管的外部文件仍须用户明确确认。</summary>
    private bool CanAdoptMod() => CanAddLocalMods() && SelectedMod is { CanAdopt: true, IsPresent: true };
    /// <summary>只回收已登记的额外文件，原包保护由界面和服务共同执行。</summary>
    private bool CanRemoveMod() => CanAddLocalMods() && SelectedMod is { CanManage: true, IsPresent: true, State: not LocalModState.Removed };
    /// <summary>恢复入口以服务提供的恢复状态为准，不猜测回收文件路径。</summary>
    private bool CanRestoreMod() => CanReadMods() && SelectedMod is { CanRestore: true };
    /// <summary>只有当前模组操作进行中才启用取消入口。</summary>
    private bool CanCancelModOperation() => IsManagingMods;

    /// <summary>统一刷新按钮许可，覆盖实例切换、列表刷新和全局忙碌状态变化。</summary>
    private void NotifyModCommands()
    {
        RefreshModsCommand.NotifyCanExecuteChanged(); AddLocalModsCommand.NotifyCanExecuteChanged();
        AdoptModCommand.NotifyCanExecuteChanged(); EnableModCommand.NotifyCanExecuteChanged();
        DisableModCommand.NotifyCanExecuteChanged(); RemoveModCommand.NotifyCanExecuteChanged();
        RestoreModCommand.NotifyCanExecuteChanged(); RecoverModOperationsCommand.NotifyCanExecuteChanged();
        OpenModsFolderCommand.NotifyCanExecuteChanged();
    }

    /// <summary>只读取选中实例的模组；不会触发实例全量文件检查。</summary>
    [RelayCommand(CanExecute = nameof(CanReadMods), IncludeCancelCommand = true)]
    private async Task RefreshModsAsync(CancellationToken token)
    {
        if (!CanReadMods()) return;
        var instance = SelectedInstance!; var revision = _modSelectionRevision;
        var automatic = _automaticModRefresh;
        IsBusy = true; IsManagingMods = true;
        try
        {
            var error = await LoadModCatalogAsync(instance, revision, token);
            if (!IsCurrentModSelection(instance, revision)) return;
            if (error is OperationCanceledException)
            {
                if (!automatic) _notifications.Cancelled("模组列表读取已取消", ModListStatus, instance.GameDirectory);
            }
            else if (error is not null) _notifications.Failure("模组列表读取失败", error, instance.GameDirectory);
            else if (!automatic)
            {
                var details = ModListStatus + (ModCatalogNotes.Length == 0 ? "" : "\n" + ModCatalogNotes);
                var problems = _allModFiles.Where(mod => !string.IsNullOrWhiteSpace(mod.Problem)).ToArray();
                if (problems.Length > 0 || ModCatalogNotes.Length > 0)
                    _notifications.Warning("模组列表已读取，部分内容需注意", details + "\n" +
                        string.Join("\n", problems.Select(mod => mod.Path + "：" + mod.Problem)),
                        [instance.GameDirectory, .. problems.Select(mod => System.IO.Path.Combine(instance.InstancePath,
                            (mod.CurrentPath ?? mod.Path).Replace('/', System.IO.Path.DirectorySeparatorChar)))]);
                else _notifications.Success("模组列表已刷新", details, instance.GameDirectory);
            }
        }
        finally { IsManagingMods = false; IsBusy = false; }
    }

    /// <summary>发布完整目录快照；读取失败清空旧快照，避免对过期条目继续操作。</summary>
    private async Task<Exception?> LoadModCatalogAsync(MinecraftInstance instance, int revision, CancellationToken token)
    {
        if (IsCurrentModSelection(instance, revision)) ModListStatus = "正在读取模组信息……";
        try
        {
            token.ThrowIfCancellationRequested();
            var catalog = await _localMods!.ListAsync(instance.InstancePath, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentModSelection(instance, revision)) return null;
            _allModFiles = catalog.Files; ApplyModFilter();
            ModListStatus = $"已启用 {catalog.Files.Count(mod => mod.State == LocalModState.Enabled)}，" +
                $"已禁用 {catalog.Files.Count(mod => mod.State == LocalModState.Disabled)}，" +
                $"已移除 {catalog.Files.Count(mod => mod.State == LocalModState.Removed)}。";
            ModCatalogNotes = string.Join("\n", catalog.Notes);
            return null;
        }
        catch (Exception ex)
        {
            if (!IsCurrentModSelection(instance, revision)) return null;
            _allModFiles = []; ApplyModFilter(); ModCatalogNotes = "";
            ModListStatus = ex is OperationCanceledException ? "已取消读取，可点击刷新列表重试。" : "模组列表读取失败：" + ex.Message;
            return ex;
        }
        finally
        {
            // 失败或主动取消后交由用户重试，避免自动读取无限重入。
            if (IsCurrentModSelection(instance, revision)) _modsNeedReload = false;
        }
    }

    /// <summary>只允许当前选择对应的异步结果更新页面，窗口关闭后的结果也丢弃。</summary>
    private bool IsCurrentModSelection(MinecraftInstance instance, int revision) => !_modPageClosing &&
        revision == _modSelectionRevision && string.Equals(instance.InstancePath, SelectedInstance?.InstancePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>选择并确认后逐个添加本地文件，单项失败不会回滚其他已完成添加。</summary>
    [RelayCommand(CanExecute = nameof(CanAddLocalMods), IncludeCancelCommand = true)]
    private async Task AddLocalModsAsync(CancellationToken token)
    {
        if (!CanAddLocalMods()) return;
        var instance = SelectedInstance!;
        IReadOnlyList<string> paths;
        try
        {
            paths = _modDialogs!.ChooseModFiles();
            if (paths.Count == 0 || !_modDialogs.ConfirmAdd(instance, paths)) return;
        }
        catch (Exception ex)
        {
            ModStatus = "无法选择本地模组：" + ex.Message;
            _notifications.Failure("本地模组选择失败", ex, instance.GameDirectory);
            return;
        }
        await RunModMutationAsync(instance, "正在添加本地模组……", async cancellation =>
        {
            var added = 0; var failures = new List<string>();
            foreach (var path in paths)
            {
                cancellation.ThrowIfCancellationRequested();
                try { await _localMods!.AddAsync(instance.InstancePath, path, cancellation); ++added; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { failures.Add(path + "：" + ex.Message); }
            }
            return new ModMutationOutcome($"已添加 {added} / {paths.Count} 个模组。" +
                (failures.Count == 0 ? "" : "\n未添加：" + string.Join("\n", failures)),
                failures.Count == 0 ? OperationNoticeKind.Success : added > 0 ? OperationNoticeKind.Warning : OperationNoticeKind.Failure);
        }, token, paths);
    }

    /// <summary>明确确认后接管一个额外文件，不自动承认目录中所有外部模组。</summary>
    [RelayCommand(CanExecute = nameof(CanAdoptMod), IncludeCancelCommand = true)]
    private async Task AdoptModAsync(CancellationToken token)
    {
        if (!CanAdoptMod()) return;
        var instance = SelectedInstance!; var mod = SelectedMod!;
        try { if (!_modDialogs!.ConfirmAdopt(instance, mod)) return; }
        catch (Exception ex)
        {
            ModStatus = "未确认接管模组：" + ex.Message;
            _notifications.Failure("接管模组未完成", ex, instance.GameDirectory, mod.Path);
            return;
        }
        await RunModMutationAsync(instance, "正在登记额外模组……", async cancellation =>
        {
            await _localMods!.AdoptAsync(instance.InstancePath, mod.Path, cancellation);
            return new ModMutationOutcome("已接管：" + mod.FileName);
        }, token);
    }

    /// <summary>启用所选额外文件，目标位置冲突时交由服务拒绝覆盖。</summary>
    [RelayCommand(CanExecute = nameof(CanEnableMod), IncludeCancelCommand = true)]
    private Task EnableModAsync(CancellationToken token) => ChangeModEnabledAsync(true, token);
    /// <summary>禁用所选额外文件，保留文件本体和原始完整性要求。</summary>
    [RelayCommand(CanExecute = nameof(CanDisableMod), IncludeCancelCommand = true)]
    private Task DisableModAsync(CancellationToken token) => ChangeModEnabledAsync(false, token);

    /// <summary>共用启禁流程并捕获原选中路径，避免刷新后操作另一条目。</summary>
    private Task ChangeModEnabledAsync(bool enabled, CancellationToken token)
    {
        if (enabled ? !CanEnableMod() : !CanDisableMod()) return Task.CompletedTask;
        var instance = SelectedInstance!; var mod = SelectedMod!;
        return RunModMutationAsync(instance, enabled ? "正在启用模组……" : "正在禁用模组……", async cancellation =>
        {
            await _localMods!.SetEnabledAsync(instance.InstancePath, mod.Path, enabled, cancellation);
            return new ModMutationOutcome((enabled ? "已启用：" : "已禁用：") + mod.FileName);
        }, token);
    }

    /// <summary>确认后移入实例回收区，文件仍可在已移除列表恢复。</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveMod), IncludeCancelCommand = true)]
    private async Task RemoveModAsync(CancellationToken token)
    {
        if (!CanRemoveMod()) return;
        var instance = SelectedInstance!; var mod = SelectedMod!;
        try { if (!_modDialogs!.ConfirmRemove(instance, mod)) return; }
        catch (Exception ex)
        {
            ModStatus = "未确认移除模组：" + ex.Message;
            _notifications.Failure("移除模组未完成", ex, instance.GameDirectory, mod.Path);
            return;
        }
        await RunModMutationAsync(instance, "正在移入模组回收区……", async cancellation =>
        {
            await _localMods!.RemoveAsync(instance.InstancePath, mod.Path, cancellation);
            return new ModMutationOutcome("已移入回收区：" + mod.FileName + "。可切换到“已移除”筛选后恢复。");
        }, token);
    }

    /// <summary>恢复所选回收文件至移除前的启禁状态。</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreMod), IncludeCancelCommand = true)]
    private async Task RestoreModAsync(CancellationToken token)
    {
        if (!CanRestoreMod()) return;
        var instance = SelectedInstance!; var mod = SelectedMod!;
        await RunModMutationAsync(instance, "正在恢复模组……", async cancellation =>
        {
            await _localMods!.RestoreAsync(instance.InstancePath, mod.Path, cancellation);
            return new ModMutationOutcome("已恢复：" + mod.FileName + "。保持移除前的启用或禁用状态。");
        }, token);
    }

    /// <summary>请求服务恢复中断事务；外部文件冲突会明确报错并保留文件。</summary>
    [RelayCommand(CanExecute = nameof(CanReadMods), IncludeCancelCommand = true)]
    private async Task RecoverModOperationsAsync(CancellationToken token)
    {
        if (!CanReadMods()) return;
        var instance = SelectedInstance!;
        await RunModMutationAsync(instance, "正在恢复未完成的模组操作……", async cancellation =>
        {
            await _localMods!.RecoverAsync(instance.InstancePath, cancellation);
            return new ModMutationOutcome("未完成的模组操作已处理，可查看刷新后的列表。");
        }, token);
    }

    /// <summary>保留批量操作的真实结果，部分失败不会被字符串摘要误判为全部成功。</summary>
    private sealed record ModMutationOutcome(string Message, OperationNoticeKind Kind = OperationNoticeKind.Success);

    /// <summary>统一写入与刷新结果；整个模组操作只发布一次通知，附带原操作对象的路径。</summary>
    private async Task RunModMutationAsync(MinecraftInstance instance, string pending,
        Func<CancellationToken, Task<ModMutationOutcome>> operation, CancellationToken token, IReadOnlyList<string>? sourcePaths = null)
    {
        var revision = _modSelectionRevision;
        var paths = new List<string> { instance.GameDirectory };
        if (sourcePaths is not null) paths.AddRange(sourcePaths);
        if (SelectedMod is { } selected)
            paths.Add(System.IO.Path.Combine(instance.InstancePath, (selected.CurrentPath ?? selected.Path).Replace('/', System.IO.Path.DirectorySeparatorChar)));
        ModMutationOutcome? outcome = null;
        IsBusy = true; IsManagingMods = true;
        ClearCheckResults(); ModStatus = pending;
        _allModFiles = []; ApplyModFilter();
        try
        {
            token.ThrowIfCancellationRequested();
            outcome = await operation(token);
            if (IsCurrentModSelection(instance, revision)) ModStatus = outcome.Message;
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException
                ? "模组操作已取消；已经完成的更改会保留，可刷新列表查看。"
                : "模组操作未完成：" + ex.Message;
            outcome = new(message, ex is OperationCanceledException ? OperationNoticeKind.Cancelled : OperationNoticeKind.Failure);
            if (IsCurrentModSelection(instance, revision)) ModStatus = message;
        }
        finally
        {
            try
            {
                var refreshError = await LoadModCatalogAsync(instance, revision, token);
                if (outcome is not null && refreshError is not null and not OperationCanceledException)
                    outcome = outcome with
                    {
                        Kind = outcome.Kind == OperationNoticeKind.Success ? OperationNoticeKind.Warning : outcome.Kind,
                        Message = outcome.Message + "\n模组列表刷新失败，可手动刷新：" + refreshError.Message
                    };
            }
            finally { IsManagingMods = false; IsBusy = false; }
        }
        if (outcome is not null && IsCurrentModSelection(instance, revision))
            _notifications.Report(new(outcome.Kind, outcome.Kind switch
            {
                OperationNoticeKind.Success => "模组操作完成", OperationNoticeKind.Warning => "模组操作部分完成",
                OperationNoticeKind.Cancelled => "模组操作已取消", _ => "模组操作未完成"
            }, outcome.Message, paths));
    }

    /// <summary>通过界面服务打开当前实例目录，失败时提供可复制的目录位置。</summary>
    [RelayCommand(CanExecute = nameof(CanAddLocalMods))]
    private void OpenModsFolder()
    {
        if (!CanAddLocalMods()) return;
        try { _modDialogs!.OpenModsFolder(SelectedInstance!); }
        catch (Exception ex)
        {
            ModStatus = "无法打开模组文件夹：" + ex.Message;
            _notifications.Failure("无法打开模组文件夹", ex, System.IO.Path.Combine(SelectedInstance!.GameDirectory, "mods"));
        }
    }

    /// <summary>取消当前扫描或变更；服务负责让已进入提交阶段的操作恢复到一致状态。</summary>
    [RelayCommand(CanExecute = nameof(CanCancelModOperation))]
    private void CancelModOperation()
    {
        RefreshModsCommand.Cancel(); AddLocalModsCommand.Cancel(); AdoptModCommand.Cancel();
        EnableModCommand.Cancel(); DisableModCommand.Cancel(); RemoveModCommand.Cancel();
        RestoreModCommand.Cancel(); RecoverModOperationsCommand.Cancel();
    }
}
