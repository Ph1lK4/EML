using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Downloads;
using EML.Core.Minecraft.Catalog;
using EML.Core.Minecraft.Gtnh;

namespace EML.Tests;

/// <summary>用内存目录和受控安装任务测试镜像页，不下载大文件、不创建真实实例。</summary>
public sealed class GtnhPackDownloadViewModelTests
{
    /// <summary>构造与隐藏筛选不联网；无本地实例的页面也能打开并明确选择新包。</summary>
    [Fact]
    public async Task OpensWithoutExistingInstanceAndRequiresExplicitSelection()
    {
        await using var fixture = new Fixture();
        fixture.Model.JavaFilterIndex = 1;
        await fixture.Model.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Service.Queries);

        await fixture.OpenAsync();
        Assert.Equal([false], fixture.Service.Queries);
        Assert.Single(fixture.Model.Releases);
        Assert.Null(fixture.Model.SelectedRelease);
        Assert.False(fixture.Model.InstallCommand.CanExecute(null));
        fixture.Model.SelectedRelease = fixture.Model.Releases[0];
        Assert.True(fixture.Model.InstallCommand.CanExecute(null));
        Assert.Empty(fixture.Notices.Items);
    }

    /// <summary>默认隐藏测试包；用户开启后重读目录，并按返回顺序显示稳定与测试变体。</summary>
    [Fact]
    public async Task PrereleasesRequireExplicitOptIn()
    {
        await using var fixture = new Fixture();
        fixture.Service.Query = (_, _) => Task.FromResult<IReadOnlyList<GtnhPackRelease>>([Stable, Java8, Beta]);
        await fixture.OpenAsync();
        Assert.DoesNotContain(Beta, fixture.Model.Releases);

        fixture.Model.IncludePrereleases = true;
        await fixture.Model.ReleasesLoadTask;

        Assert.Equal([false, true], fixture.Service.Queries);
        Assert.Equal([Stable, Java8, Beta], fixture.Model.Releases);
        fixture.Model.SelectedRelease = Beta;
        Assert.True(fixture.Model.IsPrereleaseSelection);
        Assert.Contains("测试版本", fixture.Model.ReleaseInformation);
    }

    /// <summary>Java 筛选只在本地运行；被筛掉的选中版本失效，不能继续下载隐藏目标。</summary>
    [Fact]
    public async Task JavaFilterClearsOnlyExcludedSelectionWithoutQueryingAgain()
    {
        await using var fixture = new Fixture();
        await fixture.OpenAsync();
        fixture.Model.SelectedRelease = Stable;
        fixture.Model.JavaFilterIndex = 1;
        Assert.Equal(Stable, fixture.Model.SelectedRelease);
        Assert.Equal([Stable], fixture.Model.Releases);
        fixture.Model.JavaFilterIndex = 2;
        Assert.Null(fixture.Model.SelectedRelease);
        Assert.Equal([Java8], fixture.Model.Releases);
        fixture.Model.SelectedRelease = Stable;
        Assert.Null(fixture.Model.SelectedRelease);
        Assert.Single(fixture.Service.Queries);
    }

    /// <summary>上一范围的迟到响应不能覆盖已切换的测试版本列表或已选文件。</summary>
    [Fact]
    public async Task FilterChangeRejectsLateResponse()
    {
        await using var fixture = new Fixture();
        var old = Signal<IReadOnlyList<GtnhPackRelease>>();
        fixture.Service.Query = (include, _) => include
            ? Task.FromResult<IReadOnlyList<GtnhPackRelease>>([Beta]) : old.Task;
        fixture.Model.SetActive(true);
        var stale = fixture.Model.ReleasesLoadTask;
        fixture.Model.IncludePrereleases = true;
        await fixture.Model.ReleasesLoadTask;
        fixture.Model.SelectedRelease = Beta;
        old.SetResult([Stable]);
        await stale;

        Assert.Equal([Beta], fixture.Model.Releases);
        Assert.Equal(Beta, fixture.Model.SelectedRelease);
        Assert.Empty(fixture.Notices.Items);
    }

    /// <summary>离开取消未完成查询，返回后重读；旧失败不弹窗打断新页面。</summary>
    [Fact]
    public async Task LeavingAndReturningReloadsCanceledReadWithoutStaleNotice()
    {
        await using var fixture = new Fixture();
        var old = Signal<IReadOnlyList<GtnhPackRelease>>();
        fixture.Service.Query = (_, _) => old.Task;
        fixture.Model.SetActive(true);
        var stale = fixture.Model.ReleasesLoadTask;
        fixture.Model.SetActive(false);
        Assert.True(fixture.Service.Tokens[0].IsCancellationRequested);
        fixture.Service.Query = (_, _) => Task.FromResult<IReadOnlyList<GtnhPackRelease>>([Stable]);
        await fixture.OpenAsync();
        old.SetException(new IOException("旧目录失败"));
        await stale;

        Assert.Equal([Stable], fixture.Model.Releases);
        Assert.Equal(2, fixture.Service.Queries.Count);
        Assert.Empty(fixture.Notices.Items);
    }

    /// <summary>目录读取失败保留原因，显式刷新恢复时只各通知一次。</summary>
    [Fact]
    public async Task QueryFailureAndManualRefreshAreVisible()
    {
        await using var fixture = new Fixture();
        fixture.Service.Query = (_, _) => Task.FromException<IReadOnlyList<GtnhPackRelease>>(new IOException("镜像目录暂时不可达"));
        await fixture.OpenAsync();
        Assert.Contains("镜像目录暂时不可达", fixture.Model.Status);
        Assert.Empty(fixture.Model.Releases);

        fixture.Service.Query = (_, _) => Task.FromResult<IReadOnlyList<GtnhPackRelease>>([Stable]);
        await fixture.Model.RefreshCommand.ExecuteAsync(null);

        Assert.Equal([OperationNoticeKind.Failure, OperationNoticeKind.Success], fixture.Notices.Items.Select(item => item.Kind));
        Assert.Single(fixture.Model.Releases);
    }

    /// <summary>刷新期间不能使用旧列表下载，宿主忙碌或直接执行禁用命令也不会进入安装。</summary>
    [Fact]
    public async Task HostBusyAndListRefreshPreventInstallation()
    {
        await using var fixture = new Fixture();
        await fixture.OpenAsync();
        fixture.Model.SelectedRelease = Stable;
        fixture.CanInstall = false;
        fixture.Model.NotifyHostStateChanged();
        Assert.False(fixture.Model.InstallCommand.CanExecute(null));
        await fixture.Model.InstallCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Installed);
        fixture.CanInstall = true;
        var pending = Signal<IReadOnlyList<GtnhPackRelease>>();
        fixture.Service.Query = (_, _) => pending.Task;
        var refresh = fixture.Model.RefreshCommand.ExecuteAsync(null);
        Assert.True(fixture.Model.IsLoading);
        Assert.False(fixture.Model.InstallCommand.CanExecute(null));
        pending.SetResult([Stable]);
        await refresh;
        Assert.True(fixture.Model.InstallCommand.CanExecute(null));
    }

    /// <summary>安装期间锁定包与筛选；离开页面不取消用户已确认的下载和导入。</summary>
    [Fact]
    public async Task InstallationKeepsSelectionAndContinuesWhenPageHidden()
    {
        await using var fixture = new Fixture();
        var pending = Signal<string>();
        fixture.Install = (_, _, _) => pending.Task;
        await fixture.OpenAsync();
        fixture.Model.SelectedRelease = Stable;
        var install = fixture.Model.InstallCommand.ExecuteAsync(null);
        Assert.True(fixture.Model.IsInstalling);
        Assert.False(fixture.Model.CanBrowse);
        fixture.Model.SelectedRelease = Java8;
        fixture.Model.IncludePrereleases = true;
        fixture.Model.JavaFilterIndex = 2;
        fixture.Model.SetActive(false);
        Assert.Equal(Stable, fixture.Model.SelectedRelease);
        Assert.False(fixture.Model.IncludePrereleases);
        Assert.Equal(0, fixture.Model.JavaFilterIndex);
        Assert.False(fixture.InstallToken.IsCancellationRequested);
        pending.SetResult("实例已导入，但依赖仍需处理。");
        await install;

        Assert.Equal([Stable], fixture.Installed);
        Assert.Contains("仍需处理", fixture.Model.InstallStatus);
        Assert.False(fixture.Model.IsInstalling);
        Assert.Empty(fixture.Notices.Items);
    }

    /// <summary>安装委托的完整、部分完成和取消文字均不被页面重新判为成功。</summary>
    [Theory]
    [InlineData("实例已导入并补全。")]
    [InlineData("实例已导入，但补全失败。")]
    [InlineData("用户取消了名称确认。")]
    public async Task HostResultDoesNotGenerateDuplicateNotice(string result)
    {
        await using var fixture = new Fixture();
        fixture.Install = (_, _, _) => Task.FromResult(result);
        await fixture.OpenAsync();
        fixture.Model.SelectedRelease = Stable;
        await fixture.Model.InstallCommand.ExecuteAsync(null);
        Assert.Equal(result, fixture.Model.InstallStatus);
        Assert.Empty(fixture.Notices.Items);
    }

    /// <summary>委托抛出的未处理失败仍需可见，并保留可复制的缓存路径。</summary>
    [Fact]
    public async Task UnhandledInstallFailureShowsReasonAndPath()
    {
        await using var fixture = new Fixture();
        const string path = @"C:\test\中文缓存\pack.zip";
        fixture.Install = (_, _, _) => Task.FromException<string>(new FileNotFoundException("找不到已下载的原包", path));
        await fixture.OpenAsync();
        fixture.Model.SelectedRelease = Stable;
        await fixture.Model.InstallCommand.ExecuteAsync(null);
        var notice = Assert.Single(fixture.Notices.Items);
        Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
        Assert.Contains(path, notice.Paths);
        Assert.Contains("找不到", notice.Message);
        Assert.False(fixture.Model.IsInstalling);
    }

    /// <summary>关闭时取消安装令牌并等待服务完成，不能提前释放仍在使用的下载器。</summary>
    [Fact]
    public async Task StopCancelsAndAwaitsInstallation()
    {
        await using var fixture = new Fixture();
        var pending = Signal<string>();
        fixture.Install = (_, _, _) => pending.Task;
        await fixture.OpenAsync();
        fixture.Model.SelectedRelease = Stable;
        var install = fixture.Model.InstallCommand.ExecuteAsync(null);
        var stop = fixture.Model.StopAsync();
        Assert.True(fixture.InstallToken.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        pending.SetCanceled(fixture.InstallToken);
        await Task.WhenAll(install, stop);
        Assert.False(fixture.Model.IsInstalling);
        Assert.False(fixture.Model.InstallCommand.CanExecute(null));
        Assert.Equal(OperationNoticeKind.Cancelled, Assert.Single(fixture.Notices.Items).Kind);
    }

    /// <summary>原包完成只更新阶段；过期下载进度不能在任务结束后改写结果。</summary>
    [Fact]
    public async Task ArchiveCompletionIsNotInstallationSuccessAndLateProgressIsIgnored()
    {
        await using var fixture = new Fixture();
        var pending = Signal<string>();
        IProgress<FileDownloadProgress>? progress = null;
        fixture.Install = (_, received, _) => { progress = received; return pending.Task; };
        await fixture.OpenAsync();
        fixture.Model.SelectedRelease = Stable;
        var previous = SynchronizationContext.Current;
        var context = new ImmediateContext();
        Task install;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            install = fixture.Model.InstallCommand.ExecuteAsync(null);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        progress!.Report(new(10, 10, 1) { Phase = FileDownloadPhase.Completed });
        Assert.Equal(100, fixture.Model.ArchivePercent);
        Assert.Contains("等待导入", fixture.Model.ArchiveProgress);
        Assert.True(fixture.Model.IsInstalling);
        Assert.Empty(fixture.Notices.Items);
        pending.SetResult("实例已导入。");
        await install;
        var ended = fixture.Model.ArchiveProgress;
        progress.Report(new(1, 10, 1));
        Assert.Equal(ended, fixture.Model.ArchiveProgress);
        Assert.Equal("实例已导入。", fixture.Model.InstallStatus);
    }

    /// <summary>外链只能打开固定镜像主页，远端文件名不成为浏览器命令。</summary>
    [Fact]
    public async Task OpensOnlyConfiguredMirrorWebsite()
    {
        await using var fixture = new Fixture();
        fixture.Model.OpenMirrorWebsiteCommand.Execute(null);
        Assert.Equal("https://mirror.gaytnh.com/", Assert.Single(fixture.Websites).AbsoluteUri);
        await fixture.Model.StopAsync();
        Assert.False(fixture.Model.OpenMirrorWebsiteCommand.CanExecute(null));
    }

    private static GtnhPackRelease Stable => Release("2.9.0", "17-26");
    private static GtnhPackRelease Java8 => Release("2.9.0", "8");
    private static GtnhPackRelease Beta => Release("2.9.0-beta-3", "17-26", true);

    /// <summary>以固定文件名提供目录条目，所有大小均为测试数据。</summary>
    private static GtnhPackRelease Release(string version, string java, bool prerelease = false)
    {
        var filename = $"GT_New_Horizons_{version}_Java_{java}.zip";
        return new() { Id = (prerelease ? "betas/" : "") + filename, Version = version,
            JavaVersion = java, FileName = filename, Size = 1048576, IsPrerelease = prerelease };
    }

    /// <summary>不内联异步业务续体，使测试可以单独控制迟到响应。</summary>
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>进度测试显式在同步上下文执行回调，避免依赖线程池时序。</summary>
    private sealed class ImmediateContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    /// <summary>记录公开结果，不打开真实窗口。</summary>
    private sealed class Notices : IOperationNotifications
    {
        public List<OperationNotice> Items { get; } = [];
        public void Report(OperationNotice notice) => Items.Add(notice);
    }

    /// <summary>记录查询范围与取消令牌，原包准备必须由宿主安装入口调用。</summary>
    private sealed class Service : IGtnhPackService
    {
        public List<bool> Queries { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Func<bool, CancellationToken, Task<IReadOnlyList<GtnhPackRelease>>> Query { get; set; } =
            (_, _) => Task.FromResult<IReadOnlyList<GtnhPackRelease>>([Stable, Java8]);
        public Task<IReadOnlyList<GtnhPackRelease>> GetReleasesAsync(bool includePrereleases = false, CancellationToken cancellationToken = default)
        {
            Queries.Add(includePrereleases); Tokens.Add(cancellationToken);
            return Query(includePrereleases, cancellationToken);
        }
        public Task<PreparedInstanceArchive> PrepareAsync(string releaseId, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("页面不绕过宿主直接准备原包。");
    }

    /// <summary>不构造实例管理器，证明镜像页面不要求用户先安装一个 GTNH。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        public Service Service { get; } = new();
        public Notices Notices { get; } = new();
        public List<Uri> Websites { get; } = [];
        public List<GtnhPackRelease> Installed { get; } = [];
        public bool CanInstall { get; set; } = true;
        public CancellationToken InstallToken { get; private set; }
        public Func<GtnhPackRelease, IProgress<FileDownloadProgress>, CancellationToken, Task<string>> Install { get; set; } =
            (_, _, _) => Task.FromResult("实例已导入并补全。");
        public GtnhPackDownloadViewModel Model { get; }
        /// <summary>安装回调只记录选择并执行测试提供的内存任务。</summary>
        public Fixture() => Model = new(Service, (release, progress, token) =>
        {
            Installed.Add(release); InstallToken = token; return Install(release, progress, token);
        }, () => CanInstall, Websites.Add, Notices);
        /// <summary>等待页面首次目录读取完成。</summary>
        public async Task OpenAsync() { Model.SetActive(true); await Model.ReleasesLoadTask; }
        /// <summary>等待所有测试请求结束后释放页面。</summary>
        public async ValueTask DisposeAsync() { await Model.StopAsync(); Model.Dispose(); }
    }
}
