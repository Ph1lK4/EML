using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.Input;
using EML.App.Views;
using EML.Core.Minecraft;

namespace EML.App.Tests;

/// <summary>复用现有 STA 应用验证深蓝主题；所有实例与命令均为内存数据，不调用主窗口业务构造器。</summary>
internal static class NavyThemeChecks
{
    /// <summary>检查关键颜色与文字对比，并在标准、最小窗口及扣除窗口边框的客户区尺寸下渲染。</summary>
    internal static void Run()
    {
        Assert.Equal(Color.FromRgb(12, 17, 28), Brush("WindowBackgroundBrush").Color);
        AssertContrast("TextBrush", "WindowBackgroundBrush");
        AssertContrast("TextBrush", "RaisedSurfaceBrush");
        AssertContrast("MutedTextBrush", "SurfaceBrush");
        AssertContrast("AccentForegroundBrush", "AccentBrush");
        foreach (var (width, height) in new[] { (1280, 820), (1000, 640), (1000, 600) })
        foreach (var scale in new[] { 1d, 2d })
        {
            var model = new ShellModel();
            var shell = LoadShell(model, width, height, scale);
            VerifyShell(shell, model);
            var viewport = height == 600 ? $"{width}x{height}" : width.ToString();
            WritePreview(shell, $"navy-library-{viewport}-{(int)(96 * scale)}dpi.png", scale);
            model.Instances.UseMicrosoftAccount = true;
            Layout(shell);
            var identity = Assert.IsType<LaunchIdentitySelector>(shell.FindName("InstanceLaunchIdentity"));
            Assert.Equal("K4ssia", Assert.IsType<TextBox>(identity.FindName("MicrosoftPlayerName")).Text);
            if (width == 1000) WritePreview(shell, $"navy-online-{viewport}-{(int)(96 * scale)}dpi.png", scale);
            model.Instances.Instances.Clear();
            model.Instances.SelectedInstance = null;
            Layout(shell);
            var import = Descendants(shell).OfType<Button>().First(button =>
                BindingOperations.GetBinding(button, Button.CommandProperty)?.Path.Path == "Instances.ImportCommand");
            Assert.True(import.IsEnabled);
            AssertInside(import, shell, "空实例库的导入入口");
            if (width == 1000 && height == 600 && scale == 1) WritePreview(shell, "navy-library-empty.png", scale);
            shell.Content = null;
        }
        foreach (var scale in new[] { 1d, 2d }) VerifyControlStates(scale);
    }

