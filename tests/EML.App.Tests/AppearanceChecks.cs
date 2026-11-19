using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EML.App.Services;
using EML.App.ViewModels;
using EML.App.Views;

namespace EML.App.Tests;

/// <summary>在已有 STA 应用中检查深浅主题、动态资源和独立的本地外观设置。</summary>
internal static class AppearanceChecks
{
    /// <summary>复用生产主题进行检查，结束后恢复资源，不影响后续页面和动效回归。</summary>
    internal static void Run(Border host)
    {
        VerifyPalettes();
        var application = Application.Current;
        var dictionaries = application.Resources.MergedDictionaries.ToArray();
        var resources = application.Resources.Keys.Cast<object>().ToDictionary(key => key, key => application.Resources[key]);
        var directory = Path.Combine(Path.GetTempPath(), "EML.Appearance.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            VerifyRuntimeResources(host, directory);
            VerifyPersistenceAndWallpaper(directory);
            VerifyViewModel(Path.Combine(directory, "view-model"));
            VerifyBusinessViewThemes(Path.Combine(directory, "business-views"));
            VerifyAppearanceView(Path.Combine(directory, "view-preview"));
        }
        finally
        {
            host.Child = null;
            application.Resources.Clear();
            application.Resources.MergedDictionaries.Clear();
            foreach (var dictionary in dictionaries) application.Resources.MergedDictionaries.Add(dictionary);
            foreach (var resource in resources) application.Resources[resource.Key] = resource.Value;
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>覆盖黑白和高饱和自定义颜色，检查正文、主按钮和状态信息的对比度。</summary>
    private static void VerifyPalettes()
    {
        var defaults = AppearancePalette.Create(true);
        Assert.Equal(Color.FromRgb(12, 17, 28), defaults["WindowBackgroundBrush"]);
        Assert.True(AppearancePalette.TryParseHex(" #aB12ef ", out var parsed));
        Assert.Equal(Color.FromRgb(171, 18, 239), parsed);
        foreach (var value in new[] { "", "red", "#123", "#AA112233", "#GGFFFF", "#1234567" })
            Assert.False(AppearancePalette.TryParseHex(value, out _));
        foreach (var dark in new[] { false, true })
        foreach (var accent in new string?[] { null, "#FFFFFF", "#000000", "#FFFF00", "#FF0000", "#0000FF" })
        foreach (var background in new string?[] { null, "#FFFFFF", "#000000", "#777777", "#FF0000", "#00FF00" })
        {
            var palette = AppearancePalette.Create(dark, accent, background);
            Assert.Equal(defaults.Keys.Order(), palette.Keys.Order());
            foreach (var foreground in new[] { "TextBrush", "MutedTextBrush" })
            foreach (var surface in new[] { "WindowBackgroundBrush", "SurfaceBrush", "RaisedSurfaceBrush", "HoverBrush", "AccentSoftBrush", "DangerSoftBrush", "WarningSoftBrush", "SuccessSoftBrush", "InfoSoftBrush" })
                AssertReadable(palette[foreground], palette[surface], $"{dark}/{accent}/{background}: {foreground}/{surface}");
            AssertReadable(palette["AccentForegroundBrush"], palette["AccentBrush"], "主按钮");
            AssertReadable(palette["TextSelectionForegroundBrush"], palette["TextSelectionBrush"], "选中文字");
            foreach (var status in new[] { "Danger", "Warning", "Success" })
            {
                AssertReadable(palette[status + "Brush"], palette[status + "SoftBrush"], status + "提示");
                AssertReadable(palette[status + "Brush"], palette["SurfaceBrush"], status + "正文");
            }
            VerifyWallpaperReadability(palette);
        }
        var highContrast = AppearancePalette.Create(false, "#FF0000", "#00FF00", highContrast: true);
        Assert.Equal(SystemColors.WindowColor, highContrast["WindowBackgroundBrush"]);
        Assert.Equal(SystemColors.WindowTextColor, highContrast["TextBrush"]);
        Assert.Equal(SystemColors.HighlightColor, highContrast["AccentBrush"]);
    }

    /// <summary>已创建的输入、下拉、菜单及选中实例在换色后直接使用新资源，不重新创建控件。</summary>
    private static void VerifyRuntimeResources(Border host, string directory)
    {
        using var service = new AppearanceService(directory, watchSystemTheme: false);
        // Application 只向已注册的 Window 传播资源失效；断开的离屏 Border 会缓存旧画刷。
        // 此窗口不调用 Show，不建立业务主窗口，也不创建可见 HWND。
        using var windowOwner = new WindowLifetime(new Window { Content = host });
        using var dialogOwner = new WindowLifetime(new DialogWindow("测试结果", "此弹窗不显示", tone: DialogTone.Warning));
        var dialog = (DialogWindow)dialogOwner.Window;
        var content = new StackPanel();
        content.SetResourceReference(Panel.BackgroundProperty, "WindowBackgroundBrush");
        var text = new TextBox { Text = "中文路径：D:\\我的世界\\GTNH", Margin = new Thickness(0, 0, 0, 10) };
        var combo = new ComboBox { IsEditable = true, ItemsSource = new[] { "Java 8", "Java 21", "Java 25" }, SelectedIndex = 1 };
        var list = new ListBox { ItemsSource = new[] { "GTNH 2.9.0 · 本地数据", "原版测试" }, SelectedIndex = 0, Margin = new Thickness(0, 10, 0, 0) };
        var primary = new Button { Content = "启动游戏", Style = (Style)Application.Current.FindResource("PrimaryButton"), Margin = new Thickness(0, 10, 0, 0) };
        content.Children.Add(text); content.Children.Add(combo); content.Children.Add(list); content.Children.Add(primary);
        host.Child = content;
        using var menuOwner = new MenuLifetime(new ContextMenu { Items = { new MenuItem { Header = "实例详情" }, new MenuItem { Header = "删除实例", Foreground = Brushes.Red } } });
        var menu = menuOwner.Menu;
        // ContextMenu 不能加入视觉树；离屏检查显式共享应用资源，真实 Popup 另做交互验证。
        menu.Resources.MergedDictionaries.Add(Application.Current.Resources);
        text.ContextMenu = menu;
        menu.PlacementTarget = text;
        Layout(host);
        foreach (var mode in new[] { AppearanceThemeMode.Light, AppearanceThemeMode.Dark, AppearanceThemeMode.Light })
        {
            service.Apply(service.Settings with { ThemeMode = mode, AccentHex = "#5F6FE8", BackgroundHex = null });
            Layout(host);
            menu.ApplyTemplate();
            Assert.Equal(Brush("TextBrush").Color, Assert.IsType<SolidColorBrush>(text.Foreground).Color);
            Assert.Equal(Brush("TextSelectionBrush").Color, Assert.IsType<SolidColorBrush>(text.SelectionBrush).Color);
            Assert.Equal(Brush("TextSelectionForegroundBrush").Color, Assert.IsType<SolidColorBrush>(text.SelectionTextBrush).Color);
            AssertReadable(Assert.IsType<SolidColorBrush>(text.SelectionTextBrush).Color,
                Assert.IsType<SolidColorBrush>(text.SelectionBrush).Color, "输入框选中文字");
            Assert.Equal(Brush("TextBrush").Color, Assert.IsType<SolidColorBrush>(combo.Foreground).Color);
            Assert.Equal(Brush("TextBrush").Color, Assert.IsType<SolidColorBrush>(list.Foreground).Color);
            Assert.Equal(Brush("TextBrush").Color, Assert.IsType<SolidColorBrush>(menu.Foreground).Color);
            Assert.Equal(Brush("RaisedSurfaceBrush").Color, Assert.IsType<SolidColorBrush>(menu.Background).Color);
            AssertReadable(Assert.IsType<SolidColorBrush>(primary.Foreground).Color, Assert.IsType<SolidColorBrush>(primary.Background).Color, "动态主按钮");
            Assert.True(UiMotion.GetPress(primary));
            Assert.Equal("中文路径：D:\\我的世界\\GTNH", text.Text);
            Assert.Equal(0, list.SelectedIndex);
            var editor = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
            Assert.Equal(Brush("TextBrush").Color, Assert.IsType<SolidColorBrush>(editor.Foreground).Color);
            var tint = Assert.IsType<Border>(primary.Template.FindName("HoverTint", primary));
            Assert.Equal(Brush("HoverTintBrush").Color, Assert.IsType<SolidColorBrush>(tint.Background).Color);
            Assert.Equal(Brush("TextBrush").Color, Assert.IsType<SolidColorBrush>(dialog.Foreground).Color);
            Assert.Equal(Brush("TextBrush").Color, Assert.IsType<SolidColorBrush>(dialog.Surface.Foreground).Color);
            var dialogSurface = Assert.IsType<Border>(dialog.Surface.Content);
            Assert.Equal(Brush("SurfaceBrush").Color, Assert.IsType<SolidColorBrush>(dialogSurface.Background).Color);
            Assert.Equal(Brush("WarningSoftBrush").Color, Assert.IsType<SolidColorBrush>(dialog.Surface.ToneTile.Background).Color);
            Assert.Equal(Brush("WarningBrush").Color, Assert.IsType<SolidColorBrush>(dialog.Surface.ToneIcon.Foreground).Color);
            Assert.Equal(mode == AppearanceThemeMode.Dark, Application.Current.FindResource("IsDarkTheme"));
            WritePreview(host, mode == AppearanceThemeMode.Dark ? "appearance-dark-controls.png" : "appearance-light-controls.png");
        }
        var before = service.Settings;
        Assert.Throws<ArgumentException>(() => service.Apply(before with { AccentHex = "not-a-color" }));
        Assert.Equal(before, service.Settings);
        Assert.Throws<ArgumentException>(() => service.Apply(before with { BackgroundHex = "#00000000" }));
        service.Apply(service.Settings with { ThemeMode = AppearanceThemeMode.Dark, BackgroundHex = "#FFFFFF" });
        Assert.False(service.IsDarkTheme);
        Assert.Equal(false, Application.Current.FindResource("IsDarkTheme"));
        service.Apply(service.Settings with { ThemeMode = AppearanceThemeMode.Light, BackgroundHex = "#000000" });
        Assert.True(service.IsDarkTheme);
        Assert.Equal(true, Application.Current.FindResource("IsDarkTheme"));
        host.Child = null;
    }

    /// <summary>自动遮罩需照顾未知图片的黑白端点和中间灰度，保存的透明度只作为下限。</summary>
    private static void VerifyWallpaperReadability(IReadOnlyDictionary<string, Color> palette)
    {
        const double requested = 0.35;
        var opacity = AppearanceService.SafeWallpaperOverlayOpacity(palette, requested);
        Assert.InRange(opacity, requested, 1);
        var overlay = palette["WindowBackgroundBrush"];
        for (var level = 0; level <= 255; level += 17)
        {
            var background = Color.FromRgb(
                (byte)Math.Round(overlay.R * opacity + level * (1 - opacity)),
                (byte)Math.Round(overlay.G * opacity + level * (1 - opacity)),
                (byte)Math.Round(overlay.B * opacity + level * (1 - opacity)));
            foreach (var key in new[] { "TextBrush", "MutedTextBrush", "AccentTextBrush", "SecondaryAccentBrush", "DangerBrush", "WarningBrush", "SuccessBrush" })
                AssertReadable(palette[key], background, $"壁纸遮罩 {opacity:P0}/{level}/{key}");
        }
    }

    /// <summary>页面输入在失败时保留，修正后可保存，并由下一次创建的服务读取。</summary>
    private static void VerifyViewModel(string directory)
    {
        var notices = new NoticeRecorder();
        using (var service = new AppearanceService(directory, watchSystemTheme: false))
        using (var model = new AppearanceViewModel(service, () => null,
            Path.Combine(directory, "settings", "appearance.json"), notices))
        {
            Assert.False(model.HasPendingColors);
            model.SelectAccentCommand.Execute("#4285F4");
            Assert.True(model.HasPendingColors);
            Assert.Contains("尚未应用", model.ColorStatusText);
            model.ThemeIndex = (int)AppearanceThemeMode.Light;
            Assert.True(model.HasPendingColors);
            Assert.Contains("尚未应用", model.ColorStatusText);
            model.AccentText = "invalid-color";
            model.BackgroundText = "#F4F6FA";
            var before = service.Settings;
            model.ApplyColorsCommand.Execute(null);
            Assert.Equal(before, service.Settings);
            Assert.Equal("invalid-color", model.AccentText);
            Assert.Equal("#F4F6FA", model.BackgroundText);
            Assert.True(model.HasPendingColors);
            Assert.Equal(OperationNoticeKind.Failure, notices.Items[^1].Kind);
            model.AccentText = "#176389";
            model.ApplyColorsCommand.Execute(null);
            Assert.Equal(OperationNoticeKind.Success, notices.Items[^1].Kind);
            Assert.False(model.HasPendingColors);
            Assert.DoesNotContain("尚未应用", model.ColorStatusText);
            model.ThemeIndex = (int)AppearanceThemeMode.Light;
            model.OverlayPercent = 75;
            model.PanelPercent = 96;
            model.SavePreviewCommand.Execute(null);
            Assert.Equal(0.75, service.Settings.WallpaperOverlayOpacity);
            Assert.Equal(0.96, service.Settings.PanelOpacity);
        }
        using var reloaded = new AppearanceService(directory, watchSystemTheme: false);
        Assert.Equal(AppearanceThemeMode.Light, reloaded.Settings.ThemeMode);
        Assert.Equal("#176389", reloaded.Settings.AccentHex);
        Assert.Equal("#F4F6FA", reloaded.Settings.BackgroundHex);
        Assert.Equal(0.75, reloaded.Settings.WallpaperOverlayOpacity);
        Assert.Equal(0.96, reloaded.Settings.PanelOpacity);
        var failedDirectory = Path.Combine(directory, "save-failure");
        Directory.CreateDirectory(failedDirectory);
        File.WriteAllText(Path.Combine(failedDirectory, "settings"), "阻断设置目录，模拟保存失败");
        var failedNotices = new NoticeRecorder();
        using var failedService = new AppearanceService(failedDirectory, watchSystemTheme: false);
        using var failedModel = new AppearanceViewModel(failedService, () => null,
            Path.Combine(failedDirectory, "settings", "appearance.json"), failedNotices);
        failedModel.AccentText = "#4285F4";
        failedModel.BackgroundText = "#F2F5FA";
        failedModel.ApplyColorsCommand.Execute(null);
        Assert.Equal("#4285F4", failedModel.AccentText);
        Assert.Equal("#F2F5FA", failedModel.BackgroundText);
        Assert.Contains("未保存", failedModel.StatusText);
        Assert.Single(failedNotices.Items);
        Assert.Equal(OperationNoticeKind.Failure, failedNotices.Items[0].Kind);
        Assert.False(File.Exists(Path.Combine(failedDirectory, "settings", "appearance.json")));
    }

    /// <summary>业务页共用同一应用色板，深浅模式均加载所有主页面与八个实例工作区。</summary>
    private static void VerifyBusinessViewThemes(string directory)
    {
        using var service = new AppearanceService(directory, watchSystemTheme: false);
        var host = new Border { Width = 1000, Height = 720 };
        using var owner = new WindowLifetime(new Window { Content = host });
        foreach (var mode in new[] { AppearanceThemeMode.Dark, AppearanceThemeMode.Light })
        {
            service.Apply(service.Settings with { ThemeMode = mode, BackgroundHex = null, WallpaperFileName = null });
            foreach (var page in new UserControl[] { new OnlineModsView(), new AccountsView(), new JavaEnvironmentView(),
                new LaunchSettingsView(), new InstanceDownloadsView(), new DownloadCenterView(), new GtnhHubView() })
            {
                host.Child = page;
                Layout(host);
                Assert.Equal(Brush("TextBrush").Color, Assert.IsType<SolidColorBrush>(page.Foreground).Color);
                Assert.True(page.IsMeasureValid && page.IsArrangeValid);
            }
            var workspace = new InstanceWorkspaceView();
            host.Child = workspace;
            var sections = Assert.IsAssignableFrom<TabControl>(workspace.Content);
            for (var index = 0; index < sections.Items.Count; index++)
            {
                sections.SelectedIndex = index;
                Layout(host);
                Assert.Equal(Brush("TextBrush").Color, Assert.IsType<SolidColorBrush>(workspace.Foreground).Color);
                Assert.NotNull(sections.Template.FindName("PART_SelectedContentHost", sections));
            }
            host.Child = null;
        }
    }

    /// <summary>在两档 DPI 下渲染真实外观页，覆盖深浅主题与自定义配色叠加静态壁纸。</summary>
    private static void VerifyAppearanceView(string directory)
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "preview-wallpaper.png");
        CreateImage(source);
        using var service = new AppearanceService(directory, watchSystemTheme: false);
        var wallpaper = service.ImportWallpaper(source);
        foreach (var scale in new[] { 1d, 2d })
        {
            var root = new Grid { Width = 1000, Height = 760 };
            root.SetResourceReference(Panel.BackgroundProperty, "WindowBackgroundBrush");
            var image = new Border(); image.SetResourceReference(Border.BackgroundProperty, "WallpaperBrush");
            var overlay = new Border(); overlay.SetResourceReference(Border.BackgroundProperty, "BackgroundOverlayBrush");
            var view = new AppearanceView { Margin = new Thickness(24) };
            root.Children.Add(image); root.Children.Add(overlay); root.Children.Add(view);
            using var owner = new WindowLifetime(new Window { Content = root });
            VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
            foreach (var (label, mode, accent, background, picture) in new[]
            {
                ("dark", AppearanceThemeMode.Dark, "#F0C94D", (string?)null, (string?)null),
                ("light", AppearanceThemeMode.Light, "#4285F4", (string?)null, (string?)null),
                ("custom-wallpaper", AppearanceThemeMode.Dark, "#F08080", (string?)"#12342A", (string?)wallpaper)
            })
            {
                service.Apply(service.Settings with
                {
                    ThemeMode = mode, AccentHex = accent, BackgroundHex = background,
                    WallpaperFileName = picture, WallpaperOverlayOpacity = 0.35, PanelOpacity = 0.88
                });
                using var model = new AppearanceViewModel(service, () => null,
                    Path.Combine(directory, "settings", "appearance.json"));
                view.DataContext = model;
                Layout(root);
                Assert.Equal(scale, VisualTreeHelper.GetDpi(root).DpiScaleX);
                Assert.Equal(Brush("TextBrush").Color, Assert.IsType<SolidColorBrush>(view.Foreground).Color);
                var input = Assert.IsType<TextBox>(view.FindName("AccentInput"));
                Assert.Equal(Brush("TextBrush").Color, Assert.IsType<SolidColorBrush>(input.Foreground).Color);
                var selector = Assert.IsType<ComboBox>(view.FindName("ThemeSelector"));
                Assert.True(selector.IsEnabled);
                Assert.Equal(Brush("TextBrush").Color, Assert.IsType<SolidColorBrush>(selector.Foreground).Color);
                var slider = Assert.IsType<Slider>(view.FindName("OverlaySlider"));
                Assert.NotNull(slider.Template.FindName("PART_Track", slider));
                WritePreview(root, $"appearance-{label}-{(int)(96 * scale)}dpi.png", scale);
            }
        }
    }

