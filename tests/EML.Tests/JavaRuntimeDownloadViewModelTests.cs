using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Java;

namespace EML.Tests;

/// <summary>通过生产 Java 页面模型验证查询、安装和取消，不访问网络或实际运行 Java。</summary>
public sealed class JavaRuntimeDownloadViewModelTests
{
    /// <summary>未配置下载服务的旧调用方保留检测功能，不显示可执行的下载命令。</summary>
    [Fact]
    public void DetectionOnlyCallerCannotDownload()
    {
        var model = new JavaSettingsViewModel(new Locator());
        Assert.True(model.DetectJavaCommand.CanExecute(null));
        Assert.False(model.QueryPackagesCommand.CanExecute(null));
        Assert.False(model.DownloadJavaCommand.CanExecute(null));
    }

    /// <summary>查询不触发安装；用户确认安装后登记结果和可复制路径。</summary>
    [Fact]
    public async Task QueryThenInstallRegistersEnvironmentAndReportsPaths()
    {
        var fixture = new Fixture();
        await fixture.Model.QueryPackagesCommand.ExecuteAsync(null);
        Assert.Equal((21, "x64"), Assert.Single(fixture.Service.Queries));
        Assert.Empty(fixture.Service.Installed);
        Assert.Equal(fixture.Package, fixture.Model.SelectedPackage);
        Assert.True(fixture.Model.DownloadJavaCommand.CanExecute(null));

        await fixture.Model.DownloadJavaCommand.ExecuteAsync(null);

        Assert.Equal(fixture.Package, Assert.Single(fixture.Service.Installed));
        Assert.Equal(fixture.Service.Installation, Assert.Single(fixture.Model.Installations));
        Assert.Equal(fixture.Service.Installation, fixture.Model.SelectedInstallation);
        Assert.Equal(100d, fixture.Model.DownloadPercent!.Value);
        Assert.False(fixture.Model.IsBusy);
        var notice = fixture.Notices.Items[^1];
        Assert.Equal(OperationNoticeKind.Success, notice.Kind);
        Assert.Contains(fixture.Service.Installation.ExecutablePath, notice.Paths);
        Assert.Contains(fixture.Service.InstallationRoot, notice.Paths);
        Assert.Equal(0, fixture.Locator.Calls);
    }

    /// <summary>无包和查询错误各自给出明确原因，不保留旧的可下载选择。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrFailedQueryCannotLeaveDownloadEnabled(bool failed)
    {
        var fixture = new Fixture();
        fixture.Service.Query = (_, _, _) => failed
            ? Task.FromException<IReadOnlyList<JavaRuntimePackage>>(new IOException("官方目录连接超时"))
            : Task.FromResult<IReadOnlyList<JavaRuntimePackage>>([]);

        await fixture.Model.QueryPackagesCommand.ExecuteAsync(null);

        Assert.Null(fixture.Model.SelectedPackage);
        Assert.Empty(fixture.Model.Packages);
        Assert.False(fixture.Model.DownloadJavaCommand.CanExecute(null));
        Assert.Equal(failed ? OperationNoticeKind.Failure : OperationNoticeKind.Warning, Assert.Single(fixture.Notices.Items).Kind);
        Assert.Contains(failed ? "官方目录连接超时" : "暂未提供", fixture.Model.DownloadStatus);
    }

    /// <summary>筛选变化时使已选包失效，旧查询即使忽略取消也不能覆盖新筛选。</summary>
    [Fact]
    public async Task ChangedFilterRejectsLateQuery()
    {
        var fixture = new Fixture();
        var pending = new TaskCompletionSource<IReadOnlyList<JavaRuntimePackage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Query = (_, _, _) => pending.Task;
        var query = fixture.Model.QueryPackagesCommand.ExecuteAsync(null);
        Assert.True(fixture.Model.IsBusy);
        Assert.False(fixture.Model.CanEditDownloadFilters);
        fixture.Model.SelectedMajorVersion = 25;
        pending.SetResult([fixture.Package]);
        await query;
        Assert.Empty(fixture.Model.Packages);
        Assert.Null(fixture.Model.SelectedPackage);
        Assert.False(fixture.Model.DownloadJavaCommand.CanExecute(null));
    }

