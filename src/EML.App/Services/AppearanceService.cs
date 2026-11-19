using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EML.App.Services;

/// <summary>统一应用主题资源、静态壁纸和系统外观变化，外观预览与保存明确分开。</summary>
internal sealed class AppearanceService : IDisposable
{
    private readonly AppearanceSettingsStore _store;
    private readonly ResourceDictionary _resources;
    private readonly Dispatcher _dispatcher;
    private readonly bool _watchSystemTheme;
    private bool _disposed;
    private string? _loadWarning;
    private string? _cachedWallpaperName;
    private long _cachedWallpaperLength;
    private DateTime _cachedWallpaperWriteTime;
    private BitmapSource? _cachedWallpaper;

    internal AppearanceSettings Settings { get; private set; } = new();
    internal string? StatusMessage { get; private set; }
    internal bool IsDarkTheme { get; private set; } = true;
    internal event EventHandler? Changed;

    /// <summary>在主窗口创建前读取保存的外观；读取异常只回退外观，不中断启动。</summary>
    internal AppearanceService(string launcherRoot, ResourceDictionary? resources = null, bool watchSystemTheme = true)
    {
        _store = new AppearanceSettingsStore(launcherRoot);
        _resources = resources ?? Application.Current?.Resources ?? new ResourceDictionary();
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _watchSystemTheme = watchSystemTheme;
        Settings = _store.Load(out _loadWarning);
        Apply(Settings);
        if (_watchSystemTheme)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        }
    }

    /// <summary>校验显式输入；不静默截断色值或透明度，错误由外观页面明确提示。</summary>
    internal static AppearanceSettings Validate(AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.ThemeMode) || !Enum.IsDefined(settings.WallpaperFit))
            throw new ArgumentException("主题或壁纸显示方式无效。");
        if (!AppearancePalette.TryParseHex(settings.AccentHex, out var accent))
            throw new ArgumentException("主题色需使用 #RRGGBB 格式，例如 #F0C94D。");
        string? backgroundHex = null;
        if (!string.IsNullOrWhiteSpace(settings.BackgroundHex))
        {
            if (!AppearancePalette.TryParseHex(settings.BackgroundHex, out var background))
                throw new ArgumentException("背景色需使用 #RRGGBB 格式。");
            backgroundHex = $"#{background.R:X2}{background.G:X2}{background.B:X2}";
        }
        if (!double.IsFinite(settings.WallpaperOverlayOpacity) || settings.WallpaperOverlayOpacity is < 0.35 or > 0.9)
            throw new ArgumentException("壁纸遮罩透明度需在 35% 至 90% 之间。");
        if (!double.IsFinite(settings.PanelOpacity) || settings.PanelOpacity is < 0.88 or > 1)
            throw new ArgumentException("面板不透明度需在 88% 至 100% 之间，以确保文字清晰。");
        if (settings.WallpaperFileName is { } name && (string.IsNullOrWhiteSpace(name)
            || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("壁纸记录无效，请重新选择本地图片。");
        return settings with { AccentHex = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}", BackgroundHex = backgroundHex };
    }

    /// <summary>实时替换共享语义资源；输入框、菜单和弹窗继续使用不透明表面。</summary>
    internal void Apply(AppearanceSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcher.VerifyAccess();
        settings = Validate(settings);
        var highContrast = SystemParameters.HighContrast;
        var dark = settings.ThemeMode switch
        {
            AppearanceThemeMode.Light => false,
            AppearanceThemeMode.System => SystemUsesDarkTheme(),
            _ => true
        };
        var palette = AppearancePalette.Create(dark, settings.AccentHex, settings.BackgroundHex, highContrast);
        Brush wallpaper = Brushes.Transparent;
        string? wallpaperWarning = null;
        var hasWallpaper = !highContrast && settings.WallpaperFileName is not null;
        if (hasWallpaper)
        {
            try
            {
                var image = GetWallpaper(settings.WallpaperFileName!);
                wallpaper = new ImageBrush(image)
                {
                    Stretch = settings.WallpaperFit switch
                    {
                        WallpaperFit.Fit => Stretch.Uniform,
                        WallpaperFit.Stretch => Stretch.Fill,
                        _ => Stretch.UniformToFill
                    },
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
                wallpaper.Freeze();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                hasWallpaper = false;
                wallpaperWarning = $"壁纸不可用，已回退为纯色；其他外观设置仍可使用。\n{ex.Message}\n{_store.GetWallpaperPath(settings.WallpaperFileName!)}";
            }
        }
        foreach (var (key, color) in palette) _resources[key] = FrozenBrush(color);
        var surface = palette["SurfaceBrush"];
        // 原始壁纸可能从纯黑跨到纯白；遮罩下限必须同时照顾根层的正文和状态文字。
        var overlayOpacity = hasWallpaper ? SafeWallpaperOverlayOpacity(palette, settings.WallpaperOverlayOpacity) : 1;
        var readabilityNotice = hasWallpaper && overlayOpacity > settings.WallpaperOverlayOpacity + 0.001
            ? $"为保证文字清晰，背景遮罩已自动加强至 {overlayOpacity:P0}；仍保留你设置的最低强度。" : null;
        _resources["SidebarBrush"] = FrozenBrush(surface, hasWallpaper ? settings.PanelOpacity : 1);
        _resources["WallpaperPanelBrush"] = FrozenBrush(surface, hasWallpaper ? settings.PanelOpacity : 1);
        _resources["BackgroundOverlayBrush"] = FrozenBrush(palette["WindowBackgroundBrush"], overlayOpacity);
        _resources["WallpaperBrush"] = wallpaper;
        var darkCaption = !highContrast && AppearancePalette.Contrast(Colors.White, palette["WindowBackgroundBrush"])
            >= AppearancePalette.Contrast(Colors.Black, palette["WindowBackgroundBrush"]);
        _resources["IsDarkTheme"] = darkCaption;
        _resources["HasWallpaper"] = hasWallpaper;
        Settings = settings;
        IsDarkTheme = darkCaption;
        StatusMessage = wallpaperWarning ?? _loadWarning ?? readabilityNotice;
        if (!hasWallpaper) ClearWallpaperCache();
        if (Application.Current is { } application)
            foreach (Window window in application.Windows) WindowAppearance.Refresh(window);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>仅保存当前预览设置；成功后清除初始化警告，失败保持可重试状态。</summary>
    internal void Save()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcher.VerifyAccess();
        _store.Save(Settings);
        _loadWarning = null;
        Apply(Settings);
    }

    /// <summary>导入图片并返回自管文件名，调用者可先预览再明确保存当前外观。</summary>
    internal string ImportWallpaper(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _store.ImportWallpaper(path);
    }

    /// <summary>先确保默认设置保存成功，再恢复默认外观；不删除已有壁纸副本。</summary>
    internal void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcher.VerifyAccess();
        var defaults = new AppearanceSettings();
        _store.Save(defaults);
        _loadWarning = null;
        Apply(defaults);
    }

    /// <summary>创建不可变画刷，避免动画意外修改全局色板或控件共享状态。</summary>
    private static SolidColorBrush FrozenBrush(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    /// <summary>以纯黑和纯白壁纸限定所有背景亮度，必要时加强遮罩但不更改用户保存值。</summary>
    internal static double SafeWallpaperOverlayOpacity(IReadOnlyDictionary<string, Color> palette, double requested)
    {
        var foregrounds = new[] { "TextBrush", "MutedTextBrush", "AccentTextBrush", "SecondaryAccentBrush",
            "DangerBrush", "WarningBrush", "SuccessBrush" }.Select(key => palette[key]).ToArray();
        var background = palette["WindowBackgroundBrush"];
        for (var step = (int)Math.Ceiling(requested * 100); step <= 100; step++)
        {
            var opacity = step / 100d;
            var darkest = Composite(background, Colors.Black, opacity);
            var lightest = Composite(background, Colors.White, opacity);
            // 仅检查两端比值还不够：中间的任意图片不能跨过文字自身亮度。
            if (foregrounds.All(foreground => IsReadableAcross(foreground, darkest, lightest))) return opacity;
        }
        return 1;
    }

    /// <summary>前景必须在整个背景亮度区间的一侧，并在两端均达到普通文字的对比度。</summary>
    private static bool IsReadableAcross(Color foreground, Color darkest, Color lightest)
    {
        var foregroundLevel = AppearancePalette.Contrast(foreground, Colors.Black);
        var minimumLevel = AppearancePalette.Contrast(darkest, Colors.Black);
        var maximumLevel = AppearancePalette.Contrast(lightest, Colors.Black);
        return (foregroundLevel <= minimumLevel || foregroundLevel >= maximumLevel)
            && AppearancePalette.Contrast(foreground, darkest) >= 4.5
            && AppearancePalette.Contrast(foreground, lightest) >= 4.5;
    }

    /// <summary>按遮罩透明度计算不透明合成底色，不解码或遍历壁纸像素。</summary>
    private static Color Composite(Color overlay, Color image, double opacity) => Color.FromRgb(
        (byte)Math.Round(overlay.R * opacity + image.R * (1 - opacity)),
        (byte)Math.Round(overlay.G * opacity + image.G * (1 - opacity)),
        (byte)Math.Round(overlay.B * opacity + image.B * (1 - opacity)));

    /// <summary>移除壁纸或启用高对比度后释放服务缓存，不删除保存的图片文件。</summary>
    private void ClearWallpaperCache()
    {
        _cachedWallpaper = null;
        _cachedWallpaperName = null;
        _cachedWallpaperLength = 0;
        _cachedWallpaperWriteTime = default;
    }

    /// <summary>复用已冻结的壁纸像素，拖动透明度滑块时不重复解码；文件变化则重新检查。</summary>
    private BitmapSource GetWallpaper(string fileName)
    {
        var info = new FileInfo(_store.GetWallpaperPath(fileName));
        if (!info.Exists) throw new FileNotFoundException("保存的壁纸副本不存在，请重新选择图片。", info.FullName);
        if (_cachedWallpaper is not null && _cachedWallpaperName == fileName
            && _cachedWallpaperLength == info.Length && _cachedWallpaperWriteTime == info.LastWriteTimeUtc)
            return _cachedWallpaper;
        var image = _store.LoadWallpaper(fileName);
        _cachedWallpaperName = fileName;
        _cachedWallpaperLength = info.Length;
        _cachedWallpaperWriteTime = info.LastWriteTimeUtc;
        _cachedWallpaper = image;
        return image;
    }

    /// <summary>只读取应用浅色偏好；系统不支持或读取失败时保留深色默认值。</summary>
    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    /// <summary>系统颜色变化时仅重新计算跟随模式，不读取或更改用户的其他系统设置。</summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (Settings.ThemeMode == AppearanceThemeMode.System) QueueSystemRefresh();
    }

    /// <summary>高对比度优先于自定义配色与壁纸，退出高对比后恢复保存的个人外观。</summary>
    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.HighContrast)) QueueSystemRefresh();
    }

    /// <summary>把系统线程的通知投递到 UI 线程，窗口已关闭时不再排队。</summary>
    private void QueueSystemRefresh()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        _ = _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) Apply(Settings); }));
    }

    /// <summary>关闭启动器后解除静态系统事件，避免窗口或服务被长期引用。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_watchSystemTheme)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        }
        ClearWallpaperCache();
    }
}
