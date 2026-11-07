using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Minecraft;

namespace EML.Tests;

/// <summary>执行真实界面命令，验证导入与自动补全的衔接；替身不读写磁盘，也不访问网络。</summary>
public sealed class InstancesViewModelImportTests
{
    /// <summary>四种受支持整合包在导入提交后自动补全，并显示同一次补全的最终复查。</summary>
    [Theory]
    [InlineData("Prism")]
    [InlineData("Modrinth")]
    [InlineData("CurseForge")]
    [InlineData("Zip")]
    public async Task SupportedImportAutomaticallyCompletesDependencies(string format)
    {
        var fixture = new Fixture(format);

        await fixture.ViewModel.ImportCommand.ExecuteAsync(null);

        Assert.True(fixture.Importer.Committed);
        Assert.Equal([fixture.Result.InstancePath], fixture.Downloader.Paths);
        Assert.Equal(fixture.Result.InstancePath, fixture.ViewModel.SelectedInstance?.InstancePath);
        Assert.Single(fixture.ViewModel.DownloadFiles);
        Assert.Single(fixture.ViewModel.CheckFiles);
        Assert.Equal(2, fixture.ViewModel.SelectedDetailTab);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    /// <summary>通用 ZIP 的运行描述缺失时保留导入结果和失败信息，允许用户补齐后重试。</summary>
    [Fact]
    public async Task IncompleteZipKeepsImportedInstanceAndAllowsRetry()
    {
        var fixture = new Fixture("Zip");
        fixture.Downloader.Download = (_, _) => Task.FromException<InstanceDownloadReport>(
            new InvalidDataException("缺少版本 JSON，无法确定运行依赖。"));

        await fixture.ViewModel.ImportCommand.ExecuteAsync(null);

        Assert.True(fixture.Importer.Committed);
        Assert.Equal([fixture.Result.InstancePath], fixture.Downloader.Paths);
        Assert.Equal(fixture.Result.InstancePath, fixture.ViewModel.SelectedInstance?.InstancePath);
        Assert.Contains("缺少版本 JSON", fixture.ViewModel.StatusText);
        Assert.True(fixture.ViewModel.DownloadMissingCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.IsBusy);
    }

    /// <summary>刷新失败或未发现新实例也使用导入结果中的路径，不能误补全旧选中实例。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshProblemDoesNotSkipOrRedirectAutomaticCompletion(bool scanThrows)
    {
        var fixture = new Fixture();
        var old = new MinecraftInstance("Old", Path.Combine(fixture.InstancesRoot, "Old"), "old-game", []);
        fixture.ViewModel.Instances.Add(old);
        fixture.ViewModel.SelectedInstance = old;
        fixture.Locator.Find = _ => scanThrows
            ? Task.FromException<IReadOnlyList<MinecraftInstance>>(new IOException("模拟扫描失败"))
            : Task.FromResult<IReadOnlyList<MinecraftInstance>>([old]);

        await fixture.ViewModel.ImportCommand.ExecuteAsync(null);

        Assert.Equal([fixture.Result.InstancePath], fixture.Downloader.Paths);
        Assert.Equal(fixture.Result.InstancePath, fixture.ViewModel.SelectedInstance?.InstancePath);
        Assert.Contains(fixture.ViewModel.Instances, item => item.InstancePath == fixture.Result.InstancePath);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    /// <summary>从解压到补全持续占用界面，禁止中途启动其他实例写入命令。</summary>
    [Fact]
    public async Task ImportAndCompletionKeepBusyWithoutAnIdleGap()
    {
        var fixture = new Fixture();
        var imported = NewSignal<InstanceImportResult>();
        var downloadStarted = NewSignal<bool>();
        var downloaded = NewSignal<InstanceDownloadReport>();
        fixture.Importer.Import = token => imported.Task.WaitAsync(token);
        fixture.Downloader.Download = (_, token) =>
        {
            downloadStarted.TrySetResult(true);
            return downloaded.Task.WaitAsync(token);
        };
        var busyChanges = new List<bool>();
        fixture.ViewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(InstancesViewModel.IsBusy))
                busyChanges.Add(fixture.ViewModel.IsBusy);
        };

        var operation = fixture.ViewModel.ImportCommand.ExecuteAsync(null);
        Assert.True(fixture.ViewModel.IsBusy);
        imported.SetResult(fixture.Result);
        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(fixture.ViewModel.IsBusy);
        Assert.False(operation.IsCompleted);
        Assert.False(fixture.ViewModel.CanInteract);
        Assert.False(fixture.ViewModel.ImportCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.RefreshCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.DownloadMissingCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.RenameCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.DeleteCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.ExportCommand.CanExecute(null));
        Assert.Equal([true], busyChanges);

        downloaded.SetResult(fixture.SuccessReport());
        await operation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([true, false], busyChanges);
        Assert.True(fixture.ViewModel.DownloadMissingCommand.CanExecute(null));
    }

