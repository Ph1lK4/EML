using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Java;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods;
using EML.Core.Minecraft.Mods.Online;

namespace EML.Tests;

public sealed class MainNavigationTests
{
    /// <summary>启动只展示实例列表；点击实例功能不会提前启动在线搜索。</summary>
    [Fact]
    public async Task InstanceSectionsNeverActivateOnlineSearch()
    {
        await using var app = new Harness();
        Assert.True(app.Main.IsInstancePage);
        Assert.False(app.Main.InstanceWorkspaceOpen);
        Assert.Equal(-1, app.Instances.SelectedDetailTab);

        app.Main.OpenInstanceSectionCommand.Execute("rename");
        app.Online.SearchText = "hidden query";
        await app.Online.SearchTask;

        Assert.Equal(0, app.Catalog.Searches);
        Assert.Equal(7, app.Instances.SelectedDetailTab);
        Assert.Equal("重命名实例", app.Main.WorkspaceTitle);
        Assert.True(app.Main.IsWorkspaceVisible);
    }

    /// <summary>进入在线主页面才查询；离开后输入不再自动访问平台。</summary>
    [Fact]
    public async Task OnlyOnlineMainPageActivatesAutomaticSearch()
    {
        await using var app = new Harness();
        app.Main.SelectedPage = 1;
        await app.Online.SearchTask;
        Assert.Equal(1, app.Catalog.Searches);
        Assert.True(app.Main.IsOnlinePage);
        Assert.False(app.Main.IsInstancePage);

        app.Main.SelectedPage = 2;
        app.Online.SearchText = "not visible";
        await app.Online.SearchTask;
        Assert.Equal(1, app.Catalog.Searches);
        Assert.True(app.Main.IsAccountsPage);

        app.Main.SelectedPage = 3;
        Assert.True(app.Main.IsJavaPage);
        app.Main.SelectedPage = 0;
        Assert.True(app.Main.IsInstancePage);
    }

    /// <summary>实例后台任务切换结果区时不抢走主页面，也不取消当前在线搜索。</summary>
    [Fact]
    public async Task BackgroundDetailChangeDoesNotDeactivateOnlinePage()
    {
        await using var app = new Harness();
        app.Main.SelectedPage = 1;
        await app.Online.SearchTask;
        app.Instances.SelectedDetailTab = 2;

        Assert.Equal(1, app.Main.SelectedPage);
        Assert.True(app.Main.InstanceWorkspaceOpen);
        Assert.False(app.Main.IsWorkspaceVisible);
        app.Online.SearchText = "another query";
        await app.Online.SearchTask;
        Assert.Equal(2, app.Catalog.Searches);

        app.Main.SelectedPage = 0;
        Assert.True(app.Main.IsWorkspaceVisible);
        Assert.Equal("补全实例依赖", app.Main.WorkspaceTitle);
    }

    /// <summary>右键入口保持现有页面索引，进入工作区不立即执行文件写入。</summary>
    [Theory]
    [InlineData("details", 0, "实例详情")]
    [InlineData("check", 1, "文件检查")]
    [InlineData("download", 2, "补全实例依赖")]
    [InlineData("logs", 4, "运行日志")]
    [InlineData("mods", 6, "模组管理")]
    [InlineData("rename", 7, "重命名实例")]
    public async Task MenuSectionsOpenExpectedWorkspace(string section, int index, string title)
    {
        await using var app = new Harness();
        app.Main.SelectedPage = 3;
        app.Main.OpenInstanceSectionCommand.Execute(section);
        await app.Instances.ModListLoadTask;

        Assert.Equal(0, app.Main.SelectedPage);
        Assert.Equal(index, app.Instances.SelectedDetailTab);
        Assert.Equal(title, app.Main.WorkspaceTitle);
        Assert.True(app.Main.IsWorkspaceVisible);
        Assert.Equal(0, app.Catalog.Searches);
    }

    /// <summary>启动设置从实例菜单进入独立大页面，保留目标与尚未保存的输入。</summary>
    [Fact]
    public async Task LaunchSettingsMenuUsesDedicatedPageWithoutReloadingCurrentInput()
    {
        await using var app = new Harness();
        var selected = app.Instances.SelectedInstance;
        app.Instances.PlayerName = "DraftPlayer";
        app.Instances.MaxMemoryMbText = "12288";
        app.Instances.AdditionalJvmArguments = "-XX:+UseG1GC";
        app.Main.OpenInstanceSectionCommand.Execute("details");
        app.Main.OpenInstanceSectionCommand.Execute("launch");

        Assert.Equal(5, app.Main.SelectedPage);
        Assert.True(app.Main.IsLaunchSettingsPage);
        Assert.False(app.Main.IsWorkspaceVisible);
        Assert.False(app.Main.InstanceWorkspaceOpen);
        Assert.Equal(-1, app.Instances.SelectedDetailTab);
        Assert.Same(selected, app.Instances.SelectedInstance);
        Assert.Equal("DraftPlayer", app.Instances.PlayerName);
        Assert.Equal("12288", app.Instances.MaxMemoryMbText);
        Assert.Equal("-XX:+UseG1GC", app.Instances.AdditionalJvmArguments);
        Assert.Equal(0, app.Catalog.Searches);

        app.Main.SelectedPage = 3;
        Assert.False(app.Main.IsLaunchSettingsPage);
        app.Main.OpenLaunchSettingsCommand.Execute(null);
        Assert.True(app.Main.IsLaunchSettingsPage);
        Assert.Equal("DraftPlayer", app.Instances.PlayerName);
    }

