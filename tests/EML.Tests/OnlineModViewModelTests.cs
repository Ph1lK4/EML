using EML.App.ViewModels;
using EML.App.Services;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods.Online;

namespace EML.Tests;

public sealed class OnlineModViewModelTests
{
    private static readonly OnlineProjectId ProjectId = new(ModProvider.Modrinth, "project");
    private static readonly OnlineModDependency Optional = new()
    { Key = "optional-project", Project = new(ModProvider.Modrinth, "extra"), Name = "Extra", Kind = OnlineDependencyKind.Optional };

    /// <summary>安装结果按真实成功、部分失败、全部失败或取消分类，一次安装只报告一次。</summary>
    [Theory]
    [InlineData(1, false, false, OperationNoticeKind.Success)]
    [InlineData(1, true, false, OperationNoticeKind.Warning)]
    [InlineData(0, true, false, OperationNoticeKind.Failure)]
    [InlineData(1, false, true, OperationNoticeKind.Cancelled)]
    public async Task InstallationReportsOneAccurateNotice(int installed, bool issue, bool cancelled, OperationNoticeKind expected)
    {
        var notices = new OnlineNoticeRecorder();
        var installer = new Installer { Result = new(installed, 0, cancelled, issue ? ["依赖下载被拒绝"] : []) };
        var vm = Create(new Catalog(), installer, notifications: notices);
        await SelectAsync(vm); await vm.PreviewCommand.ExecuteAsync(null);
        notices.Items.Clear();
        await vm.InstallCommand.ExecuteAsync(null);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(expected, notice.Kind);
        Assert.Contains(Instance().InstancePath, notice.Paths);
        if (issue) Assert.Contains("依赖下载被拒绝", notice.Message);
        await vm.StopAsync();
    }

    /// <summary>预览失败的具体原因与目标路径可复制，不能产生成功通知。</summary>
    [Fact]
    public async Task FailedPreviewReportsCauseAndInstancePath()
    {
        var notices = new OnlineNoticeRecorder();
        var installer = new Installer { Plan = _ => Task.FromException<OnlineModInstallPlan>(new IOException("无法读取实例清单")) };
        var vm = Create(new Catalog(), installer, notifications: notices); await SelectAsync(vm);
        await vm.PreviewCommand.ExecuteAsync(null);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
        Assert.Contains("无法读取实例清单", notice.Message);
        Assert.Contains(Instance().InstancePath, notice.Paths);
        await vm.StopAsync();
    }

    /// <summary>自动防抖搜索不弹成功，显式搜索有一次结果，过期请求的失败不得打断新页面。</summary>
    [Fact]
    public async Task SearchNotificationsSkipAutomaticAndStaleResults()
    {
        var notices = new OnlineNoticeRecorder();
        var pending = new TaskCompletionSource<OnlineModSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new Catalog { Search = query => query.Text == "old" ? pending.Task : Task.FromResult(Page("current")) };
        var vm = Create(catalog, notifications: notices);
        vm.SetActive(true); await vm.SearchTask;
        Assert.Empty(notices.Items);
        vm.SearchText = "old"; var old = vm.SearchCommand.ExecuteAsync(null);
        vm.SearchText = "current"; await vm.SearchCommand.ExecuteAsync(null);
        pending.SetException(new IOException("过期请求错误")); await old;
        var notice = Assert.Single(notices.Items);
        Assert.Equal(OperationNoticeKind.Success, notice.Kind);
        Assert.DoesNotContain("过期请求错误", notice.Message);
        await vm.StopAsync();
    }

    /// <summary>只有平台编号的需求可缺省路径，具备路径时通知保留中文及全角空格。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".minecraft/mods/\u3000中文模组（适配版）\u3000.jar")]
    public async Task InstallNoticesHandleOptionalRequirementPaths(string? relativePath)
    {
        var notices = new OnlineNoticeRecorder();
        var installer = new Installer
        {
            Plan = request => Task.FromResult(Plan(request) with
            {
                Items = [new(Version(), Version().Files[0] with
                {
                    Requirement = new() { Path = relativePath, Size = 5 }
                }, "主动选择", OnlineModPlanStatus.Download)]
            })
        };
        var vm = Create(new Catalog(), installer, notifications: notices);
        await SelectAsync(vm); await vm.PreviewCommand.ExecuteAsync(null); notices.Items.Clear();
        await vm.InstallCommand.ExecuteAsync(null);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(OperationNoticeKind.Success, notice.Kind);
        Assert.False(vm.IsInstalling);
        if (string.IsNullOrEmpty(relativePath)) Assert.Equal(Instance().InstancePath, Assert.Single(notice.Paths));
        else Assert.Contains(Path.Combine(Instance().InstancePath, relativePath.Replace('/', Path.DirectorySeparatorChar)), notice.Paths);
        await vm.StopAsync();
    }

