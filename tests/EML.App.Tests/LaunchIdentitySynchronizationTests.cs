using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using EML.App.Services;
using EML.App.ViewModels;
using EML.App.Views;
using EML.Core.Java;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Accounts;
using EML.Core.Minecraft.Launching;

namespace EML.App.Tests;

/// <summary>在现有 STA/Application 中验证真实实例模型和两个生产入口，不创建第二个 WPF 应用。</summary>
internal static class LaunchIdentitySynchronizationTests
{
    /// <summary>复用主题与 Dispatcher；内存服务禁止访问用户实例、凭据、Java 或网络。</summary>
    internal static void Run(Border host)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            foreach (var fromSettingsPage in new[] { false, true })
            foreach (var saveFirst in new[] { false, true })
                VerifyNavigationAndLaunch(host, fromSettingsPage, saveFirst);
            VerifySharedCollectionCurrency(host);
            VerifyAsynchronousInstanceSelection(host);
            VerifyReturningToInstanceRestoresUnsavedIdentity(host);
            VerifyRefreshPreservesDraftUntilExplicitReload(host);
            VerifyLateReadCannotReplaceRestoredDraft(host);
        }
        finally
        {
            host.Child = null;
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    /// <summary>从任一入口选择模式，隐藏另一个页面，再验证保存与直接启动使用同一身份快照。</summary>
    private static void VerifyNavigationAndLaunch(Border host, bool fromSettingsPage, bool saveFirst)
    {
        using var fixture = new Fixture(host);
        fixture.ShowSettings(fromSettingsPage);
        fixture.AssertMode(false);
        var selectingMode = fromSettingsPage ? fixture.SettingsMode : fixture.InstanceMode;
        selectingMode.SetCurrentValue(Selector.SelectedItemProperty, "正版模式");

        // 不手动调用 UpdateSource；真实点击必须立即写回模型，不能等隐藏页面重新显示才生效。
        Assert.True(fixture.Model.UseMicrosoftAccount);
        fixture.AssertMode(true);
        var loadCount = fixture.Store.LoadCount;
        fixture.ShowSettings(!fromSettingsPage);
        fixture.AssertMode(true);
        fixture.ShowSettings(fromSettingsPage);
        fixture.AssertMode(true);
        Assert.Equal(loadCount, fixture.Store.LoadCount);

        if (saveFirst)
        {
            Wait(fixture.Model.SaveLaunchSettingsCommand.ExecuteAsync(null));
            Assert.True(Assert.Single(fixture.Store.Saved).UseMicrosoftAccount);
        }
        else Assert.Empty(fixture.Store.Saved);

        VerifyLaunchSnapshot(fixture, true);
        if (!saveFirst) Assert.Empty(fixture.Store.Saved);

        // 从另一个页面切回离线，验证两个方向，而不是仅验证模型向控件推送。
        fixture.ShowSettings(!fromSettingsPage);
        var otherMode = fromSettingsPage ? fixture.InstanceMode : fixture.SettingsMode;
        otherMode.SetCurrentValue(Selector.SelectedItemProperty, "离线模式");
        var offlineName = Assert.IsType<TextBox>((fromSettingsPage ? fixture.InstanceIdentity : fixture.SettingsIdentity)
            .FindName("PlayerNameInput"));
        offlineName.SetCurrentValue(TextBox.TextProperty, "OfflinePlayer");
        Assert.Equal("OfflinePlayer", fixture.Model.PlayerName);
        fixture.AssertMode(false);
        fixture.ShowSettings(fromSettingsPage);
        fixture.AssertMode(false);
        VerifyLaunchSnapshot(fixture, false);
        Assert.Equal("OfflinePlayer", fixture.Launcher.Calls[^1].Settings.PlayerName);

        // 显式保存后重读应恢复保存的身份；未保存的本次启动不应偷偷改写设置。
        if (saveFirst)
        {
            Wait(fixture.Model.ReloadLaunchSettingsCommand.ExecuteAsync(null));
            fixture.AssertMode(true);
            Assert.Equal("Steve", fixture.Model.PlayerName);
        }
        Assert.Equal(saveFirst ? 1 : 0, fixture.Store.Saved.Count);
    }

    /// <summary>启动准备的忙碌与页面切换不能改写已选择的模式，也不能把正版快照退回离线。</summary>
    private static void VerifyLaunchSnapshot(Fixture fixture, bool microsoft)
    {
        var completion = new TaskCompletionSource<MinecraftLaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Launcher.NextRun = completion.Task;
        var callCount = fixture.Launcher.Calls.Count;
        Assert.True(fixture.Model.LaunchGameCommand.CanExecute(null));
        var operation = fixture.Model.LaunchGameCommand.ExecuteAsync(null);
        try
        {
            Assert.Equal(callCount + 1, fixture.Launcher.Calls.Count);
            var call = fixture.Launcher.Calls[^1];
            Assert.Equal(fixture.Model.SelectedInstance!.InstancePath, call.Path);
            Assert.Equal(microsoft, call.Settings.UseMicrosoftAccount);
            Assert.True(fixture.Model.IsBusy);
            fixture.ShowSettings(!fixture.Main.IsLaunchSettingsPage);
            fixture.AssertMode(microsoft);
            Assert.False(fixture.InstanceMode.IsEnabled);
            Assert.False(fixture.SettingsMode.IsEnabled);
        }
        finally
        {
            completion.TrySetResult(new(true, 0, false, null));
            Wait(operation);
            fixture.Launcher.NextRun = null;
        }
        fixture.AssertMode(microsoft);
        Assert.False(fixture.Model.IsBusy);
    }

    /// <summary>两个下拉框确实共享默认集合视图，但集合游标不能成为另一套启动身份状态。</summary>
    private static void VerifySharedCollectionCurrency(Border host)
    {
        using var fixture = new Fixture(host);
        fixture.InstanceMode.SetCurrentValue(Selector.SelectedItemProperty, "正版模式");
        fixture.AssertMode(true);
        var firstView = CollectionViewSource.GetDefaultView(fixture.InstanceMode.ItemsSource);
        var secondView = CollectionViewSource.GetDefaultView(fixture.SettingsMode.ItemsSource);
        Assert.Same(firstView, secondView);
        Assert.Same(fixture.Model.LaunchModes, fixture.InstanceMode.ItemsSource);
        Assert.Same(fixture.Model.LaunchModes, fixture.SettingsMode.ItemsSource);

        // 隐藏页面或其他消费者移动共享游标时，已绑定的模式仍必须以同一个模型值为准。
        firstView.MoveCurrentTo("离线模式");
        fixture.ShowSettings(true);
        fixture.AssertMode(true);
        VerifyLaunchSnapshot(fixture, true);
    }

    /// <summary>真实实例下拉框触发延迟设置加载，两个入口同时锁定并在完成后显示相同身份。</summary>
    private static void VerifyAsynchronousInstanceSelection(Border host)
    {
        using var fixture = new Fixture(host);
        var pending = new TaskCompletionSource<MinecraftLaunchSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.LoadOverride = (path, _) => path == fixture.Other.InstancePath
            ? pending.Task : Task.FromResult(new MinecraftLaunchSettings());
        fixture.ShowSettings(true);
        var target = Assert.IsType<ComboBox>(fixture.SettingsPage.FindName("TargetSelector"));
        target.SetCurrentValue(Selector.SelectedItemProperty, fixture.Other);
        Assert.Same(fixture.Other, fixture.Model.SelectedInstance);
        Assert.False(fixture.Model.IsLaunchSettingsLoaded);
        Flush(host);
        Assert.False(fixture.InstanceMode.IsEnabled);
        Assert.False(fixture.SettingsMode.IsEnabled);
        Assert.False(fixture.Model.LaunchGameCommand.CanExecute(null));

        fixture.ShowSettings(false);
        pending.SetResult(new() { UseMicrosoftAccount = true, PlayerName = "OtherOffline" });
        Wait(fixture.Model.LaunchSettingsLoadTask);
        fixture.AssertMode(true);
        Assert.Equal("OtherOffline", fixture.Model.PlayerName);
        Assert.Same(fixture.Other, fixture.InstanceList.SelectedItem);
        fixture.ShowSettings(true);
        fixture.AssertMode(true);
        VerifyLaunchSnapshot(fixture, true);
        Assert.Equal(fixture.Other.InstancePath, Assert.Single(fixture.Launcher.Calls).Path);
        Assert.Empty(fixture.Store.Saved);
    }

    /// <summary>通过两个真实实例选择控件往返，未保存的正版选择和离线名称分别留在对应实例。</summary>
    private static void VerifyReturningToInstanceRestoresUnsavedIdentity(Border host)
    {
        using var fixture = new Fixture(host);
        var firstName = Assert.IsType<TextBox>(fixture.InstanceIdentity.FindName("PlayerNameInput"));
        firstName.SetCurrentValue(TextBox.TextProperty, "FirstOffline");
        fixture.InstanceMode.SetCurrentValue(Selector.SelectedItemProperty, "正版模式");
        fixture.AssertMode(true);

        fixture.ShowSettings(true);
        var target = Assert.IsType<ComboBox>(fixture.SettingsPage.FindName("TargetSelector"));
        target.SetCurrentValue(Selector.SelectedItemProperty, fixture.Other);
        Wait(fixture.Model.LaunchSettingsLoadTask);
        fixture.AssertMode(false);
        var otherName = Assert.IsType<TextBox>(fixture.SettingsIdentity.FindName("PlayerNameInput"));
        otherName.SetCurrentValue(TextBox.TextProperty, "OtherOffline");
        fixture.ShowSettings(false);
        fixture.InstanceList.SetCurrentValue(Selector.SelectedItemProperty, fixture.First);
        Wait(fixture.Model.LaunchSettingsLoadTask);
        fixture.AssertMode(true);
        Assert.Equal("FirstOffline", fixture.Model.PlayerName);
        VerifyLaunchSnapshot(fixture, true);

        fixture.ShowSettings(true);
        target.SetCurrentValue(Selector.SelectedItemProperty, fixture.Other);
        Wait(fixture.Model.LaunchSettingsLoadTask);
        fixture.AssertMode(false);
        Assert.Equal("OtherOffline", fixture.Model.PlayerName);
        target.SetCurrentValue(Selector.SelectedItemProperty, fixture.First);
        Wait(fixture.Model.LaunchSettingsLoadTask);
        fixture.AssertMode(true);
        Assert.Equal("FirstOffline", fixture.Model.PlayerName);
        Assert.Equal(2, fixture.Store.LoadCount);
        Assert.Empty(fixture.Store.Saved);
    }

    /// <summary>真实刷新会清空控件选择并用同路径新对象重建列表，草稿只在显式重新读取时丢弃。</summary>
    private static void VerifyRefreshPreservesDraftUntilExplicitReload(Border host)
    {
        using var fixture = new Fixture(host);
        fixture.InstanceMode.SetCurrentValue(Selector.SelectedItemProperty, "正版模式");
        fixture.AssertMode(true);
        var beforeLoads = fixture.Store.LoadCount;
        var replacement = fixture.First with { Name = "First refreshed" };
        fixture.Locator.Result = [fixture.Other with { Name = "Other refreshed" }, replacement];
        var observedEmptySelection = false;
        fixture.InstanceList.SelectionChanged += (_, _) =>
        {
            if (fixture.InstanceList.SelectedItem is null) observedEmptySelection = true;
        };

        Assert.True(fixture.Model.RefreshCommand.CanExecute(null));
        Wait(fixture.Model.RefreshCommand.ExecuteAsync(null));
        Wait(fixture.Model.LaunchSettingsLoadTask);
        Flush(host);
        Assert.Equal(1, fixture.Locator.Calls);
        Assert.True(observedEmptySelection, "刷新必须经过真实列表清空选择的步骤，才能覆盖绑定引起的草稿丢失。");
        Assert.Same(replacement, fixture.Model.SelectedInstance);
        Assert.Same(replacement, fixture.InstanceList.SelectedItem);
        Assert.NotSame(fixture.First, fixture.Model.SelectedInstance);
        fixture.AssertMode(true);
        fixture.ShowSettings(true);
        var target = Assert.IsType<ComboBox>(fixture.SettingsPage.FindName("TargetSelector"));
        Assert.Same(replacement, target.SelectedItem);
        fixture.AssertMode(true);
        Assert.Equal(beforeLoads, fixture.Store.LoadCount);
        VerifyLaunchSnapshot(fixture, true);
        Assert.Empty(fixture.Store.Saved);

        Wait(fixture.Model.ReloadLaunchSettingsCommand.ExecuteAsync(null));
        fixture.AssertMode(false);
        fixture.ShowSettings(!fixture.Main.IsLaunchSettingsPage);
        fixture.AssertMode(false);
        Assert.Equal(beforeLoads + 1, fixture.Store.LoadCount);
        VerifyLaunchSnapshot(fixture, false);
        Assert.Empty(fixture.Store.Saved);
    }

    /// <summary>切回已有草稿后，已取消但迟到返回的其他实例读取不能再次覆盖两个入口的正版选择。</summary>
    private static void VerifyLateReadCannotReplaceRestoredDraft(Border host)
    {
        using var fixture = new Fixture(host);
        fixture.InstanceMode.SetCurrentValue(Selector.SelectedItemProperty, "正版模式");
        var pending = new TaskCompletionSource<MinecraftLaunchSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 故意忽略取消，让生产 revision 检查而不是测试替身负责阻止迟到结果。
        fixture.Store.LoadOverride = (path, _) => path == fixture.Other.InstancePath
            ? pending.Task : Task.FromResult(new MinecraftLaunchSettings());
        fixture.ShowSettings(true);
        var target = Assert.IsType<ComboBox>(fixture.SettingsPage.FindName("TargetSelector"));
        target.SetCurrentValue(Selector.SelectedItemProperty, fixture.Other);
        var staleLoad = fixture.Model.LaunchSettingsLoadTask;
        Assert.False(staleLoad.IsCompleted);
        Assert.False(fixture.Model.IsLaunchSettingsLoaded);

        fixture.ShowSettings(false);
        fixture.InstanceList.SetCurrentValue(Selector.SelectedItemProperty, fixture.First);
        Wait(fixture.Model.LaunchSettingsLoadTask);
        fixture.AssertMode(true);
        pending.SetResult(new() { PlayerName = "LateOther", UseMicrosoftAccount = false });
        Wait(staleLoad);
        fixture.AssertMode(true);
        Assert.Same(fixture.First, fixture.Model.SelectedInstance);
        Assert.Equal("Steve", fixture.Model.PlayerName);
        Assert.False(fixture.Model.IsLaunchSettingsLoading);
        Assert.Equal(2, fixture.Store.LoadCount);
        VerifyLaunchSnapshot(fixture, true);
    }

    /// <summary>运行布局和已排队的数据绑定，保留当前 UI 线程和生产控件的 BindingExpression。</summary>
    private static void Flush(Border host)
    {
        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
        host.UpdateLayout();
    }

    /// <summary>有界泵送 Dispatcher 等待异步模型，不阻塞其返回 UI 线程的 continuation。</summary>
    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) => frame.Continue = false;
            task.GetAwaiter().OnCompleted(() => dispatcher.BeginInvoke(DispatcherPriority.Send,
                new Action(() => frame.Continue = false)));
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            Assert.True(task.IsCompleted, "启动身份测试等待内存操作超时。");
        }
        task.GetAwaiter().GetResult();
    }

    /// <summary>保留主窗口的数据上下文和页面显隐结构，仅替换会访问外部资源的服务。</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly Border _host;
        public MinecraftInstance First { get; } = new("First", @"C:\EML-test-only\instances\First", @"C:\EML-test-only\instances\First\.minecraft", []);
        public MinecraftInstance Other { get; } = new("Other", @"C:\EML-test-only\instances\Other", @"C:\EML-test-only\instances\Other\.minecraft", []);
        public MemorySettingsStore Store { get; } = new();
        public RecordingLauncher Launcher { get; } = new();
        public ControlledLocator Locator { get; } = new();
        public InstancesViewModel Model { get; }
        public MainViewModel Main { get; }
        public LaunchIdentitySelector InstanceIdentity { get; } = new();
        public LaunchSettingsView SettingsPage { get; } = new();
        public LaunchIdentitySelector SettingsIdentity { get; }
        public ComboBox InstanceMode { get; }
        public ComboBox SettingsMode { get; }
        public ListBox InstanceList { get; } = new();

        /// <summary>在内存中选择实例并加载假账号，控件通过真实绑定继承同一实例模型。</summary>
        public Fixture(Border host)
        {
            _host = host;
            var services = new ForbiddenServices();
            var java = new JavaSettingsViewModel(services);
            var accounts = new MinecraftAccountViewModel(new PublicAccountService());
            Wait(accounts.LoadCommand.ExecuteAsync(null));
            Locator.Result = [First, Other];
            Model = new(Locator, services, services, services, @"C:\EML-test-only\instances",
                services, services, Launcher, Store, java, accounts: accounts);
            Model.Instances.Add(First);
            Model.Instances.Add(Other);
            Model.SelectedInstance = First;
            Wait(Model.LaunchSettingsLoadTask);
            Main = new(java, Model, accounts);

            var root = new Grid { DataContext = Main };
            var instancePage = new StackPanel();
            instancePage.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(MainViewModel.IsInstancePage))
                { Converter = new BooleanToVisibilityConverter() });
            InstanceIdentity.SetBinding(FrameworkElement.DataContextProperty, new Binding(nameof(MainViewModel.Instances)));
            InstanceList.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Instances.Instances"));
            InstanceList.SetBinding(Selector.SelectedItemProperty, new Binding("Instances.SelectedInstance") { Mode = BindingMode.TwoWay });
            InstanceList.DisplayMemberPath = nameof(MinecraftInstance.Name);
            instancePage.Children.Add(InstanceIdentity);
            instancePage.Children.Add(InstanceList);
            root.Children.Add(instancePage);
            SettingsPage.SetBinding(FrameworkElement.DataContextProperty, new Binding(nameof(MainViewModel.Instances)));
            SettingsPage.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(MainViewModel.IsLaunchSettingsPage))
                { Source = Main, Converter = new BooleanToVisibilityConverter() });
            root.Children.Add(SettingsPage);
            host.Child = root;
            Flush(host);
            SettingsIdentity = Assert.IsType<LaunchIdentitySelector>(SettingsPage.FindName("LaunchIdentity"));
            InstanceMode = Assert.IsType<ComboBox>(InstanceIdentity.FindName("ModeSelector"));
            SettingsMode = Assert.IsType<ComboBox>(SettingsIdentity.FindName("ModeSelector"));
            Assert.Same(Model, InstanceIdentity.DataContext);
            Assert.Same(Model, SettingsIdentity.DataContext);
        }

        /// <summary>通过生产导航模型切换页面，让隐藏控件也经历真实的绑定和显隐变化。</summary>
        public void ShowSettings(bool settings)
        {
            Main.SelectedPage = settings ? 5 : 0;
            Flush(_host);
        }

        /// <summary>同时核对模型、两个下拉框和账号展示；确保模拟操作没有移除双向绑定。</summary>
        public void AssertMode(bool microsoft)
        {
            Flush(_host);
            Assert.Equal(microsoft, Model.UseMicrosoftAccount);
            var expected = microsoft ? "正版模式" : "离线模式";
            Assert.Equal(expected, Model.SelectedLaunchMode);
            Assert.Equal(expected, InstanceMode.SelectedItem);
            Assert.Equal(expected, SettingsMode.SelectedItem);
            foreach (var mode in new[] { InstanceMode, SettingsMode })
            {
                var binding = BindingOperations.GetBindingExpression(mode, Selector.SelectedItemProperty);
                Assert.NotNull(binding);
                Assert.Equal(BindingStatus.Active, binding.Status);
                Assert.Equal(BindingMode.TwoWay, binding.ParentBinding.Mode);
            }
            foreach (var identity in new[] { InstanceIdentity, SettingsIdentity })
            {
                var account = Assert.IsType<TextBox>(identity.FindName("MicrosoftPlayerName"));
                Assert.Equal("OnlinePlayer", account.Text);
                Assert.True(account.IsReadOnly);
                var offline = Assert.IsType<TextBox>(identity.FindName("PlayerNameInput"));
                Assert.Equal(Model.PlayerName, offline.Text);
                Assert.Equal(!microsoft && Model.CanEditLaunchSettings, offline.IsEnabled);
            }
        }

        /// <summary>解除导航模型订阅并移出视觉树，不关闭共享的 Application。</summary>
        public void Dispose()
        {
            _host.Child = null;
            Main.Dispose();
        }
    }

    /// <summary>刷新仅返回测试安排的内存实例，允许用同路径新对象模拟真实扫描结果。</summary>
    private sealed class ControlledLocator : IMinecraftInstanceLocator
    {
        public IReadOnlyList<MinecraftInstance> Result { get; set; } = [];
        public int Calls { get; private set; }

        /// <summary>记录真实 RefreshCommand 发起的扫描，不读取磁盘目录。</summary>
        public Task<IReadOnlyList<MinecraftInstance>> FindAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Result);
        }
    }

    /// <summary>只保存内存快照，允许测试延迟某次读取，不创建任何设置文件。</summary>
    private sealed class MemorySettingsStore : IMinecraftLaunchSettingsStore
    {
        private readonly Dictionary<string, MinecraftLaunchSettings> _values = new(StringComparer.OrdinalIgnoreCase);
        public int LoadCount { get; private set; }
        public List<MinecraftLaunchSettings> Saved { get; } = [];
        public Func<string, CancellationToken, Task<MinecraftLaunchSettings>>? LoadOverride { get; set; }

        /// <summary>返回每实例内存值或测试提供的延迟结果。</summary>
        public Task<MinecraftLaunchSettings> LoadAsync(string instancePath, CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return LoadOverride?.Invoke(instancePath, cancellationToken)
                ?? Task.FromResult(_values.GetValueOrDefault(instancePath) ?? new MinecraftLaunchSettings());
        }

        /// <summary>记录显式保存的设置；启动操作不能绕过此调用偷偷写入。</summary>
        public Task SaveAsync(string instancePath, MinecraftLaunchSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[instancePath] = settings;
            Saved.Add(settings);
            return Task.CompletedTask;
        }
    }

    /// <summary>捕获实际传入启动服务的身份，绝不启动 Java 或读取游戏文件。</summary>
    private sealed class RecordingLauncher : IMinecraftGameLauncher
    {
        public List<(string Path, MinecraftLaunchSettings Settings)> Calls { get; } = [];
        public Task<MinecraftLaunchResult>? NextRun { get; set; }
        /// <summary>页面必须使用统一 RunAsync 入口。</summary>
        public Task<MinecraftLaunchPlan> PrepareAsync(string instancePath, MinecraftLaunchSettings settings,
            IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>保留不可变设置快照，由测试决定本次假进程何时结束。</summary>
        public Task<MinecraftLaunchResult> RunAsync(string instancePath, MinecraftLaunchSettings settings,
            IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((instancePath, settings));
            return NextRun ?? Task.FromResult(new MinecraftLaunchResult(true, 0, false, null));
        }
    }

    /// <summary>提供公开显示档案；任何请求真实会话的 UI 代码都会使测试失败。</summary>
    private sealed class PublicAccountService : IMinecraftAccountService
    {
        public bool IsConfigured => true;
        /// <summary>返回纯测试玩家名，不访问本机加密凭据。</summary>
        public Task<MinecraftAccount?> LoadAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<MinecraftAccount?>(new("11111111222233334444555555555555", "OnlinePlayer"));
        /// <summary>页面同步测试不得启动网页授权。</summary>
        public Task<MinecraftAccount> SignInAsync(IProgress<MinecraftLoginPrompt>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>账号展示和模式选择不得读取会话令牌。</summary>
        public Task<MinecraftAccountSession> GetSessionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>测试不得更改账号登录状态。</summary>
        public Task SignOutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>所有文件、扫描、下载和对话框接口一旦被误调用就失败，保证回归测试无外部副作用。</summary>
    private sealed class ForbiddenServices : IMinecraftInstanceLocator, IMinecraftInstanceManager, IMinecraftInstanceImporter,
        IMinecraftInstanceChecker, IMinecraftInstanceDownloader, IInstanceDialogs, IJavaLocator
    {
        /// <summary>实例列表完全由测试构造，不扫描磁盘。</summary>
        public Task<IReadOnlyList<MinecraftInstance>> FindAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>启动参数测试不探测真实 Java。</summary>
        Task<IReadOnlyList<JavaInstallation>> IJavaLocator.FindAllAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        /// <summary>禁止删除实例。</summary>
        public Task DeleteAsync(string instancePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止重命名实例。</summary>
        public Task<string> RenameAsync(string instancePath, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止创建导出文件。</summary>
        public Task ExportAsync(string instancePath, string destinationZip, IProgress<InstanceExportProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止读取真实压缩包。</summary>
        public Task<InstanceImportPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止导入实例。</summary>
        public Task<InstanceImportResult> ImportAsync(string archivePath, string instanceName,
            IProgress<InstanceImportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>界面同步不应隐式触发文件检查。</summary>
        public Task<InstanceCheckReport> CheckAsync(string instancePath, InstanceCheckMode mode = InstanceCheckMode.Full,
            IProgress<InstanceCheckProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止网络补全和下载。</summary>
        public Task<InstanceDownloadReport> DownloadMissingAsync(string instancePath,
            IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>禁止弹出删除确认。</summary>
        public bool ConfirmDelete(MinecraftInstance instance) => throw new NotSupportedException();
        /// <summary>禁止弹出导出路径选择。</summary>
        public string? ChooseExportPath(MinecraftInstance instance) => throw new NotSupportedException();
        /// <summary>禁止弹出导入路径选择。</summary>
        public string? ChooseImportPath() => throw new NotSupportedException();
        /// <summary>禁止确认导入。</summary>
        public string? ConfirmImport(InstanceImportPreview preview) => throw new NotSupportedException();
    }
}