    /// <summary>未导入实例时仍能浏览设置大页面，实例菜单则必须有明确目标。</summary>
    [Fact]
    public async Task LaunchSettingsPageCanOpenWithoutInstanceButCannotReuseOldTarget()
    {
        await using var app = new Harness();
        app.Instances.SelectedInstance = null;
        app.Main.OpenLaunchSettingsCommand.Execute(null);
        Assert.True(app.Main.IsLaunchSettingsPage);
        Assert.False(app.Instances.CanEditLaunchSettings);
        Assert.False(app.Main.OpenInstanceSectionCommand.CanExecute("launch"));
        Assert.False(app.Main.InstanceWorkspaceOpen);
    }

    /// <summary>迁移入口转到 GTNH 独立页面，不展示旧实例工作区或触发普通模组搜索。</summary>
    [Fact]
    public async Task GtnhNavigationIsIndependentFromInstanceWorkspaceAndOnlineSearch()
    {
        await using var app = new Harness();
        app.Main.OpenInstanceSectionCommand.Execute("details");
        app.Main.OpenGtnhMigrationCommand.Execute(null);
        Assert.Equal(4, app.Main.SelectedPage);
        Assert.True(app.Main.IsGtnhPage);
        Assert.False(app.Main.IsWorkspaceVisible);
        Assert.Equal(-1, app.Instances.SelectedDetailTab);
        Assert.False(app.Main.OpenInstanceSectionCommand.CanExecute("migration"));
        Assert.Equal(0, app.Catalog.Searches);
    }

    /// <summary>下载中心只切换展示，保留实例及启动草稿，不触发隐藏页面搜索。</summary>
    [Fact]
    public async Task DownloadCenterIsIndependentAndKeepsInstanceAndLaunchInput()
    {
        await using var app = new Harness();
        var selected = app.Instances.SelectedInstance;
        app.Instances.PlayerName = "PendingPlayer";
        app.Main.OpenInstanceSectionCommand.Execute("details");
        var changed = new List<string?>();
        app.Main.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        app.Main.SelectedPage = 7;
        app.Online.SearchText = "hidden while viewing downloads";
        await app.Online.SearchTask;

        Assert.True(app.Main.IsDownloadCenterPage);
        Assert.False(app.Main.IsDownloadsPage);
        Assert.False(app.Main.IsOnlinePage);
        Assert.False(app.Main.IsWorkspaceVisible);
        Assert.Equal(-1, app.Instances.SelectedDetailTab);
        Assert.Same(selected, app.Instances.SelectedInstance);
        Assert.Equal("PendingPlayer", app.Instances.PlayerName);
        Assert.Equal(0, app.Catalog.Searches);
        Assert.Contains(nameof(MainViewModel.IsDownloadCenterPage), changed);
        app.Main.SelectedPage = 5;
        Assert.False(app.Main.IsDownloadCenterPage);
        Assert.True(app.Main.IsLaunchSettingsPage);
    }

    /// <summary>关闭模组面板后切换实例不扫描，重新打开时只读取新的目标实例。</summary>
    [Fact]
    public async Task ReopenedModWorkspaceLoadsCurrentInstanceOnly()
    {
        await using var app = new Harness();
        app.Main.OpenInstanceSectionCommand.Execute("mods");
        await app.Instances.ModListLoadTask;
        Assert.Equal(app.Instances.SelectedInstance!.InstancePath, Assert.Single(app.Services.ModReads));

        app.Main.CloseInstanceWorkspaceCommand.Execute(null);
        app.Instances.SelectedInstance = Instance("Second");
        await app.Instances.ModListLoadTask;
        Assert.Single(app.Services.ModReads);
        Assert.Equal(-1, app.Instances.SelectedDetailTab);

        app.Main.OpenInstanceSectionCommand.Execute("mods");
        await app.Instances.ModListLoadTask;
        Assert.Equal(2, app.Services.ModReads.Count);
        Assert.Equal(Instance("Second").InstancePath, app.Services.ModReads[1]);
    }

