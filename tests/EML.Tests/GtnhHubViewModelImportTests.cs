using EML.App.Services;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;

namespace EML.Tests;

public sealed partial class GtnhHubViewModelTests
{
    /// <summary>空库可导入旧版，但刷新和切页不会默认把它选为迁移接收方。</summary>
    [Fact]
    public async Task EmptyLibraryCanImportOldInstanceWithoutSelectingItAsTarget()
    {
        var notices = new HubNoticeRecorder();
        await using var app = new Harness(notices);
        app.Instances.Instances.Clear(); app.Instances.SelectedInstance = null;
        var imported = EnableOldInstanceImport(app);
        app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
        app.Hub.ShowMigrationCommand.Execute(null);
        Assert.True(app.Hub.ImportOldInstanceCommand.CanExecute(null));
        Assert.Contains("自动补全依赖", app.Hub.MigrationImportHint);
        Assert.Contains("不会自动开始迁移", app.Hub.MigrationImportHint);

        await app.Hub.ImportOldInstanceCommand.ExecuteAsync(null);

        Assert.Equal(imported.InstancePath, Assert.Single(app.Hub.Targets).InstancePath);
        Assert.Equal([imported.InstancePath], app.Services.DownloadPaths);
        Assert.Equal(OperationNoticeKind.Success, Assert.Single(notices.Items).Kind);
        Assert.Null(app.Hub.SelectedTarget); Assert.Null(app.Instances.SelectedInstance);
        Assert.False(app.Hub.HasTarget); Assert.False(app.Hub.IsImportingOldInstance);
        await app.Hub.RefreshTargetsCommand.ExecuteAsync(null);
        app.Main.SelectedPage = 3; app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
        Assert.Null(app.Hub.SelectedTarget); Assert.Null(app.Instances.SelectedInstance);

        app.Hub.SelectedTarget = Assert.Single(app.Hub.Targets);
        Assert.True(app.Hub.HasTarget);
        Assert.Equal(imported.InstancePath, app.Instances.SelectedInstance!.InstancePath);
    }

    /// <summary>宿主忙碌或专区关闭后，直接调用命令也不能打开导入窗口。</summary>
    [Fact]
    public async Task OldInstanceImportHonorsBusyAndClosingState()
    {
        await using var app = new Harness();
        EnableOldInstanceImport(app);
        app.Instances.IsBusy = true;
        Assert.False(app.Hub.ImportOldInstanceCommand.CanExecute(null));
        await app.Hub.ImportOldInstanceCommand.ExecuteAsync(null);
        Assert.Equal(0, app.Services.ArchiveChoices);
        app.Instances.IsBusy = false;
        Assert.True(app.Hub.ImportOldInstanceCommand.CanExecute(null));
        await app.Hub.StopAsync();
        Assert.False(app.Hub.ImportOldInstanceCommand.CanExecute(null));
        await app.Hub.ImportOldInstanceCommand.ExecuteAsync(null);
        Assert.Equal(0, app.Services.ArchiveChoices);
    }

    /// <summary>导入重新扫描对象、旧目标列表被清空及 WPF 清空绑定时，仍按原路径恢复新版。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldInstanceImportRestoresNewTargetAcrossRebuiltCollections(bool clearTargetsDuringImport)
    {
        var notices = new HubNoticeRecorder();
        await using var app = new Harness(notices);
        app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
        app.Hub.SelectedTarget = app.Hub.Targets[1];
        var previous = app.Instances.SelectedInstance!;
        var imported = EnableOldInstanceImport(app);
        app.Services.LocatedInstances = app.Services.LocatedInstances.Select(instance => instance with { Name = instance.Name + " refreshed" }).ToArray();
        app.Hub.Targets.CollectionChanged += (_, _) =>
        {
            if (app.Hub.SelectedTarget is { } selected && !app.Hub.Targets.Contains(selected)) app.Hub.SelectedTarget = null;
        };
        var import = app.Services.ImportArchive!;
        app.Services.ImportArchive = token =>
        {
            if (clearTargetsDuringImport) app.Hub.Targets.Clear();
            return import(token);
        };

        await app.Hub.ImportOldInstanceCommand.ExecuteAsync(null);

        Assert.Equal(previous.InstancePath, app.Instances.SelectedInstance!.InstancePath);
        Assert.NotSame(previous, app.Instances.SelectedInstance);
        Assert.Equal(previous.InstancePath, app.Hub.SelectedTarget!.InstancePath);
        Assert.EndsWith("refreshed", app.Hub.SelectedTarget.Name);
        Assert.True(app.Hub.HasTarget);
        Assert.Contains(app.Hub.Targets, target => target.InstancePath == imported.InstancePath);
        Assert.Equal([imported.InstancePath], app.Services.DownloadPaths);
        Assert.Single(notices.Items);
    }

