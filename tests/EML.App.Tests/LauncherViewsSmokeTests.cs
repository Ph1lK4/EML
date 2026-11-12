using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EML.App.Services;
using EML.App.ViewModels;
using EML.App.Views;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Catalog;
using EML.Core.Minecraft.Gtnh;
using EML.Core.Minecraft.Migration;

namespace EML.App.Tests;

/// <summary>实际 WPF 资源与布局只能在 STA 检查；避免与其他测试并行改变应用资源。</summary>
[CollectionDefinition("Launcher WPF resources", DisableParallelization = true)]
public sealed class LauncherWpfResourceCollection { }

[Collection("Launcher WPF resources")]
public sealed class LauncherViewsSmokeTests
{
    /// <summary>加载生产主题和所有分离页面，覆盖隐藏实例页的模板、文本输入与动画清理。</summary>
    [Fact]
    public async Task ProductionViewsAndAllInstanceSectionsLayoutWithoutOpeningAWindow()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try { VerifyProductionViews(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            if (failure is null) completion.SetResult();
            else completion.SetException(failure);
        }) { IsBackground = true, Name = "EML view smoke test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>整项测试只创建一个 Application，不运行 App 启动入口，不构造业务服务。</summary>
    private static void VerifyProductionViews()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/EML.App;component/Themes/LauncherTheme.xaml", UriKind.Relative)
            });
            var host = new Border { Width = 720, Height = 520 };
            var selection = new WorkspaceSelection();
            var workspace = new InstanceWorkspaceView { DataContext = selection, Visibility = Visibility.Collapsed };
            host.Child = workspace;
            var sections = Assert.IsType<TabControl>(workspace.Content);
            Assert.Equal(8, sections.Items.Count);
            VerifyInitiallyClosedWorkspace(host, workspace, sections, selection);
            for (var index = 0; index < sections.Items.Count; index++)
            {
                selection.SelectedDetailTab = index;
                LayoutAndVerify(host, workspace);
                Assert.Equal(index, sections.SelectedIndex);
                var content = Assert.IsAssignableFrom<FrameworkElement>(((TabItem)sections.Items[index]).Content);
                var presenter = Assert.IsType<ContentPresenter>(sections.Template.FindName("PART_SelectedContentHost", sections));
                Assert.Same(content, presenter.Content);
                Assert.Contains(VisualDescendants(workspace), item => ReferenceEquals(item, content));
            }

            foreach (var page in new UserControl[] { new OnlineModsView(), new AccountsView(), new JavaEnvironmentView(), new LaunchSettingsView(), new InstanceDownloadsView(), new DownloadCenterView(), new GtnhHubView() })
            {
                host.Child = page;
                LayoutAndVerify(host, page);
            }
            VerifySingleLineTextVisibility(host);
            VerifyInputsAndDropdowns(host);
            VerifyGtnhPages(host);
            VerifyGtnhMigrationImportEntry(host);
            VerifyGtnhPackDownloads(host);
            VerifySharedLaunchIdentity(host);
            VerifyLaunchSettingsPage(host);
            VerifyInstanceDownloadsPage(host);
            VerifyDownloadCenterPage(host);
            host.Child = null;
            DialogAppearanceChecks.Run();
            Assert.Empty(application.Windows.Cast<Window>());
        }
        finally { application.Shutdown(); }
    }

    /// <summary>真实下载页面使用主模型形状绑定，验证两种筛选布局、输入可见性和版本选择。</summary>
    private static void VerifyInstanceDownloadsPage(Border host)
    {
        foreach (var minecraft in new[] { false, true })
        {
            var model = new InstanceDownloadsPreview(minecraft);
            var page = new InstanceDownloadsView
            {
                DataContext = new { Downloads = model, Instances = new { TransferPercent = 42, TransferStatus = "正在补全依赖……" } },
                FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14,
                Background = (Brush)Application.Current.FindResource("WindowBackgroundBrush")
            };
            host.Child = page;
            Layout(host);
            var search = Assert.IsType<TextBox>(page.FindName("SearchInput"));
            Assert.Same(model, search.DataContext);
            search.SetCurrentValue(TextBox.TextProperty, minecraft ? "1.20.1" : "GTNH 中文整合包");
            Assert.Equal(search.Text, model.SearchText);
            var visible = MeasureSingleLineVisibility(search, "下载实例搜索");
            Assert.True(visible.Visible, visible.Detail);
            var providers = Assert.Single(VisualDescendants(page).OfType<ComboBox>(), item =>
                BindingOperations.GetBinding(item, Selector.SelectedIndexProperty)?.Path.Path == "ProviderIndex");
            Assert.Equal(minecraft ? 2 : 0, providers.SelectedIndex);
            var projects = Assert.Single(VisualDescendants(page).OfType<ListBox>());
            Assert.Same(model.SelectedProject, projects.SelectedItem);
            var releases = Assert.Single(VisualDescendants(page).OfType<ComboBox>(), item =>
                BindingOperations.GetBinding(item, Selector.SelectedItemProperty)?.Path.Path == "SelectedRelease");
            Assert.Same(model.SelectedRelease, releases.SelectedItem);
            releases.SelectedItem = model.Releases[1];
            Assert.Same(model.Releases[1], model.SelectedRelease);
            var version = Assert.Single(VisualDescendants(page).OfType<TextBox>(), item =>
                BindingOperations.GetBinding(item, TextBox.TextProperty)?.Path.Path == "GameVersion");
            var snapshots = Assert.Single(VisualDescendants(page).OfType<CheckBox>());
            Assert.Equal(!minecraft, IsLocallyVisible(version, page));
            Assert.Equal(minecraft, IsLocallyVisible(snapshots, page));
            var description = Assert.IsType<TextBox>(page.FindName("ProjectDescriptionText"));
            Assert.Equal(!minecraft, IsLocallyVisible(description, page));
            Assert.True(description.IsReadOnly);
            Assert.Equal(model.ProjectDescription, description.Text);
            Assert.Equal(BindingMode.OneWay, BindingOperations.GetBinding(description, TextBox.TextProperty)?.Mode);
            Assert.Contains("作者提供的中文", description.Text);
            Assert.Contains("English description", description.Text);
            var detail = Assert.Single(VisualDescendants(page).OfType<ScrollViewer>(), item =>
                item.Content is StackPanel panel && panel.Children.OfType<ComboBox>().Any());
            Assert.True(detail.ViewportHeight > 0);
            detail.ScrollToBottom(); Layout(host);
            Assert.True(detail.VerticalOffset > 0, "小窗口中安装详情必须可滚动。");
            var install = Assert.Single(VisualDescendants(page).OfType<Button>(), item => Equals(item.Content, "下载并创建实例"));
            var bounds = install.TransformToAncestor(page).TransformBounds(new Rect(install.RenderSize));
            Assert.True(bounds.Top >= 0 && bounds.Bottom <= page.ActualHeight);
            Assert.True(bounds.Left >= 0 && bounds.Right <= page.ActualWidth);
            WritePageOverview(host, page, minecraft ? "download-vanilla.png" : "download-modpacks.png");
            model.IsInstalling = true;
            Layout(host);
            Assert.False(search.IsEnabled);
            Assert.False(providers.IsEnabled);
            Assert.False(projects.IsEnabled);
            Assert.False(releases.IsEnabled);
            var cancel = Assert.Single(VisualDescendants(page).OfType<Button>(), item => Equals(item.Content, "取消下载 / 安装"));
            Assert.True(IsLocallyVisible(cancel, page));
        }
    }

    /// <summary>下载中心使用真实页面模型验证状态命令、可复制错误和小窗口下的设置与滚动。</summary>
    private static void VerifyDownloadCenterPage(Border host)
    {
        var center = new DownloadCenterPreview();
        using var model = new DownloadCenterViewModel(center, new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        model.SelectedTask = model.Tasks.First(task => task.State == DownloadTaskState.Downloading);
        var page = new DownloadCenterView
        {
            DataContext = model, FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14,
            Background = (Brush)Application.Current.FindResource("WindowBackgroundBrush")
        };
        host.Child = page;
        Layout(host);
        var list = Assert.IsType<ListBox>(page.FindName("TaskList"));
        Assert.Same(model.SelectedTask, list.SelectedItem);
        Assert.Equal(model.Tasks.Count, list.Items.Count);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
        var pause = Assert.Single(VisualDescendants(page).OfType<Button>(), button => Equals(button.Content, "暂停"));
        var resume = Assert.Single(VisualDescendants(page).OfType<Button>(), button => Equals(button.Content, "继续"));
        Assert.Same(model.PauseCommand, pause.Command);
        Assert.True(pause.IsEnabled);
        Assert.False(resume.IsEnabled);
        Assert.Contains(VisualDescendants(list).OfType<TextBlock>(), block => block.Text.Contains("GTNH"));
        WritePageOverview(host, page, "download-center.png");

        list.SelectedItem = model.Tasks.First(task => task.State == DownloadTaskState.Failed);
        Layout(host);
        Assert.Equal(DownloadTaskState.Failed, model.SelectedTask!.State);
        Assert.False(pause.IsEnabled);
        Assert.False(resume.IsEnabled);
        var destination = Assert.IsType<TextBox>(page.FindName("DestinationText"));
        var error = Assert.IsType<TextBox>(page.FindName("TaskErrorText"));
        Assert.Equal(model.SelectedTask.DestinationPath, destination.Text);
        Assert.Equal(model.SelectedTask.Error, error.Text);
        Assert.True(destination.IsReadOnly && error.IsReadOnly);
        destination.SelectAll(); error.SelectAll();
        Assert.Equal(destination.Text, destination.SelectedText);
        Assert.Equal(error.Text, error.SelectedText);

        Assert.Single(VisualDescendants(page).OfType<Expander>()).IsExpanded = true;
        Layout(host);
        var concurrent = Assert.IsType<TextBox>(page.FindName("ConcurrentInput"));
        var rate = Assert.IsType<TextBox>(page.FindName("RateLimitInput"));
        concurrent.SetCurrentValue(TextBox.TextProperty, "6");
        rate.SetCurrentValue(TextBox.TextProperty, "1536");
        Layout(host);
        Assert.Equal("6", model.MaxConcurrentText);
        Assert.Equal("1536", model.RateLimitKiBText);
        foreach (var input in new[] { concurrent, rate })
        {
            var visible = MeasureSingleLineVisibility(input, input.Name);
            Assert.True(visible.Visible, visible.Detail);
        }
        var save = Assert.Single(VisualDescendants(page).OfType<Button>(), button => Equals(button.Content, "保存下载设置"));
        Assert.Same(model.SaveSettingsCommand, save.Command);
        save.Command.Execute(null);
        Assert.Equal(6, center.Settings.MaxConcurrentDownloads);
        Assert.Equal(1572864, center.Settings.BytesPerSecond);
        var saveBounds = save.TransformToAncestor(page).TransformBounds(new Rect(save.RenderSize));
        Assert.True(saveBounds.Left >= 0 && saveBounds.Right <= page.ActualWidth);
        Assert.True(saveBounds.Top >= 0 && saveBounds.Bottom <= page.ActualHeight);
        var details = Assert.IsType<ScrollViewer>(page.FindName("TaskDetailsScroll"));
        Assert.True(details.ViewportHeight > 0);
        Assert.True(list.ActualHeight > 0);
        details.ScrollToBottom(); Layout(host);
        Assert.True(details.VerticalOffset > 0, "小窗口中下载详情和操作必须可以滚动到达。");
    }

    /// <summary>下载中心离屏数据仅保留内存设置，不访问地址、修改文件或运行安装服务。</summary>
    private sealed class DownloadCenterPreview : IDownloadTaskCenter
    {
        public event EventHandler? Changed { add { } remove { } }
        public DownloadCenterSettings Settings { get; private set; } = new() { MaxConcurrentDownloads = 3, BytesPerSecond = 1048576 };
        public IReadOnlyList<DownloadTaskSnapshot> GetSnapshot() =>
        [
            new() { Id = Guid.Parse("7a0185f5-bc33-41e5-a99b-c299d2362c41"), DisplayName = "GTNH-2.9.0-client.zip", GroupName = "GTNH · 整合包下载",
                DestinationPath = @"C:\EML\downloads\GTNH-2.9.0-client.zip", State = DownloadTaskState.Downloading,
                Priority = DownloadPriority.Normal, BytesReceived = 52428800, TotalBytes = 104857600, BytesPerSecond = 1048576, ResumedBytes = 10485760 },
            new() { Id = Guid.Parse("7a0185f5-bc33-41e5-a99b-c299d2362c42"), DisplayName = "minecraft-client.jar", GroupName = "Minecraft · 运行依赖",
                DestinationPath = @"C:\EML\instances\GTNH\.minecraft\versions\1.7.10\1.7.10.jar", State = DownloadTaskState.Completed,
                Priority = DownloadPriority.Normal, BytesReceived = 5242880, TotalBytes = 5242880, FromCache = true },
            new() { Id = Guid.Parse("7a0185f5-bc33-41e5-a99b-c299d2362c43"), DisplayName = "example-library.jar", GroupName = "GTNH · 运行依赖",
                DestinationPath = @"C:\EML\instances\GTNH\.minecraft\libraries\example-library.jar", State = DownloadTaskState.Failed,
                Priority = DownloadPriority.Low, TotalBytes = 1048576, Error = "下载连接已中断。请回到实例补全页面重试。" }
        ];
        public void UpdateSettings(DownloadCenterSettings settings) => Settings = settings;
        public bool Pause(Guid id) => throw new InvalidOperationException("离屏测试不能暂停真实任务。");
        public bool Resume(Guid id) => throw new InvalidOperationException("离屏测试不能恢复真实任务。");
        public bool Cancel(Guid id) => throw new InvalidOperationException("离屏测试不能取消真实任务。");
        public bool SetPriority(Guid id, DownloadPriority priority) => throw new InvalidOperationException("离屏测试不能改变真实队列。");
        public void PauseAll() => throw new InvalidOperationException("离屏测试不能暂停真实队列。");
        public void ResumeAll() => throw new InvalidOperationException("离屏测试不能恢复真实队列。");
        public void ClearFinished() => throw new InvalidOperationException("离屏测试不能清理真实记录。");
        public Task StopAsync() => throw new InvalidOperationException("页面不能停止共享下载中心。");
    }

    /// <summary>预览使用常规页面尺寸，断言继续使用最小尺寸；导出后恢复原有测试布局。</summary>
    private static void WritePageOverview(Border host, FrameworkElement page, string name)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EML_TEXTBOX_PREVIEW_DIRECTORY"))) return;
        var width = host.Width; var height = host.Height;
        try
        {
            host.Width = 1000; host.Height = 700;
            foreach (var scroll in VisualDescendants(page).OfType<ScrollViewer>()) scroll.ScrollToTop();
            Layout(host); WriteTextPreview(page, name);
        }
        finally { host.Width = width; host.Height = height; Layout(host); }
    }

    /// <summary>提供下载页面的纯展示数据，不实例化目录查询或安装服务。</summary>
    private sealed class InstanceDownloadsPreview : INotifyPropertyChanged
    {
        private bool _isInstalling;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string[] Providers { get; } = ["Modrinth 整合包", "CurseForge 整合包", "Minecraft 官方原版"];
        public string[] Loaders { get; } = ["", "forge", "fabric"];
        public string[] Sorts { get; } = ["相关度 / 热门", "下载量", "最近更新"];
        public int ProviderIndex { get; set; }
        public int SortIndex { get; set; }
        public string SearchText { get; set; } = "";
        public string GameVersion { get; set; } = "1.20.1";
        public string Loader { get; set; } = "fabric";
        public bool IncludeSnapshots { get; set; }
        public bool IsMinecraft => ProviderIndex == 2;
        public bool IsModpack => !IsMinecraft;
        public bool CanBrowse => !IsInstalling;
        public bool HasMore => false;
        public bool HasMoreVersions => false;
        public string Status => "选择项目查看具体版本。";
        public string VersionStatus => "请选择需要安装的版本。";
        public string ProjectInformation => SelectedProject.Project.Name + "\n" + SelectedProject.Project.Summary;
        public string ProjectDescription => "作者提供的中文介绍，直接来自平台。\n\nEnglish description supplied by the author.";
        public bool HasProjectDescription => true;
        public string DescriptionStatus => "来自 Modrinth 的项目介绍，按作者提供的语言显示。";
        public string ReleaseInformation => "版本 1.20.1\n安装到新实例\n" + string.Join("\n", Enumerable.Repeat("请确认游戏版本与加载器。", 6));
        public string InstallStatus => "下载后确认名称，并自动补全依赖。";
        public string ArchiveProgress => "";
        public double ArchivePercent => 0;
        public InstanceDownloadRow[] Projects { get; }
        public InstanceDownloadRow SelectedProject { get; set; }
        public InstanceCatalogRelease[] Releases { get; }
        public InstanceCatalogRelease SelectedRelease { get; set; }
        public bool IsInstalling
        {
            get => _isInstalling;
            set
            {
                _isInstalling = value;
                PropertyChanged?.Invoke(this, new(nameof(IsInstalling)));
                PropertyChanged?.Invoke(this, new(nameof(CanBrowse)));
            }
        }

        /// <summary>创建平台对应的显示条目，两条版本用于检验双向选择绑定。</summary>
        public InstanceDownloadsPreview(bool minecraft)
        {
            ProviderIndex = minecraft ? 2 : 0;
            var provider = (InstanceCatalogProvider)ProviderIndex;
            Projects = [new(new InstanceCatalogProject { Provider = provider, Id = "preview", Name = minecraft ? "Minecraft 1.20.1" : "GTNH · 中文整合包",
                Author = "演示作者", Summary = "此处展示选中项目的简介，不访问网络。" })];
            SelectedProject = Projects[0];
            Releases = [new() { Provider = provider, ProjectId = "preview", Id = "release-one", Name = "1.20.1", GameVersions = ["1.20.1"], ReleaseType = "正式版" },
                new() { Provider = provider, ProjectId = "preview", Id = "release-two", Name = "1.20.1 · 第二个版本", GameVersions = ["1.20.1"], ReleaseType = "正式版" }];
            SelectedRelease = Releases[0];
        }
    }

    /// <summary>设置大页面绑定同一实例，忙碌时不能切换目标；小窗口仍可访问参数和固定操作栏。</summary>
    private static void VerifyLaunchSettingsPage(Border host)
    {
        var model = new LaunchSettingsPreview();
        var page = new LaunchSettingsView
        {
            DataContext = model, FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14,
            Background = (Brush)Application.Current.FindResource("WindowBackgroundBrush")
        };
        host.Child = page;
        Layout(host);
        var target = Assert.IsType<ComboBox>(page.FindName("TargetSelector"));
        Assert.Same(model.SelectedInstance, target.SelectedItem);
        target.SelectedItem = model.Instances[1];
        Assert.Same(model.Instances[1], model.SelectedInstance);
        var identity = Assert.IsType<LaunchIdentitySelector>(page.FindName("LaunchIdentity"));
        var player = Assert.IsType<TextBox>(identity.FindName("PlayerNameInput"));
        Assert.Equal("Steve", player.Text);
        Assert.True(MeasureSingleLineVisibility(player, "启动玩家名").Visible);
        player.Text = "Alex";
        Assert.Equal("Alex", model.PlayerName);
        var java = Assert.IsType<ComboBox>(page.FindName("JavaPathInput"));
        var editor = Assert.IsType<TextBox>(java.Template.FindName("PART_EditableTextBox", java));
        editor.Text = @"D:\Java\jdk-25\bin\java.exe";
        Assert.Equal(editor.Text, model.SelectedJavaPath);
        var scroll = Assert.IsType<ScrollViewer>(page.FindName("SettingsScroll"));
        Assert.True(scroll.ScrollableHeight > 0);
        Assert.True(scroll.ViewportHeight > 100);
        WritePageOverview(host, page, "launch-settings.png");
        scroll.ScrollToBottom(); Layout(host);
        Assert.True(scroll.VerticalOffset > 0);
        var arguments = Assert.Single(VisualDescendants(page).OfType<TextBox>(), item =>
            BindingOperations.GetBinding(item, TextBox.TextProperty)?.Path.Path == "AdditionalJvmArguments");
        arguments.Text = "-XX:+UseG1GC\n-Dexample=true";
        Assert.Equal(arguments.Text, model.AdditionalJvmArguments);
        Assert.True(arguments.LineCount >= 2);
        var save = Assert.Single(VisualDescendants(page).OfType<Button>(), item => Equals(item.Content, "保存设置"));
        var buttonBounds = save.TransformToAncestor(page).TransformBounds(new Rect(save.RenderSize));
        Assert.True(buttonBounds.Top >= 0 && buttonBounds.Bottom <= page.ActualHeight);
        WriteTextPreview(page, "launch-settings-parameters.png");
        model.Busy = true;
        Layout(host);
        Assert.False(target.IsEnabled);
        Assert.False(player.IsEnabled);
        Assert.False(java.IsEnabled);
        Assert.False(arguments.IsEnabled);
    }

    /// <summary>两个入口绑定同一个模型；更换账号、切换模式和离线名称编辑立即同步，忙碌时均锁定。</summary>
    private static void VerifySharedLaunchIdentity(Border host)
    {
        var model = new LaunchSettingsPreview();
        var first = new LaunchIdentitySelector
        { DataContext = model, Margin = new Thickness(24), FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14 };
        var second = new LaunchIdentitySelector
        { DataContext = model, Margin = new Thickness(24), FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14 };
        var panel = new StackPanel
        {
            Background = (Brush)Application.Current.FindResource("WindowBackgroundBrush")
        };
        panel.Children.Add(first);
        panel.Children.Add(second);
        host.Child = panel;
        Layout(host);
        var firstMode = Assert.IsType<ComboBox>(first.FindName("ModeSelector"));
        var secondMode = Assert.IsType<ComboBox>(second.FindName("ModeSelector"));
        var firstOffline = Assert.IsType<TextBox>(first.FindName("PlayerNameInput"));
        var secondOffline = Assert.IsType<TextBox>(second.FindName("PlayerNameInput"));
        var firstOnline = Assert.IsType<TextBox>(first.FindName("MicrosoftPlayerName"));
        var secondOnline = Assert.IsType<TextBox>(second.FindName("MicrosoftPlayerName"));
        firstOffline.Text = "Offline_Player";
        Layout(host);
        Assert.Equal(firstOffline.Text, secondOffline.Text);
        WriteTextPreview(panel, "launch-mode-offline.png");

        firstMode.SelectedItem = "正版模式";
        Layout(host);
        Assert.Equal("正版模式", secondMode.SelectedItem);
        Assert.False(IsLocallyVisible(firstOffline, first));
        Assert.False(IsLocallyVisible(secondOffline, second));
        Assert.True(IsLocallyVisible(firstOnline, first));
        Assert.True(secondOnline.IsReadOnly);
        Assert.Equal("尚未登录", firstOnline.Text);
        model.Accounts.AccountName = "Online_Player_123";
        Layout(host);
        Assert.Equal("Online_Player_123", firstOnline.Text);
        Assert.Equal("Online_Player_123", secondOnline.Text);
        Assert.True(MeasureSingleLineVisibility(firstOnline, "正版玩家名").Visible);
        WriteTextPreview(panel, "launch-mode-microsoft.png");

        model.Accounts.AccountName = "AnotherPlayer";
        Layout(host);
        Assert.Equal("AnotherPlayer", secondOnline.Text);
        model.Accounts.AccountName = "尚未登录";
        Layout(host);
        Assert.Equal("尚未登录", firstOnline.Text);
        Assert.Equal("正版模式", firstMode.SelectedItem);

        secondMode.SelectedItem = "离线模式";
        Layout(host);
        Assert.Equal("离线模式", firstMode.SelectedItem);
        Assert.Equal("Offline_Player", firstOffline.Text);
        Assert.True(IsLocallyVisible(secondOffline, second));
        Assert.False(IsLocallyVisible(secondOnline, second));
        model.Busy = true;
        Layout(host);
        Assert.False(firstMode.IsEnabled);
        Assert.False(secondMode.IsEnabled);
        Assert.False(firstOffline.IsEnabled);
        Assert.False(secondOffline.IsEnabled);
    }

    /// <summary>只提供启动设置的绑定数据，不创建启动服务或读写配置。</summary>
    private sealed class LaunchSettingsPreview : INotifyPropertyChanged
    {
        private bool _busy;
        private bool _useMicrosoftAccount;
        private string _playerName = "Steve";
        public event PropertyChangedEventHandler? PropertyChanged;
        public MinecraftInstance[] Instances { get; } =
        [new("GTNH 2.9.0 · 生存服", @"C:\preview\gtnh", @"C:\preview\gtnh\.minecraft", ["1.7.10"]),
         new("原版 1.20.1", @"C:\preview\vanilla", @"C:\preview\vanilla\.minecraft", ["1.20.1"])];
        public MinecraftInstance? SelectedInstance { get; set; }
        public bool CanInteract => !Busy;
        public bool CanEditLaunchSettings => !Busy;
        public bool CanEditOfflinePlayerName => !Busy && !UseMicrosoftAccount;
        public IReadOnlyList<string> LaunchModes { get; } = ["离线模式", "正版模式"];
        public AccountNamePreview Accounts { get; } = new();
        public string SelectedLaunchMode
        {
            get => UseMicrosoftAccount ? "正版模式" : "离线模式";
            set { if (value is "正版模式" or "离线模式") UseMicrosoftAccount = value == "正版模式"; }
        }
        public bool UseMicrosoftAccount
        {
            get => _useMicrosoftAccount;
            set
            {
                _useMicrosoftAccount = value;
                PropertyChanged?.Invoke(this, new(nameof(UseMicrosoftAccount)));
                PropertyChanged?.Invoke(this, new(nameof(SelectedLaunchMode)));
                PropertyChanged?.Invoke(this, new(nameof(CanEditOfflinePlayerName)));
            }
        }
        public string PlayerName
        {
            get => _playerName;
            set { _playerName = value; PropertyChanged?.Invoke(this, new(nameof(PlayerName))); }
        }
        public string SelectedJavaPath { get; set; } = @"C:\Java\bin\java.exe";
        public string MinMemoryMbText { get; set; } = "1024";
        public string MaxMemoryMbText { get; set; } = "8192";
        public string AdditionalJvmArguments { get; set; } = "";
        public string LaunchSettingsStatus => "设置按实例分别保存，修改后请点击保存。";
        public string LaunchStatus => "尚未启动游戏。";
        public object Java { get; } = new { Installations = Array.Empty<object>() };
        public bool Busy
        {
            get => _busy;
            set
            {
                _busy = value;
                PropertyChanged?.Invoke(this, new(nameof(CanInteract)));
                PropertyChanged?.Invoke(this, new(nameof(CanEditLaunchSettings)));
                PropertyChanged?.Invoke(this, new(nameof(CanEditOfflinePlayerName)));
            }
        }

        /// <summary>固定初始目标，后续通过页面双向绑定选择第二个虚拟实例。</summary>
        public LaunchSettingsPreview() => SelectedInstance = Instances[0];
    }

    /// <summary>模拟账号页公开名称变化，不提供凭据或真实账号。</summary>
    private sealed class AccountNamePreview : INotifyPropertyChanged
    {
        private string _accountName = "尚未登录";
        public event PropertyChangedEventHandler? PropertyChanged;
        public string AccountName
        {
            get => _accountName;
            set { _accountName = value; PropertyChanged?.Invoke(this, new(nameof(AccountName))); }
        }
    }

    /// <summary>生产搜索和版本输入必须完整显示一行；仅 Text 值正确无法发现内边距重复导致的裁切。</summary>
    private static void VerifySingleLineTextVisibility(Border host)
    {
        var page = new OnlineModsView { FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14 };
        TextOptions.SetTextFormattingMode(page, TextFormattingMode.Display);
        host.Child = page;
        Layout(host);
        var search = Assert.IsType<TextBox>(page.FindName("SearchInput"));
        var version = Assert.Single(VisualDescendants(page).OfType<TextBox>(), textBox =>
            BindingOperations.GetBinding(textBox, TextBox.TextProperty)?.Path.Path == "GameVersion");
        search.SetCurrentValue(TextBox.TextProperty, "应用能源 AE2");
        version.SetCurrentValue(TextBox.TextProperty, "1.20.1");
        Layout(host);
        WriteTextPreview(page);
        var results = new List<(bool Visible, string Detail)>
        {
            MeasureSingleLineVisibility(search, "在线搜索"),
            MeasureSingleLineVisibility(version, "游戏版本")
        };

        var ordinary = new TextBox
        {
            Text = "应用能源 AE2", Height = 40, Width = 300,
            FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        TextOptions.SetTextFormattingMode(ordinary, TextFormattingMode.Display);
        host.Child = ordinary;
        Layout(host);
        results.Add(MeasureSingleLineVisibility(ordinary, "普通输入框"));
        Assert.True(results.All(result => result.Visible), string.Join(Environment.NewLine, results.Select(result => result.Detail)));
    }

    /// <summary>按测试调用方指定的目录导出实际页面离屏渲染，便于检查字形；默认不生成文件。</summary>
    private static void WriteTextPreview(FrameworkElement page, string fileName = "textbox-content.png")
    {
        var directory = Environment.GetEnvironmentVariable("EML_TEXTBOX_PREVIEW_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(page.ActualWidth), (int)Math.Ceiling(page.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, fileName));
        encoder.Save(file);
    }

    /// <summary>以较小窗口显示专区各页，验证分类、规则说明、目标选择和滚动布局。</summary>
    private static void VerifyGtnhPages(Border host)
    {
        foreach (var (section, nonStargate) in new[] { (0, false), (0, true), (1, false), (2, false), (3, false) })
        {
            var model = new GtnhPreview(section, nonStargate);
            var page = new GtnhHubView
            {
                DataContext = model, FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14,
                Background = (Brush)Application.Current.FindResource("WindowBackgroundBrush")
            };
            host.Child = page;
            Layout(host);
            var target = Assert.Single(VisualDescendants(page).OfType<ComboBox>(), item =>
                BindingOperations.GetBinding(item, Selector.SelectedItemProperty)?.Path.Path == "SelectedTarget");
            Assert.Same(model.SelectedTarget, target.SelectedItem);
            Assert.Contains(VisualDescendants(target).OfType<TextBlock>(), item => item.Text.Contains("GTNH 2.9.0"));
            var visibleText = VisualDescendants(page).OfType<TextBlock>().Where(item => item.IsVisible || IsLocallyVisible(item, page))
                .Select(item => item.Text).ToArray();
            Assert.Contains(section == 1 ? "把熟悉的世界，带到新版 GTNH" : "安装前看这里", visibleText);
            if (nonStargate) Assert.Contains("非星门规则", visibleText);
            WriteTextPreview(page, $"gtnh-section-{section}-{nonStargate}.png");
            if (section != 1)
            {
                var category = Assert.Single(VisualDescendants(page).OfType<ComboBox>(), item =>
                    BindingOperations.GetBinding(item, Selector.SelectedItemProperty)?.Path.Path == "SelectedCategory");
                Assert.Equal(model.SelectedCategory, category.SelectedItem);
                Assert.Contains("来源：" + model.SelectedAddon.SourceDisplay, visibleText);
                var search = Assert.Single(VisualDescendants(page).OfType<TextBox>(), item =>
                    BindingOperations.GetBinding(item, TextBox.TextProperty)?.Path.Path == "SearchText");
                search.SetCurrentValue(TextBox.TextProperty, "建筑工具 WorldEdit");
                Layout(host);
                var visibility = MeasureSingleLineVisibility(search, "GTNH 模组搜索");
                Assert.True(visibility.Visible, visibility.Detail);
                var detail = VisualDescendants(page).OfType<ScrollViewer>().Single(item =>
                    item.Content is StackPanel panel && panel.Children.OfType<CheckBox>().Any());
                Assert.True(detail.ViewportHeight > 100);
                detail.ScrollToBottom(); Layout(host);
                Assert.True(detail.VerticalOffset > 0, "小窗口必须能滚动到安装操作和结果。");
            }
        }
    }

    /// <summary>空实例库也能使用旧实例导入入口，小窗口可以滚动到迁移操作。</summary>
    private static void VerifyGtnhMigrationImportEntry(Border host)
    {
        var import = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        var page = new GtnhHubView
        {
            DataContext = new
            {
                IsMigration = true, IsCatalogPage = false, IsPackDownloads = false,
                HasTarget = false, CanInteract = true, Targets = Array.Empty<GtnhInstanceInfo>(),
                ImportOldInstanceCommand = import, IsImportingOldInstance = false,
                MigrationImportHint = "导入后会自动补全依赖，再选择新版作为接收方；不会自动开始迁移。",
                Instances = new { TransferPercent = 0, TransferStatus = "", MigrationStatus = "", MigrationJournalPath = "" }
            },
            FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14,
            Background = (Brush)Application.Current.FindResource("WindowBackgroundBrush")
        };
        host.Child = page;
        Layout(host);
        var button = Assert.IsType<Button>(page.FindName("ImportOldInstanceButton"));
        Assert.Same(import, button.Command);
        Assert.True(button.IsEnabled);
        Assert.True(IsLocallyVisible(button, page));
        var scroll = Assert.IsType<ScrollViewer>(page.FindName("MigrationScroll"));
        Assert.True(scroll.ViewportHeight > 0);
        button.BringIntoView(); Layout(host);
        var bounds = button.TransformToAncestor(page).TransformBounds(new Rect(button.RenderSize));
        Assert.True(bounds.Left >= 0 && bounds.Right <= page.ActualWidth);
        Assert.True(bounds.Top >= 0 && bounds.Bottom <= page.ActualHeight);
        var migration = Assert.Single(VisualDescendants(page).OfType<Button>(), item => Equals(item.Content, "从旧 GTNH 迁入数据"));
        Assert.False(migration.IsEnabled);
        scroll.ScrollToBottom(); Layout(host);
        Assert.True(scroll.VerticalOffset > 0, "小窗口必须能滚动阅读迁移步骤及结果。");
        scroll.ScrollToTop(); Layout(host);
        WritePageOverview(host, page, "gtnh-migration-import.png");
    }

    /// <summary>用生产镜像下载模型验证空实例库的入口、小窗口滚动和 Java 筛选，不访问网络。</summary>
    private static void VerifyGtnhPackDownloads(Border host)
    {
        using var model = new GtnhPackDownloadViewModel(new PreviewGtnhPacks(),
            (_, _, _) => throw new InvalidOperationException("离屏检查不能下载整合包。"), () => true);
        model.SetActive(true);
        Assert.True(model.ReleasesLoadTask.IsCompletedSuccessfully);
        var page = new GtnhHubView
        {
            DataContext = new
            {
                IsPackDownloads = true, IsCatalogPage = false, IsMigration = false,
                Packs = model, Targets = Array.Empty<GtnhInstanceInfo>(), CanInteract = true,
                Instances = new { TransferPercent = 0, TransferStatus = "" }
            },
            FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14,
            Background = (Brush)Application.Current.FindResource("WindowBackgroundBrush")
        };
        host.Child = page;
        Layout(host);
        var downloads = Assert.IsType<GtnhPackDownloadView>(page.FindName("PackDownloadPage"));
        Assert.True(IsLocallyVisible(downloads, page));
        Assert.Same(model, downloads.DataContext);
        var list = Assert.IsType<ListBox>(downloads.FindName("ReleaseList"));
        Assert.True(list.ActualHeight > 0);
        list.SelectedItem = model.Releases[0];
        Layout(host);
        Assert.Same(list.SelectedItem, model.SelectedRelease);
        var information = Assert.IsType<TextBox>(downloads.FindName("ReleaseInformationText"));
        Assert.True(information.IsReadOnly);
        Assert.Contains(model.SelectedRelease!.FileName, information.Text);
        var install = Assert.Single(VisualDescendants(downloads).OfType<Button>(), item => Equals(item.Content, "下载并导入"));
        Assert.Same(model.InstallCommand, install.Command);
        Assert.True(install.IsEnabled);
        var bounds = install.TransformToAncestor(page).TransformBounds(new Rect(install.RenderSize));
        Assert.True(bounds.Top >= 0 && bounds.Bottom <= page.ActualHeight);
        Assert.True(bounds.Left >= 0 && bounds.Right <= page.ActualWidth);
        var details = Assert.IsType<ScrollViewer>(downloads.FindName("DetailsScroll"));
        Assert.True(details.ViewportHeight > 0);
        details.ScrollToBottom(); Layout(host);
        Assert.True(details.VerticalOffset > 0, "小窗口必须能阅读本体来源和依赖补全说明。");
        WriteTextPreview(page, "gtnh-pack-downloads.png");
        var filter = Assert.IsType<ComboBox>(downloads.FindName("JavaFilter"));
        filter.SelectedIndex = 2;
        Layout(host);
        Assert.All(model.Releases, release => Assert.Equal("8", release.JavaVersion));
        model.IsInstalling = true;
        Layout(host);
        Assert.False(filter.IsEnabled);
        Assert.False(list.IsEnabled);
        Assert.False(install.IsEnabled);
        model.IsInstalling = false;
    }

    /// <summary>只返回目录样本，不允许实际准备原包。</summary>
    private sealed class PreviewGtnhPacks : IGtnhPackService
    {
        /// <summary>提供同一 GTNH 版本的两个 Java 变体，验证按要求筛选。</summary>
        public Task<IReadOnlyList<GtnhPackRelease>> GetReleasesAsync(bool includePrereleases = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GtnhPackRelease>>([Pack("17-26"), Pack("8")]);
        /// <summary>视图测试不能执行下载或写入。</summary>
        public Task<PreparedInstanceArchive> PrepareAsync(string releaseId, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("不允许真实下载。");
        /// <summary>构造显示所需的版本、文件名和镜像大小信息。</summary>
        private static GtnhPackRelease Pack(string java) => new()
        {
            Id = $"GT_New_Horizons_2.9.0_Java_{java}.zip", Version = "2.9.0", JavaVersion = java,
            FileName = $"GT_New_Horizons_2.9.0_Java_{java}.zip", Size = 747848389
        };
    }

    /// <summary>离屏元素没有系统窗口，按祖先 Visibility 判断本页是否展开。</summary>
    private static bool IsLocallyVisible(DependencyObject item, DependencyObject page)
    {
        for (var current = item; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element && element.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(current, page)) return true;
        }
        return false;
    }

    /// <summary>仅供离屏绑定的预览数据，不创建服务、不执行下载或修改实例。</summary>
    private sealed class GtnhPreview(int section, bool nonStargate)
    {
        public GtnhInstanceInfo[] Targets { get; } = [new("C:\\preview", "GTNH 2.9.0 · 生存服", "C:\\preview\\.minecraft", "2.9.0")];
        public GtnhInstanceInfo SelectedTarget { get => Targets[0]; set { } }
        public GtnhAddon[] Addons { get; } = [new("preview",
            section == 2 ? "GTNH 深色界面 · 示例" : section == 3 ? "光影项目 · 示例" : nonStargate ? "扭曲时空科技 · 示例" : "WorldEdit · 建筑工具",
            "安装前请确认目标 GTNH 版本，阅读作者的兼容说明。", section == 2 ? "深色模式" : section == 3 ? "光影" : nonStargate ? "流程修改" : "功能增强",
            "GTNewHorizons/WorldEdit", "选择适合目标版本的发行文件；多人游戏还需服务端支持及相应权限。")
            { IsNonStargate = nonStargate, Kind = section == 2 ? GtnhAddonKind.ResourcePack : section == 3 ? GtnhAddonKind.Shader : GtnhAddonKind.Mod }];
        public GtnhAddon SelectedAddon { get => Addons[0]; set { } }
        public GtnhAddonRelease[] Releases { get; } = [new() { AddonId = "preview", Repository = "GTNewHorizons/WorldEdit", Tag = "示例版本", FileName = "worldedit-preview.jar", Size = 123456, DownloadUrl = "https://github.com/", ReleaseUrl = "https://github.com/", Notes = "请阅读作者的 GTNH 版本兼容说明。", Published = DateTimeOffset.Parse("2026-11-09") }];
        public GtnhAddonRelease SelectedRelease { get => Releases[0]; set { } }
        public bool CanInteract => true;
        public bool HasTarget => true;
        public bool IsRecommendations => section == 0;
        public bool IsMigration => section == 1;
        public bool IsResourcePacks => section == 2;
        public bool IsShaders => section == 3;
        public bool IsPackDownloads => false;
        public bool IsCatalogPage => section != 1;
        public bool IsNonStargateAddon => nonStargate;
        public bool HasSelectedAddon => true;
        public bool IsCatalogEmpty => false;
        public string CatalogEmptyMessage => "没有匹配的项目。";
        public string CatalogSummary => "当前显示 1 个项目";
        public string InstallationHint => section == 2 ? "安装到 resourcepacks，请在游戏中自行启用。" : section == 3 ? "安装到 shaderpacks，请核对加载器兼容性。" : "额外模组可在模组管理中启用或禁用。";
        public string[] Categories => ["全部分类", SelectedAddon.Category];
        public string SelectedCategory { get; set; } = "全部分类";
        public string[] RuleFilters => ["全部规则分组", "常规分类", "非星门规则"];
        public int RuleFilterIndex { get; set; }
        public bool IsInstalling => false;
        public bool IsImportingOldInstance => false;
        public ICommand ImportOldInstanceCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        public string MigrationImportHint => "导入后会自动补全依赖，再选择新版作为接收方；不会自动开始迁移。";
        public bool CompatibilityConfirmed { get; set; }
        public string SearchText { get; set; } = "";
        public string TargetStatus => "已识别 2 个 GTNH 实例。推荐模组安装与数据迁移都使用上方选择的目标。";
        public string ReleaseStatus => "请选择发行版本并核对适配说明。不会自动采用最新版本。";
        public string InstallStatus => "确认目标实例和发行说明后再安装。";
        public string DownloadText => "";
        public double DownloadPercent => 0;
        public object Instances { get; } = new { TransferPercent = 0, TransferStatus = "", MigrationStatus = "选择旧实例后可预览迁移范围。", MigrationJournalPath = "" };
    }

    /// <summary>读取实际文本视口和首尾字符矩形，确认行高与文字边界均没有被内容区裁掉。</summary>
    private static (bool Visible, string Detail) MeasureSingleLineVisibility(TextBox textBox, string name)
    {
        var scroll = Assert.IsAssignableFrom<ScrollViewer>(textBox.Template.FindName("PART_ContentHost", textBox));
        var presenter = Assert.Single(VisualDescendants(scroll).OfType<ScrollContentPresenter>());
        var viewport = presenter.TransformToAncestor(textBox).TransformBounds(new Rect(presenter.RenderSize));
        var first = textBox.GetRectFromCharacterIndex(0);
        var last = textBox.GetRectFromCharacterIndex(textBox.Text.Length - 1, trailingEdge: true);
        var line = new FormattedText(textBox.Text, CultureInfo.InvariantCulture, textBox.FlowDirection,
            new Typeface(textBox.FontFamily, textBox.FontStyle, textBox.FontWeight, textBox.FontStretch),
            textBox.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(textBox).PixelsPerDip);
        const double tolerance = 0.5;
        var visible = !first.IsEmpty && !last.IsEmpty && scroll.ViewportHeight + tolerance >= line.Height &&
            viewport.Height + tolerance >= first.Height && first.Top + tolerance >= viewport.Top &&
            first.Bottom <= viewport.Bottom + tolerance && first.Left + tolerance >= viewport.Left &&
            last.Right <= viewport.Right + tolerance;
        return (visible, $"{name}: visible={visible}, size={textBox.ActualWidth:F2}×{textBox.ActualHeight:F2}, " +
            $"font={textBox.FontFamily.Source}/{textBox.FontSize}, TextBox.Padding={textBox.Padding}, " +
            $"ContentHost.Margin={scroll.Margin}, ContentHost.Padding={scroll.Padding}, " +
            $"Viewport={scroll.ViewportWidth:F2}×{scroll.ViewportHeight:F2}, visibleRect={viewport}, " +
            $"lineHeight={line.Height:F2}, firstChar={first}, lastChar={last}");
    }

    /// <summary>直接使用生产隐式样式，验证选项模板、路径编辑和多行只读文本仍保持原生行为。</summary>
    private static void VerifyInputsAndDropdowns(Border host)
    {
        var items = Enumerable.Range(1, 18).Select(index => new DropdownOption($"Java 环境 {index}")).ToArray();
        var itemTemplate = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <TextBlock Text="{Binding DisplayName}"/>
            </DataTemplate>
            """);
        var regular = new ComboBox { ItemsSource = items, ItemTemplate = itemTemplate, SelectedIndex = 1, MaxDropDownHeight = 140 };
        var path = new JavaPathInput();
        var editable = new ComboBox { IsEditable = true, IsTextSearchEnabled = false, DataContext = path };
        editable.SetBinding(ComboBox.TextProperty, new Binding(nameof(JavaPathInput.JavaPath))
        { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        var multiline = new TextBox
        {
            Text = "第一行：保留可复制的状态说明\n第二行：较长的检查结果应正常换行和滚动。",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsReadOnly = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 90
        };
        var panel = new StackPanel { Width = 400, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(regular); panel.Children.Add(editable); panel.Children.Add(multiline);
        host.Child = panel;
        Layout(host);

        var selectedContent = Assert.IsType<ContentPresenter>(regular.Template.FindName("SelectionPresenter", regular));
        Assert.Equal(Visibility.Visible, selectedContent.Visibility);
        Assert.Same(itemTemplate, selectedContent.ContentTemplate);
        Assert.Contains(VisualDescendants(selectedContent).OfType<TextBlock>(), block => block.Text == items[1].DisplayName);
        regular.SelectedIndex = 0;
        Layout(host);
        Assert.Contains(VisualDescendants(selectedContent).OfType<TextBlock>(), block => block.Text == items[0].DisplayName);
        VerifyClosedDropdownLayout(regular, items[0].DisplayName);

        var toggle = Assert.IsType<ToggleButton>(regular.Template.FindName("DropDownToggle", regular));
        regular.IsEnabled = false;
        Layout(host);
        Assert.False(toggle.IsEnabled);
        Assert.Same(items[0], regular.SelectedItem);
        Assert.Contains(VisualDescendants(selectedContent).OfType<TextBlock>(), block => block.Text == items[0].DisplayName);
        regular.IsEnabled = true;

        var editor = Assert.IsAssignableFrom<TextBox>(editable.Template.FindName("PART_EditableTextBox", editable));
        var editToggle = Assert.IsType<ToggleButton>(editable.Template.FindName("DropDownToggle", editable));
        var editSelection = Assert.IsType<ContentPresenter>(editable.Template.FindName("SelectionPresenter", editable));
        Assert.Equal(Visibility.Visible, editor.Visibility);
        Assert.NotEqual(Visibility.Visible, editSelection.Visibility);
        Assert.False(editor.IsReadOnly);
        Assert.Equal(path.JavaPath, editor.Text);
        path.JavaPath = @"D:\Java\jdk-25\bin\java.exe";
        Layout(host);
        Assert.Equal(path.JavaPath, editable.Text);
        Assert.Equal(path.JavaPath, editor.Text);
        editor.Text = @"E:\自定义 Java\bin\java.exe";
        Layout(host);
        Assert.Equal(editor.Text, editable.Text);
        Assert.Equal(editor.Text, path.JavaPath);
        Assert.NotNull(BindingOperations.GetBindingExpression(editable, ComboBox.TextProperty));

        var editorBounds = editor.TransformToAncestor(editable).TransformBounds(new Rect(editor.RenderSize));
        var arrowBounds = editToggle.TransformToAncestor(editable).TransformBounds(new Rect(editToggle.RenderSize));
        Assert.True(editorBounds.Width > 0 && arrowBounds.Width > 0);
        Assert.True(arrowBounds.Left >= editorBounds.Right - 0.5, "可编辑区域不能被下拉按钮覆盖，否则无法点选 Java 路径文字。");
        editable.IsReadOnly = true;
        Layout(host);
        Assert.True(editor.IsReadOnly);
        Assert.True(editToggle.IsEnabled);
        editable.IsEnabled = false;
        Layout(host);
        Assert.False(editor.IsEnabled);
        Assert.False(editToggle.IsEnabled);
        Assert.False(editable.IsDropDownOpen);

        Assert.IsAssignableFrom<ScrollViewer>(multiline.Template.FindName("PART_ContentHost", multiline));
        Assert.True(multiline.LineCount >= 2);
        multiline.SelectAll();
        Assert.Equal(multiline.Text, multiline.SelectedText);
        Assert.True(multiline.IsReadOnly);
    }

    /// <summary>只离屏测量关闭的 Popup 子树，验证高度上限和选项模板，不创建弹出窗口。</summary>
    private static void VerifyClosedDropdownLayout(ComboBox comboBox, string selectedName)
    {
        var popup = Assert.IsType<Popup>(comboBox.Template.FindName("PART_Popup", comboBox));
        Assert.False(popup.IsOpen);
        var dropdown = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
        foreach (var height in new[] { 140d, 90d })
        {
            comboBox.MaxDropDownHeight = height;
            dropdown.Measure(new Size(comboBox.ActualWidth, double.PositiveInfinity));
            dropdown.Arrange(new Rect(new Point(), dropdown.DesiredSize));
            dropdown.UpdateLayout();
            var scroll = Assert.Single(VisualDescendants(dropdown).OfType<ScrollViewer>());
            Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
            Assert.True(scroll.ActualHeight > 0 && scroll.ActualHeight <= height,
                "下拉选项的滚动区域必须遵守 MaxDropDownHeight。");
        }
        var selected = Assert.IsType<ComboBoxItem>(comboBox.ItemContainerGenerator.ContainerFromIndex(comboBox.SelectedIndex));
        Assert.True(selected.IsSelected);
        Assert.Contains(VisualDescendants(selected).OfType<TextBlock>(), block => block.Text == selectedName);
        selected.IsEnabled = false;
        dropdown.UpdateLayout();
        Assert.False(selected.IsEnabled);
        Assert.True(selected.IsSelected);
        Assert.Contains(VisualDescendants(selected).OfType<TextBlock>(), block => block.Text == selectedName);
        Assert.False(popup.IsOpen);
    }

    /// <summary>真实双向绑定在初始化和关闭后保持 -1，防止 TabControl 自动选首项并弹出工作区。</summary>
    private static void VerifyInitiallyClosedWorkspace(Border host, UserControl workspace, TabControl sections, WorkspaceSelection selection)
    {
        Layout(host);
        sections.ApplyTemplate();
        Assert.Equal(-1, sections.SelectedIndex);
        Assert.Equal(-1, selection.SelectedDetailTab);
        Assert.Empty(selection.Changes);

        workspace.Visibility = Visibility.Visible;
        Layout(host);
        Assert.Equal(-1, sections.SelectedIndex);
        Assert.Empty(selection.Changes);

        selection.SelectedDetailTab = 0;
        Layout(host);
        Assert.Equal(0, sections.SelectedIndex);
        selection.SelectedDetailTab = -1;
        workspace.Visibility = Visibility.Collapsed;
        Layout(host);
        workspace.Visibility = Visibility.Visible;
        Layout(host);
        Assert.Equal(-1, sections.SelectedIndex);
        Assert.Equal(-1, selection.SelectedDetailTab);
        Assert.Equal(new[] { 0, -1 }, selection.Changes);
    }

    /// <summary>使用较小的内容区完成真实测量与布局；展开说明内容，避免遗漏延迟创建的模板。</summary>
    private static void LayoutAndVerify(Border host, UserControl page)
    {
        Layout(host);
        foreach (var expander in VisualDescendants(page).OfType<Expander>().ToArray()) expander.IsExpanded = true;
        Layout(host);
        Assert.Equal(720d, page.ActualWidth);
        Assert.Equal(520d, page.ActualHeight);
        Assert.True(page.IsMeasureValid && page.IsArrangeValid);

        var controls = VisualDescendants(page).OfType<Control>().ToArray();
        Assert.NotEmpty(controls);
        foreach (var textBox in controls.OfType<TextBox>())
        {
            // 缺少该命名部件时控件仍能创建，但实际输入和复制文本会失效。
            Assert.NotNull(textBox.Template.FindName("PART_ContentHost", textBox));
        }
        foreach (var comboBox in controls.OfType<ComboBox>().Where(item => item.IsEditable))
        {
            Assert.IsAssignableFrom<TextBox>(comboBox.Template.FindName("PART_EditableTextBox", comboBox));
            Assert.NotNull(comboBox.Template.FindName("PART_Popup", comboBox));
        }
        foreach (var button in controls.OfType<Button>().Where(button => UiMotion.GetPress(button))) VerifyButtonFeedback(button);

        page.Opacity = 0.8;
        UiMotion.SetEnter(page, true);
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        page.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        UiMotion.SetEnter(page, false);
        Assert.Equal(0.8, page.Opacity);
        Assert.False(page.HasAnimatedProperties);
        Layout(host);
    }

    /// <summary>动画只操作模板内部缩放，关闭或禁用时恢复尺寸，保留外部布局变换。</summary>
    private static void VerifyButtonFeedback(Button button)
    {
        var scale = Assert.IsType<ScaleTransform>(button.Template.FindName("InteractionScale", button));
        var externalTransform = new TranslateTransform(3, 4);
        button.RenderTransform = externalTransform;
        button.SetCurrentValue(UIElement.IsEnabledProperty, true);
        UiMotion.SetPress(button, true);
        button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
        button.SetCurrentValue(UIElement.IsEnabledProperty, false);
        Assert.Equal(1d, scale.ScaleX);
        Assert.Equal(1d, scale.ScaleY);
        Assert.False(scale.HasAnimatedProperties);
        UiMotion.SetPress(button, false);
        Assert.Same(externalTransform, button.RenderTransform);
        button.SetCurrentValue(UIElement.IsEnabledProperty, true);
    }

    /// <summary>离屏布局不会创建 HWND、弹出窗口或执行页面绑定命令。</summary>
    private static void Layout(Border host)
    {
        host.Measure(new Size(720, 520));
        host.Arrange(new Rect(0, 0, 720, 520));
        host.UpdateLayout();
    }

    /// <summary>展开实际可视树并应用控件模板，触发编译 XAML 中的资源引用解析。</summary>
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        if (root is Control control) control.ApplyTemplate();
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in VisualDescendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    /// <summary>只模拟导航选中值并记录全部变更；没有文件、网络服务或自动扫描行为。</summary>
    private sealed class WorkspaceSelection : INotifyPropertyChanged
    {
        private int _selectedDetailTab = -1;
        public List<int> Changes { get; } = [];
        public event PropertyChangedEventHandler? PropertyChanged;
        public int SelectedDetailTab
        {
            get => _selectedDetailTab;
            set
            {
                if (_selectedDetailTab == value) return;
                _selectedDetailTab = value;
                Changes.Add(value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDetailTab)));
            }
        }
    }

    private sealed record DropdownOption(string DisplayName);

    /// <summary>模拟可编辑 Java 路径，确认控件模板不会切断调用方的双向绑定。</summary>
    private sealed class JavaPathInput : INotifyPropertyChanged
    {
        private string _javaPath = @"C:\Program Files\Java\bin\java.exe";
        public event PropertyChangedEventHandler? PropertyChanged;
        public string JavaPath
        {
            get => _javaPath;
            set
            {
                if (_javaPath == value) return;
                _javaPath = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(JavaPath)));
            }
        }
    }
}