    /// <summary>主页面切换关闭单实例面板，同时让隐藏的模组页停止安排扫描。</summary>
    [Fact]
    public async Task LeavingInstancesClosesWorkspaceWithoutChangingSelection()
    {
        await using var app = new Harness();
        var selected = app.Instances.SelectedInstance;
        app.Main.OpenInstanceSectionCommand.Execute("details");
        app.Main.SelectedPage = 3;
        Assert.False(app.Main.InstanceWorkspaceOpen);
        Assert.False(app.Main.IsWorkspaceVisible);
        Assert.Equal(-1, app.Instances.SelectedDetailTab);
        Assert.Same(selected, app.Instances.SelectedInstance);
    }

    /// <summary>关闭面板和离开在线页不会取消用户已确认的安装任务。</summary>
    [Fact]
    public async Task ClosingWorkspaceDoesNotCancelOnlineInstallation()
    {
        await using var app = new Harness();
        app.Main.SelectedPage = 1;
        await app.Online.SearchTask;
        app.Online.SelectedProject = new(Catalog.Project());
        await app.Online.DetailTask;
        await app.Online.VersionTask;
        await app.Online.PreviewCommand.ExecuteAsync(null);
        var installation = app.Online.InstallCommand.ExecuteAsync(null);
        try
        {
            Assert.True(app.Online.IsInstalling);
            app.Main.OpenInstanceSectionCommand.Execute("details");
            app.Main.CloseInstanceWorkspaceCommand.Execute(null);
            app.Main.SelectedPage = 3;
            Assert.False(app.Installer.InstallToken.IsCancellationRequested);
            Assert.False(installation.IsCompleted);
            Assert.True(app.Online.IsInstalling);
        }
        finally
        {
            app.Installer.Completion.TrySetResult(new(1, 0, false, []));
            await installation;
        }
        Assert.Contains("1", app.Online.InstallStatus);
    }

    /// <summary>无实例或未知菜单入口不会沿用上一个目标，清空选择会关闭旧面板。</summary>
    [Fact]
    public async Task MissingSelectionAndUnknownSectionCannotOpenWorkspace()
    {
        await using var app = new Harness();
        Assert.False(app.Main.OpenInstanceSectionCommand.CanExecute("unknown"));
        app.Main.OpenInstanceSectionCommand.Execute("unknown");
        Assert.False(app.Main.InstanceWorkspaceOpen);
        app.Main.OpenInstanceSectionCommand.Execute("details");
        app.Instances.SelectedInstance = null;
        Assert.False(app.Main.InstanceWorkspaceOpen);
        Assert.Equal(-1, app.Instances.SelectedDetailTab);
        Assert.False(app.Main.OpenInstanceSectionCommand.CanExecute("details"));
        app.Main.OpenInstanceSectionCommand.Execute("details");
        Assert.False(app.Main.InstanceWorkspaceOpen);
    }

    /// <summary>无效导航值被忽略；释放后实例事件不再重新打开窗口工作区。</summary>
    [Fact]
    public async Task InvalidPageAndDisposedEventsCannotChangeNavigation()
    {
        await using var app = new Harness();
        app.Main.SelectedPage = -1;
        app.Main.SelectedPage = 9;
        Assert.Equal(0, app.Main.SelectedPage);
        app.Main.Dispose();
        app.Instances.SelectedDetailTab = 4;
        Assert.False(app.Main.InstanceWorkspaceOpen);
        Assert.False(app.Main.OpenInstanceSectionCommand.CanExecute("logs"));
    }

    /// <summary>创建纯内存实例，不访问用户的测试包或游戏目录。</summary>
    private static MinecraftInstance Instance(string name = "First") => new(name, "C:\\navigation-test\\" + name,
        "C:\\navigation-test\\" + name + "\\.minecraft", ["1.20.1"])
        { Versions = [new("fabric", "1.20.1", "Fabric", "unused")] };

    /// <summary>统一组合真实导航模型与无磁盘、无网络的受控服务。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public NoIo Services { get; } = new();
        public Catalog Catalog { get; } = new();
        public Installer Installer { get; } = new();
        public InstancesViewModel Instances { get; }
        public MainViewModel Main { get; }
        public OnlineModsViewModel Online => Instances.OnlineMods!;

        public Harness()
        {
            Instances = new(Services, Services, Services, Services, "C:\\navigation-test", Services, Services,
                localMods: Services, onlineCatalog: Catalog, onlineInstaller: Installer);
            Main = new(new JavaSettingsViewModel(Services), Instances);
            Instances.SelectedInstance = Instance();
        }