    /// <summary>导入失败时不开始下载，也不会将未提交实例显示为已导入。</summary>
    [Fact]
    public async Task ImportFailureDoesNotDownload()
    {
        var fixture = new Fixture();
        fixture.Importer.Import = _ => Task.FromException<InstanceImportResult>(new IOException("模拟导入失败"));

        await fixture.ViewModel.ImportCommand.ExecuteAsync(null);

        Assert.False(fixture.Importer.Committed);
        Assert.Empty(fixture.Downloader.Paths);
        Assert.Contains("导入失败", fixture.ViewModel.TransferStatus);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    /// <summary>取消尚未提交的导入会传递令牌，并且不开始后续补全。</summary>
    [Fact]
    public async Task CancelBeforeImportCommitDoesNotDownload()
    {
        var fixture = new Fixture();
        var importing = NewSignal<bool>();
        fixture.Importer.Import = async token =>
        {
            importing.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return fixture.Result;
        };

        var operation = fixture.ViewModel.ImportCommand.ExecuteAsync(null);
        await importing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.ViewModel.ImportCommand.Cancel();
        await operation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(fixture.Importer.Committed);
        Assert.Empty(fixture.Downloader.Paths);
        Assert.Contains("取消", fixture.ViewModel.TransferStatus);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    /// <summary>导入提交后在刷新期间取消，保留新实例且不开始下载。</summary>
    [Fact]
    public async Task CancelAfterCommitKeepsImportedInstance()
    {
        var fixture = new Fixture();
        var scanning = NewSignal<bool>();
        var scanned = NewSignal<IReadOnlyList<MinecraftInstance>>();
        fixture.Locator.Find = _ =>
        {
            scanning.TrySetResult(true);
            return scanned.Task;
        };

        var operation = fixture.ViewModel.ImportCommand.ExecuteAsync(null);
        await scanning.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.ViewModel.ImportCommand.Cancel();
        scanned.SetResult([fixture.Instance]);
        await operation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(fixture.Importer.Committed);
        Assert.Empty(fixture.Downloader.Paths);
        Assert.Equal(fixture.Result.InstancePath, fixture.ViewModel.SelectedInstance?.InstancePath);
        Assert.Contains("已导入", fixture.ViewModel.StatusText);
        Assert.DoesNotContain("没有新增实例", fixture.ViewModel.StatusText);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    /// <summary>自动补全既可通过导入取消，也可通过补全取消停止，两个入口都保留已导入实例。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherCancelActionStopsAutomaticCompletion(bool cancelThroughDownload)
    {
        var fixture = new Fixture();
        var downloading = NewSignal<bool>();
        CancellationToken receivedToken = default;
        fixture.Downloader.Download = async (_, token) =>
        {
            receivedToken = token;
            downloading.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return fixture.SuccessReport();
        };

        var operation = fixture.ViewModel.ImportCommand.ExecuteAsync(null);
        await downloading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.ViewModel.CancelDependencyDownloadCommand.CanExecute(null));
        if (cancelThroughDownload) fixture.ViewModel.CancelDependencyDownloadCommand.Execute(null);
        else fixture.ViewModel.ImportCommand.Cancel();
        await operation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(receivedToken.IsCancellationRequested);
        Assert.True(fixture.Importer.Committed);
        Assert.Equal(fixture.Result.InstancePath, fixture.ViewModel.SelectedInstance?.InstancePath);
        Assert.Contains("已导入", fixture.ViewModel.StatusText);
        Assert.Contains("取消", fixture.ViewModel.TransferStatus);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.False(fixture.ViewModel.CancelDependencyDownloadCommand.CanExecute(null));
    }

    /// <summary>下载器返回取消报告而非抛异常时，也正确显示导入已经成功。</summary>
    [Fact]
    public async Task CanceledDownloadReportKeepsImportSuccessVisible()
    {
        var fixture = new Fixture();
        fixture.Downloader.Download = (_, _) => Task.FromResult(new InstanceDownloadReport([], null, IsCanceled: true));

        await fixture.ViewModel.ImportCommand.ExecuteAsync(null);

        Assert.True(fixture.Importer.Committed);
        Assert.Contains("已导入", fixture.ViewModel.StatusText);
        Assert.Contains("取消", fixture.ViewModel.TransferStatus);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    /// <summary>自动补全失败不回滚导入，恢复网络后手动命令仍可对同一实例重试。</summary>
    [Fact]
    public async Task DownloadFailurePreservesInstanceAndAllowsManualRetry()
    {
        var fixture = new Fixture();
        fixture.Downloader.Download = (_, _) => Task.FromException<InstanceDownloadReport>(new HttpRequestException("模拟网络故障"));

        await fixture.ViewModel.ImportCommand.ExecuteAsync(null);

        Assert.True(fixture.Importer.Committed);
        Assert.Equal(fixture.Result.InstancePath, fixture.ViewModel.SelectedInstance?.InstancePath);
        Assert.Contains("已导入", fixture.ViewModel.StatusText);
        Assert.Contains("模拟网络故障", fixture.ViewModel.StatusText);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.True(fixture.ViewModel.DownloadMissingCommand.CanExecute(null));

        fixture.Downloader.Download = (_, _) => Task.FromResult(fixture.SuccessReport());
        await fixture.ViewModel.DownloadMissingCommand.ExecuteAsync(null);

        Assert.Equal([fixture.Result.InstancePath, fixture.Result.InstancePath], fixture.Downloader.Paths);
        Assert.Single(fixture.ViewModel.DownloadFiles);
        Assert.Single(fixture.ViewModel.CheckFiles);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    /// <summary>创建异步信号，避免测试替身在设置结果时重入界面命令。</summary>
    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>组装纯内存依赖，测试对象为生产 ViewModel 及其自动生成的命令。</summary>
    private sealed class Fixture
    {
        public string InstancesRoot { get; } = Path.Combine(Path.GetTempPath(), "EML-ui-import-tests", "instances");
        public InstanceImportResult Result { get; }
        public MinecraftInstance Instance { get; }
        public FakeImporter Importer { get; }
        public FakeLocator Locator { get; }
        public FakeDownloader Downloader { get; }
        public InstancesViewModel ViewModel { get; }

        /// <summary>设置一个可识别的导入结果，不创建目录或依赖真实整合包。</summary>
        public Fixture(string format = "CurseForge")
        {
            var path = Path.Combine(InstancesRoot, "New pack");
            Result = new(path, Path.Combine(path, ".minecraft"), new InstanceImportManifest
            {
                Name = "New pack", SourceFormat = format, MinecraftVersion = "1.20.1"
            });
            Instance = new(Result.Manifest.Name, path, Result.GameDirectory, ["1.20.1"]) { Format = format };
            Importer = new FakeImporter(Result);
            Locator = new FakeLocator { Find = _ => Task.FromResult<IReadOnlyList<MinecraftInstance>>([Instance]) };
            Downloader = new FakeDownloader { Download = (_, _) => Task.FromResult(SuccessReport()) };
            ViewModel = new(Locator, new UnusedManager(), Importer, new FakeDialogs(), InstancesRoot,
                new UnusedChecker(), Downloader);
        }

        /// <summary>提供下载及最终复查结果，验证界面发布的是共享补全工作流的真实返回值。</summary>
        public InstanceDownloadReport SuccessReport()
        {
            var file = new InstanceFileRequirement { Path = ".minecraft/mods/test.jar", Source = InstanceFileSource.DirectDownload };
            return new([new(file, InstanceDownloadState.Downloaded, "已校验")], new InstanceCheckReport
            {
                InstancePath = Result.InstancePath, Mode = InstanceCheckMode.Full, HasManifest = true,
                Files = [new(file, InstanceFileState.Verified, "已校验")]
            });
        }
    }

    /// <summary>模拟可暂停或失败的导入，只在成功返回时记录提交。</summary>
    private sealed class FakeImporter(InstanceImportResult result) : IMinecraftInstanceImporter
    {
        public Func<CancellationToken, Task<InstanceImportResult>> Import { get; set; } = _ => Task.FromResult(result);
        public bool Committed { get; private set; }

        /// <summary>返回已知格式的预览，跳过真实压缩包读取。</summary>
        public Task<InstanceImportPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstanceImportPreview(result.Manifest.Name, result.Manifest.SourceFormat, "1.20.1", [], 1, 1, 0, []));

        /// <summary>执行测试设定的导入行为，成功时才标记实例已提交。</summary>
        public async Task<InstanceImportResult> ImportAsync(string archivePath, string instanceName,
            IProgress<InstanceImportProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var imported = await Import(cancellationToken);
            Committed = true;
            return imported;
        }
    }

    /// <summary>替换实例扫描，允许模拟扫描失败或暂未发现新目录。</summary>
    private sealed class FakeLocator : IMinecraftInstanceLocator
    {
        public required Func<CancellationToken, Task<IReadOnlyList<MinecraftInstance>>> Find { get; set; }
        /// <summary>将扫描交给每个测试控制。</summary>
        public Task<IReadOnlyList<MinecraftInstance>> FindAllAsync(CancellationToken cancellationToken = default) => Find(cancellationToken);
    }

    /// <summary>记录每次补全的实际路径并模拟网络结果，不执行下载。</summary>
    private sealed class FakeDownloader : IMinecraftInstanceDownloader
    {
        public required Func<string, CancellationToken, Task<InstanceDownloadReport>> Download { get; set; }
        public List<string> Paths { get; } = [];
        /// <summary>保存目标路径，并传递同一取消令牌给可控下载行为。</summary>
        public Task<InstanceDownloadReport> DownloadMissingAsync(string instancePath,
            IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Paths.Add(instancePath);
            return Download(instancePath, cancellationToken);
        }
    }

    /// <summary>自动确认导入对话框，不创建 WPF 窗口。</summary>
    private sealed class FakeDialogs : IInstanceDialogs
    {
        /// <summary>返回虚拟压缩包路径。</summary>
        public string? ChooseImportPath() => "test-pack.zip";
        /// <summary>采用测试预览的建议名称。</summary>
        public string? ConfirmImport(InstanceImportPreview preview) => preview.SuggestedName;
        /// <summary>本组测试不允许删除实例。</summary>
        public bool ConfirmDelete(MinecraftInstance instance) => throw new NotSupportedException();
        /// <summary>本组测试不允许导出实例。</summary>
        public string? ChooseExportPath(MinecraftInstance instance) => throw new NotSupportedException();
    }

    /// <summary>防止导入工作流意外删除、重命名或导出实例。</summary>
    private sealed class UnusedManager : IMinecraftInstanceManager
    {
        /// <summary>若流程误调用删除，立即让测试失败。</summary>
        public Task DeleteAsync(string instancePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>若流程误调用重命名，立即让测试失败。</summary>
        public Task<string> RenameAsync(string instancePath, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>若流程误调用导出，立即让测试失败。</summary>
        public Task ExportAsync(string instancePath, string destinationZip, IProgress<InstanceExportProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>完整复查属于下载服务，界面不应另外执行一套检查流程。</summary>
    private sealed class UnusedChecker : IMinecraftInstanceChecker
    {
        /// <summary>若界面绕过补全服务另行检查，立即让测试失败。</summary>
        public Task<InstanceCheckReport> CheckAsync(string instancePath, InstanceCheckMode mode = InstanceCheckMode.Full,
            IProgress<InstanceCheckProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