    /// <summary>导入失败或取消仅使用普通导入的单次通知，并恢复原有新版选择。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledOldImportKeepsTargetAndDoesNotDuplicateNotifications(bool canceled)
    {
        var notices = new HubNoticeRecorder();
        await using var app = new Harness(notices);
        app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
        app.Hub.SelectedTarget = app.Hub.Targets[1];
        var target = app.Hub.SelectedTarget.InstancePath;
        EnableOldInstanceImport(app);
        var pending = new TaskCompletionSource<InstanceImportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken importToken = default;
        app.Services.ImportArchive = token =>
        {
            importToken = token;
            return canceled ? pending.Task.WaitAsync(token) : Task.FromException<InstanceImportResult>(new IOException("旧实例压缩包损坏"));
        };

        var operation = app.Hub.ImportOldInstanceCommand.ExecuteAsync(null);
        if (canceled)
        {
            Assert.True(app.Hub.IsImportingOldInstance); Assert.True(app.Instances.IsBusy);
            Assert.True(app.Hub.ImportOldInstanceCancelCommand.CanExecute(null));
            app.Hub.ImportOldInstanceCancelCommand.Execute(null);
        }
        await operation;

        Assert.Equal(canceled, importToken.IsCancellationRequested);
        Assert.Equal(target, app.Hub.SelectedTarget!.InstancePath);
        Assert.Equal(target, app.Instances.SelectedInstance!.InstancePath);
        Assert.False(app.Hub.IsImportingOldInstance); Assert.False(app.Instances.IsBusy);
        Assert.Empty(app.Services.DownloadPaths);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(canceled ? OperationNoticeKind.Cancelled : OperationNoticeKind.Failure, notice.Kind);
        if (!canceled) Assert.Contains("旧实例压缩包损坏", notice.Message);
    }

    /// <summary>补全阶段取消保留已导入的旧实例和新版选择，取消同样传递给现有下载流程。</summary>
    [Fact]
    public async Task CancelingOldImportDuringCompletionPreservesImportedInstance()
    {
        var notices = new HubNoticeRecorder();
        await using var app = new Harness(notices);
        app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
        app.Hub.SelectedTarget = app.Hub.Targets[1];
        var target = app.Hub.SelectedTarget.InstancePath;
        var imported = EnableOldInstanceImport(app);
        var pending = new TaskCompletionSource<InstanceDownloadReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken downloadToken = default;
        app.Services.Download = (_, token) => { downloadToken = token; return pending.Task.WaitAsync(token); };

        var operation = app.Hub.ImportOldInstanceCommand.ExecuteAsync(null);
        Assert.True(app.Hub.IsImportingOldInstance); Assert.True(app.Instances.IsBusy);
        app.Hub.ImportOldInstanceCancelCommand.Execute(null);
        await operation;

        Assert.True(downloadToken.IsCancellationRequested);
        Assert.Contains(app.Instances.Instances, instance => instance.InstancePath == imported.InstancePath);
        Assert.Equal(target, app.Hub.SelectedTarget!.InstancePath);
        Assert.Equal(target, app.Instances.SelectedInstance!.InstancePath);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(OperationNoticeKind.Cancelled, notice.Kind); Assert.Contains("已导入", notice.Title);
    }

    /// <summary>切离专区不取消已确认导入；在后台完成后返回时仍恢复新版或保留未选目标。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingHubKeepsOldImportRunningAndRestoresTargetOnReturn(bool hasTarget)
    {
        await using var app = new Harness();
        if (!hasTarget) { app.Instances.Instances.Clear(); app.Instances.SelectedInstance = null; }
        app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
        if (hasTarget) app.Hub.SelectedTarget = app.Hub.Targets[1];
        var target = app.Hub.SelectedTarget?.InstancePath;
        var imported = EnableOldInstanceImport(app);
        var pending = new TaskCompletionSource<InstanceImportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken importToken = default;
        app.Services.ImportArchive = token => { importToken = token; return pending.Task.WaitAsync(token); };

        var operation = app.Hub.ImportOldInstanceCommand.ExecuteAsync(null);
        app.Main.SelectedPage = 3;
        Assert.False(importToken.IsCancellationRequested); Assert.False(operation.IsCompleted);
        pending.SetResult(imported); await operation;
        Assert.Equal(target, app.Instances.SelectedInstance?.InstancePath);
        app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;

        Assert.Contains(app.Hub.Targets, item => item.InstancePath == imported.InstancePath);
        Assert.Equal(target, app.Hub.SelectedTarget?.InstancePath);
        Assert.Equal(hasTarget, app.Hub.HasTarget);
    }

    /// <summary>关闭同时取消包装命令和底层导入，并等待底层确认结束后才完成关闭。</summary>
    [Fact]
    public async Task ClosingHubCancelsAndWaitsForUnderlyingOldImport()
    {
        var notices = new HubNoticeRecorder();
        await using var app = new Harness(notices);
        EnableOldInstanceImport(app);
        var pending = new TaskCompletionSource<InstanceImportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken importToken = default;
        app.Services.ImportArchive = token => { importToken = token; return pending.Task; };
        var operation = app.Hub.ImportOldInstanceCommand.ExecuteAsync(null);

        var stopping = app.Hub.StopAsync();
        Assert.True(importToken.IsCancellationRequested);
        Assert.False(stopping.IsCompleted); Assert.False(operation.IsCompleted);
        Assert.False(app.Hub.ImportOldInstanceCommand.CanExecute(null));
        pending.SetCanceled(importToken);
        await stopping; await operation;

        Assert.False(app.Instances.IsBusy); Assert.False(app.Hub.IsImportingOldInstance);
        Assert.Equal(OperationNoticeKind.Cancelled, Assert.Single(notices.Items).Kind);
    }