    /// <summary>检查保存读回和静态壁纸副本，测试仅操作随机创建的临时目录。</summary>
    private static void VerifyPersistenceAndWallpaper(string directory)
    {
        var source = Path.Combine(directory, "我的壁纸（测试）.png");
        CreateImage(source);
        string imported;
        AppearanceSettings saved;
        using (var service = new AppearanceService(directory, watchSystemTheme: false))
        {
            imported = service.ImportWallpaper(source);
            service.Apply(service.Settings with
            {
                ThemeMode = AppearanceThemeMode.Light, AccentHex = "#176389", BackgroundHex = "#F2F5FA",
                WallpaperFileName = imported, WallpaperFit = WallpaperFit.Fit,
                WallpaperOverlayOpacity = 0.7, PanelOpacity = 0.95
            });
            service.Save();
            saved = service.Settings;
            Assert.Equal(Path.GetFileName(imported), imported);
            Assert.Equal(Stretch.Uniform, Assert.IsType<ImageBrush>(Application.Current.FindResource("WallpaperBrush")).Stretch);
            using var original = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(original.Length > 0);
        }
        File.Delete(source);
        using (var reloaded = new AppearanceService(directory, watchSystemTheme: false))
        {
            Assert.Equal(saved, reloaded.Settings);
            Assert.NotNull(Assert.IsType<ImageBrush>(Application.Current.FindResource("WallpaperBrush")).ImageSource);
            Assert.Throws<ArgumentException>(() => reloaded.ImportWallpaper("https://example.invalid/wallpaper.png"));
            Assert.Throws<ArgumentException>(() => reloaded.ImportWallpaper(@"\\server\share\wallpaper.png"));
            Assert.Throws<ArgumentException>(() => reloaded.Apply(reloaded.Settings with { WallpaperFileName = "../outside.png" }));
            var invalid = Path.Combine(directory, "not-an-image.png");
            File.WriteAllText(invalid, "这不是图像");
            Assert.ThrowsAny<Exception>(() => reloaded.ImportWallpaper(invalid));
            var unsupported = Path.Combine(directory, "animation.gif");
            File.WriteAllText(unsupported, "GIF89a");
            Assert.Throws<ArgumentException>(() => reloaded.ImportWallpaper(unsupported));
            var oversized = Path.Combine(directory, "oversized.png");
            using (var output = File.Create(oversized)) output.SetLength(AppearanceSettingsStore.MaximumWallpaperBytes + 1);
            Assert.Throws<InvalidDataException>(() => reloaded.ImportWallpaper(oversized));
            reloaded.Apply(reloaded.Settings with { WallpaperFileName = "missing-wallpaper.png" });
            Assert.Equal(false, Application.Current.FindResource("HasWallpaper"));
            Assert.False(string.IsNullOrWhiteSpace(reloaded.StatusMessage));
            Assert.Contains("missing-wallpaper.png", reloaded.StatusMessage);
            var damagedWallpaper = Path.Combine(directory, "settings", "appearance", "damaged-wallpaper.png");
            File.WriteAllText(damagedWallpaper, "图像已损坏");
            reloaded.Apply(reloaded.Settings with { WallpaperFileName = "damaged-wallpaper.png" });
            Assert.Equal(false, Application.Current.FindResource("HasWallpaper"));
            Assert.False(string.IsNullOrWhiteSpace(reloaded.StatusMessage));
            reloaded.Reset();
            Assert.Equal(AppearanceThemeMode.Dark, reloaded.Settings.ThemeMode);
            Assert.Null(reloaded.Settings.WallpaperFileName);
            Assert.True(File.Exists(Path.Combine(directory, "settings", "appearance", imported)));
        }
        using (var reset = new AppearanceService(directory, watchSystemTheme: false))
        {
            Assert.Equal(AppearanceThemeMode.Dark, reset.Settings.ThemeMode);
            Assert.Null(reset.Settings.WallpaperFileName);
        }
        var settingsPath = Path.Combine(directory, "settings", "appearance.json");
        File.WriteAllText(settingsPath, "{not-valid-json");
        using var damaged = new AppearanceService(directory, watchSystemTheme: false);
        Assert.Equal(AppearanceThemeMode.Dark, damaged.Settings.ThemeMode);
        Assert.Equal("{not-valid-json", File.ReadAllText(settingsPath));
        Assert.False(string.IsNullOrWhiteSpace(damaged.StatusMessage));
        var failedRoot = Path.Combine(directory, "save-failure");
        Directory.CreateDirectory(failedRoot);
        File.WriteAllText(Path.Combine(failedRoot, "settings"), "阻断设置目录以模拟保存失败");
        using var failedSave = new AppearanceService(failedRoot, watchSystemTheme: false);
        failedSave.Apply(failedSave.Settings with { ThemeMode = AppearanceThemeMode.Light });
        Assert.ThrowsAny<IOException>(() => failedSave.Save());
        Assert.Equal(AppearanceThemeMode.Light, failedSave.Settings.ThemeMode);
        Assert.False(File.Exists(Path.Combine(failedRoot, "settings", "appearance.json")));
    }

