using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Java;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Accounts;
using EML.Core.Minecraft.Launching;

namespace EML.Tests;

/// <summary>通过生产启动命令验证设置隔离和进程生命周期，所有依赖使用内存替身。</summary>
public sealed class InstancesViewModelLaunchingTests
{
    /// <summary>选中实例后显示默认离线名称、内存和自动 Java，不选实例时不能启动。</summary>
    [Fact]
    public async Task SelectionLoadsDefaultsAndEnablesLaunch()
    {
        var fixture = new Fixture();
        Assert.False(fixture.ViewModel.LaunchGameCommand.CanExecute(null));

        await fixture.SelectAsync();

        Assert.Equal("Steve", fixture.ViewModel.PlayerName);
        Assert.Equal("1024", fixture.ViewModel.MinMemoryMbText);
        Assert.Equal("8192", fixture.ViewModel.MaxMemoryMbText);
        Assert.Equal("", fixture.ViewModel.SelectedJavaPath);
        Assert.True(fixture.ViewModel.CanEditLaunchSettings);
        Assert.True(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.SaveLaunchSettingsCommand.CanExecute(null));
        Assert.Empty(fixture.Notifications.Notices);
    }

    /// <summary>启动界面用两个明确模式替代隐含复选框；正版模式锁定离线名称输入。</summary>
    [Fact]
    public async Task LaunchModeSwitchesBetweenOfflineAndMicrosoftIdentity()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();

        Assert.Equal(["离线模式", "正版模式"], fixture.ViewModel.LaunchModes);
        Assert.Equal("离线模式", fixture.ViewModel.SelectedLaunchMode);
        Assert.False(fixture.ViewModel.UseMicrosoftAccount);
        Assert.True(fixture.ViewModel.CanEditOfflinePlayerName);

        fixture.ViewModel.SelectedLaunchMode = "正版模式";

        Assert.True(fixture.ViewModel.UseMicrosoftAccount);
        Assert.Equal("正版模式", fixture.ViewModel.SelectedLaunchMode);
        Assert.False(fixture.ViewModel.CanEditOfflinePlayerName);

        fixture.ViewModel.SelectedLaunchMode = "离线模式";

