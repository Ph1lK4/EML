using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;

namespace EML.App.ViewModels;

/// <summary>管理外观页的输入、即时预览和持久化；不接触游戏实例与登录状态。</summary>
public sealed partial class AppearanceViewModel : ObservableObject, IDisposable
{
    private readonly AppearanceService _service;
    private readonly Func<string?> _chooseWallpaper;
    private readonly IOperationNotifications _notifications;
    private bool _loading;
    private bool _disposed;

    [ObservableProperty] private int themeIndex;
    [ObservableProperty] private string accentText = "#F0C94D";
    [ObservableProperty] private string backgroundText = "";
    [ObservableProperty] private int fitIndex;
    [ObservableProperty] private double overlayPercent = 55;
    [ObservableProperty] private double panelPercent = 94;
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private string wallpaperName = "未设置壁纸";
    [ObservableProperty] private bool isBusy;

    public IReadOnlyList<string> ThemeModes { get; } = ["暗色", "亮色", "跟随系统"];
    public IReadOnlyList<string> FitModes { get; } = ["填充（裁切边缘）", "适应（完整显示）", "拉伸"];
    public bool CanInteract => !_disposed && !IsBusy;
    public bool HasWallpaper => _service.Settings.WallpaperFileName is not null;
    public string SettingsPath { get; }
    public bool HasPendingColors => !string.Equals(AccentText.Trim(), _service.Settings.AccentHex, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(BackgroundText.Trim(), _service.Settings.BackgroundHex ?? "", StringComparison.OrdinalIgnoreCase);
    public string ColorStatusText => HasPendingColors ? "配色尚未应用，请点击“应用配色”。" : "与当前界面配色一致。";

    /// <summary>通过可替换的图片选择器连接系统对话框，测试无需打开窗口。</summary>
    internal AppearanceViewModel(AppearanceService service, Func<string?> chooseWallpaper,
        string settingsPath, IOperationNotifications? notifications = null)
    {
        _service = service;
        _chooseWallpaper = chooseWallpaper;
        _notifications = notifications ?? NullOperationNotifications.Instance;
        SettingsPath = settingsPath;
        _service.Changed += OnAppearanceChanged;
        ReadSettings();
        StatusText = _service.StatusMessage ?? "外观自动保存，切换主题会立即生效。";
    }

    /// <summary>系统主题改变时只刷新说明，不覆盖正在编辑的色值。</summary>
    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        StatusText = _service.StatusMessage ?? "外观已更新。";
        OnPropertyChanged(nameof(HasWallpaper));
        RefreshColorStatus();
    }

    /// <summary>输入或预设改变后明确显示待应用状态，不借用主题切换的旧保存提示。</summary>
    partial void OnAccentTextChanged(string value) => RefreshColorStatus();

    /// <summary>背景色清空同样属于待应用更改，直到与实际配色一致。</summary>
    partial void OnBackgroundTextChanged(string value) => RefreshColorStatus();

    /// <summary>同步局部配色反馈，切换主题或保存壁纸时仍保留尚未提交的输入状态。</summary>
    private void RefreshColorStatus()
    {
        OnPropertyChanged(nameof(HasPendingColors));
        OnPropertyChanged(nameof(ColorStatusText));
    }

    /// <summary>读取持久化状态；批量赋值期间不触发自动保存。</summary>
    private void ReadSettings()
    {
        _loading = true;
        try
        {
            var settings = _service.Settings;
            ThemeIndex = (int)settings.ThemeMode;
            AccentText = settings.AccentHex;
            BackgroundText = settings.BackgroundHex ?? "";
            FitIndex = (int)settings.WallpaperFit;
            OverlayPercent = Math.Round(settings.WallpaperOverlayOpacity * 100);
            PanelPercent = Math.Round(settings.PanelOpacity * 100);
            WallpaperName = settings.WallpaperFileName is null ? "未设置壁纸" : "已保存本地壁纸副本";
            OnPropertyChanged(nameof(HasWallpaper));
        }
        finally { _loading = false; }
    }

    /// <summary>切换主题立刻生效，不强迫用户先提交尚未完成的颜色输入。</summary>
    partial void OnThemeIndexChanged(int value)
    {
        if (!_loading && value is >= 0 and <= 2)
            Persist(_service.Settings with { ThemeMode = (AppearanceThemeMode)value });
    }

    /// <summary>图片填充方式改变时只更新当前背景，不重新复制图片。</summary>
    partial void OnFitIndexChanged(int value)
    {
        if (!_loading && value is >= 0 and <= 2)
            Persist(_service.Settings with { WallpaperFit = (WallpaperFit)value });
    }

    /// <summary>遮罩强度随拖动预览，保存由拖动结束或键盘提交入口执行。</summary>
    partial void OnOverlayPercentChanged(double value) => PreviewWallpaper();

    /// <summary>面板透明度只用于侧栏和卡片，输入框、菜单及弹窗保持不透明。</summary>
    partial void OnPanelPercentChanged(double value) => PreviewWallpaper();