    /// <summary>导入前尚未完成的目标扫描即使忽略取消，迟到结果也不能给旧实例补上默认选择。</summary>
    [Fact]
    public async Task LateTargetRefreshCannotSelectImportedOldInstance()
    {
        await using var app = new Harness();
        app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
        app.Instances.SelectedInstance = null;
        var imported = EnableOldInstanceImport(app);
        var pending = new TaskCompletionSource<GtnhInstanceInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = false;
        app.Services.InspectGtnh = (path, token) =>
        {
            if (token.CanBeCanceled && !held) { held = true; return pending.Task; }
            return Task.FromResult<GtnhInstanceInfo?>(path.EndsWith("vanilla") ? null : new(path, Path.GetFileName(path), path + "\\.minecraft", "2.8.4"));
        };
        var refreshing = app.Hub.RefreshTargetsCommand.ExecuteAsync(null);
        try
        {
            await app.Hub.ImportOldInstanceCommand.ExecuteAsync(null);
            pending.SetResult(new(imported.InstancePath, "迟到旧版", imported.GameDirectory, "2.8.4"));
            await refreshing;
            Assert.Null(app.Hub.SelectedTarget); Assert.Null(app.Instances.SelectedInstance);
            Assert.Contains(app.Hub.Targets, target => target.InstancePath == imported.InstancePath);
        }
        finally { pending.TrySetResult(null); }
    }

    /// <summary>首次识别未完成时导入仍保留全局选择路径，最终只恢复已识别为 GTNH 的新版。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldImportWhileFirstTargetScanPendingRestoresOnlyRecognizedSelection(bool selectedVanilla)
    {
        await using var app = new Harness();
        app.Instances.SelectedInstance = app.Instances.Instances[selectedVanilla ? 2 : 1];
        var previousPath = app.Instances.SelectedInstance.InstancePath;
        EnableOldInstanceImport(app);
        var pending = new TaskCompletionSource<GtnhInstanceInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = false;
        app.Services.InspectGtnh = (path, token) =>
        {
            if (token.CanBeCanceled && !held) { held = true; return pending.Task; }
            return Task.FromResult<GtnhInstanceInfo?>(path.EndsWith("vanilla") ? null : new(path, Path.GetFileName(path), path + "\\.minecraft", "2.8.4"));
        };
        app.Main.SelectedPage = 4;
        var scan = app.Hub.TargetLoadTask;
        try
        {
            Assert.True(app.Hub.IsLoadingTargets); Assert.False(app.Hub.HasTarget);
            Assert.True(app.Hub.ImportOldInstanceCommand.CanExecute(null));
            await app.Hub.ImportOldInstanceCommand.ExecuteAsync(null);
            pending.SetResult(null); await scan;
            Assert.Equal(selectedVanilla ? null : previousPath, app.Hub.SelectedTarget?.InstancePath);
            Assert.Equal(selectedVanilla ? null : previousPath, app.Instances.SelectedInstance?.InstancePath);
        }
        finally { pending.TrySetResult(null); }
    }

    /// <summary>准备内存中的旧实例与成功补全结果，扫描刻意返回新对象以覆盖引用失效情况。</summary>
    private static InstanceImportResult EnableOldInstanceImport(Harness app)
    {
        var path = "C:\\hub-test\\imported-old";
        var manifest = new InstanceImportManifest { Name = "旧版（带本地数据）", SourceFormat = "Prism", MinecraftVersion = "1.7.10" };
        var result = new InstanceImportResult(path, path + "\\.minecraft", manifest);
        app.Services.ChooseArchive = () => "C:\\packs\\GTNH旧版（含存档）.zip";
        app.Services.ConfirmArchive = preview => preview.SuggestedName;
        app.Services.InspectArchive = _ => Task.FromResult(new InstanceImportPreview(manifest.Name, "Prism", "1.7.10", [], 10, 0, 0, []));
        app.Services.ImportArchive = _ => Task.FromResult(result);
        app.Services.LocatedInstances = [.. app.Instances.Instances.Select(instance => instance with { Warnings = ["重新扫描"] }),
            new(manifest.Name, result.InstancePath, result.GameDirectory, ["1.7.10"])];
        app.Services.Download = (instancePath, _) => Task.FromResult(new InstanceDownloadReport([], new()
        {
            InstancePath = instancePath, HasManifest = true, Mode = InstanceCheckMode.Full,
            RuntimeSourceFormat = "Prism", LaunchProfilePath = "launch.json"
        }));
        return result;
    }
}