    /// <summary>切换实例后迟到的安装异常不能向新实例弹出失败提示。</summary>
    [Fact]
    public async Task LateInstallFailureDoesNotNotifyAfterInstanceSwitch()
    {
        var notices = new OnlineNoticeRecorder();
        var pending = new TaskCompletionSource<OnlineModInstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(new Catalog(), new Installer { Install = (_, _, _) => pending.Task }, notifications: notices);
        await SelectAsync(vm); await vm.PreviewCommand.ExecuteAsync(null); notices.Items.Clear();
        var install = vm.InstallCommand.ExecuteAsync(null);
        vm.SetInstance(Instance("new"));
        pending.SetException(new IOException("旧实例下载失败"));
        await install;
        Assert.Empty(notices.Items);
        Assert.False(vm.IsInstalling);
        await vm.StopAsync();
    }

    /// <summary>当前详情失败必须可见，切换实例后迟到的同一异常则不能弹窗。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetailFailureOnlyNotifiesCurrentRequest(bool switchInstance)
    {
        var notices = new OnlineNoticeRecorder();
        var pending = new TaskCompletionSource<OnlineModProject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(new Catalog { Detail = _ => pending.Task }, notifications: notices);
        vm.SelectedProject = new(Project("old")); var detail = vm.DetailTask;
        if (switchInstance) vm.SetInstance(Instance("new"));
        pending.SetException(new IOException("项目详情服务器不可达"));
        await detail;
        if (switchInstance) Assert.Empty(notices.Items);
        else
        {
            var notice = Assert.Single(notices.Items);
            Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
            Assert.Contains("项目详情服务器不可达", notice.Message);
        }
        await vm.StopAsync();
    }

    /// <summary>创建和选择实例不联网，进入在线页才开始防抖查询。</summary>
    [Fact]
    public async Task SearchStartsOnlyWhenOnlinePageOpens()
    {
        var catalog = new Catalog(); var vm = Create(catalog);
        Assert.Equal(0, catalog.Searches);
        vm.SetActive(true); await vm.SearchTask;
        Assert.Equal(1, catalog.Searches);
        Assert.Equal("1.7.10", catalog.LastQuery!.GameVersion); Assert.Equal("forge", catalog.LastQuery.Loader);
        await vm.StopAsync();
    }

    /// <summary>输入防抖只发送最后一次条件，避免快速输入产生重复网络查询。</summary>
    [Fact]
    public async Task DebounceSendsOnlyFinalQuery()
    {
        var catalog = new Catalog(); var vm = Create(catalog);
        vm.SetActive(true); vm.SearchText = "g"; vm.SearchText = "gt"; vm.SearchText = "gtnh";
        await vm.SearchTask;
        Assert.Equal(1, catalog.Searches); Assert.Equal("gtnh", catalog.LastQuery!.Text);
        await vm.StopAsync();
    }

    /// <summary>不响应取消的旧搜索也不能在新搜索后回写结果。</summary>
    [Fact]
    public async Task LateSearchCannotReplaceCurrentResults()
    {
        var first = new TaskCompletionSource<OnlineModSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new Catalog { Search = query => query.Text == "old" ? first.Task : Task.FromResult(Page("new")) };
        var vm = Create(catalog); vm.SearchText = "old";
        var previous = vm.SearchCommand.ExecuteAsync(null);
        vm.SearchText = "new"; await vm.SearchCommand.ExecuteAsync(null);
        first.SetResult(Page("old")); await previous;
        Assert.Equal("new", Assert.Single(vm.Projects).Project.Name); Assert.False(vm.IsSearching);
        await vm.StopAsync();
    }

