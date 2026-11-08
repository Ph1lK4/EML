using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;

namespace EML.App.ViewModels;

public partial class InstancesViewModel
{
    private readonly IMinecraftInstanceMigrationService? _migration;
    private readonly IInstanceMigrationDialogs? _migrationDialogs;
    private int _migrationSelectionRevision;
    private GtnhInstanceInfo? _migrationTarget;
    private MigrationRecoveryInfo? _migrationRecovery;
    [ObservableProperty] private string migrationStatus = "选中新版 GTNH 实例后，可从旧实例迁入玩家数据。";
    [ObservableProperty] private string migrationJournalPath = "";
    /// <summary>保留最近一次只读识别任务，供界面流程与测试等待，避免轮询状态。</summary>
    public Task MigrationStateLoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>只读识别当前接收方和恢复记录，过期异步结果不会作用于另一实例。</summary>
    private async Task LoadMigrationStateAsync(MinecraftInstance? instance)
    {
        var revision = ++_migrationSelectionRevision;
        _migrationTarget = null; _migrationRecovery = null;
        NotifyMigrationCommands();
        if (_migration is null || instance is null) return;
        try
        {
            var target = await _migration.InspectAsync(instance.InstancePath);
            var recovery = await _migration.GetRecoveryAsync(instance.InstancePath);
            if (revision != _migrationSelectionRevision) return;
            _migrationTarget = target; _migrationRecovery = recovery;
            MigrationStatus = recovery is { Status: not "Completed" }
                ? "此实例存在未完成迁移，请先恢复上次迁移。"
                : target is null ? "当前实例未被识别为支持迁移的 GTNH。"
                : $"接收方：{target.Name}（GTNH {target.Version}）。先完成依赖补全并试运行，再迁入旧版数据。只保留语言偏好，不迁移完整汉化包。";
        }
        catch (Exception ex)
        {
            if (revision == _migrationSelectionRevision) MigrationStatus = "迁移信息读取失败：" + ex.Message;
        }
        finally { if (revision == _migrationSelectionRevision) NotifyMigrationCommands(); }
    }

    /// <summary>迁移与恢复共用全局忙碌状态，恢复可用于不再满足 GTNH 标识的实例。</summary>
    private bool CanMigrate() => !IsBusy && _migrationDialogs is not null && _migrationTarget is not null &&
        (_migrationRecovery is null || _migrationRecovery.Status == "Completed");
    /// <summary>只有本机留有最近一笔可恢复事务时才提供恢复按钮。</summary>
    private bool CanRestoreMigration() => !IsBusy && _migrationDialogs is not null && _migrationRecovery is not null && SelectedInstance is not null;
    /// <summary>识别结束或操作结束时刷新按钮许可。</summary>
    private void NotifyMigrationCommands()
    {
        MigratePlayerDataCommand.NotifyCanExecuteChanged(); RestoreMigrationCommand.NotifyCanExecuteChanged();
    }

    /// <summary>先选择旧实例和范围，再生成只读预览；最终确认后才复制文件。</summary>
    [RelayCommand(CanExecute = nameof(CanMigrate), IncludeCancelCommand = true)]
    private async Task MigratePlayerDataAsync(CancellationToken token)
    {
        if (!CanMigrate()) return;
        var target = _migrationTarget!;
        IsBusy = true; SelectedDetailTab = 5; MigrationJournalPath = "";
        var revision = ++_transferRevision;
        string? outcome = null;
        try
        {
            MigrationStatus = "正在查找其他 GTNH 实例……";
            var sources = new List<GtnhInstanceInfo>();
            foreach (var instance in Instances.Where(value => !string.Equals(value.InstancePath, target.InstancePath, StringComparison.OrdinalIgnoreCase)))
            {
                token.ThrowIfCancellationRequested();
                try { if (await _migration!.InspectAsync(instance.InstancePath, token) is { } source) sources.Add(source); }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* 单个无效候选不影响其他实例。 */ }
            }
            if (sources.Count == 0) { outcome = "需要同时保留旧版和新版两个本地 GTNH 实例。请先导入另一个实例。"; return; }
            var choice = _migrationDialogs!.ChooseSource(target, sources);
            if (choice is null) { outcome = "已取消，实例未修改。"; return; }
            var groups = await _migration!.GetGroupsAsync(choice.Source.InstancePath, choice.Mode, token);
            var selection = _migrationDialogs.ChooseGroups(choice.Source, target, groups);
            if (selection is null) { outcome = "已取消，实例未修改。"; return; }
            MigrationStatus = "正在读取所选数据并生成迁移预览……";
            var request = new InstanceMigrationRequest(choice.Source.InstancePath, target.InstancePath,
                choice.Mode, selection.Groups, selection.TargetTested, selection.GamesClosed)
            { ExtraModsRiskAccepted = selection.ExtraModsRiskAccepted };
            var plan = await _migration.PreviewAsync(request, token);
            token.ThrowIfCancellationRequested();
            var overwrite = _migrationDialogs.ConfirmMigration(plan);
            if (overwrite is null) { outcome = "已取消，实例未修改。"; return; }
            ClearCheckResults();
            var result = await _migration.MigrateAsync(plan, overwrite.Value, MigrationProgress(revision), token);
            MigrationJournalPath = result.JournalPath;
            outcome = $"{string.Join("\n", result.Messages)}\n复制 {result.CopiedFiles} 个文件，跳过 {result.SkippedFiles} 个文件。";
        }
        catch (OperationCanceledException) { outcome = "迁移已取消；若提交已开始，会先尝试恢复。"; }
        catch (Exception ex) { outcome = "迁移未完成：" + ex.Message; }
        finally
        {
            ++_transferRevision;
            await LoadMigrationStateAsync(SelectedInstance);
            if (outcome is not null) MigrationStatus = outcome;
            IsBusy = false;
        }
    }

    /// <summary>按本机写前日志恢复最近一次迁移；游戏后再次修改的文件由服务保留。</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreMigration), IncludeCancelCommand = true)]
    private async Task RestoreMigrationAsync(CancellationToken token)
    {
        if (!CanRestoreMigration()) return;
        var instance = SelectedInstance!; var recovery = _migrationRecovery!;
        if (!_migrationDialogs!.ConfirmRestore(instance.Name, recovery)) return;
        IsBusy = true; SelectedDetailTab = 5;
        var revision = ++_transferRevision;
        string? outcome = null;
        try
        {
            ClearCheckResults();
            var result = await _migration!.RestoreAsync(instance.InstancePath, recovery.Id, MigrationProgress(revision), token);
            outcome = string.Join("\n", result.Messages); MigrationJournalPath = result.JournalPath;
        }
        catch (OperationCanceledException) { outcome = "已取消恢复预检，实例未修改。"; }
        catch (Exception ex) { outcome = "恢复未完成：" + ex.Message; }
        finally
        {
            ++_transferRevision;
            await LoadMigrationStateAsync(SelectedInstance);
            if (outcome is not null) MigrationStatus = outcome;
            IsBusy = false;
        }
    }

    /// <summary>只显示当前操作的进度，忽略排队中的旧操作回调。</summary>
    private IProgress<InstanceMigrationProgress> MigrationProgress(int revision) => new Progress<InstanceMigrationProgress>(p =>
    {
        if (revision != _transferRevision || !IsBusy) return;
        TransferPercent = p.Total == 0 ? 0 : 100d * p.Completed / p.Total;
        TransferStatus = $"{p.Stage}：{p.Completed}/{p.Total}  {p.Path}";
        MigrationStatus = p.Stage;
    });
}
