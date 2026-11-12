using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Downloads;
using EML.Core.Java;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Catalog;
using EML.Core.Minecraft.Gtnh;
using EML.Core.Minecraft.Migration;

namespace EML.Tests;

/// <summary>专区仅操作明确识别的目标，测试使用可控服务，不访问玩家实例或网络。</summary>
public sealed partial class GtnhHubViewModelTests
{
    /// <summary>空实例库也能打开本体下载；隐藏页面不查询镜像，专区关闭会取消仍在进行的读取。</summary>
    [Fact]
    public async Task PackDownloadPageWorksWithoutAnExistingTargetAndSharesHostBusyState()
    {
        var packs = new PackService();
        await using var app = new Harness(packs: packs);
        app.Instances.Instances.Clear();
        app.Instances.SelectedInstance = null;
        app.Main.SelectedPage = 4;
        await app.Hub.TargetLoadTask;
        Assert.Equal(0, packs.Reads);
        app.Hub.ShowPackDownloadsCommand.Execute(null);
        var downloads = Assert.IsType<GtnhPackDownloadViewModel>(app.Hub.Packs);
        await downloads.ReleasesLoadTask;
        Assert.True(app.Hub.IsPackDownloads);
        Assert.False(app.Hub.HasTarget);
        Assert.False(app.Hub.IsCatalogPage);
        downloads.SelectedRelease = Assert.Single(downloads.Releases);
        Assert.True(downloads.InstallCommand.CanExecute(null));

        app.Instances.IsBusy = true;
        Assert.False(downloads.InstallCommand.CanExecute(null));
        app.Instances.IsBusy = false;
        Assert.True(downloads.InstallCommand.CanExecute(null));
        app.Hub.ShowMigrationCommand.Execute(null);
        Assert.True(app.Hub.IsMigration);
        Assert.False(downloads.InstallCommand.CanExecute(null));
        Assert.Equal(1, packs.Reads);
    }

    /// <summary>推荐模组安装按服务结果通知一次，失败原因和实际目标文件路径均保留。</summary>
    [Theory]
    [InlineData(true, false, OperationNoticeKind.Success)]
    [InlineData(false, false, OperationNoticeKind.Failure)]
    [InlineData(false, true, OperationNoticeKind.Cancelled)]
    public async Task AddonInstallReportsOutcomeAndDestination(bool succeeded, bool cancelled, OperationNoticeKind expected)
    {
        var notices = new HubNoticeRecorder();
        await using var app = new Harness(notices);
        app.Addons.Install = _ => Task.FromResult(new GtnhAddonInstallResult(succeeded, cancelled,
            succeeded ? "安装完成" : cancelled ? "用户已取消" : "官方摘要校验失败"));
        await app.SelectAddonAsync();
        app.Hub.SelectedRelease = Assert.Single(app.Hub.Releases); app.Hub.CompatibilityConfirmed = true;
        notices.Items.Clear();
        await app.Hub.InstallCommand.ExecuteAsync(null);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(expected, notice.Kind);
        Assert.Contains(Path.Combine(app.Hub.SelectedTarget!.GameDirectory, "mods", "test.jar"), notice.Paths);
        if (!succeeded && !cancelled) Assert.Contains("官方摘要校验失败", notice.Message);
    }

    /// <summary>平台原名与本地别名不同，成功、失败和取消都应提供服务返回的实际可复制路径。</summary>
    [Theory]
    [InlineData(true, false, OperationNoticeKind.Success)]
    [InlineData(false, false, OperationNoticeKind.Failure)]
    [InlineData(false, true, OperationNoticeKind.Cancelled)]
    public async Task ResourceInstallReportsActualAliasedDestination(bool succeeded, bool cancelled, OperationNoticeKind expected)
    {
        var notices = new HubNoticeRecorder();
        var addon = new GtnhAddon("test", "界面资源包", "用途", "界面", "ModernityGTNH/Modernity-GTNH-UI", "说明")
        { Kind = GtnhAddonKind.ResourcePack };
        await using var app = new Harness(notices, [addon]);
        app.Addons.Read = _ => Task.FromResult<IReadOnlyList<GtnhAddonRelease>>(
            [Release("default") with { FileName = "default.zip", Kind = GtnhAddonKind.ResourcePack }]);
        app.Hub.ShowResourcePacksCommand.Execute(null);
        await app.SelectAddonAsync();
        var game = app.Hub.SelectedTarget!.GameDirectory;
        var actual = Path.Combine(game, "resourcepacks", "eml-modernitygtnh--modernity-gtnh-ui-202.zip");
        app.Addons.Install = _ => Task.FromResult(new GtnhAddonInstallResult(succeeded, cancelled, "安装结果")
        { DestinationPath = actual });
        app.Hub.SelectedRelease = Assert.Single(app.Hub.Releases); app.Hub.CompatibilityConfirmed = true;
        notices.Items.Clear();

        await app.Hub.InstallCommand.ExecuteAsync(null);

        var notice = Assert.Single(notices.Items);
        Assert.Equal(expected, notice.Kind);
        Assert.Contains(actual, notice.Paths);
        Assert.DoesNotContain(Path.Combine(game, "resourcepacks", "default.zip"), notice.Paths);
    }