    /// <summary>用内存像素生成小型 PNG，不依赖用户图片或外网素材。</summary>
    private static void CreateImage(string path)
    {
        var pixels = Enumerable.Range(0, 48).Select(index => index % 4 == 3 ? (byte)255 : (byte)(index * 5)).ToArray();
        var bitmap = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }

    /// <summary>读取实际应用画刷，动态换色检查不使用平行的测试常量。</summary>
    private static SolidColorBrush Brush(string key) => Assert.IsType<SolidColorBrush>(Application.Current.FindResource(key));

    /// <summary>以普通正文的对比度门槛验证一组前景与背景。</summary>
    private static void AssertReadable(Color foreground, Color background, string label) =>
        Assert.True(AppearancePalette.Contrast(foreground, background) >= 4.5, $"{label} 对比度不足：{foreground}/{background}。");

    /// <summary>完成离屏布局后读取真实控件模板，不打开任何系统窗口。</summary>
    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
    }

    /// <summary>仅在指定的验证目录输出生产控件截图，供主代理检查实际换色。</summary>
    private static void WritePreview(FrameworkElement root, string filename, double scale = 1)
    {
        var directory = Environment.GetEnvironmentVariable("EML_TEXTBOX_PREVIEW_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale),
            (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, filename)); encoder.Save(output);
    }

    /// <summary>保证即使断言失败，也会关闭未显示菜单的临时弹出状态。</summary>
    private sealed class MenuLifetime(ContextMenu menu) : IDisposable
    {
        internal ContextMenu Menu { get; } = menu;
        public void Dispose() => Menu.IsOpen = false;
    }

    /// <summary>释放未显示的测试窗口，让原离屏宿主可以继续用于其他页面检查。</summary>
    private sealed class WindowLifetime(Window window) : IDisposable
    {
        internal Window Window { get; } = window;
        public void Dispose()
        {
            Window.Content = null;
            Window.Close();
        }
    }

    /// <summary>收集外观页面的结果消息，不打开真实弹窗。</summary>
    private sealed class NoticeRecorder : IOperationNotifications
    {
        internal List<OperationNotice> Items { get; } = [];
        public void Report(OperationNotice notice) => Items.Add(notice);
    }
}