    /// <summary>下载失败只报告失败并恢复操作，不把未经验证的 Java 加入检测列表。</summary>
    [Fact]
    public async Task InstallFailureDoesNotRegisterEnvironment()
    {
        var fixture = new Fixture();
        await fixture.Model.QueryPackagesCommand.ExecuteAsync(null);
        fixture.Service.Install = (_, _, _) => Task.FromException<JavaInstallation>(new IOException("Java 包校验失败"));

        await fixture.Model.DownloadJavaCommand.ExecuteAsync(null);

        Assert.Empty(fixture.Model.Installations);
        Assert.False(fixture.Model.IsBusy);
        Assert.True(fixture.Model.DownloadJavaCommand.CanExecute(null));
        var notice = fixture.Notices.Items[^1];
        Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
        Assert.Contains("Java 包校验失败", notice.Message);
        Assert.Contains(fixture.Service.InstallationRoot, notice.Paths);
    }

    /// <summary>下载占用本页操作锁，重复点击不会增加安装调用；取消结束原任务。</summary>
    [Fact]
    public async Task BusyDownloadPreventsConcurrentOperationsAndSupportsCancel()
    {
        var fixture = new Fixture();
        await fixture.Model.QueryPackagesCommand.ExecuteAsync(null);
        fixture.Service.Install = async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return fixture.Service.Installation;
        };
        var download = fixture.Model.DownloadJavaCommand.ExecuteAsync(null);
        Assert.True(fixture.Model.IsInstalling);
        Assert.False(fixture.Model.DetectJavaCommand.CanExecute(null));
        Assert.False(fixture.Model.QueryPackagesCommand.CanExecute(null));
        await fixture.Model.DownloadJavaCommand.ExecuteAsync(null);
        Assert.Single(fixture.Service.Installed);
        fixture.Model.CancelOperationCommand.Execute(null);
        await download.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Model.IsBusy);
        Assert.Empty(fixture.Model.Installations);
        Assert.Equal(OperationNoticeKind.Cancelled, fixture.Notices.Items[^1].Kind);
    }

    /// <summary>关闭取消并等待查询，之后即使直接调用命令也不再访问服务。</summary>
    [Fact]
    public async Task StopCancelsPendingQueryAndPreventsFutureWork()
    {
        var fixture = new Fixture();
        fixture.Service.Query = async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return [];
        };
        var query = fixture.Model.QueryPackagesCommand.ExecuteAsync(null);
        await fixture.Model.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await query;
        Assert.False(fixture.Model.CanEditDownloadFilters);
        Assert.False(fixture.Model.DetectJavaCommand.CanExecute(null));
        await fixture.Model.QueryPackagesCommand.ExecuteAsync(null);
        Assert.Single(fixture.Service.Queries);
        Assert.Empty(fixture.Notices.Items);
    }

    /// <summary>路径大小写不产生重复条目，外部安装结果不会被已开始的检测覆盖。</summary>
    [Fact]
    public async Task RegisterInstallationSurvivesAnOlderDetectionSnapshot()
    {
        var fixture = new Fixture();
        var pending = new TaskCompletionSource<IReadOnlyList<JavaInstallation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Locator.Find = _ => pending.Task;
        var detection = fixture.Model.DetectJavaCommand.ExecuteAsync(null);
        fixture.Model.RegisterInstallation(fixture.Service.Installation);
        fixture.Model.RegisterInstallation(fixture.Service.Installation with { ExecutablePath = fixture.Service.Installation.ExecutablePath.ToUpperInvariant() });
        pending.SetResult([]);
        await detection;
        Assert.Single(fixture.Model.Installations);
        Assert.Equal(fixture.Service.Installation.ExecutablePath.ToUpperInvariant(), fixture.Model.SelectedInstallation!.ExecutablePath);
    }

    /// <summary>后续完整检测不再保留已被用户删除的旧托管环境。</summary>
    [Fact]
    public async Task FreshDetectionCanRemovePreviouslyRegisteredEnvironment()
    {
        var fixture = new Fixture();
        fixture.Model.RegisterInstallation(fixture.Service.Installation);
        await fixture.Model.DetectJavaCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Model.Installations);
    }

    /// <summary>切换架构后旧包不能继续下载，服务返回不匹配包时也不加入选择。</summary>
    [Fact]
    public async Task ArchitectureChangeInvalidatesOldPackage()
    {
        var fixture = new Fixture();
        await fixture.Model.QueryPackagesCommand.ExecuteAsync(null);
        fixture.Model.SelectedArchitecture = "aarch64";
        Assert.Null(fixture.Model.SelectedPackage);
        Assert.False(fixture.Model.DownloadJavaCommand.CanExecute(null));
        await fixture.Model.QueryPackagesCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Model.Packages);
    }

    /// <summary>组装真实模型，所有外部操作由受控内存服务替代。</summary>
    private sealed class Fixture
    {
        public JavaRuntimePackage Package { get; } = new(21, "21.0.9+10", "x64", "jdk.zip", "https://example.invalid/jdk.zip", new string('a', 64), 123456);
        public RuntimeService Service { get; } = new();
        public Locator Locator { get; } = new();
        public Notices Notices { get; } = new();
        public JavaSettingsViewModel Model { get; }
        /// <summary>固定默认架构让测试不依赖执行机器。</summary>
        public Fixture()
        {
            Service.Query = (_, _, _) => Task.FromResult<IReadOnlyList<JavaRuntimePackage>>([Package]);
            Model = new(Locator, Notices, Service) { SelectedArchitecture = "x64", SelectedMajorVersion = 21 };
        }
    }

    /// <summary>模拟检测快照与迟到读取，任何扫描不会访问文件系统。</summary>
    private sealed class Locator : IJavaLocator
    {
        public int Calls { get; private set; }
        public Func<CancellationToken, Task<IReadOnlyList<JavaInstallation>>> Find { get; set; } = _ => Task.FromResult<IReadOnlyList<JavaInstallation>>([]);
        /// <summary>计数并执行测试预设的扫描。</summary>
        public Task<IReadOnlyList<JavaInstallation>> FindAllAsync(CancellationToken cancellationToken = default)
        { Calls++; return Find(cancellationToken); }
    }

    /// <summary>只记录请求，下载文件和启动进程均不发生。</summary>
    private sealed class RuntimeService : IJavaRuntimeService
    {
        public string InstallationRoot => @"C:\EML-test-only\runtimes\java";
        public JavaInstallation Installation { get; } = new(@"C:\EML-test-only\runtimes\java\21\bin\java.exe", "21.0.9", "x64");
        public List<(int, string)> Queries { get; } = [];
        public List<JavaRuntimePackage> Installed { get; } = [];
        public Func<int, string, CancellationToken, Task<IReadOnlyList<JavaRuntimePackage>>> Query { get; set; } = (_, _, _) => Task.FromResult<IReadOnlyList<JavaRuntimePackage>>([]);
        public Func<JavaRuntimePackage, IProgress<JavaRuntimeInstallProgress>?, CancellationToken, Task<JavaInstallation>>? Install { get; set; }
        /// <summary>记录目录查询条件。</summary>
        public Task<IReadOnlyList<JavaRuntimePackage>> GetPackagesAsync(int majorVersion, string architecture, CancellationToken cancellationToken = default)
        { Queries.Add((majorVersion, architecture)); return Query(majorVersion, architecture, cancellationToken); }
        /// <summary>记录安装快照并交由测试决定完成、异常或取消。</summary>
        public Task<JavaInstallation> InstallAsync(JavaRuntimePackage package, IProgress<JavaRuntimeInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        { Installed.Add(package); return Install?.Invoke(package, progress, cancellationToken) ?? Task.FromResult(Installation); }
    }

    /// <summary>收集用户可见结果，测试不创建弹窗。</summary>
    private sealed class Notices : IOperationNotifications
    {
        public List<OperationNotice> Items { get; } = [];
        /// <summary>按操作顺序记录通知。</summary>
        public void Report(OperationNotice notice) => Items.Add(notice);
    }
}