    /// <summary>验证深色输入、下拉和菜单真实模板；Popup 始终关闭，只渲染其已测量的子树。</summary>
    private static void VerifyControlStates(double scale)
    {
        var values = new InputModel();
        var root = new Border { Width = 1000, Height = 700, Padding = new Thickness(28), Background = Brush("WindowBackgroundBrush") };
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
        TextElement.SetFontFamily(root, new FontFamily("Microsoft YaHei UI"));
        TextElement.SetFontSize(root, 14);
        TextElement.SetForeground(root, Brush("TextBrush"));
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
        root.Child = columns;
        var inputs = new StackPanel();
        var menus = new StackPanel();
        Grid.SetColumn(menus, 2);
        columns.Children.Add(inputs); columns.Children.Add(menus);
        inputs.Children.Add(Heading("输入与选择"));
        inputs.Children.Add(Label("JVM 参数 · 已选择文本"));
        var text = new TextBox { Text = "-Xmx8192M -Dfile.encoding=UTF-8", IsInactiveSelectionHighlightEnabled = true };
        inputs.Children.Add(text);
        text.Select(0, 9);
        FocusManager.SetIsFocusScope(root, true);
        FocusManager.SetFocusedElement(root, text);
        Assert.Same(text, FocusManager.GetFocusedElement(root));
        inputs.Children.Add(Label("Java 路径 · 支持中文与空格"));
        var java = new ComboBox { IsEditable = true, ItemsSource = values.Paths, MaxDropDownHeight = 180, DataContext = values };
        java.SetBinding(ComboBox.TextProperty, new Binding(nameof(InputModel.JavaPath)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        java.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(InputModel.SelectedJavaPath)) { Mode = BindingMode.TwoWay });
        inputs.Children.Add(java);
        Layout(root);
        var editor = Assert.IsType<TextBox>(java.Template.FindName("PART_EditableTextBox", java));
        editor.SetCurrentValue(TextBox.TextProperty, @"D:\我的游戏（长期存档）\Java 环境\jdk-25\bin\java.exe");
        Layout(root);
        Assert.Equal(editor.Text, java.Text);
        Assert.Equal(editor.Text, values.JavaPath);
        java.SetCurrentValue(Selector.SelectedItemProperty, values.Paths[1]);
        Layout(root);
        Assert.Equal(values.Paths[1], values.SelectedJavaPath);
        Assert.Equal(values.Paths[1], values.JavaPath);
        Assert.Equal(values.Paths[1], editor.Text);
        Assert.True(editor.GetRectFromCharacterIndex(0).Height > 0, "可编辑路径必须有可见的字形区域。");
        Assert.Equal("-Xmx8192M", text.SelectedText.TrimEnd());
        Assert.True(text.GetRectFromCharacterIndex(0).Height > 0);

        var popup = Assert.IsType<Popup>(java.Template.FindName("PART_Popup", java));
        Assert.False(popup.IsOpen);
        var dropdown = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
        VisualTreeHelper.SetRootDpi(dropdown, new DpiScale(scale, scale));
        dropdown.Measure(new Size(java.ActualWidth, 180));
        dropdown.Arrange(new Rect(new Point(), dropdown.DesiredSize));
        dropdown.UpdateLayout();
        Assert.Contains(Descendants(dropdown).OfType<TextBlock>(), item => item.Text.Contains("我的世界（中文路径）", StringComparison.Ordinal));
        Assert.True(dropdown.ActualHeight is > 30 and <= 190);
        inputs.Children.Add(new Image { Source = Snapshot(dropdown, scale), Width = dropdown.ActualWidth, Height = dropdown.ActualHeight, Margin = new Thickness(0, 6, 0, 12), HorizontalAlignment = HorizontalAlignment.Left });

        var checkedBox = new CheckBox { Content = "自动加入必要依赖", IsChecked = true, Margin = new Thickness(0, 2, 0, 4) };
        var uncheckedBox = new CheckBox { Content = "同时安装可选依赖", IsChecked = false, Margin = new Thickness(0, 2, 0, 4) };
        var disabledBox = new CheckBox { Content = "正在安装时不可更改", IsChecked = true, IsEnabled = false };
        inputs.Children.Add(checkedBox); inputs.Children.Add(uncheckedBox); inputs.Children.Add(disabledBox);
        var expanded = new Expander
        {
            Header = "兼容性与风险说明", IsExpanded = true, Margin = new Thickness(0, 14, 0, 0),
            Content = new TextBlock { Text = "迁移前保留旧实例和存档备份。额外模组可能与新版本不兼容，请确认后继续。", TextWrapping = TextWrapping.Wrap, Foreground = Brush("MutedTextBrush"), Margin = new Thickness(24, 8, 0, 8) }
        };
        inputs.Children.Add(expanded);
        inputs.Children.Add(new Expander { Header = "高级参数", IsExpanded = false });

        menus.Children.Add(Heading("实例菜单"));
        var menu = new ContextMenu { Width = 310, FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14 };
        foreach (var header in new[] { "启动游戏", "实例详情", "启动设置", "模组管理", "文件检查", "补全实例依赖", "运行日志" })
            menu.Items.Add(new MenuItem { Header = header });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "重命名" });
        menu.Items.Add(new MenuItem { Header = "导出实例 ZIP" });
        var danger = new MenuItem { Header = "删除实例…", Foreground = Brush("DangerBrush") };
        menu.Items.Add(danger);
        Assert.False(menu.IsOpen);
        menu.ApplyTemplate();
        VisualTreeHelper.SetRootDpi(menu, new DpiScale(scale, scale));
        menu.Measure(new Size(310, 520));
        menu.Arrange(new Rect(new Point(), menu.DesiredSize));
        menu.UpdateLayout();
        Assert.True(menu.ActualHeight > 200);
        Assert.True(Contrast(((SolidColorBrush)danger.Foreground).Color, ((SolidColorBrush)menu.Background).Color) >= 4.5);
        menus.Children.Add(new Image { Source = Snapshot(menu, scale), Width = menu.ActualWidth, Height = menu.ActualHeight, HorizontalAlignment = HorizontalAlignment.Left });
        menus.Children.Add(new Button { Content = "下载并安装", Style = (Style)Application.Current.FindResource("PrimaryButton"), Margin = new Thickness(0, 16, 0, 8) });
        menus.Children.Add(new Button { Content = "删除实例…", Style = (Style)Application.Current.FindResource("DangerButton") });
        Layout(root);
        AssertInside(expanded, root, "展开的风险说明");
        AssertInside(java, root, "可编辑 Java 路径");
        Assert.True(checkedBox.IsChecked); Assert.False(uncheckedBox.IsChecked); Assert.False(disabledBox.IsEnabled);
        Assert.False(popup.IsOpen); Assert.False(menu.IsOpen);
        WritePreview(root, $"navy-controls-{(int)(96 * scale)}dpi.png", scale);
        root.Child = null;
    }

    /// <summary>控件证据页标题只存在于测试，不向产品加入新页面。</summary>
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextBrush"), Margin = new Thickness(0, 0, 0, 18) };

    /// <summary>生成清楚的控件状态标签，辅助截图审查。</summary>
    private static TextBlock Label(string text) => new() { Text = text, Foreground = Brush("MutedTextBrush"), Margin = new Thickness(0, 12, 0, 7) };

    /// <summary>只加载主窗口生产 XAML 的展示树；去除事件回调，替换窗口宿主，不运行真实服务或创建 HWND。</summary>
    private static NavyShellHost LoadShell(ShellModel model, int width, int height, double scale)
    {
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("EML.App.Tests.MainWindow.preview.xaml")
            ?? throw new InvalidOperationException("Missing production MainWindow XAML test resource.");
        using var source = new StreamReader(input);
        // 在解析前补齐程序集；仅修改 xmlns 声明不会改变已解析元素及附加属性的 XName。
        var markup = source.ReadToEnd()
            .Replace("clr-namespace:EML.App.Views\"", "clr-namespace:EML.App.Views;assembly=EML.App\"", StringComparison.Ordinal)
            .Replace("clr-namespace:EML.App.Services\"", "clr-namespace:EML.App.Services;assembly=EML.App\"", StringComparison.Ordinal);
        var document = XDocument.Parse(markup);
        var root = document.Root!;
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace preview = "clr-namespace:EML.App.Tests;assembly=EML.App.Tests";
        root.Name = preview + nameof(NavyShellHost);
        root.Add(new XAttribute(XNamespace.Xmlns + "preview", preview.NamespaceName));
        root.Attribute(xaml + "Class")?.Remove();
        foreach (var name in new[] { "Title", "WindowStartupLocation", "MinWidth", "MinHeight" }) root.Attribute(name)?.Remove();
        root.SetAttributeValue("Width", width);
        root.SetAttributeValue("Height", height);
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name == presentation + "Window.Resources") element.Name = preview + "NavyShellHost.Resources";
            foreach (var attribute in element.Attributes().ToArray())
            {
                if (attribute.Name == xaml + "Shared" || attribute.Value.StartsWith("On", StringComparison.Ordinal)
                    && new[] { "KeyDown", "Click", "MouseLeftButtonDown", "IsVisibleChanged", "Handler" }.Contains(attribute.Name.LocalName))
                    attribute.Remove();
                else if (attribute.Value.Contains("AncestorType=Window", StringComparison.Ordinal))
                    attribute.Value = attribute.Value.Replace("AncestorType=Window", "AncestorType=preview:NavyShellHost", StringComparison.Ordinal);
            }
        }
        root.Descendants(presentation + "EventSetter").Remove();
        using var reader = document.CreateReader();
        var shell = Assert.IsType<NavyShellHost>(XamlReader.Load(reader));
        shell.DataContext = model;
        // 先设置根 DPI，再测量、布局和生成字形；不是对 96 DPI 的 PNG 放大。
        VisualTreeHelper.SetRootDpi(shell, new DpiScale(scale, scale));
        Layout(shell);
        Assert.Equal(scale, VisualTreeHelper.GetDpi(shell).DpiScaleX);
        return shell;
    }

    /// <summary>验证八个原页面仍可导航、已选实例正确显示、主动作和身份输入在最小窗口内可见。</summary>
    private static void VerifyShell(NavyShellHost shell, ShellModel model)
    {
        var navigation = Assert.IsType<ListBox>(shell.FindName("NavigationList"));
        var pages = navigation.Items.OfType<ListBoxItem>().Where(item => item.Tag is int).ToArray();
        Assert.Equal(new[] { 0, 4, 6, 1, 7, 2, 3, 5 }, pages.Select(item => (int)item.Tag).ToArray());
        foreach (var page in pages) AssertInside(page, navigation, $"导航：{page.Tag}");
        navigation.SetCurrentValue(Selector.SelectedValueProperty, 3);
        Assert.Equal(3, model.SelectedPage);
        navigation.SetCurrentValue(Selector.SelectedValueProperty, 0);
        Assert.True(model.IsInstancePage);
        Layout(shell);
        var list = Assert.IsType<ListBox>(shell.FindName("InstanceList"));
        Assert.Same(model.Instances.SelectedInstance, list.SelectedItem);
        Assert.True(list.ActualHeight >= 110, "最小窗口仍应至少容纳实例行，不能被固定头部挤掉。");
        var launch = Descendants(shell).OfType<Button>().Single(button =>
            BindingOperations.GetBinding(button, Button.CommandProperty)?.Path.Path == "Instances.LaunchGameCommand");
        AssertInside(launch, shell, "启动游戏");
        Assert.True(launch.IsEnabled);
        Assert.True(Contrast(((SolidColorBrush)launch.Foreground).Color, ((SolidColorBrush)launch.Background).Color) >= 4.5);
        var identity = Assert.IsType<LaunchIdentitySelector>(shell.FindName("InstanceLaunchIdentity"));
        var player = Assert.IsType<TextBox>(identity.FindName("PlayerNameInput"));
        Assert.Equal("Steve", player.Text);
        AssertInside(player, shell, "离线玩家名");
        Assert.True(player.ActualHeight >= 36);
        player.SetCurrentValue(TextBox.TextProperty, "Test_Player");
        Assert.Equal("Test_Player", model.Instances.PlayerName);
        player.SetCurrentValue(TextBox.TextProperty, "Steve");
    }

    /// <summary>以根尺寸完成离屏布局，确保同一帧的绑定和文本测量已更新。</summary>
    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
    }

    /// <summary>检查重要交互区域不被固定导航、页头或窗口边界裁切。</summary>
    private static void AssertInside(FrameworkElement element, FrameworkElement shell, string name)
    {
        var bounds = element.TransformToAncestor(shell).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Top >= 0
            && bounds.Right <= shell.ActualWidth + 1 && bounds.Bottom <= shell.ActualHeight + 1,
            $"{name} 在 {shell.ActualWidth}×{shell.ActualHeight} 中越界：{bounds}。");
    }

    /// <summary>只在调用方指定的测试产物目录写入 PNG，不触碰真实实例或正式素材。</summary>
    private static void WritePreview(FrameworkElement root, string filename, double scale)
    {
        var directory = Environment.GetEnvironmentVariable("EML_TEXTBOX_PREVIEW_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Snapshot(root, scale)));
        using var output = File.Create(Path.Combine(directory, filename));
        encoder.Save(output);
    }

    /// <summary>以子树自身的实际 DPI 栅格化；适用于关闭的 Popup 和 ContextMenu，不创建系统窗口。</summary>
    private static RenderTargetBitmap Snapshot(FrameworkElement root, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale),
            (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        return bitmap;
    }

    /// <summary>仅用于可编辑下拉框验证的内存数据，模拟用户自定义 Java 路径。</summary>
    private sealed class InputModel : INotifyPropertyChanged
    {
        private string _javaPath = @"C:\Program Files\Java\jdk-21\bin\java.exe";
        private string? _selected;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string[] Paths { get; } = [@"C:\Program Files\Java\jdk-21\bin\java.exe", @"D:\我的世界（中文路径）\Java 25\bin\java.exe", @"D:\EML\Java\jdk-8\bin\java.exe"];
        public InputModel() => _selected = Paths[0];
        public string JavaPath { get => _javaPath; set { if (_javaPath == value) return; _javaPath = value; PropertyChanged?.Invoke(this, new(nameof(JavaPath))); } }
        public string? SelectedJavaPath { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(SelectedJavaPath))); } }
    }

    /// <summary>遍历真实控件模板，保持键盘输入和内容布局检查与生产控件一致。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is Control control) control.ApplyTemplate();
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    /// <summary>读取应用实际加载的画刷，避免测试只验证一份平行的颜色常量。</summary>
    private static SolidColorBrush Brush(string key) => Assert.IsType<SolidColorBrush>(Application.Current.FindResource(key));

    /// <summary>常规正文和按钮文案至少满足 4.5:1 对比度。</summary>
    private static void AssertContrast(string foreground, string background) =>
        Assert.True(Contrast(Brush(foreground).Color, Brush(background).Color) >= 4.5, $"{foreground}/{background} 对比度不足。");

    /// <summary>将 sRGB 转为线性亮度，计算文字与底色的对比度。</summary>
    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value) { var channel = value / 255d; return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4); }
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>只模拟页面导航；不会触发扫描、认证、下载或游戏进程。</summary>
    private sealed class ShellModel : INotifyPropertyChanged
    {
        private int _selectedPage;
        public event PropertyChangedEventHandler? PropertyChanged;
        public InstanceModel Instances { get; } = new();
        public object Accounts { get; } = new { AccountName = "K4ssia", StatusText = "已登录" };
        public object? Java => null;
        public object? Gtnh => null;
        public object? Downloads => null;
        public object? TransferCenter => null;
        public bool IsWorkspaceVisible => false;
        public IRelayCommand OpenInstanceSectionCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand CloseInstanceWorkspaceCommand { get; } = new RelayCommand(() => { });
        public int SelectedPage
        {
            get => _selectedPage;
            set { _selectedPage = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
        }
        public bool IsInstancePage => SelectedPage == 0;
        public bool IsOnlinePage => SelectedPage == 1;
        public bool IsAccountsPage => SelectedPage == 2;
        public bool IsJavaPage => SelectedPage == 3;
        public bool IsGtnhPage => SelectedPage == 4;
        public bool IsLaunchSettingsPage => SelectedPage == 5;
        public bool IsDownloadsPage => SelectedPage == 6;
        public bool IsDownloadCenterPage => SelectedPage == 7;
    }

    /// <summary>提供真实实例模型及内存身份偏好；命令只用于验证状态和绑定，不执行副作用。</summary>
    private sealed class InstanceModel : INotifyPropertyChanged
    {
        private MinecraftInstance? _selected;
        private bool _microsoft;
        private string _playerName = "Steve";
        public event PropertyChangedEventHandler? PropertyChanged;
        public ObservableCollection<MinecraftInstance> Instances { get; } =
        [
            Instance("GTNH 2.9.0 — 新的征程", "1.7.10", "Forge · GT New Horizons", "Prism"),
            Instance("GTNH 2.8.4 — 主存档", "1.7.10", "Forge · GT New Horizons", "Prism"),
            Instance("Prominence II: Hasturian Era v4.1.5hf", "1.20.1", "Fabric", "CurseForge"),
            Instance("Cobblemon Official Modpack", "1.21.1", "NeoForge", "Modrinth")
        ];
        public InstanceModel() => _selected = Instances[0];
        public MinecraftInstance? SelectedInstance { get => _selected; set { _selected = value; Changed(); } }
        public string InstancesRoot => @"C:\Games\EML\instances";
        public bool CanInteract => true;
        public bool IsBusy => false;
        public bool CanEditLaunchSettings => SelectedInstance is not null;
        public bool CanEditOfflinePlayerName => !UseMicrosoftAccount && CanEditLaunchSettings;
        public string[] LaunchModes { get; } = ["离线模式", "正版模式"];
        public string SelectedLaunchMode { get => UseMicrosoftAccount ? "正版模式" : "离线模式"; set => UseMicrosoftAccount = value == "正版模式"; }
        public bool UseMicrosoftAccount { get => _microsoft; set { _microsoft = value; Changed(); } }
        public string PlayerName { get => _playerName; set { _playerName = value; Changed(); } }
        public object Accounts { get; } = new { AccountName = "K4ssia" };
        public bool IsGameRunning => false;
        public int TransferPercent { get; set; }
        public string TransferStatus => "";
        public string StatusText => $"已找到 {Instances.Count} 个本地实例。";
        public int SelectedDetailTab { get; set; } = -1;
        public IAsyncRelayCommand LaunchGameCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);
        public IAsyncRelayCommand ImportCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);
        public IAsyncRelayCommand ExportCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);
        public IRelayCommand RefreshCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand DeleteCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand StopGameCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand ImportCancelCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand ExportCancelCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand CancelLaunchPreparationCommand { get; } = new RelayCommand(() => { });
        private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        private static MinecraftInstance Instance(string name, string version, string loader, string format) =>
            new(name, @"C:\Games\EML\instances\" + name, @"C:\Games\EML\instances\" + name + @"\.minecraft", [version])
            { Format = format, Versions = [new(version, version, loader, "version.json")] };
    }
}

/// <summary>供离屏 XAML 绑定使用的纯展示宿主，不拥有主窗口的文件、账号或网络服务。</summary>
public sealed class NavyShellHost : ContentControl { }