    /// <summary>发行状态分别统计可安装与不可自动安装的文件，摘要缺失不能算作可安装。</summary>
    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(0, 2)]
    public async Task ReleaseStatusSeparatesInstallableAndUnavailableFiles(int installable, int unavailable)
    {
        await using var app = new Harness();
        app.Addons.Read = _ => Task.FromResult<IReadOnlyList<GtnhAddonRelease>>(
            Enumerable.Range(0, installable).Select(index => Release("ready-" + index))
                .Concat(Enumerable.Range(0, unavailable).Select(index => Release("unverified-" + index) with
                { Sha256 = "", Problem = "没有官方摘要" })).ToArray());

        await app.SelectAddonAsync();

        Assert.Contains($"可安装 {installable} 个，不能自动安装 {unavailable} 个", app.Hub.ReleaseStatus);
        if (installable == 0) Assert.Contains("当前没有可自动安装的 JAR 文件", app.Hub.ReleaseStatus);
        Assert.Null(app.Hub.SelectedRelease);
        Assert.False(app.Hub.InstallCommand.CanExecute(null));
    }

    /// <summary>无发行资产时直接说明作者未发布对应格式，避免空列表看似仍可安装。</summary>
    [Theory]
    [InlineData(GtnhAddonKind.Mod, "JAR")]
    [InlineData(GtnhAddonKind.ResourcePack, "ZIP")]
    public async Task EmptyReleaseStatusExplainsMissingPublishedArchive(GtnhAddonKind kind, string extension)
    {
        var addon = new GtnhAddon("test", "测试项目", "用途", "工具", "GTNewHorizons/Test", "说明") { Kind = kind };
        await using var app = new Harness(catalog: [addon]);
        app.Addons.Read = _ => Task.FromResult<IReadOnlyList<GtnhAddonRelease>>([]);
        if (kind == GtnhAddonKind.ResourcePack) app.Hub.ShowResourcePacksCommand.Execute(null);

        await app.SelectAddonAsync();

        Assert.Empty(app.Hub.Releases);
        Assert.Contains("可安装 0 个，不能自动安装 0 个", app.Hub.ReleaseStatus);
        Assert.Contains($"本次未找到可供此入口安装的 {extension} 文件", app.Hub.ReleaseStatus);
    }

    /// <summary>自动成功读取不弹框，用户主动刷新版本会提示，平台失败则给出原因。</summary>
    [Fact]
    public async Task ExplicitAddonRefreshAndFailedReadsAreVisible()
    {
        var notices = new HubNoticeRecorder();
        await using var app = new Harness(notices); await app.SelectAddonAsync();
        Assert.Empty(notices.Items);
        await app.Hub.RefreshReleasesCommand.ExecuteAsync(null);
        Assert.Equal(OperationNoticeKind.Success, Assert.Single(notices.Items).Kind);
        notices.Items.Clear();
        app.Addons.Read = _ => Task.FromException<IReadOnlyList<GtnhAddonRelease>>(new IOException("无法连接发行服务器"));
        await app.Hub.RefreshReleasesCommand.ExecuteAsync(null);
        var error = Assert.Single(notices.Items);
        Assert.Equal(OperationNoticeKind.Failure, error.Kind);
        Assert.Contains("无法连接发行服务器", error.Message);
    }

    [Fact]
    public async Task OpeningHubListsOnlyRecognizedInstancesWithoutRequestingReleases()
    {
        await using var app = new Harness();
        Assert.Empty(app.Hub.Targets); Assert.Equal(0, app.Addons.Reads);
        app.Main.SelectedPage = 4;
        await app.Hub.TargetLoadTask;
        Assert.Equal(new[] { "old", "new" }, app.Hub.Targets.Select(item => item.Name));
        Assert.Equal(app.Instances.Instances[0].InstancePath, app.Hub.SelectedTarget!.InstancePath);
        Assert.True(app.Hub.HasTarget); Assert.Equal(0, app.Addons.Reads);
        app.Main.OpenGtnhMigrationCommand.Execute(null);
        Assert.True(app.Hub.IsMigration); Assert.False(app.Main.IsWorkspaceVisible);
    }

    [Fact]
    public async Task ExplicitReleaseAndCompatibilityConfirmationAreRequired()
    {
        await using var app = new Harness(); await app.SelectAddonAsync();
        Assert.Null(app.Hub.SelectedRelease);
        Assert.False(app.Hub.InstallCommand.CanExecute(null));
        app.Hub.SelectedRelease = Assert.Single(app.Hub.Releases);
        Assert.False(app.Hub.InstallCommand.CanExecute(null));
        app.Hub.CompatibilityConfirmed = true;
        Assert.True(app.Hub.InstallCommand.CanExecute(null));
        await app.Hub.InstallCommand.ExecuteAsync(null);
        Assert.Equal(app.Hub.SelectedTarget!.InstancePath, app.Addons.InstalledPath);
        Assert.Equal(1, app.Addons.Installs);
        Assert.Contains("完成", app.Hub.InstallStatus);
        Assert.False(app.Hub.CompatibilityConfirmed); Assert.False(app.Instances.IsBusy);
    }

    [Fact]
    public async Task MissingDigestOrOtherBusyOperationCannotInstall()
    {
        await using var app = new Harness(); await app.SelectAddonAsync();
        app.Hub.SelectedRelease = Release("unverified") with { Problem = "没有官方摘要", Sha256 = "" };
        app.Hub.CompatibilityConfirmed = true;
        await app.Hub.InstallCommand.ExecuteAsync(null);
        Assert.Equal(0, app.Addons.Installs);
        app.Hub.SelectedRelease = Assert.Single(app.Hub.Releases); app.Hub.CompatibilityConfirmed = true;
        app.Instances.IsBusy = true;
        Assert.False(app.Hub.InstallCommand.CanExecute(null));
        await app.Hub.InstallCommand.ExecuteAsync(null);
        Assert.Equal(0, app.Addons.Installs);
        app.Instances.IsBusy = false;
    }

    [Fact]
    public async Task SwitchingTargetDiscardsOldReleaseResponseAndResetsConfirmation()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<GtnhAddonRelease>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = new Harness();
        app.Addons.Read = path => path.EndsWith("old") ? pending.Task : Task.FromResult<IReadOnlyList<GtnhAddonRelease>>([Release("new")]);
        try
        {
            app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
            app.Hub.SelectedAddon = Assert.Single(app.Hub.Addons);
            var interrupted = app.Hub.ReleasesLoadTask;
            app.Hub.SelectedTarget = app.Hub.Targets[1];
            await app.Hub.ReleasesLoadTask;
            pending.SetResult([Release("old")]); await interrupted;
            Assert.Equal("new.jar", Assert.Single(app.Hub.Releases).FileName);
            Assert.Equal(app.Instances.Instances[1], app.Instances.SelectedInstance);
            Assert.Null(app.Hub.SelectedRelease); Assert.False(app.Hub.CompatibilityConfirmed);
        }
        finally { pending.TrySetResult([]); }
    }

    [Fact]
    public async Task ReenteringHubRestartsInterruptedReleaseReadWithoutLosingTarget()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<GtnhAddonRelease>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = new Harness(); var reads = 0;
        app.Addons.Read = _ => ++reads == 1 ? pending.Task : Task.FromResult<IReadOnlyList<GtnhAddonRelease>>([Release("current")]);
        try
        {
            app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
            app.Hub.SelectedAddon = Assert.Single(app.Hub.Addons); var interrupted = app.Hub.ReleasesLoadTask;
            app.Main.SelectedPage = 3;
            app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask; await app.Hub.ReleasesLoadTask;
            Assert.Equal(2, reads);
            pending.SetResult([Release("old")]); await interrupted;
            Assert.Equal("current.jar", Assert.Single(app.Hub.Releases).FileName);
            Assert.True(app.Hub.HasTarget);
        }
        finally { pending.TrySetResult([]); }
    }

    [Fact]
    public async Task LeavingHubDoesNotCancelInstallationButClosingDoes()
    {
        var pending = new TaskCompletionSource<GtnhAddonInstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = new Harness();
        app.Addons.Install = token => pending.Task.WaitAsync(token);
        await app.SelectAddonAsync();
        app.Hub.SelectedRelease = Assert.Single(app.Hub.Releases); app.Hub.CompatibilityConfirmed = true;
        var installation = app.Hub.InstallCommand.ExecuteAsync(null);
        Assert.True(app.Instances.IsBusy); Assert.False(app.Hub.CanInteract);
        app.Main.SelectedPage = 3;
        Assert.False(app.Addons.InstallToken.IsCancellationRequested); Assert.False(installation.IsCompleted);
        await app.Hub.StopAsync(); await installation;
        Assert.True(app.Addons.InstallToken.IsCancellationRequested);
        Assert.False(app.Instances.IsBusy);
    }

    [Fact]
    public async Task SelectingNonGtnhElsewhereCannotRetainAnInstallTarget()
    {
        await using var app = new Harness(); await app.SelectAddonAsync();
        app.Hub.SelectedRelease = Assert.Single(app.Hub.Releases); app.Hub.CompatibilityConfirmed = true;
        app.Instances.SelectedInstance = app.Instances.Instances[2];
        Assert.False(app.Hub.HasTarget); Assert.Null(app.Hub.SelectedTarget);
        Assert.Null(app.Hub.SelectedRelease); Assert.False(app.Hub.InstallCommand.CanExecute(null));
        Assert.Equal(0, app.Addons.Installs);
    }

    /// <summary>真实 WPF 会响应集合移除并清空选择，未变化的刷新应保留对象与用户的发行选择。</summary>
    [Fact]
    public async Task RefreshAndMatchingSearchKeepExistingSelectionObjects()
    {
        await using var app = new Harness(); await app.SelectAddonAsync();
        app.Hub.SelectedRelease = Assert.Single(app.Hub.Releases); app.Hub.CompatibilityConfirmed = true;
        var selected = app.Hub.SelectedRelease; var target = app.Hub.SelectedTarget; var changes = 0;
        app.Hub.Targets.CollectionChanged += (_, _) => changes++;
        app.Hub.Addons.CollectionChanged += (_, _) => changes++;
        app.Hub.SearchText = "测试";
        await app.Hub.RefreshTargetsCommand.ExecuteAsync(null);
        app.Main.SelectedPage = 0; app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
        Assert.Equal(0, changes); Assert.Same(target, app.Hub.SelectedTarget);
        Assert.Same(selected, app.Hub.SelectedRelease); Assert.True(app.Hub.CompatibilityConfirmed);
        Assert.Equal(1, app.Addons.Reads);
    }

    /// <summary>迁移中损坏 GTNH 标识时仍能选中实例恢复，但不能把它当正常目标下载模组。</summary>
    [Fact]
    public async Task UnrecognizedInstanceWithRecoveryRemainsSelectableWithoutAddonDownloads()
    {
        await using var app = new Harness(); app.Services.HasRecovery = true;
        app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
        Assert.Equal(3, app.Hub.Targets.Count);
        app.Hub.SelectedTarget = app.Hub.Targets[2];
        app.Hub.SelectedAddon = Assert.Single(app.Hub.Addons);
        Assert.True(app.Hub.HasTarget); Assert.Equal("待恢复", app.Hub.SelectedTarget.Version);
        Assert.False(app.Hub.RefreshReleasesCommand.CanExecute(null));
        Assert.False(app.Hub.InstallCommand.CanExecute(null)); Assert.Equal(0, app.Addons.Reads);
    }

    /// <summary>功能分类、规则分组和关键词同时取交集，大小写与首尾空格不影响搜索。</summary>
    [Theory]
    [InlineData(0, "地图", "map", "test,map-nonstargate")]
    [InlineData(1, "地图", "MAP", "test")]
    [InlineData(2, "地图", " map ", "map-nonstargate")]
    [InlineData(2, "性能", "map", "")]
    public async Task CategoryRuleAndSearchFiltersUseIntersection(int rule, string category, string search, string expected)
    {
        await using var app = new Harness(catalog: Catalog());
        app.Hub.SelectedCategory = category; app.Hub.RuleFilterIndex = rule; app.Hub.SearchText = search;
        Assert.Equal(expected.Length == 0 ? [] : expected.Split(','), app.Hub.Addons.Select(addon => addon.Id));
        Assert.Equal(expected.Length == 0, app.Hub.IsCatalogEmpty);
        Assert.DoesNotContain("字体", app.Hub.Categories);
        Assert.Equal(0, app.Addons.Reads);
    }

    /// <summary>任一筛选隐藏已选项目时，同时清除发行文件和兼容确认，防止安装不可见的旧选择。</summary>
    [Theory]
    [InlineData("category")]
    [InlineData("rule")]
    [InlineData("search")]
    public async Task HiddenAddonClearsReleaseAndConfirmation(string filter)
    {
        await using var app = new Harness(catalog: Catalog());
        app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
        app.Hub.SelectedAddon = app.Hub.Addons.Single(addon => addon.Id == "test");
        await app.Hub.ReleasesLoadTask;
        app.Hub.SelectedRelease = Assert.Single(app.Hub.Releases); app.Hub.CompatibilityConfirmed = true;
        Assert.True(app.Hub.InstallCommand.CanExecute(null));

        if (filter == "category") app.Hub.SelectedCategory = "性能";
        else if (filter == "rule") app.Hub.RuleFilterIndex = 2;
        else app.Hub.SearchText = "性能";

        Assert.Null(app.Hub.SelectedAddon); Assert.False(app.Hub.HasSelectedAddon);
        Assert.Empty(app.Hub.Releases); Assert.Null(app.Hub.SelectedRelease);
        Assert.False(app.Hub.CompatibilityConfirmed); Assert.False(app.Hub.InstallCommand.CanExecute(null));
        Assert.Equal(1, app.Addons.Reads);
    }

    /// <summary>资源目录与模组互相隔离，迁移仍使用索引一，各页切换不会改变目标实例。</summary>
    [Fact]
    public async Task ResourceCatalogIsSeparateAndNavigationPreservesMigrationAndTarget()
    {
        await using var app = new Harness(catalog: Catalog());
        app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
        app.Hub.SelectedTarget = app.Hub.Targets[1];
        var target = app.Hub.SelectedTarget;
        app.Hub.SelectedCategory = "地图";
        app.Hub.SelectedAddon = app.Hub.Addons.Single(addon => addon.Id == "test");
        await app.Hub.ReleasesLoadTask;
        app.Hub.SelectedRelease = Assert.Single(app.Hub.Releases); app.Hub.CompatibilityConfirmed = true;

        app.Hub.ShowResourcePacksCommand.Execute(null);
        Assert.Equal(2, app.Hub.SelectedSection); Assert.True(app.Hub.IsResourcePacks);
        Assert.Equal("全部分类", app.Hub.SelectedCategory);
        Assert.Equal("resource", Assert.Single(app.Hub.Addons).Id);
        Assert.Equal(new[] { "全部分类", "字体" }, app.Hub.Categories);
        Assert.Null(app.Hub.SelectedAddon); Assert.Empty(app.Hub.Releases); Assert.False(app.Hub.CompatibilityConfirmed);
        Assert.Same(target, app.Hub.SelectedTarget); Assert.Same(app.Instances.Instances[1], app.Instances.SelectedInstance);

        app.Hub.ShowMigrationCommand.Execute(null);
        Assert.Equal(1, app.Hub.SelectedSection); Assert.True(app.Hub.IsMigration); Assert.False(app.Hub.IsCatalogPage);
        Assert.Same(target, app.Hub.SelectedTarget);
        app.Hub.ShowRecommendationsCommand.Execute(null);
        Assert.Equal(0, app.Hub.SelectedSection); Assert.True(app.Hub.IsRecommendations);
        Assert.All(app.Hub.Addons, addon => Assert.Equal(GtnhAddonKind.Mod, addon.Kind));
        Assert.DoesNotContain(app.Hub.Addons, addon => addon.Id == "resource");
        Assert.Same(target, app.Hub.SelectedTarget);
    }

    /// <summary>外部页面或需人工安装的项目不请求发行接口，切换实例后仍保留具体原因。</summary>
    [Theory]
    [InlineData(GtnhAddonProvider.External, null)]
    [InlineData(GtnhAddonProvider.Github, "需按作者说明先安装前置组件。")]
    [InlineData(GtnhAddonProvider.CurseForge, "此条目只提供人工安装说明。")]
    public async Task ManualEntriesNeverQueryReleasesAndKeepReasonAcrossTargets(GtnhAddonProvider provider, string? reason)
    {
        var addon = new GtnhAddon("manual", "手动项目", "用途", "工具", "GTNewHorizons/Manual", "说明")
        { Provider = provider, ManualReason = reason, CurseForgeProjectId = 123 };
        await using var app = new Harness(catalog: [addon]);
        await app.SelectAddonAsync();
        var status = app.Hub.ReleaseStatus;
        if (reason is not null) Assert.Equal(reason, status);
        else Assert.Contains("作者页面", status);
        Assert.False(app.Hub.RefreshReleasesCommand.CanExecute(null));
        await app.Hub.RefreshReleasesCommand.ExecuteAsync(null);

        app.Hub.SelectedTarget = app.Hub.Targets[1]; await app.Hub.ReleasesLoadTask;
        Assert.Equal(status, app.Hub.ReleaseStatus); Assert.Equal(0, app.Addons.Reads);
        Assert.Empty(app.Hub.Releases); Assert.False(app.Hub.InstallCommand.CanExecute(null));
    }

    /// <summary>普通目录条目不误显示非星门警告；选择标记条目时才显示该规则提示。</summary>
    [Fact]
    public async Task NonStargateWarningFollowsOnlyExplicitCatalogFlag()
    {
        await using var app = new Harness(catalog: Catalog());
        app.Hub.SelectedAddon = app.Hub.Addons.Single(addon => addon.Id == "test");
        Assert.False(app.Hub.IsNonStargateAddon);
        Assert.DoesNotContain("非星门规则模组", app.Hub.SelectedAddon.RuleDescription);
        app.Hub.SelectedAddon = app.Hub.Addons.Single(addon => addon.Id == "map-nonstargate");
        Assert.True(app.Hub.IsNonStargateAddon);
        Assert.Contains("非星门规则模组", app.Hub.SelectedAddon.RuleDescription);
    }

    /// <summary>提供可交叉筛选的人工目录，资源包独占分类便于检查页间隔离。</summary>
    private static IReadOnlyList<GtnhAddon> Catalog() =>
    [
        new("test", "地图助手", "Map overlay", "地图", "GTNewHorizons/Test", "说明"),
        new("map-nonstargate", "全图显示", "Map reveal", "地图", "GTNewHorizons/Reveal", "说明") { IsNonStargate = true },
        new("performance", "性能优化", "Map metrics", "性能", "GTNewHorizons/Metrics", "说明"),
        new("unrelated", "定位工具", "坐标查找", "地图", "GTNewHorizons/Coordinates", "说明"),
        new("resource", "中文字体", "字体资源包", "字体", "", "说明")
        { Kind = GtnhAddonKind.ResourcePack, Provider = GtnhAddonProvider.External, ManualReason = "请查看作者安装说明。" }
    ];

    /// <summary>构造可安装的固定发行文件，测试不会实际使用其中的网络地址。</summary>
    private static GtnhAddonRelease Release(string name) => new()
    {
        AddonId = "test", Repository = "GTNewHorizons/Test", ReleaseId = 1, AssetId = 2, Tag = "1.0",
        FileName = name + ".jar", Size = 5, Sha256 = new('a', 64),
        DownloadUrl = "https://github.com/GTNewHorizons/Test/releases/download/1.0/test.jar",
        ReleaseUrl = "https://github.com/GTNewHorizons/Test/releases/tag/1.0"
    };

    /// <summary>组合真实主导航与专区，测试结束等待全部异步任务。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public NoIo Services { get; } = new();
        public AddonService Addons { get; }
        public InstancesViewModel Instances { get; }
        public MainViewModel Main { get; }
        public GtnhHubViewModel Hub => Instances.Gtnh!;
        public Harness(IOperationNotifications? notifications = null, IReadOnlyList<GtnhAddon>? catalog = null, IGtnhPackService? packs = null)
        {
            Addons = new(catalog);
            Instances = new(Services, Services, Services, Services, "C:\\hub-test", Services, Services,
                migration: Services, gtnhAddons: Addons, notifications: notifications, gtnhPacks: packs);
            foreach (var name in new[] { "old", "new", "GTNH-looking-vanilla" })
                Instances.Instances.Add(new(name, "C:\\hub-test\\" + name, "C:\\hub-test\\" + name + "\\.minecraft", ["1.7.10"]));
            Instances.SelectedInstance = Instances.Instances[0];
            Main = new(new JavaSettingsViewModel(Services), Instances);
        }
        public async Task SelectAddonAsync()
        {
            Main.SelectedPage = 4; await Hub.TargetLoadTask;
            Hub.SelectedAddon = Assert.Single(Hub.Addons); await Hub.ReleasesLoadTask;
        }
        public async ValueTask DisposeAsync() { await Hub.StopAsync(); Main.Dispose(); }
    }

    /// <summary>本体目录只返回内存版本，禁止测试误下载或读取真实实例。</summary>
    private sealed class PackService : IGtnhPackService
    {
        public int Reads { get; private set; }
        /// <summary>提供不依赖已有实例的稳定版客户端。</summary>
        public Task<IReadOnlyList<GtnhPackRelease>> GetReleasesAsync(bool includePrereleases = false, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<GtnhPackRelease>>([new()
            {
                Id = "GT_New_Horizons_2.9.0_Java_17-26.zip", Version = "2.9.0", JavaVersion = "17-26",
                FileName = "GT_New_Horizons_2.9.0_Java_17-26.zip", Size = 747848389
            }]);
        }
        /// <summary>此用例只检查页面入口和互斥，不能执行安装。</summary>
        public Task<PreparedInstanceArchive> PrepareAsync(string releaseId, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("不应实际下载整合包。");
    }

    /// <summary>保存 GTNH 操作通知，不创建窗口或操作剪贴板。</summary>
    private sealed class HubNoticeRecorder : IOperationNotifications
    {
        public List<OperationNotice> Items { get; } = [];
        /// <summary>记录通知用于核对等级、数量和可复制路径。</summary>
        public void Report(OperationNotice notice) => Items.Add(notice);
    }

    /// <summary>发行列表和下载均可被测试暂停，验证取消、乱序与固定目标。</summary>
    private sealed class AddonService : IGtnhAddonService
    {
        public IReadOnlyList<GtnhAddon> Addons { get; }
        /// <summary>允许筛选用例提供多种目录项，默认保持单项目安装测试的行为。</summary>
        public AddonService(IReadOnlyList<GtnhAddon>? catalog = null) => Addons = catalog ??
            [new("test", "测试推荐", "用途", "工具", "GTNewHorizons/Test", "核对发行说明")];
        public Func<string, Task<IReadOnlyList<GtnhAddonRelease>>>? Read { get; set; }
        public Func<CancellationToken, Task<GtnhAddonInstallResult>>? Install { get; set; }
        public int Reads { get; private set; }
        public int Installs { get; private set; }
        public string? InstalledPath { get; private set; }
        public CancellationToken InstallToken { get; private set; }
        public Task<IReadOnlyList<GtnhAddonRelease>> GetReleasesAsync(string instancePath, string addonId, CancellationToken token = default)
        { Reads++; return Read?.Invoke(instancePath) ?? Task.FromResult<IReadOnlyList<GtnhAddonRelease>>([Release("test")]); }
        public Task<GtnhAddonInstallResult> InstallAsync(string instancePath, GtnhAddonRelease release, bool compatibilityConfirmed,
            IProgress<FileDownloadProgress>? progress = null, CancellationToken token = default)
        {
            Installs++; InstalledPath = instancePath; InstallToken = token;
            return Install?.Invoke(token) ?? Task.FromResult(new GtnhAddonInstallResult(true, false, "安装完成"));
        }
    }

    /// <summary>仅模拟识别，不允许本专区测试触发其他实例写入操作。</summary>
    private sealed class NoIo : IJavaLocator, IMinecraftInstanceLocator, IMinecraftInstanceManager, IMinecraftInstanceImporter,
        IInstanceDialogs, IMinecraftInstanceChecker, IMinecraftInstanceDownloader, IMinecraftInstanceMigrationService
    {
        public bool HasRecovery { get; set; }
        public IReadOnlyList<MinecraftInstance> LocatedInstances { get; set; } = [];
        public Func<string, CancellationToken, Task<GtnhInstanceInfo?>>? InspectGtnh { get; set; }
        public Func<string?>? ChooseArchive { get; set; }
        public Func<InstanceImportPreview, string?>? ConfirmArchive { get; set; }
        public Func<CancellationToken, Task<InstanceImportPreview>>? InspectArchive { get; set; }
        public Func<CancellationToken, Task<InstanceImportResult>>? ImportArchive { get; set; }
        public Func<string, CancellationToken, Task<InstanceDownloadReport>>? Download { get; set; }
        public int ArchiveChoices { get; private set; }
        public int Imports { get; private set; }
        public List<string> DownloadPaths { get; } = [];
        Task<IReadOnlyList<JavaInstallation>> IJavaLocator.FindAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JavaInstallation>>([]);
        Task<IReadOnlyList<MinecraftInstance>> IMinecraftInstanceLocator.FindAllAsync(CancellationToken cancellationToken) => Task.FromResult(LocatedInstances);
        Task<GtnhInstanceInfo?> IMinecraftInstanceMigrationService.InspectAsync(string path, CancellationToken token) =>
            InspectGtnh?.Invoke(path, token) ?? Task.FromResult<GtnhInstanceInfo?>(path.EndsWith("vanilla") ? null : new(path, Path.GetFileName(path), path + "\\.minecraft", "2.8.4"));
        public Task<MigrationRecoveryInfo?> GetRecoveryAsync(string instancePath, CancellationToken token = default) =>
            Task.FromResult<MigrationRecoveryInfo?>(HasRecovery && instancePath.EndsWith("vanilla") ? new("test", "Pending", DateTimeOffset.UtcNow) : null);
        public Task<IReadOnlyList<MigrationGroup>> GetGroupsAsync(string sourceInstance, GtnhMigrationMode mode, CancellationToken token = default) => throw new NotSupportedException();
        public Task<InstanceMigrationPlan> PreviewAsync(InstanceMigrationRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<InstanceMigrationResult> MigrateAsync(InstanceMigrationPlan plan, bool overwriteConflicts, IProgress<InstanceMigrationProgress>? progress = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<InstanceMigrationResult> RestoreAsync(string instancePath, string migrationId, IProgress<InstanceMigrationProgress>? progress = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task DeleteAsync(string instancePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RenameAsync(string instancePath, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportAsync(string instancePath, string destinationZip, IProgress<InstanceExportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstanceImportPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default) =>
            InspectArchive?.Invoke(cancellationToken) ?? throw new NotSupportedException();
        /// <summary>导入用例显式注入内存结果，其他用例仍拒绝意外写入。</summary>
        public Task<InstanceImportResult> ImportAsync(string archivePath, string instanceName, IProgress<InstanceImportProgress>? progress = null, CancellationToken cancellationToken = default)
        { Imports++; return ImportArchive?.Invoke(cancellationToken) ?? throw new NotSupportedException(); }
        public bool ConfirmDelete(MinecraftInstance instance) => throw new NotSupportedException();
        public string? ChooseExportPath(MinecraftInstance instance) => throw new NotSupportedException();
        public string? ChooseImportPath() { ArchiveChoices++; return ChooseArchive is { } choose ? choose() : throw new NotSupportedException(); }
        public string? ConfirmImport(InstanceImportPreview preview) => ConfirmArchive is { } confirm ? confirm(preview) : throw new NotSupportedException();
        public Task<InstanceCheckReport> CheckAsync(string instancePath, InstanceCheckMode mode = InstanceCheckMode.Full, IProgress<InstanceCheckProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>记录补全目标，证明旧实例入口沿用已有自动补全流程。</summary>
        public Task<InstanceDownloadReport> DownloadMissingAsync(string instancePath, IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        { DownloadPaths.Add(instancePath); return Download?.Invoke(instancePath, cancellationToken) ?? throw new NotSupportedException(); }
    }
}