    /// <summary>旧结果的迟到图标不能出现在新查询对应的卡片上。</summary>
    [Fact]
    public async Task LateIconCannotUpdateNewSearchRow()
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var icons = new Icons { Get = url => url!.Contains("old") ? pending.Task : Task.FromResult<string?>("new.png") };
        var catalog = new Catalog { Search = query => Task.FromResult(new OnlineModSearchPage(
            [Project(query.Text) with { IconUrl = "https://cdn.modrinth.com/" + query.Text + ".png" }], false, [])) };
        var vm = new OnlineModsViewModel(catalog, new Installer(), icons); vm.SetInstance(Instance());
        vm.SearchText = "old"; await vm.SearchCommand.ExecuteAsync(null); var old = Assert.Single(vm.Projects);
        vm.SearchText = "new"; await vm.SearchCommand.ExecuteAsync(null);
        pending.SetResult("old.png"); await vm.StopAsync();
        Assert.Null(old.IconPath); Assert.Equal("new.png", Assert.Single(vm.Projects).IconPath);
    }

    /// <summary>同名跨平台项目保留各自身份，同一项目的重复页结果只显示一次。</summary>
    [Fact]
    public async Task PagingDeduplicatesByProviderAndProjectOnly()
    {
        var mr = Project("same"); var cf = mr with { Id = new(ModProvider.CurseForge, "project") };
        var catalog = new Catalog { Search = query => Task.FromResult(new OnlineModSearchPage([mr, cf], query.Page == 0, [])) };
        var vm = Create(catalog); await vm.SearchCommand.ExecuteAsync(null);
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Projects.Count); Assert.False(vm.HasMore); Assert.Equal(1, catalog.LastQuery!.Page);
        await vm.StopAsync();
    }

    /// <summary>切换实例后，旧项目的详情和版本列表不得被重新显示。</summary>
    [Fact]
    public async Task InstanceSwitchDiscardsPendingDetails()
    {
        var first = new TaskCompletionSource<OnlineModProject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new Catalog { Detail = _ => first.Task };
        var vm = Create(catalog); vm.SelectedProject = new(Project("old"));
        var pending = vm.DetailTask; vm.SetInstance(Instance("second"));
        first.SetResult(Project("old")); await pending;
        Assert.Null(vm.ProjectDetails); Assert.Empty(vm.Versions); Assert.Null(vm.Plan);
        await vm.StopAsync();
    }

    /// <summary>离开页面可取消详情请求，但返回仍保留的项目时应恢复读取，不能停留在空详情。</summary>
    [Fact]
    public async Task ReopeningOnlinePageRestartsInterruptedProjectDetails()
    {
        var pending = new TaskCompletionSource<OnlineModProject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var catalog = new Catalog { Detail = _ => ++reads == 1 ? pending.Task : Task.FromResult(Project("test")) };
        var vm = Create(catalog);
        try
        {
            await vm.SearchCommand.ExecuteAsync(null);
            vm.SetActive(true);
            vm.SelectedProject = Assert.Single(vm.Projects);
            var interrupted = vm.DetailTask;
            Assert.True(vm.IsLoadingDetails);

            vm.SetActive(false);
            pending.SetResult(Project("test"));
            await interrupted;
            Assert.Null(vm.ProjectDetails);

            vm.SetActive(true);
            await vm.SearchTask;
            await vm.DetailTask;
            await vm.VersionTask;
            Assert.NotNull(vm.ProjectDetails);
            Assert.NotEmpty(vm.Versions);
            Assert.True(vm.PreviewCommand.CanExecute(null));
        }
        finally { pending.TrySetResult(Project("test")); await vm.StopAsync(); }
    }

    /// <summary>切页打断固定版本详情后，返回页面应恢复文件选择和生成清单能力。</summary>
    [Fact]
    public async Task ReopeningOnlinePageRestartsInterruptedVersionDetails()
    {
        var pending = new TaskCompletionSource<OnlineModVersion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var catalog = new Catalog { VersionDetail = _ => ++reads == 1 ? pending.Task : Task.FromResult(Version()) };
        var vm = Create(catalog);
        try
        {
            await vm.SearchCommand.ExecuteAsync(null);
            vm.SetActive(true);
            vm.SelectedProject = Assert.Single(vm.Projects);
            await vm.DetailTask;
            var interrupted = vm.VersionTask;
            Assert.True(vm.IsLoadingVersion);

            vm.SetActive(false);
            pending.SetResult(Version());
            await interrupted;
            Assert.Null(vm.VersionDetails);

            vm.SetActive(true);
            await vm.SearchTask;
            await vm.DetailTask;
            await vm.VersionTask;
            Assert.NotNull(vm.VersionDetails);
            Assert.NotNull(vm.SelectedFile);
            Assert.True(vm.PreviewCommand.CanExecute(null));
        }
        finally { pending.TrySetResult(Version()); await vm.StopAsync(); }
    }

    /// <summary>立即返回时重新读取；旧服务忽略取消并迟到，也不能覆盖恢复后的详情。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReopeningOnlinePageIgnoresInterruptedLateResponse(bool versionRequest)
    {
        var oldProject = new TaskCompletionSource<OnlineModProject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldVersion = new TaskCompletionSource<OnlineModVersion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectReads = 0; var versionReads = 0;
        var catalog = new Catalog
        {
            Detail = _ => !versionRequest && ++projectReads == 1 ? oldProject.Task : Task.FromResult(Project("current")),
            VersionDetail = _ => versionRequest && ++versionReads == 1 ? oldVersion.Task : Task.FromResult(Version() with { VersionNumber = "current" })
        };
        var vm = Create(catalog);
        try
        {
            await vm.SearchCommand.ExecuteAsync(null);
            vm.SetActive(true);
            vm.SelectedProject = Assert.Single(vm.Projects);
            if (versionRequest) await vm.DetailTask;
            var interrupted = versionRequest ? vm.VersionTask : vm.DetailTask;
            Assert.False(interrupted.IsCompleted);

            vm.SetActive(false);
            vm.SetActive(true);
            vm.SetActive(true);
            await vm.DetailTask;
            await vm.VersionTask;
            Assert.Equal(2, versionRequest ? versionReads : projectReads);
            Assert.True(vm.PreviewCommand.CanExecute(null));

            oldProject.TrySetResult(Project("stale"));
            oldVersion.TrySetResult(Version() with { VersionNumber = "stale" });
            await interrupted;
            Assert.Equal("current", vm.ProjectDetails!.Name);
            Assert.Equal("current", vm.VersionDetails!.VersionNumber);
            Assert.False(vm.IsLoadingDetails);
            Assert.False(vm.IsLoadingVersion);
        }
        finally
        {
            oldProject.TrySetResult(Project("stale")); oldVersion.TrySetResult(Version());
            await vm.StopAsync();
        }
    }

    /// <summary>完整加载的页面返回时不重查，也不丢失文件、依赖勾选与已确认前的安装清单。</summary>
    [Fact]
    public async Task ReopeningLoadedPagePreservesFileDependencyChoicesAndPlan()
    {
        var details = 0; var versions = 0;
        var version = Version();
        version = version with { Files = [version.Files[0], version.Files[0] with { FileName = "alternative.jar", Primary = false }] };
        var catalog = new Catalog
        {
            Detail = _ => { ++details; return Task.FromResult(Project("test")); },
            VersionDetail = _ => { ++versions; return Task.FromResult(version); }
        };
        var vm = Create(catalog);
        try
        {
            await vm.SearchCommand.ExecuteAsync(null);
            vm.SetActive(true);
            vm.SelectedProject = Assert.Single(vm.Projects);
            await vm.DetailTask; await vm.VersionTask;
            vm.SelectedFile = version.Files[1];
            vm.IncludeRequired = false;
            Assert.Single(vm.OptionalDependencies).IsSelected = true;
            await vm.PreviewCommand.ExecuteAsync(null);
            var plan = vm.Plan;
            var selectedVersion = vm.SelectedVersion;

            vm.SetActive(false);
            vm.SetActive(true);
            await vm.SearchTask; await vm.DetailTask; await vm.VersionTask;

            Assert.Equal(1, catalog.Searches); Assert.Equal(1, details); Assert.Equal(1, versions);
            Assert.Same(selectedVersion, vm.SelectedVersion);
            Assert.Same(version.Files[1], vm.SelectedFile);
            Assert.False(vm.IncludeRequired);
            Assert.True(Assert.Single(vm.OptionalDependencies).IsSelected);
            Assert.NotNull(plan); Assert.Same(plan, vm.Plan);
            Assert.True(vm.InstallCommand.CanExecute(null));
        }
        finally { await vm.StopAsync(); }
    }

    /// <summary>必要依赖默认加入，可选依赖默认关闭；用户改选后清单必须重建。</summary>
    [Fact]
    public async Task DependencyChoicesAreExplicitAndInvalidateOldPlan()
    {
        var installer = new Installer(); var vm = Create(new Catalog(), installer); await SelectAsync(vm);
        Assert.True(vm.IncludeRequired); Assert.False(Assert.Single(vm.OptionalDependencies).IsSelected);
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.True(installer.LastRequest!.IncludeRequired); Assert.Empty(installer.LastRequest.OptionalDependencyKeys);
        Assert.True(vm.InstallCommand.CanExecute(null));
        vm.IncludeRequired = false; Assert.Null(vm.Plan); Assert.False(vm.InstallCommand.CanExecute(null));
        vm.OptionalDependencies[0].IsSelected = true;
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.False(installer.LastRequest!.IncludeRequired); Assert.Equal(Optional.Key, Assert.Single(installer.LastRequest.OptionalDependencyKeys));
        await vm.StopAsync();
    }

    /// <summary>依赖解析途中取消勾选使旧计划失效，即使服务忽略取消也不能显示可安装状态。</summary>
    [Fact]
    public async Task LatePlanDoesNotIgnoreChangedDependencyChoice()
    {
        var pending = new TaskCompletionSource<OnlineModInstallPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new Installer { Plan = _ => pending.Task };
        var vm = Create(new Catalog(), installer); await SelectAsync(vm);
        var preview = vm.PreviewCommand.ExecuteAsync(null); var request = installer.LastRequest!;
        vm.IncludeRequired = false;
        pending.SetResult(Plan(request)); await preview;
        Assert.Null(vm.Plan); Assert.False(vm.IsPlanning); Assert.False(vm.InstallCommand.CanExecute(null));
        await vm.StopAsync();
    }

    /// <summary>递归发现的可选依赖出现在预览中但不会偷偷被勾选。</summary>
    [Fact]
    public async Task DiscoveredOptionalDependencyIsUnchecked()
    {
        var extra = Optional with { Key = "nested", Name = "Nested" };
        var installer = new Installer { Plan = request => Task.FromResult(Plan(request) with { OptionalDependencies = [Optional, extra] }) };
        var vm = Create(new Catalog(), installer); await SelectAsync(vm); await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.OptionalDependencies.Count); Assert.All(vm.OptionalDependencies, item => Assert.False(item.IsSelected));
        vm.OptionalDependencies[1].IsSelected = true; Assert.Null(vm.Plan);
        await vm.StopAsync();
    }

    /// <summary>游戏运行等宿主忙碌状态会阻止真正的文件安装，包括程序直接执行命令的情况。</summary>
    [Fact]
    public async Task HostBusyBlocksInstall()
    {
        var busy = false; var installer = new Installer(); var vm = Create(new Catalog(), installer, () => !busy);
        await SelectAsync(vm); await vm.PreviewCommand.ExecuteAsync(null);
        busy = true; vm.NotifyHostStateChanged(); await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(0, installer.Installs); Assert.False(vm.InstallCommand.CanExecute(null));
        await vm.StopAsync();
    }

    /// <summary>必要文件失败时展示未完成并清空旧计划，不报告整个实例启动就绪。</summary>
    [Fact]
    public async Task PartialInstallReportsFailureAndReleasesHost()
    {
        var busy = false; var changes = 0;
        var installer = new Installer { Result = new(1, 0, false, ["required.jar 下载失败"]) };
        var vm = new OnlineModsViewModel(new Catalog(), installer, canModify: () => !busy,
            setBusy: value => busy = value, filesChanged: () => ++changes);
        vm.SetInstance(Instance()); await SelectAsync(vm); await vm.PreviewCommand.ExecuteAsync(null);
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Contains("安装未完成", vm.InstallStatus); Assert.Contains("required.jar", vm.InstallStatus);
        Assert.False(busy); Assert.False(vm.IsInstalling); Assert.Null(vm.Plan); Assert.True(changes > 0);
        await vm.StopAsync();
    }

    /// <summary>用户修改筛选条件会清除详情与计划，不能下载旧筛选下的文件。</summary>
    [Fact]
    public async Task ChangingFilterClearsPlanAndSelection()
    {
        var vm = Create(new Catalog()); await SelectAsync(vm); await vm.PreviewCommand.ExecuteAsync(null);
        vm.GameVersion = "1.20.1";
        Assert.Null(vm.Plan); Assert.Null(vm.SelectedProject); Assert.Null(vm.VersionDetails);
        await vm.StopAsync();
    }

    /// <summary>前一次安装排队中的进度不能覆盖同一实例的新安装。</summary>
    [Fact]
    public async Task LateProgressCannotUpdateNextInstall()
    {
        var queue = new QueuedContext(); IProgress<OnlineModInstallProgress>? previous = null;
        var installer = new Installer
        {
            Install = (_, progress, _) =>
            {
                if (previous is null) previous = progress;
                else { previous.Report(new("旧下载", "old.jar", 0, 1, 1, 2)); queue.Drain(); }
                return Task.FromResult(new OnlineModInstallResult(1, 0, false, []));
            }
        };
        var vm = Create(new Catalog(), installer); await SelectAsync(vm); await vm.PreviewCommand.ExecuteAsync(null);
        var original = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(queue);
            // 受控服务同步完成，只有 Progress.Post 被保留在自定义 UI 队列。
            Assert.True(vm.InstallCommand.ExecuteAsync(null).IsCompletedSuccessfully);
            Assert.True(vm.PreviewCommand.ExecuteAsync(null).IsCompletedSuccessfully);
            Assert.True(vm.InstallCommand.ExecuteAsync(null).IsCompletedSuccessfully);
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        Assert.DoesNotContain("old.jar", vm.ProgressText);
        await vm.StopAsync();
    }

    /// <summary>下载期间外部切换实例会请求取消，旧失败结果不能写到新实例状态。</summary>
    [Fact]
    public async Task InstanceSwitchDuringInstallCancelsAndDiscardsOldFailure()
    {
        var pending = new TaskCompletionSource<OnlineModInstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var installer = new Installer { Install = (_, _, token) => { observed = token; return pending.Task; } };
        var vm = Create(new Catalog(), installer); await SelectAsync(vm); await vm.PreviewCommand.ExecuteAsync(null);
        var install = vm.InstallCommand.ExecuteAsync(null);
        vm.SetInstance(Instance("new")); Assert.True(observed.IsCancellationRequested);
        pending.SetException(new IOException("old instance failure")); await install;
        Assert.DoesNotContain("old instance failure", vm.InstallStatus); Assert.False(vm.IsInstalling);
        await vm.StopAsync();
    }

    /// <summary>中文展示按平台固定编号匹配，名称相同的其他项目不能套用译名。</summary>
    [Fact]
    public async Task ChineseDisplayKeepsOriginalProjectAndDoesNotMatchNames()
    {
        var original = Project("JourneyMap") with { Summary = "Original platform summary" };
        var other = original with { Id = new(ModProvider.Modrinth, "other") };
        var crossPlatform = original with { Id = new(ModProvider.CurseForge, ProjectId.ProjectId) };
        var localization = new Localization();
        var catalog = new Catalog { Search = _ => Task.FromResult(new OnlineModSearchPage([original, other, crossPlatform], false, [])) };
        var vm = Create(catalog, localization: localization);

        await vm.SearchCommand.ExecuteAsync(null);

        var row = vm.Projects[0];
        Assert.Same(original, row.Project);
        Assert.Equal("旅行地图（JourneyMap）", row.DisplayName);
        Assert.Equal("查看地图和保存路径点。", row.DisplaySummary);
        Assert.Contains("EML 整理", row.SummaryNote);
        Assert.Equal("JourneyMap", row.Project.Name);
        Assert.Equal("Original platform summary", row.Project.Summary);
        foreach (var unknown in vm.Projects.Skip(1))
        {
            Assert.Equal("JourneyMap", unknown.DisplayName);
            Assert.Equal("Original platform summary", unknown.DisplaySummary);
            Assert.Empty(unknown.SummaryNote);
        }
        await vm.StopAsync();
    }

    /// <summary>中文搜索只替换关键词，分页、平台与兼容筛选仍使用原本的快照。</summary>
    [Fact]
    public async Task ChineseSearchKeepsFiltersAndPagingAndExplainsAlias()
    {
        var catalog = new Catalog { Search = query => Task.FromResult(new OnlineModSearchPage([Project("JourneyMap")], query.Page == 0, [])) };
        var vm = Create(catalog, localization: new Localization());
        vm.SearchText = "  旅行地图  "; vm.ProviderIndex = 2; vm.GameVersion = "1.20.1";
        vm.Loader = "fabric"; vm.CategoryIndex = 7; vm.EnvironmentIndex = 1; vm.SortIndex = 2;

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("JourneyMap", catalog.LastQuery!.Text);
        Assert.Contains("JourneyMap", vm.Status); Assert.Contains("旅行地图", vm.Status);
        Assert.Equal("  旅行地图  ", vm.SearchText);
        Assert.Equal(0, catalog.LastQuery.Page);
        await vm.LoadMoreCommand.ExecuteAsync(null);
        var query = catalog.LastQuery!;
        Assert.Equal("JourneyMap", query.Text); Assert.Equal(1, query.Page);
        Assert.Equal(ModProvider.CurseForge, query.Provider); Assert.Equal("1.20.1", query.GameVersion);
        Assert.Equal("fabric", query.Loader); Assert.Equal("utility", query.Category);
        Assert.Equal("client", query.Environment); Assert.Equal(OnlineModSort.Updated, query.Sort);
        await vm.StopAsync();
    }

    /// <summary>英文和未收录的中文仍按原词搜索，不采用猜测翻译。</summary>
    [Theory]
    [InlineData("JourneyMap")]
    [InlineData("unknown mod")]
    [InlineData("未知的中文模组")]
    public async Task UnmappedSearchStaysOriginal(string input)
    {
        var catalog = new Catalog(); var vm = Create(catalog, localization: new Localization());
        vm.SearchText = input; await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(input, catalog.LastQuery!.Text);
        Assert.DoesNotContain("已按", vm.Status);
        await vm.StopAsync();
    }

    /// <summary>详情和依赖均显示中文，但平台原始介绍、精确文件与安装请求保持不变。</summary>
    [Fact]
    public async Task LocalizedDetailsAndDependenciesPreserveInstallationIdentity()
    {
        var information = Project("JourneyMap") with { Summary = "Official summary", Description = "Official full description", Loaders = ["forge"] };
        var required = Optional with { Key = "required", Project = ProjectId, Name = "JourneyMap", Kind = OnlineDependencyKind.Required };
        var optional = Optional with { Description = "Original dependency summary" };
        var version = Version() with { Changelog = "Official changelog", Dependencies = [required, optional] };
        var installer = new Installer();
        var catalog = new Catalog { Detail = _ => Task.FromResult(information), VersionDetail = _ => Task.FromResult(version) };
        var vm = Create(catalog, installer, localization: new Localization());
        var notified = new List<string?>();
        vm.PropertyChanged += (_, change) => notified.Add(change.PropertyName);

        await SelectAsync(vm);

        Assert.Equal("旅行地图（JourneyMap）", vm.ProjectDisplayName);
        Assert.Equal("查看地图和保存路径点。", vm.ProjectDisplaySummary);
        Assert.Contains("EML 整理", vm.ProjectSummaryNote);
        Assert.Contains(nameof(vm.ProjectDisplayName), notified);
        Assert.Contains(nameof(vm.ProjectDisplaySummary), notified);
        Assert.Contains(nameof(vm.ProjectSummaryNote), notified);
        Assert.Equal("Official summary", vm.ProjectDetails!.Summary);
        Assert.Equal("Official full description", vm.ProjectDetails.Description);
        Assert.Equal("Official changelog", vm.VersionDetails!.Changelog);
        Assert.Contains("加载器：Forge", vm.ProjectInformation);
        Assert.Contains("旅行地图（JourneyMap）", vm.DependencyInformation);
        var choice = Assert.Single(vm.OptionalDependencies);
        Assert.Same(optional, choice.Dependency);
        Assert.Equal("附加功能（Extra）", choice.DisplayName);
        Assert.Equal("提供额外功能。", choice.DisplaySummary);
        Assert.Contains("EML 整理", choice.SummaryNote);
        Assert.False(choice.IsSelected);
        choice.IsSelected = true;
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(ProjectId, installer.LastRequest!.Project);
        Assert.Equal(version.Id, installer.LastRequest.VersionId);
        Assert.Equal("test.jar", installer.LastRequest.FileName);
        Assert.Equal(optional.Key, Assert.Single(installer.LastRequest.OptionalDependencyKeys));
        Assert.True(Assert.Single(vm.OptionalDependencies).IsSelected);
        Assert.Equal("附加功能（Extra）", vm.OptionalDependencies[0].DisplayName);
        vm.SetInstance(null);
        Assert.Empty(vm.ProjectDisplayName); Assert.Empty(vm.ProjectDisplaySummary); Assert.Empty(vm.ProjectSummaryNote);
        await vm.StopAsync();
    }

    /// <summary>缺少中文简介时仍保留平台内容，不产生空白或误导性的翻译标记。</summary>
    [Fact]
    public void MissingChineseSummaryFallsBackToOriginal()
    {
        var localization = new Localization { EmptySummary = true };
        var row = new OnlineModResultRow(Project("JourneyMap") with { Summary = "Official summary" }, localization);
        Assert.Equal("旅行地图（JourneyMap）", row.DisplayName);
        Assert.Equal("Official summary", row.DisplaySummary);
        Assert.Empty(row.SummaryNote);
        var unknown = new OptionalModChoice(Optional with { Project = null, Name = "Unknown", Description = "Unknown summary" }, () => { }, localization: localization);
        Assert.Equal("Unknown", unknown.DisplayName); Assert.Equal("Unknown summary", unknown.DisplaySummary);
    }

    /// <summary>版本通道和加载器只改变显示文字，不产生新的筛选值。</summary>
    [Fact]
    public void PlatformLabelsHaveReadableChinesePresentation()
    {
        Assert.Equal("正式版", OnlineModDisplayText.Channel("release"));
        Assert.Equal("测试版", OnlineModDisplayText.Channel("beta"));
        Assert.Equal("早期测试版", OnlineModDisplayText.Channel("alpha"));
        Assert.Equal("未标注", OnlineModDisplayText.Channel("unknown"));
        Assert.Equal("全部加载器", OnlineModDisplayText.Loader(""));
        Assert.Equal("NeoForge", OnlineModDisplayText.Loader("neoforge"));
    }

    /// <summary>构造无磁盘访问的实例与服务，只测试真实 ViewModel 的行为。</summary>
    private static OnlineModsViewModel Create(Catalog catalog, Installer? installer = null, Func<bool>? canModify = null,
        IOnlineModLocalization? localization = null, IOperationNotifications? notifications = null)
    {
        var vm = new OnlineModsViewModel(catalog, installer ?? new Installer(), canModify: canModify,
            localization: localization, notifications: notifications);
        vm.SetInstance(Instance()); return vm;
    }

    /// <summary>仅保存通知数据，测试不会显示窗口。</summary>
    private sealed class OnlineNoticeRecorder : IOperationNotifications
    {
        public List<OperationNotice> Items { get; } = [];
        /// <summary>记录一次操作的真实结果供断言。</summary>
        public void Report(OperationNotice notice) => Items.Add(notice);
    }
    private static MinecraftInstance Instance(string name = "test") => new(name, "C:\\test\\" + name, "C:\\test\\" + name + "\\.minecraft", ["1.7.10"])
    { Versions = [new("forge", "1.7.10", "Forge 10.13.4", "unused")] };
    private static OnlineModProject Project(string name) => new() { Id = ProjectId, Name = name };
    private static OnlineModSearchPage Page(string name) => new([Project(name)], false, []);
    private static OnlineModVersion Version() => new()
    {
        Project = ProjectId, Id = "version", VersionNumber = "1.0", GameVersions = ["1.7.10"], Loaders = ["forge"],
        Files = [new() { FileName = "test.jar", Primary = true, Requirement = new() { Size = 5 } }], Dependencies = [Optional]
    };
    private static OnlineModInstallPlan Plan(OnlineModPlanRequest request) => new()
    { Id = "plan", Request = request, Items = [new(Version(), Version().Files[0], "主动选择", OnlineModPlanStatus.Download)] };
    private static async Task SelectAsync(OnlineModsViewModel vm)
    { vm.SelectedProject = new(Project("test")); await vm.DetailTask; await vm.VersionTask; }

    /// <summary>可控的目录服务用于乱序完成、分页和无网络测试。</summary>
    private sealed class Catalog : IOnlineModCatalog
    {
        public int Searches { get; private set; }
        public OnlineModQuery? LastQuery { get; private set; }
        public Func<OnlineModQuery, Task<OnlineModSearchPage>>? Search { get; init; }
        public Func<OnlineProjectId, Task<OnlineModProject>>? Detail { get; init; }
        public Func<string, Task<OnlineModVersion>>? VersionDetail { get; init; }
        public Task<OnlineModSearchPage> SearchAsync(OnlineModQuery query, CancellationToken token = default)
        { ++Searches; LastQuery = query; return Search?.Invoke(query) ?? Task.FromResult(Page("test")); }
        public Task<OnlineModProject> GetProjectAsync(OnlineProjectId project, CancellationToken token = default) => Detail?.Invoke(project) ?? Task.FromResult(Project("test"));
        public Task<OnlineModVersionPage> GetVersionsAsync(OnlineProjectId project, string gameVersion, string loader, int page = 0, CancellationToken token = default) => Task.FromResult(new OnlineModVersionPage([Version()], false));
        public Task<OnlineModVersion> GetVersionAsync(OnlineProjectId project, string versionId, CancellationToken token = default) => VersionDetail?.Invoke(versionId) ?? Task.FromResult(Version());
        public Task<OnlineModFile> ResolveDownloadAsync(OnlineModVersion version, OnlineModFile file, CancellationToken token = default) => Task.FromResult(file);
    }

    /// <summary>翻译只按固定身份提供；故意为英文提供错误映射，以验证界面不改写英文搜索。</summary>
    private sealed class Localization : IOnlineModLocalization
    {
        public bool EmptySummary { get; init; }
        public OnlineModTranslation? Find(OnlineProjectId project) => project == ProjectId
            ? new("旅行地图", EmptySummary ? " " : "查看地图和保存路径点。")
            : project == Optional.Project ? new("附加功能", "提供额外功能。") : null;
        public string? ResolveSearchTerm(string text) => text switch
        { "旅行地图" => "JourneyMap", "JourneyMap" => "wrong translation", _ => null };
    }

    /// <summary>记录用户最终确认的依赖选择和安装次数。</summary>
    private sealed class Installer : IOnlineModInstaller
    {
        public OnlineModPlanRequest? LastRequest { get; private set; }
        public int Installs { get; private set; }
        public Func<OnlineModPlanRequest, Task<OnlineModInstallPlan>>? Plan { get; init; }
        public Func<OnlineModInstallPlan, IProgress<OnlineModInstallProgress>?, CancellationToken, Task<OnlineModInstallResult>>? Install { get; init; }
        public OnlineModInstallResult Result { get; init; } = new(1, 0, false, []);
        public Task<OnlineModInstallPlan> PlanAsync(OnlineModPlanRequest request, CancellationToken token = default)
        { LastRequest = request; return Plan?.Invoke(request) ?? Task.FromResult(OnlineModViewModelTests.Plan(request)); }
        public Task<OnlineModInstallResult> InstallAsync(OnlineModInstallPlan plan, IProgress<OnlineModInstallProgress>? progress = null, CancellationToken token = default)
        { ++Installs; return Install?.Invoke(plan, progress, token) ?? Task.FromResult(Result); }
    }

    /// <summary>允许图标晚于搜索结果完成，验证旧查询不会影响新卡片。</summary>
    private sealed class Icons : IModIconCache
    {
        public required Func<string?, Task<string?>> Get { get; init; }
        public Task<string?> GetAsync(string? address, CancellationToken token = default) => Get(address);
    }

    /// <summary>模拟 WPF 消息队列，准确控制已排队进度的交付时机。</summary>
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<Action> _queue = [];
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue(() => callback(state));
        public void Drain() { while (_queue.TryDequeue(out var action)) action(); }
    }
}
