namespace EML.App.Services;

/// <summary>界面配色模式；跟随系统只读取 Windows 的应用颜色偏好。</summary>
internal enum AppearanceThemeMode { Dark, Light, System }

/// <summary>静态壁纸的缩放方式，不支持视频或远程背景。</summary>
internal enum WallpaperFit { Fill, Fit, Stretch }

/// <summary>独立保存启动器外观，不包含实例、账号或游戏启动参数。</summary>
internal sealed record AppearanceSettings
{
    public AppearanceThemeMode ThemeMode { get; init; } = AppearanceThemeMode.Dark;
    public string AccentHex { get; init; } = "#F0C94D";
    public string? BackgroundHex { get; init; }
    public string? WallpaperFileName { get; init; }
    public WallpaperFit WallpaperFit { get; init; } = WallpaperFit.Fill;
    public double WallpaperOverlayOpacity { get; init; } = 0.55;
    public double PanelOpacity { get; init; } = 0.94;
}
