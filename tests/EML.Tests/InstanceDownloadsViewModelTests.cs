using EML.App.ViewModels;
using EML.Core.Downloads;
using EML.Core.Minecraft.Catalog;

namespace EML.Tests;

/// <summary>使用受控目录与安装委托测试下载页面，不访问网络、文件系统或真实实例。</summary>
public sealed class InstanceDownloadsViewModelTests
{
    /// <summary>构造和隐藏时修改筛选均不联网，显式激活才请求目录。</summary>
    [Fact]
    public async Task HiddenPageDoesNotQueryCatalog()
    {
        await using var fixture = new Fixture();
        fixture.ViewModel.ProviderIndex = 1;
        fixture.ViewModel.SearchText = "pack";
        fixture.ViewModel.GameVersion = "1.20.1";
        fixture.ViewModel.Loader = "fabric";
        await fixture.ViewModel.SearchCommand.ExecuteAsync(null);
        fixture.ViewModel.SelectedProject = new(Project(InstanceCatalogProvider.CurseForge));
        Assert.Empty(fixture.Catalog.Searches);
        Assert.Empty(fixture.Catalog.Versions);
        Assert.Empty(fixture.Catalog.DetailsCalls);
        Assert.False(fixture.ViewModel.SearchCommand.CanExecute(null));

        fixture.ViewModel.SetActive(true);
        await fixture.ViewModel.SearchTask;
        Assert.Single(fixture.Catalog.Searches);
        Assert.True(fixture.ViewModel.SearchCommand.CanExecute(null));
    }