        Assert.False(fixture.ViewModel.UseMicrosoftAccount);
        Assert.True(fixture.ViewModel.CanEditOfflinePlayerName);
    }

    /// <summary>兼容已保存的布尔模式，两个入口选择后都把本次身份直接交给启动服务。</summary>
    [Theory]
    [InlineData(true, "正版模式")]
    [InlineData(false, "离线模式")]
    public async Task ModeSelectionLoadsSavesAndLaunchesWithoutChangingOfflineName(bool microsoft, string mode)
    {
        var fixture = new Fixture();
        fixture.Store.Load = (_, _) => Task.FromResult(new MinecraftLaunchSettings
            { UseMicrosoftAccount = microsoft, PlayerName = "MyOfflineName" });
        await fixture.SelectAsync();
        Assert.Equal(mode, fixture.ViewModel.SelectedLaunchMode);
        fixture.ViewModel.SelectedLaunchMode = "正版模式";
        fixture.ViewModel.SelectedLaunchMode = "离线模式";
        Assert.Equal("MyOfflineName", fixture.ViewModel.PlayerName);
        fixture.ViewModel.SelectedLaunchMode = mode;

        await fixture.ViewModel.SaveLaunchSettingsCommand.ExecuteAsync(null);
        await StartLaunch(fixture.ViewModel);

        Assert.Equal(microsoft, Assert.Single(fixture.Store.Saved).Settings.UseMicrosoftAccount);
        var launched = Assert.Single(fixture.Launcher.Calls).Settings;
        Assert.Equal(microsoft, launched.UseMicrosoftAccount);
        Assert.Equal("MyOfflineName", launched.PlayerName);
    }

    /// <summary>绑定清空或异常值不能把已经选中的正版模式改成离线。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("未知")]
    public async Task UnknownModeDoesNotChangeIdentity(string? selection)
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.ViewModel.SelectedLaunchMode = "正版模式";
        fixture.ViewModel.SelectedLaunchMode = selection!;
        Assert.True(fixture.ViewModel.UseMicrosoftAccount);
    }

    /// <summary>确认下载后只替换 Java 路径，重试仍使用点击启动时的身份、内存和 JVM 参数。</summary>
    [Fact]
    public async Task MissingJavaDownloadsAfterConsentAndRetriesFrozenSettings()
    {
        var runtimes = new RuntimeService();
        var dialogs = new JavaDialogs();
        var java = new JavaSettingsViewModel(new JavaLocator());
        var fixture = new Fixture(javaRuntimes: runtimes, javaDialogs: dialogs, java: java);
        await fixture.SelectAsync();
        fixture.ViewModel.PlayerName = "OriginalName";
        fixture.ViewModel.UseMicrosoftAccount = true;
        fixture.ViewModel.MaxMemoryMbText = "6144";
        fixture.ViewModel.AdditionalJvmArguments = "-Dtest=value";
        fixture.Launcher.Run = (_, _) => fixture.Launcher.Calls.Count == 1
            ? Task.FromException<MinecraftLaunchResult>(MissingJava())
            : Task.FromResult(new MinecraftLaunchResult(true, 0, false, null));
        dialogs.OnConfirm = () =>
        {
            fixture.ViewModel.PlayerName = "ChangedLater";
            fixture.ViewModel.UseMicrosoftAccount = false;
        };

        await StartLaunch(fixture.ViewModel);

        Assert.Single(dialogs.Calls);
        Assert.Single(runtimes.Queries);
        Assert.Single(runtimes.Installs);
        Assert.Equal(2, fixture.Launcher.Calls.Count);
        var second = fixture.Launcher.Calls[1];
        Assert.Equal(fixture.Instance.InstancePath, second.Path);
        Assert.Equal(fixture.Launcher.Calls[0].Settings with { JavaPath = runtimes.Installation.ExecutablePath }, second.Settings);
        Assert.True(second.Settings.UseMicrosoftAccount);
        Assert.Equal("OriginalName", second.Settings.PlayerName);
        Assert.Equal(runtimes.Installation.ExecutablePath, fixture.ViewModel.SelectedJavaPath);
        Assert.Equal(runtimes.Installation, Assert.Single(java.Installations));
        Assert.Empty(fixture.Store.Saved);
        Assert.Contains(fixture.Notifications.Notices, notice => notice.Title == "Java 安装完成");
        Assert.False(fixture.ViewModel.IsBusy);
    }

    /// <summary>取消确认不会查询网络或安装环境，也不会再次启动。</summary>
    [Fact]
    public async Task DecliningJavaDownloadDoesNotQueryOrRetry()
    {
        var runtimes = new RuntimeService();
        var dialogs = new JavaDialogs { Accept = false };
        var fixture = new Fixture(javaRuntimes: runtimes, javaDialogs: dialogs);
        await fixture.SelectAsync();
        fixture.Launcher.Run = (_, _) => Task.FromException<MinecraftLaunchResult>(MissingJava());

        await StartLaunch(fixture.ViewModel);

        Assert.Single(dialogs.Calls);
        Assert.Single(fixture.Launcher.Calls);
        Assert.Empty(runtimes.Queries);
        Assert.Empty(runtimes.Installs);
        Assert.Equal(OperationNoticeKind.Cancelled, Assert.Single(fixture.Notifications.Notices).Kind);
    }

    /// <summary>下载失败或网络查询失败保留原因，不用原本不兼容的 Java 偷偷重试。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JavaAcquisitionFailureDoesNotRetry(bool queryFails)
    {
        var runtimes = new RuntimeService();
        if (queryFails) runtimes.Query = (_, _) => throw new IOException("查询网络失败");
        else runtimes.Install = _ => throw new IOException("Java 下载失败");
        var fixture = new Fixture(javaRuntimes: runtimes, javaDialogs: new JavaDialogs());
        await fixture.SelectAsync();
        fixture.Launcher.Run = (_, _) => Task.FromException<MinecraftLaunchResult>(MissingJava());

        await StartLaunch(fixture.ViewModel);

        Assert.Single(fixture.Launcher.Calls);
        Assert.Contains(queryFails ? "查询网络失败" : "Java 下载失败", fixture.ViewModel.LaunchStatus);
        Assert.Equal("", fixture.ViewModel.SelectedJavaPath);
        Assert.Empty(fixture.Store.Saved);
    }

    /// <summary>第二次仍缺 Java 时终止，避免重复弹窗和无限下载。</summary>
    [Fact]
    public async Task JavaRecoveryRetriesAtMostOnce()
    {
        var runtimes = new RuntimeService();
        var dialogs = new JavaDialogs();
        var fixture = new Fixture(javaRuntimes: runtimes, javaDialogs: dialogs);
        await fixture.SelectAsync();
        fixture.Launcher.Run = (_, _) => Task.FromException<MinecraftLaunchResult>(MissingJava());

        await StartLaunch(fixture.ViewModel);

        Assert.Equal(2, fixture.Launcher.Calls.Count);
        Assert.Single(dialogs.Calls);
        Assert.Single(runtimes.Installs);
        Assert.Contains("失败", fixture.ViewModel.LaunchStatus);
    }

    /// <summary>认证和实例文件错误不触发 Java 下载，已经运行后的异常也不重复创建游戏进程。</summary>
    [Theory]
    [InlineData("authentication")]
    [InlineData("file")]
    [InlineData("running")]
    public async Task NonJavaFailureNeverOffersDownload(string failure)
    {
        var runtimes = new RuntimeService();
        var dialogs = new JavaDialogs();
        var fixture = new Fixture(javaRuntimes: runtimes, javaDialogs: dialogs);
        await fixture.SelectAsync();
        fixture.Launcher.Run = (progress, _) =>
        {
            if (failure == "running") progress!.Report(new(MinecraftLaunchStage.Running, "running"));
            Exception error = failure switch
            {
                "authentication" => new MinecraftAuthenticationException("会话失效"),
                "file" => new FileNotFoundException("缺少游戏类库"),
                _ => MissingJava()
            };
            return Task.FromException<MinecraftLaunchResult>(error);
        };

        await StartLaunch(fixture.ViewModel);

        Assert.Single(fixture.Launcher.Calls);
        Assert.Empty(dialogs.Calls);
        Assert.Empty(runtimes.Queries);
    }

    /// <summary>下载中的取消沿用启动令牌，完成前保持忙碌且不会创建第二次进程。</summary>
    [Fact]
    public async Task CancelDuringJavaInstallStopsLaunchRecovery()
    {
        var entered = NewSignal<bool>();
        var runtimes = new RuntimeService
        {
            Install = async token =>
            {
                entered.SetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("不会执行");
            }
        };
        var fixture = new Fixture(javaRuntimes: runtimes, javaDialogs: new JavaDialogs());
        await fixture.SelectAsync();
        fixture.Launcher.Run = (_, _) => Task.FromException<MinecraftLaunchResult>(MissingJava());

        var operation = StartLaunch(fixture.ViewModel);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.ViewModel.IsBusy);
        fixture.ViewModel.CancelLaunchPreparationCommand.Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(fixture.Launcher.Calls);
        Assert.Contains("取消", fixture.ViewModel.LaunchStatus);
        Assert.Equal("", fixture.ViewModel.SelectedJavaPath);
        Assert.False(fixture.ViewModel.IsBusy);
    }

    /// <summary>官方无更高版本包时，只在实例声明兼容的版本内继续查找，不猜测其他 Java。</summary>
    [Fact]
    public async Task RecoveryUsesOnlyDeclaredCompatibleVersions()
    {
        var runtimes = new RuntimeService { Query = (major, _) => major == 25 ? [] : [Package(major)] };
        var fixture = new Fixture(javaRuntimes: runtimes, javaDialogs: new JavaDialogs());
        await fixture.SelectAsync();
        fixture.Launcher.Run = (_, _) => fixture.Launcher.Calls.Count == 1
            ? Task.FromException<MinecraftLaunchResult>(new JavaRuntimeRequiredException("缺少 Java", [17, 25], "x64"))
            : Task.FromResult(new MinecraftLaunchResult(true, 0, false, null));

        await StartLaunch(fixture.ViewModel);

        Assert.Equal([25, 17], runtimes.Queries.Select(query => query.Major));
        Assert.Equal(17, Assert.Single(runtimes.Installs).MajorVersion);
    }

    /// <summary>未提供对应官方包时说明手动安装，不下载不兼容版本。</summary>
    [Fact]
    public async Task NoCompatibleOfficialPackageDoesNotInstallOrRetry()
    {
        var runtimes = new RuntimeService { Query = (_, _) => [] };
        var fixture = new Fixture(javaRuntimes: runtimes, javaDialogs: new JavaDialogs());
        await fixture.SelectAsync();
        fixture.Launcher.Run = (_, _) => Task.FromException<MinecraftLaunchResult>(MissingJava());

        await StartLaunch(fixture.ViewModel);

        Assert.Single(fixture.Launcher.Calls);
        Assert.Empty(runtimes.Installs);
        Assert.Contains("官方暂无", fixture.ViewModel.LaunchStatus);
    }

    /// <summary>未支持的版本或架构只提示手动处理，不询问下载另一套不兼容 Java。</summary>
    [Theory]
    [InlineData(26, "x64")]
    [InlineData(25, "unknown")]
    public async Task UnsupportedJavaRequirementDoesNotOfferWrongDownload(int major, string architecture)
    {
        var runtimes = new RuntimeService();
        var dialogs = new JavaDialogs();
        var fixture = new Fixture(javaRuntimes: runtimes, javaDialogs: dialogs);
        await fixture.SelectAsync();
        fixture.Launcher.Run = (_, _) => Task.FromException<MinecraftLaunchResult>(
            new JavaRuntimeRequiredException("缺少 Java", [major], architecture));

        await StartLaunch(fixture.ViewModel);

        Assert.Single(fixture.Launcher.Calls);
        Assert.Empty(dialogs.Calls);
        Assert.Empty(runtimes.Queries);
        Assert.Contains("手动安装", fixture.ViewModel.LaunchStatus);
    }

    /// <summary>安装元数据返回错误版本或架构时不采用该包，也不尝试启动。</summary>
    [Theory]
    [InlineData(17, "x64")]
    [InlineData(25, "x86")]
    public async Task MismatchedPackageMetadataIsNotInstalled(int major, string architecture)
    {
        var runtimes = new RuntimeService { Query = (_, _) => [Package(major) with { Architecture = architecture }] };
        var fixture = new Fixture(javaRuntimes: runtimes, javaDialogs: new JavaDialogs());
        await fixture.SelectAsync();
        fixture.Launcher.Run = (_, _) => Task.FromException<MinecraftLaunchResult>(MissingJava());

        await StartLaunch(fixture.ViewModel);

        Assert.Empty(runtimes.Installs);
        Assert.Single(fixture.Launcher.Calls);
    }

    /// <summary>为启动恢复测试构造结构化的 Java 需求。</summary>
    private static JavaRuntimeRequiredException MissingJava() => new("缺少兼容 Java", [25], "x64");

    /// <summary>模拟官方校验元数据，不发起实际下载。</summary>
    private static JavaRuntimePackage Package(int major) => new(major, major + ".0.1", "x64", "java.zip",
        "https://example.invalid/java.zip", new string('a', 64), 100);

    /// <summary>内存安装服务用于记录查询、下载和取消，绝不启动真实可执行文件。</summary>
    private sealed class RuntimeService : IJavaRuntimeService
    {
        public string InstallationRoot => @"C:\Test root\runtimes\java";
        public JavaInstallation Installation { get; } = new(@"C:\Test root\runtimes\java\jdk-25\bin\java.exe", "25.0.1", "amd64");
        public List<(int Major, string Architecture)> Queries { get; } = [];
        public List<JavaRuntimePackage> Installs { get; } = [];
        public Func<int, string, IReadOnlyList<JavaRuntimePackage>> Query { get; set; } = (major, _) => [Package(major)];
        public Func<CancellationToken, Task<JavaInstallation>>? Install { get; set; }
        /// <summary>记录请求的主版本与架构，允许测试模拟无包或网络错误。</summary>
        public Task<IReadOnlyList<JavaRuntimePackage>> GetPackagesAsync(int majorVersion, string architecture, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add((majorVersion, architecture));
            return Task.FromResult(Query(majorVersion, architecture));
        }
        /// <summary>返回内存安装结果，或由测试控制取消与错误。</summary>
        public Task<JavaInstallation> InstallAsync(JavaRuntimePackage package, IProgress<JavaRuntimeInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Installs.Add(package);
            progress?.Report(new("正在解压", 75));
            return Install?.Invoke(cancellationToken) ?? Task.FromResult(Installation with
            {
                ExecutablePath = Installation.ExecutablePath.Replace("jdk-25", "jdk-" + package.MajorVersion, StringComparison.Ordinal),
                Version = package.Version
            });
        }
    }

    /// <summary>记录一次用户确认，不创建 WPF 对话框。</summary>
    private sealed class JavaDialogs : IJavaDownloadDialogs
    {
        public bool Accept { get; set; } = true;
        public Action? OnConfirm { get; set; }
        public List<(string Instance, JavaRuntimeRequiredException Requirement)> Calls { get; } = [];
        /// <summary>返回测试预设的同意或取消。</summary>
        public bool ConfirmDownload(string instanceName, JavaRuntimeRequiredException requirement, string installationRoot)
        {
            Calls.Add((instanceName, requirement));
            OnConfirm?.Invoke();
            return Accept;
        }
    }

    /// <summary>Java 页面注册测试不应调用整机扫描。</summary>
    private sealed class JavaLocator : IJavaLocator
    {
        /// <summary>误调用检测立即失败。</summary>
        public Task<IReadOnlyList<JavaInstallation>> FindAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>切换实例再返回时保留本次选择，磁盘中的旧离线偏好不能覆盖当前正版草稿。</summary>
    [Fact]
    public async Task ReturningToInstanceKeepsUnsavedIdentityAndText()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.ViewModel.SelectedLaunchMode = "正版模式";
        fixture.ViewModel.PlayerName = "OfflineDraft";
        fixture.ViewModel.MaxMemoryMbText = "尚未填完";
        var other = fixture.Instance with { Name = "Other", InstancePath = fixture.Instance.InstancePath + "2" };
        fixture.ViewModel.SelectedInstance = other;
        await fixture.ViewModel.LaunchSettingsLoadTask;
        Assert.False(fixture.ViewModel.UseMicrosoftAccount);
        Assert.Equal("Steve", fixture.ViewModel.PlayerName);

        await fixture.SelectAsync();

        Assert.True(fixture.ViewModel.UseMicrosoftAccount);
        Assert.Equal("OfflineDraft", fixture.ViewModel.PlayerName);
        Assert.Equal("尚未填完", fixture.ViewModel.MaxMemoryMbText);
        Assert.Empty(fixture.Store.Saved);
        fixture.ViewModel.MaxMemoryMbText = "8192";
        await StartLaunch(fixture.ViewModel);
        Assert.True(Assert.Single(fixture.Launcher.Calls).Settings.UseMicrosoftAccount);
    }

    /// <summary>列表刷新时会先清空选择再生成对象，同路径新对象仍使用当前模式。</summary>
    [Fact]
    public async Task RescannedInstanceKeepsDraftUntilExplicitReload()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.ViewModel.SelectedLaunchMode = "正版模式";
        fixture.ViewModel.SelectedInstance = null;
        fixture.ViewModel.SelectedInstance = fixture.Instance with { Name = "Refreshed" };
        await fixture.ViewModel.LaunchSettingsLoadTask;
        Assert.True(fixture.ViewModel.UseMicrosoftAccount);

        await fixture.ViewModel.ReloadLaunchSettingsCommand.ExecuteAsync(null);

        Assert.False(fixture.ViewModel.UseMicrosoftAccount);
        await StartLaunch(fixture.ViewModel);
        Assert.False(Assert.Single(fixture.Launcher.Calls).Settings.UseMicrosoftAccount);
    }

    /// <summary>启动先冻结本次选择，忙碌状态通知不能改变实际送入启动器的身份。</summary>
    [Fact]
    public async Task LaunchCapturesIdentityBeforeBusyNotifications()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.ViewModel.SelectedLaunchMode = "正版模式";
        fixture.ViewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(InstancesViewModel.IsBusy) && fixture.ViewModel.IsBusy)
                fixture.ViewModel.UseMicrosoftAccount = false;
        };

        await StartLaunch(fixture.ViewModel);

        Assert.True(Assert.Single(fixture.Launcher.Calls).Settings.UseMicrosoftAccount);
    }

    /// <summary>删除成功即丢弃草稿，即使随后刷新失败，同名新实例也要读取自己的设置。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedInstanceDoesNotPassDraftToReusedPath(bool scanFails)
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.ViewModel.SelectedLaunchMode = "正版模式";
        fixture.Services.ConfirmRemoval = () => true;
        fixture.Services.Remove = () => Task.CompletedTask;
        fixture.Services.Find = () => scanFails
            ? Task.FromException<IReadOnlyList<MinecraftInstance>>(new IOException("扫描失败"))
            : Task.FromResult<IReadOnlyList<MinecraftInstance>>([]);

        await fixture.ViewModel.DeleteCommand.ExecuteAsync(null);
        fixture.ViewModel.SelectedInstance = null;
        fixture.Store.Load = (_, _) => Task.FromResult(new MinecraftLaunchSettings { PlayerName = "NewInstance" });
        await fixture.SelectAsync();

        Assert.False(fixture.ViewModel.UseMicrosoftAccount);
        Assert.Equal("NewInstance", fixture.ViewModel.PlayerName);
    }

    /// <summary>扫描确认实例已被外部删除时，也必须清除其草稿以支持同名重新导入。</summary>
    [Fact]
    public async Task MissingInstanceDoesNotPassDraftToReusedPath()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.ViewModel.SelectedLaunchMode = "正版模式";
        fixture.Services.Find = () => Task.FromResult<IReadOnlyList<MinecraftInstance>>([]);

        await fixture.ViewModel.RefreshCommand.ExecuteAsync(null);
        await fixture.SelectAsync();

        Assert.False(fixture.ViewModel.UseMicrosoftAccount);
    }

    /// <summary>改名后保留当前实例的草稿，但旧目录名后来被重用时不能继承该草稿。</summary>
    [Fact]
    public async Task RenamedInstanceKeepsDraftAtNewPathOnly()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.ViewModel.SelectedLaunchMode = "正版模式";
        fixture.ViewModel.PlayerName = "OriginalDraft";
        var renamed = fixture.Instance with { Name = "Renamed", InstancePath = fixture.Instance.InstancePath + " renamed" };
        fixture.Services.Rename = () => Task.FromResult(renamed.InstancePath);
        fixture.Services.Find = () => Task.FromResult<IReadOnlyList<MinecraftInstance>>([renamed]);

        await fixture.ViewModel.RenameCommand.ExecuteAsync(null);
        await fixture.ViewModel.LaunchSettingsLoadTask;
        Assert.Equal(renamed.InstancePath, fixture.ViewModel.SelectedInstance!.InstancePath);
        Assert.True(fixture.ViewModel.UseMicrosoftAccount);
        Assert.Equal("OriginalDraft", fixture.ViewModel.PlayerName);
        await fixture.SelectAsync();
        Assert.False(fixture.ViewModel.UseMicrosoftAccount);
        Assert.Equal("Steve", fixture.ViewModel.PlayerName);
    }

    /// <summary>展示共享账号档案；登录、换号、退出无需再次选实例，也不请求或复制游戏令牌。</summary>
    [Fact]
    public async Task AccountDisplayFollowsLoginAndLogoutWhileKeepingOfflineName()
    {
        var service = new AccountDisplayService();
        var accounts = new MinecraftAccountViewModel(service);
        var fixture = new Fixture(accounts);
        await fixture.SelectAsync();
        fixture.ViewModel.PlayerName = "OfflineName";
        fixture.ViewModel.SelectedLaunchMode = "正版模式";
        Assert.Same(accounts, fixture.ViewModel.Accounts);
        Assert.Equal("尚未登录", fixture.ViewModel.Accounts!.AccountName);

        await accounts.LoginCommand.ExecuteAsync(null);
        Assert.Equal("OnlineAlex", fixture.ViewModel.Accounts.AccountName);
        service.NextName = "AnotherPlayer";
        await accounts.LoginCommand.ExecuteAsync(null);
        Assert.Equal("AnotherPlayer", fixture.ViewModel.Accounts.AccountName);
        await accounts.LogoutCommand.ExecuteAsync(null);
        Assert.Equal("尚未登录", fixture.ViewModel.Accounts.AccountName);
        Assert.True(fixture.ViewModel.UseMicrosoftAccount);
        Assert.Equal("OfflineName", fixture.ViewModel.PlayerName);
    }

    /// <summary>读取尚未结束时，即使直接执行命令也不能保存或使用上一实例的设置启动。</summary>
    [Fact]
    public async Task LoadingSettingsBlocksLaunchAndSave()
    {
        var fixture = new Fixture();
        var pending = NewSignal<MinecraftLaunchSettings>();
        fixture.Store.Load = (_, _) => pending.Task;
        fixture.ViewModel.SelectedInstance = fixture.Instance;

        Assert.True(fixture.ViewModel.IsLaunchSettingsLoading);
        Assert.False(fixture.ViewModel.CanEditLaunchSettings);
        Assert.False(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
        await StartLaunch(fixture.ViewModel);
        await fixture.ViewModel.SaveLaunchSettingsCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Launcher.Calls);
        Assert.Empty(fixture.Store.Saved);

        pending.SetResult(new MinecraftLaunchSettings { PlayerName = "Loaded" });
        await fixture.ViewModel.LaunchSettingsLoadTask;
        Assert.Equal("Loaded", fixture.ViewModel.PlayerName);
        Assert.True(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
    }

    /// <summary>快速切换实例时，忽略取消的旧读取即使迟到返回也不能覆盖新设置。</summary>
    [Fact]
    public async Task LateSettingsFromPreviousSelectionCannotOverwriteCurrentInput()
    {
        var fixture = new Fixture();
        var first = NewSignal<MinecraftLaunchSettings>();
        var second = NewSignal<MinecraftLaunchSettings>();
        var other = fixture.Instance with { Name = "Other", InstancePath = fixture.Instance.InstancePath + "2" };
        fixture.Store.Load = (path, _) => path == fixture.Instance.InstancePath ? first.Task : second.Task;
        fixture.ViewModel.SelectedInstance = fixture.Instance;
        var oldLoad = fixture.ViewModel.LaunchSettingsLoadTask;
        fixture.ViewModel.SelectedInstance = other;
        second.SetResult(new MinecraftLaunchSettings { PlayerName = "OtherPlayer", MaxMemoryMb = 4096 });
        await fixture.ViewModel.LaunchSettingsLoadTask;
        fixture.ViewModel.PlayerName = "UserEditing";

        first.SetResult(new MinecraftLaunchSettings { PlayerName = "StalePlayer" });
        await oldLoad;
        await fixture.ViewModel.SaveLaunchSettingsCommand.ExecuteAsync(null);

        Assert.Equal("UserEditing", fixture.ViewModel.PlayerName);
        Assert.Equal("4096", fixture.ViewModel.MaxMemoryMbText);
        Assert.Equal(other.InstancePath, Assert.Single(fixture.Store.Saved).Path);
    }

    /// <summary>清空选择会让尚未结束的读取失效，不重新启用已经没有目标的启动按钮。</summary>
    [Fact]
    public async Task ClearingSelectionInvalidatesPendingSettings()
    {
        var fixture = new Fixture();
        var pending = NewSignal<MinecraftLaunchSettings>();
        fixture.Store.Load = (_, _) => pending.Task;
        fixture.ViewModel.SelectedInstance = fixture.Instance;
        var load = fixture.ViewModel.LaunchSettingsLoadTask;
        fixture.ViewModel.SelectedInstance = null;
        pending.SetResult(new MinecraftLaunchSettings { PlayerName = "OldPlayer" });
        await load;

        Assert.False(fixture.ViewModel.IsLaunchSettingsLoaded);
        Assert.False(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
        Assert.Equal("Steve", fixture.ViewModel.PlayerName);
    }

    /// <summary>读取失败明确显示原因并阻止覆盖，修复后用户可以显式重读。</summary>
    [Fact]
    public async Task ReadFailureBlocksLaunchUntilSuccessfulReload()
    {
        var fixture = new Fixture();
        fixture.Store.Load = (_, _) => Task.FromException<MinecraftLaunchSettings>(new IOException("设置无法读取"));
        await fixture.SelectAsync();

        Assert.Contains("设置无法读取", fixture.ViewModel.LaunchSettingsStatus);
        var failedNotice = Assert.Single(fixture.Notifications.Notices);
        Assert.Equal(OperationNoticeKind.Failure, failedNotice.Kind);
        Assert.Contains(fixture.Instance.InstancePath, failedNotice.Paths);
        Assert.False(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.SaveLaunchSettingsCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.ReloadLaunchSettingsCommand.CanExecute(null));

        fixture.Store.Load = (_, _) => Task.FromResult(new MinecraftLaunchSettings { PlayerName = "Recovered" });
        await fixture.ViewModel.ReloadLaunchSettingsCommand.ExecuteAsync(null);
        Assert.Equal("Recovered", fixture.ViewModel.PlayerName);
        Assert.True(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
        Assert.Equal(OperationNoticeKind.Success, fixture.Notifications.Notices[^1].Kind);
    }

    /// <summary>界面将本次实例和用户输入作为独立设置传入启动服务，不依赖上次保存。</summary>
    [Fact]
    public async Task LaunchPassesPlayerJavaMemoryAndJvmArguments()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.ViewModel.PlayerName = "Custom_Player";
        fixture.ViewModel.SelectedJavaPath = "C:\\Java 21\\bin\\java.exe";
        fixture.ViewModel.MinMemoryMbText = "2048";
        fixture.ViewModel.MaxMemoryMbText = "6144";
        fixture.ViewModel.AdditionalJvmArguments = "-XX:+UseG1GC -Dexample=\"hello world\"";

        await StartLaunch(fixture.ViewModel);

        var call = Assert.Single(fixture.Launcher.Calls);
        Assert.Equal(fixture.Instance.InstancePath, call.Path);
        Assert.Equal("Custom_Player", call.Settings.PlayerName);
        Assert.Equal("C:\\Java 21\\bin\\java.exe", call.Settings.JavaPath);
        Assert.Equal(2048, call.Settings.MinMemoryMb);
        Assert.Equal(6144, call.Settings.MaxMemoryMb);
        Assert.Equal(fixture.ViewModel.AdditionalJvmArguments, call.Settings.AdditionalJvmArguments);
        Assert.Empty(fixture.Store.Saved);
        Assert.Equal(4, fixture.ViewModel.SelectedDetailTab);
    }

    /// <summary>空 Java 文本传入 null，使各实例由共享启动服务选择匹配版本。</summary>
    [Fact]
    public async Task EmptyJavaPathRequestsAutomaticSelection()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.ViewModel.SelectedJavaPath = "  ";
        await StartLaunch(fixture.ViewModel);
        Assert.Null(Assert.Single(fixture.Launcher.Calls).Settings.JavaPath);
    }

    /// <summary>非法文本不会退回旧值或交给 Java，错误发生后仍可修正并重试。</summary>
    [Theory]
    [InlineData("memory-text")]
    [InlineData("memory-range")]
    [InlineData("name")]
    [InlineData("jvm")]
    public async Task InvalidSettingsDoNotStartOrSave(string kind)
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        switch (kind)
        {
            case "memory-text": fixture.ViewModel.MaxMemoryMbText = "8192abc"; break;
            case "memory-range": fixture.ViewModel.MinMemoryMbText = "16384"; break;
            case "name": fixture.ViewModel.PlayerName = "bad name!"; break;
            case "jvm": fixture.ViewModel.AdditionalJvmArguments = "-Xmx2G"; break;
        }

        await fixture.ViewModel.SaveLaunchSettingsCommand.ExecuteAsync(null);
        await StartLaunch(fixture.ViewModel);

        Assert.Empty(fixture.Store.Saved);
        Assert.Empty(fixture.Launcher.Calls);
        Assert.Contains("保存失败", fixture.ViewModel.LaunchSettingsStatus);
        Assert.Contains("失败", fixture.ViewModel.LaunchStatus);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.False(fixture.ViewModel.IsPreparingLaunch);
        Assert.True(fixture.ViewModel.CanEditLaunchSettings);
    }

    /// <summary>显式保存写入对应实例，失败时保留输入并恢复交互。</summary>
    [Fact]
    public async Task SavePersistsSelectionAndReportsFailureWithoutClearingInput()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.ViewModel.PlayerName = "SavedPlayer";
        await fixture.ViewModel.SaveLaunchSettingsCommand.ExecuteAsync(null);
        var saved = Assert.Single(fixture.Store.Saved);
        Assert.Equal(fixture.Instance.InstancePath, saved.Path);
        Assert.Equal("SavedPlayer", saved.Settings.PlayerName);
        fixture.Store.Save = (_, _, _) => Task.FromException(new IOException("磁盘写入失败"));

        await fixture.ViewModel.SaveLaunchSettingsCommand.ExecuteAsync(null);

        Assert.Contains("磁盘写入失败", fixture.ViewModel.LaunchSettingsStatus);
        Assert.Equal("SavedPlayer", fixture.ViewModel.PlayerName);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.Equal([OperationNoticeKind.Success, OperationNoticeKind.Failure], fixture.Notifications.Notices.Select(notice => notice.Kind));
        Assert.Contains("磁盘写入失败", fixture.Notifications.Notices[^1].Message);
        Assert.Contains(fixture.Instance.InstancePath, fixture.Notifications.Notices[^1].Paths);
    }

    /// <summary>从完整检查到进程结束持续占用界面，停止只请求取消并等待启动服务退出。</summary>
    [Fact]
    public async Task PreparingAndRunningBlockAllConflictingOperationsUntilStopped()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        var report = NewSignal<IProgress<MinecraftLaunchProgress>>();
        var canceled = NewSignal<bool>();
        var finish = NewSignal<MinecraftLaunchResult>();
        fixture.Launcher.Run = async (progress, token) =>
        {
            report.SetResult(progress!);
            using var registration = token.Register(() => canceled.TrySetResult(true));
            return await finish.Task;
        };
        var busy = new List<bool>();
        fixture.ViewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(InstancesViewModel.IsBusy)) busy.Add(fixture.ViewModel.IsBusy);
        };

        var operation = StartLaunch(fixture.ViewModel);
        var progress = await report.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.ViewModel.IsPreparingLaunch);
        Assert.True(fixture.ViewModel.CancelLaunchPreparationCommand.CanExecute(null));
        AssertConflictingCommandsDisabled(fixture.ViewModel);
        progress.Report(new(MinecraftLaunchStage.Running, "Java 已创建", LogPath: "current.log"));
        Assert.IsType<BoundedLaunchProgress>(progress).FlushPending();
        progress.Report(new(MinecraftLaunchStage.Running, "重复阶段事件", LogPath: "current.log"));
        Assert.IsType<BoundedLaunchProgress>(progress).FlushPending();
        var runningNotice = Assert.Single(fixture.Notifications.Notices);
        Assert.Equal(OperationNoticeKind.Success, runningNotice.Kind);
        Assert.Contains("请在游戏窗口确认加载结果", runningNotice.Message);
        Assert.True(fixture.ViewModel.IsGameRunning);
        Assert.False(fixture.ViewModel.IsPreparingLaunch);
        Assert.True(fixture.ViewModel.StopGameCommand.CanExecute(null));
        AssertConflictingCommandsDisabled(fixture.ViewModel);

        fixture.ViewModel.StopGameCommand.Execute(null);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(operation.IsCompleted);
        Assert.True(fixture.ViewModel.IsBusy);
        Assert.False(fixture.ViewModel.StopGameCommand.CanExecute(null));
        finish.SetResult(new(true, 143, true, "current.log"));
        await operation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([true, false], busy);
        Assert.False(fixture.ViewModel.IsGameRunning);
        Assert.Equal(143, fixture.ViewModel.LastExitCode);
        Assert.Contains("已停止", fixture.ViewModel.LaunchStatus);
        Assert.Equal("current.log", fixture.ViewModel.LaunchLogPath);
        Assert.True(fixture.ViewModel.RenameCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
        Assert.Equal(2, fixture.Notifications.Notices.Count);
        Assert.Equal(OperationNoticeKind.Cancelled, fixture.Notifications.Notices[^1].Kind);
    }

    /// <summary>准备阶段取消会传递到共享检查流程，最终不会保留运行状态。</summary>
    [Fact]
    public async Task CancelPreparationDoesNotReportRunningGame()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        var preparing = NewSignal<bool>();
        fixture.Launcher.Run = async (_, token) =>
        {
            preparing.SetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return new(false, null, true, null);
        };
        var operation = StartLaunch(fixture.ViewModel);
        await preparing.Task;
        fixture.ViewModel.CancelLaunchPreparationCommand.Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("取消启动准备", fixture.ViewModel.LaunchStatus);
        Assert.False(fixture.ViewModel.IsGameRunning);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.Null(fixture.ViewModel.LastExitCode);
        Assert.Equal(OperationNoticeKind.Cancelled, Assert.Single(fixture.Notifications.Notices).Kind);
    }

    /// <summary>检查或启动失败清楚报告原因，不把失败结果伪装成已进入游戏。</summary>
    [Fact]
    public async Task LauncherFailureRestoresUiAndPreservesReason()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.Launcher.Run = (_, _) => Task.FromException<MinecraftLaunchResult>(new IOException("必要运行库缺失"));

        await StartLaunch(fixture.ViewModel);

        Assert.Contains("必要运行库缺失", fixture.ViewModel.LaunchStatus);
        Assert.Contains(fixture.ViewModel.LaunchLogLines, line => line.Contains("必要运行库缺失"));
        Assert.False(fixture.ViewModel.IsGameRunning);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.True(fixture.ViewModel.LaunchGameCommand.CanExecute(null));
        var notice = Assert.Single(fixture.Notifications.Notices);
        Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
        Assert.Contains("必要运行库缺失", notice.Message);
        Assert.Contains(fixture.Instance.InstancePath, notice.Paths);
    }

    /// <summary>异常退出保留退出码与日志路径，不能用成功创建进程推断游戏加载成功。</summary>
    [Fact]
    public async Task NonzeroExitIsVisibleWithLogPath()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.Launcher.Run = (_, _) => Task.FromResult(new MinecraftLaunchResult(true, 1, false, "failure.log"));

        await StartLaunch(fixture.ViewModel);

        Assert.Equal(1, fixture.ViewModel.LastExitCode);
        Assert.Equal("failure.log", fixture.ViewModel.LaunchLogPath);
        Assert.Contains("退出码：1", fixture.ViewModel.LaunchStatus);
        var notice = Assert.Single(fixture.Notifications.Notices);
        Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
        Assert.Contains("failure.log", notice.Paths);
    }

    /// <summary>大量输出只留下最近一千行，任务结束后的排队事件也不能改变状态。</summary>
    [Fact]
    public async Task LogsAreBoundedAndLateProgressIsIgnored()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        var reported = NewSignal<IProgress<MinecraftLaunchProgress>>();
        var finish = NewSignal<MinecraftLaunchResult>();
        fixture.Launcher.Run = (progress, _) =>
        {
            reported.SetResult(progress!);
            return finish.Task;
        };
        var operation = StartLaunch(fixture.ViewModel);
        var progress = await reported.Task;
        progress.Report(new(MinecraftLaunchStage.Running, "running"));
        for (var i = 0; i < 1100; i++) progress.Report(new(MinecraftLaunchStage.Output, "line" + i));
        Assert.IsType<BoundedLaunchProgress>(progress).FlushPending();
        Assert.Equal(1000, fixture.ViewModel.LaunchLogLines.Count);
        Assert.Equal("line100", fixture.ViewModel.LaunchLogLines[0]);
        Assert.Equal("line1099", fixture.ViewModel.LaunchLogLines[^1]);
        finish.SetResult(new(true, 0, false, "complete.log"));
        await operation;
        var lastMessage = fixture.ViewModel.LaunchStatus;

        progress.Report(new(MinecraftLaunchStage.Running, "stale", LogPath: "wrong.log"));

        Assert.False(fixture.ViewModel.IsGameRunning);
        Assert.Equal(lastMessage, fixture.ViewModel.LaunchStatus);
        Assert.Equal("complete.log", fixture.ViewModel.LaunchLogPath);
        Assert.Equal(1000, fixture.ViewModel.LaunchLogLines.Count);
        Assert.DoesNotContain("stale", fixture.ViewModel.LaunchLogLines);
    }

    /// <summary>进程很快退出时，尚未来得及调度的最后日志也会显示，随后运行状态保持结束。</summary>
    [Fact]
    public async Task FastExitFlushesBufferedOutputBeforeFinalStatus()
    {
        var fixture = new Fixture();
        await fixture.SelectAsync();
        fixture.Launcher.Run = (progress, _) =>
        {
            progress!.Report(new(MinecraftLaunchStage.Running, "running", LogPath: "fast.log"));
            progress.Report(new(MinecraftLaunchStage.Output, "last startup error", true));
            return Task.FromResult(new MinecraftLaunchResult(true, 1, false, "fast.log"));
        };

        await StartLaunch(fixture.ViewModel);

        Assert.False(fixture.ViewModel.IsGameRunning);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.Equal(1, fixture.ViewModel.LastExitCode);
        Assert.Contains(fixture.ViewModel.LaunchLogLines, line => line.Contains("last startup error"));
        Assert.Contains("退出码：1", fixture.ViewModel.LaunchLogLines[^1]);
    }

    /// <summary>验证准备或运行中所有可能改写实例的入口共同受忙碌状态约束。</summary>
    private static void AssertConflictingCommandsDisabled(InstancesViewModel model)
    {
        Assert.True(model.IsBusy);
        Assert.False(model.CanInteract);
        Assert.False(model.CanEditLaunchSettings);
        Assert.False(model.LaunchGameCommand.CanExecute(null));
        Assert.False(model.SaveLaunchSettingsCommand.CanExecute(null));
        Assert.False(model.ReloadLaunchSettingsCommand.CanExecute(null));
        Assert.False(model.RenameCommand.CanExecute(null));
        Assert.False(model.DeleteCommand.CanExecute(null));
        Assert.False(model.ExportCommand.CanExecute(null));
        Assert.False(model.ImportCommand.CanExecute(null));
        Assert.False(model.DownloadMissingCommand.CanExecute(null));
        Assert.False(model.QuickCheckCommand.CanExecute(null));
        Assert.False(model.FullCheckCommand.CanExecute(null));
    }

    /// <summary>测试同步投递 UI 进度，避免断言依赖线程池回调抵达时间。</summary>
    private static Task StartLaunch(InstancesViewModel model)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new ImmediateProgressContext());
        try { return model.LaunchGameCommand.ExecuteAsync(null); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    /// <summary>创建可控异步信号，防止结束任务时重入测试断言。</summary>
    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>只在这些纯内存测试中立即处理回调，生产仍使用 WPF 的 UI 同步上下文。</summary>
    private sealed class ImmediateProgressContext : SynchronizationContext
    {
        /// <summary>立即执行进度回调，让日志和阶段断言可确定地观测状态。</summary>
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    /// <summary>组装生产 ViewModel，不访问用户实例、不启动 Java、不触碰磁盘。</summary>
    private sealed class Fixture
    {
        public MinecraftInstance Instance { get; } = new("Test instance", "C:\\Test root\\instances\\Test instance", "C:\\Test root\\instances\\Test instance\\.minecraft", []);
        public FakeLauncher Launcher { get; } = new();
        public FakeSettingsStore Store { get; } = new();
        public RecordingNotifications Notifications { get; } = new();
        public UnusedServices Services { get; } = new();
        public InstancesViewModel ViewModel { get; }
        /// <summary>让所有非启动服务在误调用时直接失败。</summary>
        public Fixture(MinecraftAccountViewModel? accounts = null, IJavaRuntimeService? javaRuntimes = null,
            IJavaDownloadDialogs? javaDialogs = null, JavaSettingsViewModel? java = null)
        {
            var unused = Services;
            ViewModel = new(unused, unused, unused, unused, "C:\\Test root\\instances", unused, unused, Launcher, Store,
                notifications: Notifications, accounts: accounts, java: java, javaRuntimes: javaRuntimes, javaDownloadDialogs: javaDialogs);
        }
        /// <summary>选择测试实例并等待本次启动设置载入。</summary>
        public async Task SelectAsync()
        {
            ViewModel.SelectedInstance = Instance;
            await ViewModel.LaunchSettingsLoadTask;
        }
    }

    /// <summary>仅提供公开玩家档案；UI 误取会话令牌时测试直接失败。</summary>
    private sealed class AccountDisplayService : IMinecraftAccountService
    {
        public bool IsConfigured => true;
        public string NextName { get; set; } = "OnlineAlex";
        public Task<MinecraftAccount?> LoadAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult<MinecraftAccount?>(null);
        public Task<MinecraftAccount> SignInAsync(IProgress<MinecraftLoginPrompt>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MinecraftAccount("test-profile", NextName));
        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MinecraftAccountSession> GetSessionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("显示玩家名不能读取登录令牌。");
    }

    /// <summary>记录启动生命周期的可见结果，测试不会弹出窗口。</summary>
    private sealed class RecordingNotifications : IOperationNotifications
    {
        public List<OperationNotice> Notices { get; } = [];
        /// <summary>保存每条通知，以验证进度事件不会造成重复弹窗。</summary>
        public void Report(OperationNotice notice) => Notices.Add(notice);
    }

    /// <summary>记录实际启动目标和设置，由测试控制准备、输出、退出和取消。</summary>
    private sealed class FakeLauncher : IMinecraftGameLauncher
    {
        public List<(string Path, MinecraftLaunchSettings Settings)> Calls { get; } = [];
        public Func<IProgress<MinecraftLaunchProgress>?, CancellationToken, Task<MinecraftLaunchResult>> Run { get; set; } =
            (_, _) => Task.FromResult(new MinecraftLaunchResult(true, 0, false, null));
        /// <summary>界面必须使用统一 Run 流程，不能自行准备后绕过服务执行。</summary>
        public Task<MinecraftLaunchPlan> PrepareAsync(string instancePath, MinecraftLaunchSettings settings,
            IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>捕获不可变输入并执行测试预设的生命周期。</summary>
        public Task<MinecraftLaunchResult> RunAsync(string instancePath, MinecraftLaunchSettings settings,
            IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((instancePath, settings));
            return Run(progress, cancellationToken);
        }
    }

    /// <summary>模拟每实例设置存储，能够制造迟到读取或磁盘失败。</summary>
    private sealed class FakeSettingsStore : IMinecraftLaunchSettingsStore
    {
        public List<(string Path, MinecraftLaunchSettings Settings)> Saved { get; } = [];
        public Func<string, CancellationToken, Task<MinecraftLaunchSettings>> Load { get; set; } =
            (_, _) => Task.FromResult(new MinecraftLaunchSettings());
        public Func<string, MinecraftLaunchSettings, CancellationToken, Task> Save { get; set; } = (_, _, _) => Task.CompletedTask;
        /// <summary>将读取时机和错误交给各测试控制。</summary>
        public Task<MinecraftLaunchSettings> LoadAsync(string instancePath, CancellationToken cancellationToken = default) => Load(instancePath, cancellationToken);
        /// <summary>记录成功保存的路径和输入，失败时不伪造已提交结果。</summary>
        public async Task SaveAsync(string instancePath, MinecraftLaunchSettings settings, CancellationToken cancellationToken = default)
        {
            await Save(instancePath, settings, cancellationToken);
            Saved.Add((instancePath, settings));
        }
    }

    /// <summary>启动测试不应触发扫描、下载或管理操作，任何误调用都会立即失败。</summary>
    private sealed class UnusedServices : IMinecraftInstanceLocator, IMinecraftInstanceManager, IMinecraftInstanceImporter,
        IMinecraftInstanceChecker, IMinecraftInstanceDownloader, IInstanceDialogs
    {
        public Func<Task<IReadOnlyList<MinecraftInstance>>> Find { get; set; } = () => throw new NotSupportedException();
        public Func<Task> Remove { get; set; } = () => throw new NotSupportedException();
        public Func<Task<string>> Rename { get; set; } = () => throw new NotSupportedException();
        public Func<bool> ConfirmRemoval { get; set; } = () => throw new NotSupportedException();
        /// <summary>禁止启动流程偷偷刷新扫描。</summary>
        public Task<IReadOnlyList<MinecraftInstance>> FindAllAsync(CancellationToken cancellationToken = default) => Find();
        /// <summary>禁止启动流程删除实例。</summary>
        public Task DeleteAsync(string instancePath, CancellationToken cancellationToken = default) => Remove();
        /// <summary>禁止启动流程改名实例。</summary>
        public Task<string> RenameAsync(string instancePath, string newName, CancellationToken cancellationToken = default) => Rename();
        /// <summary>禁止启动流程导出实例。</summary>
        public Task ExportAsync(string instancePath, string destinationZip, IProgress<InstanceExportProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止启动流程检查新的导入包。</summary>
        public Task<InstanceImportPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止启动流程导入新实例。</summary>
        public Task<InstanceImportResult> ImportAsync(string archivePath, string instanceName,
            IProgress<InstanceImportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>完整启动检查属于启动服务，界面不应另外发起检查。</summary>
        public Task<InstanceCheckReport> CheckAsync(string instancePath, InstanceCheckMode mode = InstanceCheckMode.Full,
            IProgress<InstanceCheckProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>启动按钮不自动下载或覆盖实例文件。</summary>
        public Task<InstanceDownloadReport> DownloadMissingAsync(string instancePath,
            IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>不允许启动测试显示删除确认。</summary>
        public bool ConfirmDelete(MinecraftInstance instance) => ConfirmRemoval();
        /// <summary>不允许启动测试弹出导出选择。</summary>
        public string? ChooseExportPath(MinecraftInstance instance) => throw new NotSupportedException();
        /// <summary>不允许启动测试弹出导入选择。</summary>
        public string? ChooseImportPath() => throw new NotSupportedException();
        /// <summary>不允许启动测试确认新导入。</summary>
        public string? ConfirmImport(InstanceImportPreview preview) => throw new NotSupportedException();
    }
}