        /// <summary>退出前等待所有测试安排的异步任务，避免影响后续用例。</summary>
        public async ValueTask DisposeAsync()
        {
            await Online.StopAsync();
            await Instances.StopModListLoadingAsync();
            Main.Dispose();
        }
    }

    /// <summary>目录查询立即返回固定身份，不包含任何外部平台访问。</summary>
    private sealed class Catalog : IOnlineModCatalog
    {
        private static readonly OnlineProjectId Id = new(ModProvider.Modrinth, "project1");
        public int Searches { get; private set; }
        public static OnlineModProject Project() => new() { Id = Id, Name = "Test Mod" };
        public static OnlineModVersion Version() => new()
        {
            Project = Id, Id = "version1", VersionNumber = "1", GameVersions = ["1.20.1"], Loaders = ["fabric"],
            Files = [new() { FileName = "test.jar", Requirement = new() { Size = 1 } }]
        };
        public Task<OnlineModSearchPage> SearchAsync(OnlineModQuery query, CancellationToken token = default)
        { Searches++; return Task.FromResult(new OnlineModSearchPage([Project()], false, [])); }
        public Task<OnlineModProject> GetProjectAsync(OnlineProjectId project, CancellationToken token = default) => Task.FromResult(Project());
        public Task<OnlineModVersionPage> GetVersionsAsync(OnlineProjectId project, string gameVersion, string loader, int page = 0, CancellationToken token = default) =>
            Task.FromResult(new OnlineModVersionPage([Version()], false));
        public Task<OnlineModVersion> GetVersionAsync(OnlineProjectId project, string versionId, CancellationToken token = default) => Task.FromResult(Version());
        public Task<OnlineModFile> ResolveDownloadAsync(OnlineModVersion version, OnlineModFile file, CancellationToken token = default) => Task.FromResult(file);
    }

    /// <summary>保留真实取消令牌，验证导航只改变展示而不会取消安装。</summary>
    private sealed class Installer : IOnlineModInstaller
    {
        public TaskCompletionSource<OnlineModInstallResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken InstallToken { get; private set; }
        public Task<OnlineModInstallPlan> PlanAsync(OnlineModPlanRequest request, CancellationToken token = default) =>
            Task.FromResult(new OnlineModInstallPlan { Id = "plan", Request = request,
                Items = [new(Catalog.Version(), Catalog.Version().Files[0], "用户选择", OnlineModPlanStatus.Download)] });
        public Task<OnlineModInstallResult> InstallAsync(OnlineModInstallPlan plan, IProgress<OnlineModInstallProgress>? progress = null, CancellationToken token = default)
        { InstallToken = token; return Completion.Task.WaitAsync(token); }
    }

    /// <summary>导航不应调用写入方法；任何意外文件操作都直接让测试失败。</summary>
    private sealed class NoIo : IJavaLocator, IMinecraftInstanceLocator, IMinecraftInstanceManager, IMinecraftInstanceImporter,
        IInstanceDialogs, IMinecraftInstanceChecker, IMinecraftInstanceDownloader, ILocalModManager
    {
        public List<string> ModReads { get; } = [];
        Task<IReadOnlyList<JavaInstallation>> IJavaLocator.FindAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JavaInstallation>>([]);
        Task<IReadOnlyList<MinecraftInstance>> IMinecraftInstanceLocator.FindAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MinecraftInstance>>([]);
        public Task<LocalModCatalog> ListAsync(string instancePath, CancellationToken token = default)
        { ModReads.Add(instancePath); return Task.FromResult(new LocalModCatalog([], [])); }
        public Task DeleteAsync(string instancePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RenameAsync(string instancePath, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportAsync(string instancePath, string destinationZip, IProgress<InstanceExportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstanceImportPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstanceImportResult> ImportAsync(string archivePath, string instanceName, IProgress<InstanceImportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public bool ConfirmDelete(MinecraftInstance instance) => throw new NotSupportedException();
        public string? ChooseExportPath(MinecraftInstance instance) => throw new NotSupportedException();
        public string? ChooseImportPath() => throw new NotSupportedException();
        public string? ConfirmImport(InstanceImportPreview preview) => throw new NotSupportedException();
        public Task<InstanceCheckReport> CheckAsync(string instancePath, InstanceCheckMode mode = InstanceCheckMode.Full, IProgress<InstanceCheckProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstanceDownloadReport> DownloadMissingAsync(string instancePath, IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddAsync(string instancePath, string sourcePath, CancellationToken token = default) => throw new NotSupportedException();
        public Task AdoptAsync(string instancePath, string path, CancellationToken token = default) => throw new NotSupportedException();
        public Task SetEnabledAsync(string instancePath, string path, bool enabled, CancellationToken token = default) => throw new NotSupportedException();
        public Task RemoveAsync(string instancePath, string path, CancellationToken token = default) => throw new NotSupportedException();
        public Task RestoreAsync(string instancePath, string path, CancellationToken token = default) => throw new NotSupportedException();
        public Task RecoverAsync(string instancePath, CancellationToken token = default) => throw new NotSupportedException();
    }
}