    /// <summary>三种来源传递自身筛选及固定身份，版本选择保持平台对应关系。</summary>
    [Theory]
    [InlineData(InstanceCatalogProvider.Modrinth)]
    [InlineData(InstanceCatalogProvider.CurseForge)]
    [InlineData(InstanceCatalogProvider.Minecraft)]
    public async Task ProvidersPassSearchAndVersionFilters(InstanceCatalogProvider provider)
    {
        await using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        vm.ProviderIndex = (int)provider;
        vm.SearchText = "  Adventure  "; vm.GameVersion = " 1.20.1 ";
        vm.Loader = "fabric"; vm.SortIndex = 2; vm.IncludeSnapshots = true;
        vm.SetActive(true);
        await vm.SearchTask;
        var query = Assert.Single(fixture.Catalog.Searches).Query;
        Assert.Equal(provider, query.Provider);
        Assert.Equal("Adventure", query.Query);
        Assert.Equal("1.20.1", query.GameVersion);
        Assert.Equal("fabric", query.Loader);
        Assert.Equal(2, query.Sort);
        Assert.True(query.IncludeSnapshots);
        Assert.Equal(0, query.Offset);
        Assert.Equal(provider == InstanceCatalogProvider.Minecraft, vm.IsMinecraft);
        vm.SelectedProject = Assert.Single(vm.Projects);
        await vm.VersionsTask;
        var versions = Assert.Single(fixture.Catalog.Versions);
        Assert.Equal(provider, versions.Project.Provider);
        Assert.Equal("1.20.1", versions.GameVersion);
        Assert.Equal("fabric", versions.Loader);
        Assert.Equal(provider, vm.SelectedRelease!.Provider);
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    /// <summary>切换来源立即取消旧查询；忽略取消的迟到响应不能填回旧平台项目。</summary>
    [Fact]
    public async Task ChangingProviderRejectsLateSearchResponse()
    {
        await using var fixture = new Fixture();
        var pending = Signal<InstanceCatalogPage>();
        fixture.Catalog.Search = (query, _) => query.Provider == InstanceCatalogProvider.Modrinth
            ? pending.Task : Task.FromResult(Page(query.Provider, "current"));
        var vm = fixture.ViewModel;
        vm.SetActive(true);
        var oldTask = vm.SearchTask;
        try
        {
            Assert.True(vm.IsSearching);
            vm.ProviderIndex = 1;
            await vm.SearchTask;
            Assert.True(fixture.Catalog.Searches[0].Token.IsCancellationRequested);
            Assert.Equal("current", Assert.Single(vm.Projects).Project.Id);
            pending.SetResult(Page(InstanceCatalogProvider.Modrinth, "stale"));
            await oldTask;
            Assert.Equal(InstanceCatalogProvider.CurseForge, Assert.Single(vm.Projects).Project.Provider);
            Assert.Equal("current", vm.Projects[0].Project.Id);
            Assert.False(vm.IsSearching);
        }
        finally { pending.TrySetResult(Page(InstanceCatalogProvider.Modrinth, "stale")); }
    }

    /// <summary>所有搜索筛选都会清除旧结果并使旧请求失效，避免下载与新筛选无关的条目。</summary>
    [Theory]
    [InlineData("query")]
    [InlineData("version")]
    [InlineData("loader")]
    [InlineData("sort")]
    [InlineData("snapshot")]
    public async Task ChangingFilterRejectsLateSearchResponse(string filter)
    {
        await using var fixture = new Fixture();
        var pending = Signal<InstanceCatalogPage>();
        var reads = 0;
        fixture.Catalog.Search = (_, _) => ++reads == 1 ? pending.Task :
            Task.FromResult(Page(InstanceCatalogProvider.Modrinth, "current"));
        var vm = fixture.ViewModel;
        vm.SetActive(true);
        var oldTask = vm.SearchTask;
        try
        {
            ChangeFilter(vm, filter);
            Assert.True(fixture.Catalog.Searches[0].Token.IsCancellationRequested);
            Assert.Empty(vm.Projects);
            Assert.Null(vm.SelectedRelease);
            Assert.False(vm.IsSearching);
            await vm.SearchCommand.ExecuteAsync(null);
            pending.SetResult(Page(InstanceCatalogProvider.Modrinth, "stale"));
            await oldTask;
            Assert.Equal("current", Assert.Single(vm.Projects).Project.Id);
        }
        finally { pending.TrySetResult(Page(InstanceCatalogProvider.Modrinth, "stale")); }
    }

    /// <summary>平台或版本筛选改变后旧版本响应被忽略，新的固定文件选择保持不变。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingSourceOrFilterRejectsLateVersions(bool changeProvider)
    {
        await using var fixture = new Fixture();
        var pending = Signal<InstanceVersionPage>();
        var reads = 0;
        fixture.Catalog.VersionList = (call, _) => ++reads == 1 ? pending.Task :
            Task.FromResult(new InstanceVersionPage([Release(call.Project, "current")], 1, false));
        var vm = fixture.ViewModel;
        await ActivateAndSelectAsync(vm);
        var oldTask = vm.VersionsTask;
        try
        {
            Assert.True(vm.IsLoadingVersions);
            if (changeProvider) vm.ProviderIndex = 1;
            else { vm.GameVersion = "1.21.1"; await vm.SearchCommand.ExecuteAsync(null); }
            await vm.SearchTask;
            vm.SelectedProject = Assert.Single(vm.Projects);
            await vm.VersionsTask;
            Assert.True(fixture.Catalog.Versions[0].Token.IsCancellationRequested);
            var selected = vm.SelectedRelease;
            Assert.Equal("current", selected!.Id);
            pending.SetResult(new([Release(Project(InstanceCatalogProvider.Modrinth), "stale")], 1, false));
            await oldTask;
            Assert.Same(selected, vm.SelectedRelease);
            Assert.Equal("current", Assert.Single(vm.Releases).Id);
            Assert.False(vm.IsLoadingVersions);
        }
        finally { pending.TrySetResult(new([], 0, false)); }
    }

    /// <summary>宿主忙碌时不能安装，安装中不能再次调用委托；切换页面不会取消已确认任务。</summary>
    [Fact]
    public async Task InstallationHonorsHostLockAndSurvivesLeavingPage()
    {
        await using var fixture = new Fixture();
        var completion = Signal<string>();
        fixture.Install = (_, _, token) => completion.Task.WaitAsync(token);
        await ActivateAndSelectAsync(fixture.ViewModel);
        await fixture.ViewModel.VersionsTask;
        fixture.CanModify = false;
        fixture.ViewModel.NotifyHostStateChanged();
        Assert.False(fixture.ViewModel.InstallCommand.CanExecute(null));
        await fixture.ViewModel.InstallCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.InstallCalls);
        fixture.CanModify = true;
        fixture.ViewModel.NotifyHostStateChanged();
        var installation = fixture.ViewModel.InstallCommand.ExecuteAsync(null);
        try
        {
            Assert.True(fixture.ViewModel.IsInstalling);
            Assert.False(fixture.ViewModel.CanBrowse);
            Assert.False(fixture.ViewModel.InstallCommand.CanExecute(null));
            Assert.False(fixture.ViewModel.SearchCommand.CanExecute(null));
            fixture.ViewModel.SetActive(false);
            Assert.False(fixture.InstallToken.IsCancellationRequested);
            Assert.False(installation.IsCompleted);
            completion.SetResult("实例已导入并保留。");
            await installation;
            Assert.Equal(1, fixture.InstallCalls);
            Assert.Equal("实例已导入并保留。", fixture.ViewModel.InstallStatus);
            Assert.False(fixture.ViewModel.IsInstalling);
        }
        finally { completion.TrySetResult("测试结束"); await installation; }
    }

    /// <summary>关闭会取消目录请求，并等待忽略取消的请求真正结束后才返回。</summary>
    [Fact]
    public async Task StopCancelsAndAwaitsLateCatalogTask()
    {
        await using var fixture = new Fixture();
        var pending = Signal<InstanceCatalogPage>();
        fixture.Catalog.Search = (_, _) => pending.Task;
        fixture.ViewModel.SetActive(true);
        var stopping = fixture.ViewModel.StopAsync();
        try
        {
            Assert.True(Assert.Single(fixture.Catalog.Searches).Token.IsCancellationRequested);
            Assert.False(stopping.IsCompleted);
            Assert.False(fixture.ViewModel.CanBrowse);
            pending.SetResult(Page(InstanceCatalogProvider.Modrinth));
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(fixture.ViewModel.Projects);
            fixture.ViewModel.SetActive(true);
            Assert.Single(fixture.Catalog.Searches);
        }
        finally { pending.TrySetResult(new([], 0, false)); await stopping; }
    }