    /// <summary>忙碌时锁定外观操作，避免图片导入期间重复提交。</summary>
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInteract));
        ApplyColorsCommand.NotifyCanExecuteChanged();
        ChooseWallpaperCommand.NotifyCanExecuteChanged();
        RemoveWallpaperCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
    }

    /// <summary>滑动只即时预览；写入失败由独立保存入口报告而不吞掉异常。</summary>
    private void PreviewWallpaper()
    {
        if (_loading || !CanInteract) return;
        try
        {
            _service.Apply(_service.Settings with
            {
                WallpaperOverlayOpacity = OverlayPercent / 100,
                PanelOpacity = PanelPercent / 100
            });
            StatusText = _service.StatusMessage ?? "已预览；松开滑块后自动保存。";
        }
        catch (Exception ex) { ReportFailure("外观预览失败", ex); }
    }

    /// <summary>滑块结束拖动或键盘调整后持久化当前预览，不弹出连续成功提示。</summary>
    [RelayCommand]
    private void SavePreview()
    {
        if (_loading || !CanInteract) return;
        try { _service.Save(); StatusText = _service.StatusMessage ?? "外观已保存，下次启动会自动恢复。"; }
        catch (Exception ex) { ReportFailure("外观设置未保存", ex); }
    }

    /// <summary>完整校验颜色后应用，非法输入保留在输入框中供用户修正。</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void ApplyColors()
    {
        try
        {
            var accent = AccentText.Trim();
            var background = string.IsNullOrWhiteSpace(BackgroundText) ? null : BackgroundText.Trim();
            if (!AppearancePalette.TryParseHex(accent, out _) ||
                (background is not null && !AppearancePalette.TryParseHex(background, out _)))
                throw new ArgumentException("颜色请填写 #RRGGBB，例如 #4285F4；背景色留空则跟随主题。 ");
            if (!Persist(_service.Settings with { AccentHex = accent.ToUpperInvariant(), BackgroundHex = background?.ToUpperInvariant() })) return;
            ReadSettings();
            _notifications.Success("配色已保存", "所有页面、菜单和弹窗已同步更新。", SettingsPath);
        }
        catch (Exception ex) { ReportFailure("配色未应用", ex); }
    }

    /// <summary>预设只填充强调色色值，仍由明确的应用按钮提交。</summary>
    [RelayCommand]
    private void SelectAccent(string? hex)
    {
        if (CanInteract && AppearancePalette.TryParseHex(hex, out _)) AccentText = hex!;
    }

    /// <summary>图片验证与重编码在后台完成，完成后才更新主题并保存关联。</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task ChooseWallpaperAsync()
    {
        string? path = null;
        try
        {
            path = _chooseWallpaper();
            if (string.IsNullOrWhiteSpace(path)) return;
            IsBusy = true;
            StatusText = "正在检查并保存壁纸……";
            var filename = await Task.Run(() => _service.ImportWallpaper(path));
            if (_disposed) return;
            if (!Persist(_service.Settings with { WallpaperFileName = filename })) return;
            ReadSettings();
            _notifications.Success("壁纸已保存", "使用独立副本显示；移动或删除原图不会影响启动器壁纸。", SettingsPath);
        }
        catch (Exception ex) { ReportFailure("壁纸未导入", ex, path); }
        finally { IsBusy = false; }
    }

    /// <summary>移除关联即可恢复纯色，不删除用户原图或旧副本。</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void RemoveWallpaper()
    {
        if (!Persist(_service.Settings with { WallpaperFileName = null })) return;
        ReadSettings();
        _notifications.Success("已移除壁纸", "已恢复纯色背景，没有删除原始图片。", SettingsPath);
    }

    /// <summary>恢复默认主题并保存；不触及实例、账号或启动参数。</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void Reset()
    {
        try
        {
            _service.Reset();
            ReadSettings();
            StatusText = "已恢复默认的深蓝金配色。";
            _notifications.Success("外观已恢复默认", "实例、账号和启动设置均未改变。", SettingsPath);
        }
        catch (Exception ex) { ReportFailure("恢复默认外观失败", ex); }
    }

    /// <summary>先预览再原子保存，失败时明确说明未保存，不显示成功状态。</summary>
    private bool Persist(AppearanceSettings settings)
    {
        try
        {
            _service.Apply(settings);
            _service.Save();
            StatusText = string.IsNullOrWhiteSpace(_service.StatusMessage)
                ? "外观已保存，下次启动会自动恢复。" : _service.StatusMessage;
            return true;
        }
        catch (Exception ex) { ReportFailure("外观设置未保存", ex); return false; }
    }

    /// <summary>错误保留完整原因与可复制路径，当前输入不被清空。</summary>
    private void ReportFailure(string title, Exception ex, string? path = null)
    {
        if (_disposed) return;
        StatusText = title + "：" + ex.Message;
        _notifications.Failure(title, ex, path ?? SettingsPath);
    }

    /// <summary>窗口关闭时解除服务事件订阅，后台图片处理不再更新界面。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.Changed -= OnAppearanceChanged;
    }
}
