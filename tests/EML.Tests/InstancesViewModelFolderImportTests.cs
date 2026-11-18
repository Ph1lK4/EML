using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Minecraft;

namespace EML.Tests;

/// <summary>目录导入使用真实 ViewModel 命令；替身不读磁盘或访问其他启动器的账号。</summary>
public sealed class InstancesViewModelFolderImportTests
{
    /// <summary>成功复制后只补全新实例，且最终通知包含源目录和新目录。</summary>
    [Fact]
    public async Task CopyThenCompletionUsesNewPathAndOneSuccessNotice()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.ImportFolderCommand.ExecuteAsync(null);
        Assert.Equal([fixture.Result.InstancePath], fixture.Services.Downloads);
        Assert.Equal(fixture.Source, Assert.Single(fixture.Services.Copies));
        Assert.Equal(fixture.Result.InstancePath, fixture.ViewModel.SelectedInstance?.InstancePath);
        var notice = Assert.Single(fixture.Services.Notices);
        Assert.Equal(OperationNoticeKind.Success, notice.Kind);
        Assert.Contains(fixture.Source.SourcePath, notice.Paths);
        Assert.Contains(fixture.Result.InstancePath, notice.Paths);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    /// <summary>关闭发现窗口不做分析、复制或补全，也不弹出结果通知。</summary>
    [Fact]
    public async Task DismissingDiscoveryDoesNothing()
    {
        var fixture = new Fixture();
        fixture.Services.Choice = null;
        await fixture.ViewModel.ImportFolderCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Services.Copies);
        Assert.Empty(fixture.Services.Downloads);
        Assert.Empty(fixture.Services.Notices);
        Assert.Equal(0, fixture.Services.Inspections);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    /// <summary>预览取消不会调用复制或补全服务。</summary>
    [Fact]
    public async Task CancelingPreviewDoesNotCopy()
    {
        var fixture = new Fixture();
        fixture.Services.NewName = null;
        await fixture.ViewModel.ImportFolderCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.Services.Inspections);
        Assert.Empty(fixture.Services.Copies);
        Assert.Empty(fixture.Services.Downloads);
        Assert.Empty(fixture.Services.Notices);
    }

    /// <summary>复制异常报告失败原因与源路径，不把原路径交给补全器。</summary>
    [Fact]
    public async Task CopyFailureReportsReasonWithoutInstalling()
    {
        var fixture = new Fixture();
        fixture.Services.Copy = _ => Task.FromException<InstanceImportResult>(new IOException("存档复制期间发生变化"));
        await fixture.ViewModel.ImportFolderCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Services.Downloads);
        var notice = Assert.Single(fixture.Services.Notices);
        Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
        Assert.Contains("存档复制期间发生变化", notice.Message);
        Assert.Contains(fixture.Source.SourcePath, notice.Paths);
        Assert.Empty(fixture.ViewModel.Instances);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    /// <summary>补全网络失败仍保留已复制实例并允许之后手动继续。</summary>
    [Fact]
    public async Task CompletionFailurePreservesCommittedInstance()
    {
        var fixture = new Fixture();
        fixture.Services.Download = _ => Task.FromException<InstanceDownloadReport>(new HttpRequestException("网络不可用"));
        await fixture.ViewModel.ImportFolderCommand.ExecuteAsync(null);
        Assert.Equal(fixture.Result.InstancePath, fixture.ViewModel.SelectedInstance?.InstancePath);
        var notice = Assert.Single(fixture.Services.Notices);
        Assert.Equal(OperationNoticeKind.Warning, notice.Kind);
        Assert.Contains("已导入", notice.Message);
        Assert.Contains("网络不可用", notice.Message);
        Assert.True(fixture.ViewModel.DownloadMissingCommand.CanExecute(null));
    }

    /// <summary>复制阶段取消说明未新增实例，源文件不经过任何管理操作。</summary>
    [Fact]
    public async Task CancelingCopyDoesNotPublishAnInstance()
    {
        var fixture = new Fixture();
        fixture.Services.Copy = _ => Task.FromException<InstanceImportResult>(new OperationCanceledException());
        await fixture.ViewModel.ImportFolderCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Services.Downloads);
        Assert.Empty(fixture.ViewModel.Instances);
        Assert.Contains("没有新增实例", fixture.ViewModel.StatusText);
        Assert.Single(fixture.Services.Notices);
    }

    /// <summary>补全取消不能声称复制失败或没有新增实例。</summary>
    [Fact]
    public async Task CancelingCompletionKeepsNewInstance()
    {
        var fixture = new Fixture();
        fixture.Services.Download = _ => Task.FromResult(new InstanceDownloadReport([], null, IsCanceled: true));
        await fixture.ViewModel.ImportFolderCommand.ExecuteAsync(null);
        Assert.Equal(fixture.Result.InstancePath, fixture.ViewModel.SelectedInstance?.InstancePath);
        Assert.Contains("已导入", fixture.ViewModel.StatusText);
        Assert.Contains("取消", fixture.ViewModel.TransferStatus);
        Assert.Single(fixture.Services.Notices);
    }

    /// <summary>复制进行时禁用其他导入与管理，取消会传递到同一复制操作。</summary>
    [Fact]
    public async Task BusyStateBlocksOtherOperationsAndCancelReachesCopy()
    {
        var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Services.Copy = async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return fixture.Result;
        };
        var operation = fixture.ViewModel.ImportFolderCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(fixture.ViewModel.IsBusy);
        Assert.False(fixture.ViewModel.ImportCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.ImportFolderCommand.CanExecute(null));
        fixture.ViewModel.ImportFolderCommand.Cancel();
        await operation;
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.Empty(fixture.Services.Downloads);
    }

    /// <summary>同名已有实例在确认说明中提示改名，不能悄悄覆盖或删除。</summary>
    [Fact]
    public async Task ExistingNameAddsPreviewWarning()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Instances.Add(new(fixture.Result.Manifest.Name, "existing", "existing/.minecraft", []));
        fixture.Services.NewName = null;
        await fixture.ViewModel.ImportFolderCommand.ExecuteAsync(null);
        Assert.Contains(fixture.Services.LastPreview!.Import.Notes, note => note.Contains("已有同名实例"));
        Assert.Empty(fixture.Services.Copies);
    }

    /// <summary>创建一组虚拟源目录与导入结果，不要求真实启动器安装。</summary>
    private sealed class Fixture
    {
        internal ExternalMinecraftInstance Source { get; } = new("GTNH", "Prism", "C:/external/GTNH", "C:/external/GTNH/.minecraft", "1.7.10", "Forge");
        internal InstanceImportResult Result { get; } = new("C:/eml/instances/GTNH", "C:/eml/instances/GTNH/.minecraft",
            new InstanceImportManifest { Name = "GTNH", SourceFormat = "Prism", MinecraftVersion = "1.7.10" });
        internal Services Services { get; }
        internal InstancesViewModel ViewModel { get; }

        /// <summary>使用一个严格替身实现接口；不应触及的归档、删除和检查调用直接失败。</summary>
        internal Fixture()
        {
            Services = new(Source, Result);
            ViewModel = new(Services, Services, Services, Services, "C:/eml/instances", Services, Services,
                notifications: Services, externalDiscovery: Services, folderImporter: Services, externalDialogs: Services);
        }
    }

    /// <summary>最小内存替身，记录实际请求并支持取消及失败注入。</summary>
    private sealed class Services : IExternalInstanceDiscovery, IMinecraftFolderImporter, IExternalInstanceDialogs,
        IMinecraftInstanceLocator, IMinecraftInstanceManager, IMinecraftInstanceImporter, IInstanceDialogs,
        IMinecraftInstanceChecker, IMinecraftInstanceDownloader, IOperationNotifications
    {
        private readonly InstanceImportResult _result;
        internal ExternalMinecraftInstance? Choice { get; set; }
        internal string? NewName { get; set; } = "GTNH";
        internal Func<CancellationToken, Task<InstanceImportResult>> Copy { get; set; }
        internal Func<CancellationToken, Task<InstanceDownloadReport>> Download { get; set; }
        internal List<ExternalMinecraftInstance> Copies { get; } = [];
        internal List<string> Downloads { get; } = [];
        internal List<OperationNotice> Notices { get; } = [];
        internal int Inspections { get; private set; }
        internal FolderInstanceImportPreview? LastPreview { get; private set; }

        /// <summary>默认返回可启动的完整补全报告，不借助任何实际文件。</summary>
        internal Services(ExternalMinecraftInstance source, InstanceImportResult result)
        {
            Choice = source; _result = result;
            Copy = _ => Task.FromResult(result);
            Download = _ => Task.FromResult(new InstanceDownloadReport([], new InstanceCheckReport
            {
                InstancePath = result.InstancePath, HasManifest = true, Mode = InstanceCheckMode.Full, LaunchProfilePath = "launch.json"
            }));
        }

        /// <summary>模拟发现候选，无账号或文件访问。</summary>
        public Task<ExternalInstanceDiscoveryResult> DiscoverAsync(string? directory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalInstanceDiscoveryResult(Choice is null ? [] : [Choice], []));
        /// <summary>返回测试指定的选择。</summary>
        public Task<ExternalMinecraftInstance?> ChooseInstanceAsync(IExternalInstanceDiscovery discovery, CancellationToken cancellationToken = default) => Task.FromResult(Choice);
        /// <summary>保存确认内容，便于断言同名提示。</summary>
        public string? ConfirmCopy(FolderInstanceImportPreview preview) { LastPreview = preview; return NewName; }
        /// <summary>统计预览调用并返回虚拟文件大小。</summary>
        public Task<FolderInstanceImportPreview> InspectAsync(ExternalMinecraftInstance instance, CancellationToken cancellationToken = default)
        {
            Inspections++;
            return Task.FromResult(new FolderInstanceImportPreview(instance,
                new(_result.Manifest.Name, "Prism", "1.7.10", [], 2, 0, 0, []), 10, "C:/eml/instances", []));
        }
        /// <summary>记录复制源，实际结果由测试控制。</summary>
        public Task<InstanceImportResult> ImportAsync(ExternalMinecraftInstance instance, string instanceName,
            IProgress<InstanceImportProgress>? progress = null, CancellationToken cancellationToken = default)
        { Copies.Add(instance); return Copy(cancellationToken); }
        /// <summary>刻意返回空扫描结果，验证提交后的回退选择仍可展示新实例。</summary>
        public Task<IReadOnlyList<MinecraftInstance>> FindAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MinecraftInstance>>([]);
        /// <summary>记录补全目标，确保它不是原启动器的实例。</summary>
        public Task<InstanceDownloadReport> DownloadMissingAsync(string instancePath, IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        { Downloads.Add(instancePath); return Download(cancellationToken); }
        /// <summary>收集最终用户通知，不产生弹窗。</summary>
        public void Report(OperationNotice notice) => Notices.Add(notice);
        /// <summary>目录导入不应调用归档预览。</summary>
        public Task<InstanceImportPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>目录导入不应生成或重新导入 ZIP。</summary>
        public Task<InstanceImportResult> ImportAsync(string archivePath, string instanceName, IProgress<InstanceImportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>目录复制流程不允许删除实例。</summary>
        public Task DeleteAsync(string instancePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>目录复制流程不允许移动原实例。</summary>
        public Task<string> RenameAsync(string instancePath, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>目录复制流程不应中转 ZIP。</summary>
        public Task ExportAsync(string instancePath, string destinationZip, IProgress<InstanceExportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>导入确认使用独立窗口接口。</summary>
        public bool ConfirmDelete(MinecraftInstance instance) => throw new NotSupportedException();
        /// <summary>不应请求导出位置。</summary>
        public string? ChooseExportPath(MinecraftInstance instance) => throw new NotSupportedException();
        /// <summary>不应请求归档文件。</summary>
        public string? ChooseImportPath() => throw new NotSupportedException();
        /// <summary>不应使用归档专用确认。</summary>
        public string? ConfirmImport(InstanceImportPreview preview) => throw new NotSupportedException();
        /// <summary>完整检查归下载服务负责，界面不重复执行。</summary>
        public Task<InstanceCheckReport> CheckAsync(string instancePath, InstanceCheckMode mode = InstanceCheckMode.Full, IProgress<InstanceCheckProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