    /// <summary>关闭时取消已确认安装，并等待安装委托完成清理，不能先释放窗口资源。</summary>
    [Fact]
    public async Task StopCancelsAndAwaitsInstallation()
    {
        await using var fixture = new Fixture();
        var completion = Signal<string>();
        fixture.Install = (_, _, _) => completion.Task;
        await ActivateAndSelectAsync(fixture.ViewModel);
        await fixture.ViewModel.VersionsTask;
        var installation = fixture.ViewModel.InstallCommand.ExecuteAsync(null);
        var stopping = fixture.ViewModel.StopAsync();
        try
        {
            Assert.True(fixture.InstallToken.IsCancellationRequested);
            Assert.False(stopping.IsCompleted);
            completion.SetCanceled(fixture.InstallToken);
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            await installation;
            Assert.Contains("取消", fixture.ViewModel.InstallStatus);
            Assert.False(fixture.ViewModel.IsInstalling);
            Assert.False(fixture.ViewModel.InstallCommand.CanExecute(null));
        }
        finally { completion.TrySetResult("测试结束"); await stopping; }
    }

    /// <summary>查询、版本与安装错误分别留在相应状态文字中，不生成成功结果。</summary>
    [Fact]
    public async Task ErrorsAreVisibleAndDoNotCreateSuccessfulState()
    {
        await using var fixture = new Fixture();
        fixture.Catalog.Search = (_, _) => Task.FromException<InstanceCatalogPage>(new IOException("目录不可达"));
        fixture.ViewModel.SetActive(true);
        await fixture.ViewModel.SearchTask;
        Assert.Contains("目录不可达", fixture.ViewModel.Status);
        Assert.Empty(fixture.ViewModel.Projects);
        fixture.Catalog.Search = (query, _) => Task.FromResult(Page(query.Provider));
        fixture.Catalog.VersionList = (_, _) => Task.FromException<InstanceVersionPage>(new IOException("版本服务不可达"));
        await fixture.ViewModel.SearchCommand.ExecuteAsync(null);
        fixture.ViewModel.SelectedProject = Assert.Single(fixture.ViewModel.Projects);
        await fixture.ViewModel.VersionsTask;
        Assert.Contains("版本服务不可达", fixture.ViewModel.VersionStatus);
        Assert.Null(fixture.ViewModel.SelectedRelease);
        fixture.ViewModel.SelectedRelease = Release(fixture.ViewModel.SelectedProject.Project);
        fixture.Install = (_, _, _) => Task.FromException<string>(new IOException("导入失败"));
        await fixture.ViewModel.InstallCommand.ExecuteAsync(null);
        Assert.Contains("安装未完成", fixture.ViewModel.InstallStatus);
        Assert.Contains("导入失败", fixture.ViewModel.InstallStatus);
        Assert.False(fixture.ViewModel.IsInstalling);
    }

