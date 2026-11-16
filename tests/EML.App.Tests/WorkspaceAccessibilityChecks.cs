using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.Input;
using EML.App.Views;
using EML.Core.Minecraft;

namespace EML.App.Tests;

/// <summary>验证实例工作区在真实主页面控件树中可被辅助工具访问，不调用实例或下载服务。</summary>
internal static class WorkspaceAccessibilityChecks
{
    /// <summary>复用烟雾测试唯一的 STA/Application，检查页面切换、关闭重开和操作权限。</summary>
    internal static void Run()
    {
        var model = new ShellModel();
        var shell = LoadShell(model);
        try
        {
            var overlay = Assert.IsType<Grid>(shell.FindName("WorkspaceOverlay"));
            var list = Assert.IsType<ListBox>(shell.FindName("InstanceList"));
            var navigation = Assert.IsType<ListBox>(shell.FindName("NavigationList"));
            var close = Assert.IsType<Button>(shell.FindName("CloseWorkspaceButton"));
            var workspace = VisualDescendants(overlay).OfType<InstanceWorkspaceView>().Single();
            var sections = Assert.IsAssignableFrom<TabControl>(workspace.Content);
            Assert.Equal(8, sections.Items.Count);
            Assert.Equal(-1, sections.SelectedIndex);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);

            // 从同一个根节点连续查询，防止仅创建新 Peer 的测试掩盖页面切换后的陈旧子树。
            var rootPeer = UIElementAutomationPeer.CreatePeerForElement(shell)!;
            _ = PeerDescendants(rootPeer).ToArray();
            var visited = new Dictionary<int, FrameworkElement>();
            foreach (var index in new[] { 1, 4, 6, 0, 2, 3, 5, 7, 1, 6, 4 })
            {
                model.Instances.SelectedDetailTab = index;
                Layout(shell);
                Assert.Equal(index, sections.SelectedIndex);
                Assert.Equal(Visibility.Visible, overlay.Visibility);
                Assert.True(overlay.IsEnabled);
                Assert.False(list.IsEnabled);
                Assert.True(navigation.IsEnabled);
                var action = FindSectionAction(workspace, index);
                visited[index] = action;
                var peers = PeerDescendants(rootPeer).ToArray();
                var sectionPeer = FindPeer(peers, sections);
                Assert.Equal(AutomationControlType.Group, sectionPeer.GetAutomationControlType());
                Assert.Equal(((TabItem)sections.Items[index]).Header, sectionPeer.GetName());
                Assert.Null(sectionPeer.GetPattern(PatternInterface.Selection));
                Assert.DoesNotContain(PeerDescendants(sectionPeer), peer => peer is TabItemAutomationPeer);
                var actionPeer = FindPeer(peers, action);
                Assert.True(action.ActualWidth > 0 && action.ActualHeight > 0);
                Assert.True(actionPeer.IsEnabled());
                Assert.True(FindPeer(peers, close).IsEnabled());
                Assert.True(FindPeer(peers, navigation).IsEnabled());
                foreach (var previous in visited.Where(item => item.Key != index))
                    Assert.DoesNotContain(peers.OfType<UIElementAutomationPeer>(), peer => ReferenceEquals(peer.Owner, previous.Value));

                if (action is Button button)
                {
                    Assert.Equal(button.Content, actionPeer.GetName());
                    Assert.IsAssignableFrom<IInvokeProvider>(actionPeer.GetPattern(PatternInterface.Invoke));
                    Assert.True(button.Focusable && button.IsTabStop);
                }
            }

            VerifyCommandState(shell, model, rootPeer, workspace, close, navigation);

            model.Instances.SelectedDetailTab = -1;
            Layout(shell);
            Assert.Equal(-1, sections.SelectedIndex);
            Assert.Null(sections.SelectedContent);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            var closedPeers = PeerDescendants(rootPeer).OfType<UIElementAutomationPeer>().ToArray();
            Assert.DoesNotContain(closedPeers, peer => visited.Values.Any(action => ReferenceEquals(peer.Owner, action)));
            Assert.True(list.IsEnabled);

            model.Instances.SelectedDetailTab = 6;
            Layout(shell);
            Assert.Equal(6, sections.SelectedIndex);
            Assert.Same(visited[6], FindSectionAction(workspace, 6));
            Assert.True(FindPeer(PeerDescendants(rootPeer), visited[6]).IsEnabled());

            // 可写控件的绑定也必须保留；无障碍修复不得将展示内容复制成失去绑定的另一份视图。
            model.Instances.SelectedDetailTab = 7;
            Layout(shell);
            var name = VisualDescendants(workspace).OfType<TextBox>()
                .Single(control => AutomationProperties.GetName(control) == "新的实例名称");
            name.SetCurrentValue(TextBox.TextProperty, "中文实例 · 重命名测试");
            Assert.Equal(name.Text, model.Instances.NewName);
            Assert.True(FindPeer(PeerDescendants(rootPeer), name).IsEnabled());
        }
        finally { shell.Content = null; }
    }

    /// <summary>通过 UIA Invoke 执行内存命令，并验证忙碌时按钮仍可识别但不可执行。</summary>
    private static void VerifyCommandState(WorkspaceAccessibilityShell shell, ShellModel model, AutomationPeer rootPeer,
        InstanceWorkspaceView workspace, Button close, ListBox navigation)
    {
        model.Instances.SelectedDetailTab = 1;
        Layout(shell);
        var quickCheck = Assert.IsType<Button>(FindSectionAction(workspace, 1));
        var peer = FindPeer(PeerDescendants(rootPeer), quickCheck);
        var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        invoke.Invoke();
        Layout(shell);
        Assert.Equal(1, model.Instances.QuickCheckCount);

        model.Instances.IsBusy = true;
        Layout(shell);
        Assert.Same(peer, FindPeer(PeerDescendants(rootPeer), quickCheck));
        Assert.False(peer.IsEnabled());
        Assert.False(quickCheck.IsEnabled);
        Assert.Throws<ElementNotEnabledException>(() => invoke.Invoke());
        Assert.Equal(1, model.Instances.QuickCheckCount);
        Assert.True(FindPeer(PeerDescendants(rootPeer), close).IsEnabled());
        Assert.True(navigation.IsEnabled);

        model.Instances.IsBusy = false;
        Layout(shell);
        Assert.True(peer.IsEnabled());
    }

    /// <summary>每页选择实际显示的控件，断言按对象身份定位，避免匹配到主页面同名按钮。</summary>
    private static FrameworkElement FindSectionAction(InstanceWorkspaceView workspace, int index)
    {
        if (index == 0) return VisualDescendants(workspace).OfType<TextBox>().First();
        var names = new Dictionary<int, string>
        {
            [1] = "快速检查", [2] = "补全实例依赖", [3] = "前往启动设置", [4] = "停止游戏",
            [5] = "前往 GTNH 数据迁移", [6] = "添加本地模组", [7] = "保存名称"
        };
        return VisualDescendants(workspace).OfType<Button>().Single(button => Equals(button.Content, names[index]));
    }

    /// <summary>要求控件从主界面的自动化树真实可达，而不是单独为目标创建一个孤立 Peer。</summary>
    private static UIElementAutomationPeer FindPeer(IEnumerable<AutomationPeer> peers, FrameworkElement element)
    {
        var snapshot = peers.ToArray();
        var matches = snapshot.OfType<UIElementAutomationPeer>().Where(peer => ReferenceEquals(peer.Owner, element)).ToArray();
        Assert.True(matches.Length == 1, $"Expected one reachable {element.GetType().Name} '{(element as ContentControl)?.Content}', found {matches.Length}. "
            + string.Join(" | ", snapshot.Select(peer => $"{peer.GetType().Name}:{peer.GetName()}")));
        return matches[0];
    }

    /// <summary>使用生产主窗口标记，移除业务构造器和事件处理器，保留模板与页面绑定。</summary>
    private static WorkspaceAccessibilityShell LoadShell(ShellModel model)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("EML.App.Tests.MainWindow.preview.xaml")!;
        using var text = new StreamReader(stream);
        var markup = text.ReadToEnd()
            .Replace("clr-namespace:EML.App.Views\"", "clr-namespace:EML.App.Views;assembly=EML.App\"", StringComparison.Ordinal)
            .Replace("clr-namespace:EML.App.Services\"", "clr-namespace:EML.App.Services;assembly=EML.App\"", StringComparison.Ordinal);
        var document = XDocument.Parse(markup);
        var root = document.Root!;
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace preview = "clr-namespace:EML.App.Tests;assembly=EML.App.Tests";
        root.Name = preview + nameof(WorkspaceAccessibilityShell);
        root.Add(new XAttribute(XNamespace.Xmlns + "preview", preview.NamespaceName));
        root.Attribute(xaml + "Class")?.Remove();
        foreach (var name in new[] { "Title", "WindowStartupLocation", "MinWidth", "MinHeight" }) root.Attribute(name)?.Remove();
        root.SetAttributeValue("Width", 1280);
        root.SetAttributeValue("Height", 820);
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name == presentation + "Window.Resources") element.Name = preview + "WorkspaceAccessibilityShell.Resources";
            foreach (var attribute in element.Attributes().ToArray())
            {
                if (attribute.Name == xaml + "Shared" || attribute.Value.StartsWith("On", StringComparison.Ordinal)
                    && new[] { "KeyDown", "Click", "MouseLeftButtonDown", "IsVisibleChanged", "Handler" }.Contains(attribute.Name.LocalName))
                    attribute.Remove();
                else if (attribute.Value.Contains("AncestorType=Window", StringComparison.Ordinal))
                    attribute.Value = attribute.Value.Replace("AncestorType=Window", "AncestorType=preview:WorkspaceAccessibilityShell", StringComparison.Ordinal);
            }
        }
        root.Descendants(presentation + "EventSetter").Remove();
        using var reader = document.CreateReader();
        var shell = Assert.IsType<WorkspaceAccessibilityShell>(XamlReader.Load(reader));
        shell.DataContext = model;
        Layout(shell);
        return shell;
    }

    /// <summary>离屏布局后处理正常 Dispatcher 队列，让绑定、命令及自动化失效通知完成。</summary>
    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>只遍历已呈现的可视子树，不把未选页的逻辑控件误算为当前页面。</summary>
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        if (root is Control control) control.ApplyTemplate();
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in VisualDescendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    /// <summary>沿辅助工具可用的父子关系递归，验证实际可达性。</summary>
    private static IEnumerable<AutomationPeer> PeerDescendants(AutomationPeer root)
    {
        yield return root;
        foreach (var child in root.GetChildren() ?? [])
            foreach (var peer in PeerDescendants(child)) yield return peer;
    }

    /// <summary>模拟主页面导航；从实例索引派生面板显隐，不创建真实业务对象。</summary>
    private sealed class ShellModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public InstanceModel Instances { get; } = new();
        public ShellModel() => Instances.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(InstanceModel.SelectedDetailTab))
                PropertyChanged?.Invoke(this, new(nameof(IsWorkspaceVisible)));
        };
        public bool IsWorkspaceVisible => Instances.SelectedDetailTab >= 0;
        public bool IsInstancePage => true;
        public bool IsOnlinePage => false;
        public bool IsAccountsPage => false;
        public bool IsJavaPage => false;
        public bool IsGtnhPage => false;
        public bool IsLaunchSettingsPage => false;
        public bool IsDownloadsPage => false;
        public bool IsDownloadCenterPage => false;
        public int SelectedPage { get; set; }
        public IRelayCommand CloseInstanceWorkspaceCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand OpenLaunchSettingsCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand OpenGtnhMigrationCommand { get; } = new RelayCommand(() => { });
    }

    /// <summary>命令只记录计数，路径仅用于显示；测试不访问任何实例文件。</summary>
    private sealed class InstanceModel : INotifyPropertyChanged
    {
        private int _selectedDetailTab = -1;
        private bool _isBusy;
        public event PropertyChangedEventHandler? PropertyChanged;
        public ObservableCollection<MinecraftInstance> Instances { get; } =
            [new("无障碍测试", @"C:\audit\instance", @"C:\audit\instance\.minecraft", ["1.7.10"])];
        public MinecraftInstance SelectedInstance { get; set; }
        public string NewName { get; set; } = "无障碍测试";
        public string StatusText => "内存测试";
        public int QuickCheckCount { get; private set; }
        public IRelayCommand QuickCheckCommand { get; }
        public IRelayCommand StopGameCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand DownloadMissingCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand AddLocalModsCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand RenameCommand { get; } = new RelayCommand(() => { });
        public InstanceModel()
        {
            SelectedInstance = Instances[0];
            QuickCheckCommand = new RelayCommand(() => QuickCheckCount++, () => CanInteract);
        }
        public bool CanInteract => !IsBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
                PropertyChanged?.Invoke(this, new(nameof(CanInteract)));
                QuickCheckCommand.NotifyCanExecuteChanged();
            }
        }
        public int SelectedDetailTab
        {
            get => _selectedDetailTab;
            set
            {
                if (_selectedDetailTab == value) return;
                _selectedDetailTab = value;
                PropertyChanged?.Invoke(this, new(nameof(SelectedDetailTab)));
            }
        }
    }
}

/// <summary>注册可由 WPF 正常失效的根 Peer；不使用游离于控件的手工 Peer 实例。</summary>
public sealed class WorkspaceAccessibilityShell : ContentControl
{
    /// <summary>为离屏的测试根控件提供真实的自动化入口。</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