    /// <summary>禁止分发或没有完整性要求的文件不可安装，项目网页仍可作为人工处理入口。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestrictedOrUnverifiedReleaseCannotInstall(bool restricted)
    {
        await using var fixture = new Fixture();
        await ActivateAndSelectAsync(fixture.ViewModel);
        await fixture.ViewModel.VersionsTask;
        var release = fixture.ViewModel.SelectedRelease!;
        fixture.ViewModel.SelectedRelease = restricted
            ? release with { Problem = "作者不允许第三方直接下载。", DownloadUrls = [] }
            : release with { Hashes = new Dictionary<string, string>() };
        Assert.False(fixture.ViewModel.SelectedRelease.CanDownload);
        Assert.False(fixture.ViewModel.InstallCommand.CanExecute(null));
        await fixture.ViewModel.InstallCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.InstallCalls);
        Assert.True(fixture.ViewModel.OpenWebsiteCommand.CanExecute(null));
        fixture.ViewModel.OpenWebsiteCommand.Execute(null);
        Assert.Equal("modrinth.com", Assert.Single(fixture.Websites).Host);
        if (restricted) Assert.Contains("不允许第三方", fixture.ViewModel.ReleaseInformation);
    }

    /// <summary>使用平台给出的下一页偏移并去重，不因重复卡片漏掉后续分页。</summary>
    [Fact]
    public async Task PaginationUsesProviderOffsetAndDeduplicatesProjects()
    {
        await using var fixture = new Fixture();
        fixture.Catalog.Search = (query, _) => Task.FromResult(query.Offset == 0
            ? new InstanceCatalogPage([Project(query.Provider, "one")], 25, true)
            : new InstanceCatalogPage([Project(query.Provider, "one"), Project(query.Provider, "two")], 50, false));
        fixture.ViewModel.SetActive(true);
        await fixture.ViewModel.SearchTask;
        await fixture.ViewModel.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(25, fixture.Catalog.Searches[1].Query.Offset);
        Assert.Equal(new[] { "one", "two" }, fixture.ViewModel.Projects.Select(row => row.Project.Id));
        Assert.False(fixture.ViewModel.LoadMoreCommand.CanExecute(null));
    }

    /// <summary>中文搜索保持用户输入，平台中文名称和简介不影响安装所用的固定版本身份。</summary>
    [Fact]
    public async Task ChineseSearchAndDisplayPreserveInstalledReleaseIdentity()
    {
        await using var fixture = new Fixture();
        var project = new InstanceCatalogProject
        { Provider = InstanceCatalogProvider.Modrinth, Id = "1KVo5zza", Name = "平台中文名称", Summary = "作者提供的中文简介" };
        fixture.Catalog.Search = (_, _) => Task.FromResult(new InstanceCatalogPage([project], 1, false));
        fixture.ViewModel.SearchText = "极致优化";
        await ActivateAndSelectAsync(fixture.ViewModel);
        await fixture.ViewModel.VersionsTask;
        await fixture.ViewModel.DetailsTask;
        Assert.Equal("极致优化", Assert.Single(fixture.Catalog.Searches).Query.Query);
        Assert.Equal("极致优化", fixture.ViewModel.SearchText);
        Assert.Contains("平台中文名称", fixture.ViewModel.ProjectInformation);
        Assert.Contains("作者提供的中文简介", fixture.ViewModel.ProjectInformation);
        Assert.DoesNotContain("EML 整理", fixture.ViewModel.ProjectInformation);
        Assert.Same(project, fixture.ViewModel.SelectedProject!.Project);
        var expectedRelease = fixture.ViewModel.SelectedRelease!;
        InstanceCatalogRelease? installed = null;
        fixture.Install = (release, _, _) => { installed = release; return Task.FromResult("完成"); };
        await fixture.ViewModel.InstallCommand.ExecuteAsync(null);
        Assert.Same(expectedRelease, installed);
        Assert.Equal("1KVo5zza", installed!.ProjectId);
    }

    /// <summary>只为选中的整合包读取正文，搜索和翻页不会逐个请求完整介绍。</summary>
    [Fact]
    public async Task SearchDoesNotPrefetchAllProjectDescriptions()
    {
        await using var fixture = new Fixture();
        fixture.Catalog.Search = (query, _) => Task.FromResult(query.Offset == 0
            ? new InstanceCatalogPage([Project(query.Provider, "one"), Project(query.Provider, "two")], 2, true)
            : new InstanceCatalogPage([Project(query.Provider, "three")], 3, false));
        var vm = fixture.ViewModel;
        vm.SetActive(true);
        await vm.SearchTask;
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Catalog.DetailsCalls);
        vm.SelectedProject = vm.Projects[1];
        await vm.DetailsTask;
        Assert.Equal("two", Assert.Single(fixture.Catalog.DetailsCalls).Project.Id);
    }

    /// <summary>作者正文中的中文与英文均保留，不把人工简介或自动翻译混入详情。</summary>
    [Theory]
    [InlineData(InstanceCatalogProvider.Modrinth)]
    [InlineData(InstanceCatalogProvider.CurseForge)]
    public async Task ProjectDetailsPreservePlatformChineseAndOtherLanguages(InstanceCatalogProvider provider)
    {
        await using var fixture = new Fixture();
        const string body = "Author introduction.\n\n中文介绍：探索世界并建造工厂。\n\n支持与更新：请查看作者主页。";
        fixture.Catalog.Details = (project, _) => Task.FromResult(project with { Description = body });
        var vm = fixture.ViewModel;
        vm.ProviderIndex = (int)provider;
        await ActivateAndSelectAsync(vm);
        await vm.DetailsTask;
        Assert.Equal(body, vm.ProjectDescription);
        Assert.True(vm.HasProjectDescription);
        Assert.False(vm.IsLoadingDetails);
        Assert.Equal(provider, Assert.Single(fixture.Catalog.DetailsCalls).Project.Provider);
        Assert.DoesNotContain("EML 整理", vm.ProjectInformation);
    }

    /// <summary>详情接口提供更新的中文文案时刷新卡片与详情，但原搜索对象和下载身份保持不变。</summary>
    [Fact]
    public async Task PlatformChineseDetailsRefreshDisplayWithoutChangingDownloadIdentity()
    {
        await using var fixture = new Fixture();
        var project = Project(InstanceCatalogProvider.Modrinth, "pack1234") with
        { Name = "Search English name", Summary = "Search English summary" };
        fixture.Catalog.Search = (_, _) => Task.FromResult(new InstanceCatalogPage([project], 1, false));
        var pending = Signal<InstanceCatalogProject>();
        fixture.Catalog.Details = (_, _) => pending.Task;
        var vm = fixture.ViewModel;
        await ActivateAndSelectAsync(vm);
        var row = vm.SelectedProject!;
        var rowChanges = new List<string?>();
        var pageChanges = new List<string?>();
        row.PropertyChanged += (_, args) => rowChanges.Add(args.PropertyName);
        vm.PropertyChanged += (_, args) => pageChanges.Add(args.PropertyName);
        try
        {
            Assert.Equal("Search English name", row.DisplayName);
            Assert.Equal("Search English summary", row.DisplaySummary);
            var details = project with { Name = "平台中文名称", Summary = "作者更新的中文简介", Description = "完整的中文介绍。" };
            pending.SetResult(details);
            await Task.WhenAll(vm.DetailsTask, vm.VersionsTask);
            Assert.Same(project, row.Project);
            Assert.Same(details, row.Details);
            Assert.Equal("平台中文名称", row.DisplayName);
            Assert.Equal("作者更新的中文简介", row.DisplaySummary);
            Assert.Contains("平台中文名称", vm.ProjectInformation);
            Assert.Contains("作者更新的中文简介", vm.ProjectInformation);
            Assert.Equal("完整的中文介绍。", vm.ProjectDescription);
            Assert.Contains(nameof(InstanceDownloadRow.DisplayName), rowChanges);
            Assert.Contains(nameof(InstanceDownloadRow.DisplaySummary), rowChanges);
            Assert.Contains(nameof(InstanceDownloadsViewModel.ProjectInformation), pageChanges);
            Assert.Same(project, Assert.Single(fixture.Catalog.Versions).Project);
            InstanceCatalogRelease? installed = null;
            fixture.Install = (release, _, _) => { installed = release; return Task.FromResult("完成"); };
            await vm.InstallCommand.ExecuteAsync(null);
            Assert.Equal(project.Id, installed!.ProjectId);
            Assert.Equal(project.Provider, installed.Provider);
            Assert.Equal("Search English name", project.Name);
            Assert.Equal("Search English summary", project.Summary);
        }
        finally { pending.TrySetResult(project); await vm.DetailsTask; }
    }

    /// <summary>返回其他平台或项目的详情会显示错误，不替换当前文案或影响已有安装选择。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MismatchedDescriptionIdentityIsRejected(bool differentProvider)
    {
        await using var fixture = new Fixture();
        fixture.Catalog.Details = (project, _) => Task.FromResult(project with
        {
            Id = differentProvider ? project.Id : "wrong-project",
            Provider = differentProvider ? InstanceCatalogProvider.CurseForge : project.Provider,
            Name = "其他项目名称", Summary = "其他项目简介", Description = "不应显示的其他项目正文"
        });
        var vm = fixture.ViewModel;
        await ActivateAndSelectAsync(vm);
        var row = vm.SelectedProject!;
        await Task.WhenAll(vm.DetailsTask, vm.VersionsTask);
        Assert.Contains("失败", vm.DescriptionStatus);
        Assert.Null(row.Details);
        Assert.Equal(row.Project.Name, row.DisplayName);
        Assert.Equal(row.Project.Summary, row.DisplaySummary);
        Assert.False(vm.HasProjectDescription);
        Assert.DoesNotContain("其他项目", vm.ProjectInformation);
        Assert.DoesNotContain("其他项目", vm.ProjectDescription);
        Assert.Equal(row.Project.Id, vm.SelectedRelease!.ProjectId);
        Assert.Equal(row.Project.Provider, vm.SelectedRelease.Provider);
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    /// <summary>刷新介绍失败时保留已经成功显示的作者正文，错误不清空缓存的展示信息。</summary>
    [Fact]
    public async Task FailedDescriptionRefreshKeepsPreviouslyLoadedContent()
    {
        await using var fixture = new Fixture();
        fixture.Catalog.Details = (project, _) => Task.FromResult(project with
        { Name = "平台中文名", Summary = "平台中文简介", Description = "先前成功读取的完整介绍" });
        var vm = fixture.ViewModel;
        await ActivateAndSelectAsync(vm);
        await Task.WhenAll(vm.DetailsTask, vm.VersionsTask);
        var previousDetails = vm.SelectedProject!.Details;
        var previousRelease = vm.SelectedRelease;
        fixture.Catalog.Details = (_, _) => Task.FromException<InstanceCatalogProject>(new IOException("刷新介绍断网"));
        await vm.RefreshDescriptionCommand.ExecuteAsync(null);
        Assert.Contains("刷新介绍断网", vm.DescriptionStatus);
        Assert.Equal("先前成功读取的完整介绍", vm.ProjectDescription);
        Assert.True(vm.HasProjectDescription);
        Assert.Same(previousDetails, vm.SelectedProject.Details);
        Assert.Equal("平台中文名", vm.SelectedProject.DisplayName);
        Assert.Equal("平台中文简介", vm.SelectedProject.DisplaySummary);
        Assert.Same(previousRelease, vm.SelectedRelease);
        Assert.True(vm.InstallCommand.CanExecute(null));
        Assert.False(vm.IsLoadingDetails);
    }

    /// <summary>离开再返回页面时复用已经成功读取的介绍，不重复查询平台或版本列表。</summary>
    [Fact]
    public async Task ReenteringPageReusesSuccessfulDescription()
    {
        await using var fixture = new Fixture();
        fixture.Catalog.Details = (project, _) => Task.FromResult(project with { Description = "已读取的作者中文正文" });
        var vm = fixture.ViewModel;
        await ActivateAndSelectAsync(vm);
        await Task.WhenAll(vm.DetailsTask, vm.VersionsTask);
        var details = vm.SelectedProject!.Details;
        vm.SetActive(false);
        vm.SetActive(true);
        await Task.WhenAll(vm.DetailsTask, vm.VersionsTask);
        Assert.Single(fixture.Catalog.DetailsCalls);
        Assert.Single(fixture.Catalog.Versions);
        Assert.Single(fixture.Catalog.Searches);
        Assert.Same(details, vm.SelectedProject.Details);
        Assert.Equal("已读取的作者中文正文", vm.ProjectDescription);
        Assert.True(vm.HasProjectDescription);
    }

    /// <summary>正文请求失败只影响介绍，版本选择和已校验文件安装仍可继续。</summary>
    [Fact]
    public async Task DescriptionFailureDoesNotBlockVersionsOrInstallation()
    {
        await using var fixture = new Fixture();
        fixture.Catalog.Details = (_, _) => Task.FromException<InstanceCatalogProject>(new IOException("介绍接口暂不可用"));
        var vm = fixture.ViewModel;
        await ActivateAndSelectAsync(vm);
        await Task.WhenAll(vm.DetailsTask, vm.VersionsTask);
        Assert.Contains("介绍接口暂不可用", vm.DescriptionStatus);
        Assert.False(vm.HasProjectDescription);
        Assert.False(vm.IsLoadingDetails);
        Assert.NotNull(vm.SelectedRelease);
        Assert.True(vm.InstallCommand.CanExecute(null));
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.InstallCalls);
    }

    /// <summary>正文尚在等待时，版本查询独立完成，用户无需等介绍才能安装。</summary>
    [Fact]
    public async Task PendingDescriptionDoesNotDelayVersions()
    {
        await using var fixture = new Fixture();
        var pending = Signal<InstanceCatalogProject>();
        fixture.Catalog.Details = (_, _) => pending.Task;
        var vm = fixture.ViewModel;
        await ActivateAndSelectAsync(vm);
        try
        {
            await vm.VersionsTask;
            Assert.True(vm.IsLoadingDetails);
            Assert.False(vm.DetailsTask.IsCompleted);
            Assert.NotNull(vm.SelectedRelease);
            Assert.True(vm.InstallCommand.CanExecute(null));
        }
        finally
        {
            pending.TrySetResult(Project(InstanceCatalogProvider.Modrinth));
            await vm.DetailsTask;
        }
    }

    /// <summary>快速切换项目后，旧详情即使忽略取消也不能覆盖当前项目正文。</summary>
    [Fact]
    public async Task ChangingProjectRejectsLateDescription()
    {
        await using var fixture = new Fixture();
        var pending = Signal<InstanceCatalogProject>();
        fixture.Catalog.Search = (query, _) => Task.FromResult(new InstanceCatalogPage(
            [Project(query.Provider, "old"), Project(query.Provider, "current")], 2, false));
        fixture.Catalog.Details = (project, _) => project.Id == "old" ? pending.Task
            : Task.FromResult(project with { Description = "当前整合包的中文介绍" });
        var vm = fixture.ViewModel;
        vm.SetActive(true);
        await vm.SearchTask;
        vm.SelectedProject = vm.Projects[0];
        var oldTask = vm.DetailsTask;
        try
        {
            vm.SelectedProject = vm.Projects[1];
            await vm.DetailsTask;
            Assert.True(fixture.Catalog.DetailsCalls[0].Token.IsCancellationRequested);
            Assert.Equal("当前整合包的中文介绍", vm.ProjectDescription);
            pending.SetResult(Project(InstanceCatalogProvider.Modrinth, "old") with { Description = "旧项目正文" });
            await oldTask;
            Assert.Equal("current", vm.SelectedProject.Project.Id);
            Assert.Equal("当前整合包的中文介绍", vm.ProjectDescription);
            Assert.False(vm.IsLoadingDetails);
        }
        finally { pending.TrySetResult(Project(InstanceCatalogProvider.Modrinth, "old")); await oldTask; }
    }

    /// <summary>切换平台或筛选会清除旧正文，迟到的介绍不能出现在新查询页面。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingSourceOrFilterRejectsLateDescription(bool changeProvider)
    {
        await using var fixture = new Fixture();
        var pending = Signal<InstanceCatalogProject>();
        fixture.Catalog.Details = (_, _) => pending.Task;
        var vm = fixture.ViewModel;
        await ActivateAndSelectAsync(vm);
        var oldTask = vm.DetailsTask;
        try
        {
            if (changeProvider) vm.ProviderIndex = 1;
            else vm.GameVersion = "1.21.1";
            Assert.True(Assert.Single(fixture.Catalog.DetailsCalls).Token.IsCancellationRequested);
            Assert.Null(vm.SelectedProject);
            pending.SetResult(Project(InstanceCatalogProvider.Modrinth) with { Description = "过时介绍" });
            await oldTask;
            Assert.False(vm.HasProjectDescription);
            Assert.DoesNotContain("过时介绍", vm.ProjectDescription);
            Assert.False(vm.IsLoadingDetails);
        }
        finally { pending.TrySetResult(Project(InstanceCatalogProvider.Modrinth)); await oldTask; }
    }

    /// <summary>返回页面会重试未完成的详情，并且不重复请求已经获得的版本列表。</summary>
    [Fact]
    public async Task ReenteringPageRetriesCanceledDescription()
    {
        await using var fixture = new Fixture();
        var pending = Signal<InstanceCatalogProject>();
        var calls = 0;
        fixture.Catalog.Details = (project, token) => ++calls == 1 ? pending.Task.WaitAsync(token)
            : Task.FromResult(project with { Description = "重试后取得作者中文正文" });
        var vm = fixture.ViewModel;
        await ActivateAndSelectAsync(vm);
        await vm.VersionsTask;
        var firstTask = vm.DetailsTask;
        vm.SetActive(false);
        await firstTask;
        Assert.True(fixture.Catalog.DetailsCalls[0].Token.IsCancellationRequested);
        vm.SetActive(true);
        await vm.DetailsTask;
        Assert.Equal(2, fixture.Catalog.DetailsCalls.Count);
        Assert.Single(fixture.Catalog.Versions);
        Assert.Equal("重试后取得作者中文正文", vm.ProjectDescription);
    }

    /// <summary>介绍失败后可以单独重试，不清空已有版本或重复搜索目录。</summary>
    [Fact]
    public async Task RefreshDescriptionRetriesOnlyTheSelectedProject()
    {
        await using var fixture = new Fixture();
        fixture.Catalog.Details = (_, _) => Task.FromException<InstanceCatalogProject>(new IOException("介绍离线"));
        var vm = fixture.ViewModel;
        await ActivateAndSelectAsync(vm);
        await Task.WhenAll(vm.VersionsTask, vm.DetailsTask);
        var release = vm.SelectedRelease;
        fixture.Catalog.Details = (project, _) => Task.FromResult(project with { Description = "平台恢复后的介绍" });
        Assert.True(vm.RefreshDescriptionCommand.CanExecute(null));
        await vm.RefreshDescriptionCommand.ExecuteAsync(null);
        Assert.Equal("平台恢复后的介绍", vm.ProjectDescription);
        Assert.True(vm.HasProjectDescription);
        Assert.Same(release, vm.SelectedRelease);
        Assert.Equal(2, fixture.Catalog.DetailsCalls.Count);
        Assert.Single(fixture.Catalog.Searches);
        Assert.Single(fixture.Catalog.Versions);
    }

    /// <summary>关闭页面会取消并等待忽略取消的正文请求，避免窗口关闭后被迟到响应更新。</summary>
    [Fact]
    public async Task StopCancelsAndAwaitsLateDescription()
    {
        await using var fixture = new Fixture();
        var pending = Signal<InstanceCatalogProject>();
        fixture.Catalog.Details = (_, _) => pending.Task;
        var vm = fixture.ViewModel;
        await ActivateAndSelectAsync(vm);
        var stopping = vm.StopAsync();
        try
        {
            Assert.True(Assert.Single(fixture.Catalog.DetailsCalls).Token.IsCancellationRequested);
            Assert.False(stopping.IsCompleted);
            Assert.False(vm.RefreshDescriptionCommand.CanExecute(null));
            pending.SetResult(Project(InstanceCatalogProvider.Modrinth) with { Description = "关闭后的迟到正文" });
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain("关闭后的迟到正文", vm.ProjectDescription);
            Assert.False(vm.HasProjectDescription);
        }
        finally { pending.TrySetResult(Project(InstanceCatalogProvider.Modrinth)); await stopping; }
    }

    /// <summary>激活并选择项目，不等待版本，以便测试受控的未完成响应。</summary>
    private static async Task ActivateAndSelectAsync(InstanceDownloadsViewModel model)
    {
        model.SetActive(true);
        await model.SearchTask;
        model.SelectedProject = Assert.Single(model.Projects);
    }

    /// <summary>按测试参数修改一项真实筛选属性，触发生产取消与清空逻辑。</summary>
    private static void ChangeFilter(InstanceDownloadsViewModel model, string filter)
    {
        switch (filter)
        {
            case "query": model.SearchText = "new pack"; break;
            case "version": model.GameVersion = "1.21.1"; break;
            case "loader": model.Loader = "fabric"; break;
            case "sort": model.SortIndex = 2; break;
            case "snapshot": model.IncludeSnapshots = true; break;
            default: throw new ArgumentException("未知测试筛选。", nameof(filter));
        }
    }

    /// <summary>异步信号不会在 SetResult 调用栈里重入页面状态变更。</summary>
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>提供固定平台身份和展示数据，不请求真实项目。</summary>
    private static InstanceCatalogProject Project(InstanceCatalogProvider provider, string id = "project") => new()
    { Provider = provider, Id = id, Name = "测试整合包", WebsiteUrl = "https://modrinth.com/modpack/example" };

    /// <summary>构造一页完整目录，供查询替身复用。</summary>
    private static InstanceCatalogPage Page(InstanceCatalogProvider provider, string id = "project") => new([Project(provider, id)], 1, false);

    /// <summary>固定版本带有完整性摘要和地址，但测试从不实际下载该地址。</summary>
    private static InstanceCatalogRelease Release(InstanceCatalogProject project, string id = "version") => new()
    {
        Provider = project.Provider, ProjectId = project.Id, Id = id, Name = "测试版本", GameVersions = ["1.20.1"],
        Hashes = new Dictionary<string, string> { ["sha1"] = new('a', 40) }, DownloadUrls = ["https://example.invalid/pack.zip"],
        WebsiteUrl = project.WebsiteUrl, FileName = "test-pack.zip"
    };

    /// <summary>组合真实 ViewModel 与可暂停的纯内存依赖，并统一等待任务结束。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        public Catalog Catalog { get; } = new();
        public InstanceDownloadsViewModel ViewModel { get; }
        public Func<InstanceCatalogRelease, IProgress<FileDownloadProgress>, CancellationToken, Task<string>> Install { get; set; } =
            (_, _, _) => Task.FromResult("实例已导入。");
        public bool CanModify { get; set; } = true;
        public int InstallCalls { get; private set; }
        public CancellationToken InstallToken { get; private set; }
        public List<Uri> Websites { get; } = [];

        /// <summary>记录安装调用和令牌，所有下载行为由测试决定。</summary>
        public Fixture() => ViewModel = new(Catalog, (release, progress, token) =>
        {
            InstallCalls++; InstallToken = token;
            return Install(release, progress, token);
        }, () => CanModify, openWebsite: Websites.Add);

        /// <summary>测试退出后等待所有已登记请求和安装，避免残留异步回调。</summary>
        public async ValueTask DisposeAsync() => await ViewModel.StopAsync();
    }

    /// <summary>保存版本请求完整参数，用于核对筛选和取消令牌。</summary>
    private sealed record VersionCall(InstanceCatalogProject Project, string GameVersion, string Loader, int Offset, int Limit, CancellationToken Token);

    /// <summary>目录只返回测试数据，精确版本网络解析必须留在真正安装服务中。</summary>
    private sealed class Catalog : IInstanceCatalog
    {
        public List<(InstanceCatalogQuery Query, CancellationToken Token)> Searches { get; } = [];
        public List<VersionCall> Versions { get; } = [];
        public List<(InstanceCatalogProject Project, CancellationToken Token)> DetailsCalls { get; } = [];
        public Func<InstanceCatalogQuery, CancellationToken, Task<InstanceCatalogPage>> Search { get; set; } =
            (query, _) => Task.FromResult(Page(query.Provider));
        public Func<VersionCall, CancellationToken, Task<InstanceVersionPage>> VersionList { get; set; } =
            (call, _) => Task.FromResult(new InstanceVersionPage([Release(call.Project)], 1, false));
        public Func<InstanceCatalogProject, CancellationToken, Task<InstanceCatalogProject>> Details { get; set; } =
            (project, _) => Task.FromResult(project);

        /// <summary>记录搜索快照并调用受控异步行为。</summary>
        public Task<InstanceCatalogPage> SearchAsync(InstanceCatalogQuery query, CancellationToken cancellationToken = default)
        { Searches.Add((query, cancellationToken)); return Search(query, cancellationToken); }

        /// <summary>记录按需详情查询，允许模拟迟到、错误与成功的作者原文响应。</summary>
        public Task<InstanceCatalogProject> GetProjectAsync(InstanceCatalogProject project, CancellationToken cancellationToken = default)
        { DetailsCalls.Add((project, cancellationToken)); return Details(project, cancellationToken); }

        /// <summary>记录项目和版本条件，允许模拟忽略取消的旧响应。</summary>
        public Task<InstanceVersionPage> GetVersionsAsync(InstanceCatalogProject project, string gameVersion = "", string loader = "",
            int offset = 0, int limit = 20, CancellationToken cancellationToken = default)
        {
            var call = new VersionCall(project, gameVersion, loader, offset, limit, cancellationToken);
            Versions.Add(call); return VersionList(call, cancellationToken);
        }

        /// <summary>页面不应自行解析或下载固定文件，误调用时立即失败。</summary>
        public Task<InstanceCatalogRelease> GetReleaseAsync(InstanceCatalogProvider provider, string projectId, string releaseId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
