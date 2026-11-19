using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EML.App.Services;

/// <summary>按语义生成深浅主题；用户颜色只改变外观，不改变成功、警告和失败的含义。</summary>
internal static class AppearancePalette
{
    /// <summary>生成完整画刷色板，并为任意用户背景和强调色自动选择可读的文字颜色。</summary>
    internal static IReadOnlyDictionary<string, Color> Create(bool isDark, string? accentHex = null,
        string? backgroundHex = null, bool highContrast = false)
    {
        if (highContrast) return HighContrast();
        var customBackground = TryParseHex(backgroundHex, out var suppliedBackground);
        var customAccent = TryParseHex(accentHex, out var suppliedAccent);
        var background = customBackground ? suppliedBackground : Hex(isDark ? "0C111C" : "F2F5FA");
        // 自定义底色可能与主题模式的明暗相反，实际底色决定文字和表面的适配方向。
        var darkSurface = customBackground ? Contrast(Colors.White, background) >= Contrast(Colors.Black, background) : isDark;
        var surface = customBackground ? FitSurface(Mix(background, Colors.White, darkSurface ? 0.035 : 0.72), darkSurface)
            : Hex(isDark ? "131C2B" : "FFFFFF");
        var raised = customBackground ? FitSurface(Mix(background, Colors.White, darkSurface ? 0.075 : 0.42), darkSurface)
            : Hex(isDark ? "1B2638" : "E8EEF6");
        var hover = customBackground ? FitSurface(Mix(raised, darkSurface ? Colors.White : Colors.Black, 0.035), darkSurface)
            : Hex(isDark ? "223148" : "DDE6F1");
        var accent = customAccent ? suppliedAccent : Hex("F0C94D");
        var accentSoft = !customBackground && !customAccent ? Hex(isDark ? "292719" : "F7EED0") : FitSurface(Mix(surface, accent, 0.12), darkSurface);
        var foregrounds = new[] { background, surface, raised, hover, accentSoft };
        var text = Readable(Hex(darkSurface ? "EDF2F7" : "152338"), foregrounds);
        var muted = Readable(Hex(darkSurface ? "9BAAC0" : "4D6078"), foregrounds);
        var accentText = Readable(accent, foregrounds);
        var secondary = Readable(Hex(darkSurface ? "77B7D1" : "176389"), foregrounds);
        var colors = new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["WindowBackgroundBrush"] = background,
            ["BackgroundOverlayBrush"] = background,
            ["SurfaceBrush"] = surface,
            ["SidebarBrush"] = surface,
            ["RaisedSurfaceBrush"] = raised,
            ["BorderBrush"] = customBackground ? Mix(surface, text, 0.24) : Hex(isDark ? "28384F" : "B8C5D8"),
            ["TextBrush"] = text,
            ["MutedTextBrush"] = muted,
            ["AccentBrush"] = accent,
            ["AccentForegroundBrush"] = ButtonForeground(accent),
            ["AccentTextBrush"] = accentText,
            ["AccentSoftBrush"] = accentSoft,
            ["SelectionBorderBrush"] = customBackground || customAccent ? Mix(surface, accentText, 0.55) : Hex(isDark ? "78682D" : "B58B21"),
            ["SecondaryAccentBrush"] = secondary,
            ["HoverBrush"] = hover,
            ["HoverBorderBrush"] = WithAlpha(secondary, darkSurface ? (byte)150 : (byte)170),
            ["FocusSoftBrush"] = Mix(surface, secondary, 0.28),
            ["HoverTintBrush"] = Color.FromArgb(darkSurface ? (byte)18 : (byte)10, darkSurface ? (byte)255 : (byte)0, darkSurface ? (byte)255 : (byte)0, darkSurface ? (byte)255 : (byte)0),
            ["PressedTintBrush"] = Color.FromArgb(36, 0, 0, 0),
            ["WorkspaceOverlayBrush"] = WithAlpha(background, 210),
            ["ScrollThumbBrush"] = customBackground ? Mix(surface, text, 0.45) : Hex(isDark ? "51627A" : "7B8EA9"),
            ["TextSelectionBrush"] = secondary,
            ["TextSelectionForegroundBrush"] = text
        };
        AddStatus(colors, "Danger", darkSurface ? "F2939B" : "A32037", darkSurface ? "33202C" : "FCE9ED", surface, customBackground, darkSurface, foregrounds);
        AddStatus(colors, "Success", darkSurface ? "86CCAC" : "176243", darkSurface ? "18332E" : "E4F4EB", surface, customBackground, darkSurface, foregrounds);
        AddStatus(colors, "Warning", darkSurface ? "E6B67A" : "845000", darkSurface ? "332C23" : "FFF0D5", surface, customBackground, darkSurface, foregrounds);
        colors["InfoSoftBrush"] = !customBackground ? Hex(isDark ? "192E40" : "E6F1F8") : FitSurface(Mix(surface, secondary, 0.12), darkSurface);
        // 运行状态按钮沿用正文色，因此正文同时覆盖所有状态底色。
        var allBackgrounds = foregrounds.Concat(new[] { colors["DangerSoftBrush"], colors["SuccessSoftBrush"], colors["WarningSoftBrush"], colors["InfoSoftBrush"] }).ToArray();
        colors["TextBrush"] = Readable(text, allBackgrounds);
        colors["MutedTextBrush"] = Readable(muted, allBackgrounds);
        colors["SecondaryAccentBrush"] = Readable(secondary, allBackgrounds);
        colors["TextSelectionForegroundBrush"] = ButtonForeground(colors["TextSelectionBrush"]);
        return colors;
    }

    /// <summary>只接受不透明的六位十六进制色值，避免主题透明度绕过可读性保护。</summary>
    internal static bool TryParseHex(string? value, out Color color)
    {
        color = default;
        var digits = value?.Trim();
        if (digits?.StartsWith('#') == true) digits = digits[1..];
        if (digits?.Length != 6 || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number)) return false;
        color = Color.FromRgb((byte)(number >> 16), (byte)(number >> 8), (byte)number);
        return true;
    }

    /// <summary>按线性 sRGB 相对亮度计算两个不透明颜色之间的文字对比度。</summary>
    internal static double Contrast(Color first, Color second)
    {
        var left = Luminance(first); var right = Luminance(second);
        return (Math.Max(left, right) + 0.05) / (Math.Min(left, right) + 0.05);
    }

    /// <summary>为固定含义的状态生成底色、正文和边框，不受用户强调色改变。</summary>
    private static void AddStatus(Dictionary<string, Color> colors, string name, string foreground, string background,
        Color surface, bool customBackground, bool darkSurface, IReadOnlyList<Color> backgrounds)
    {
        var preferred = Hex(foreground);
        var soft = customBackground ? FitSurface(Mix(surface, preferred, 0.10), darkSurface) : Hex(background);
        var readable = Readable(preferred, backgrounds.Append(soft).ToArray());
        colors[name + "Brush"] = readable;
        colors[name + "SoftBrush"] = soft;
        colors[name + "BorderBrush"] = Mix(soft, readable, 0.46);
    }

    /// <summary>优先保留原色；必要时向可读性更好的黑或白逐步调整，而非限制用户输入颜色。</summary>
    private static Color Readable(Color preferred, IReadOnlyList<Color> backgrounds)
    {
        if (backgrounds.All(background => Contrast(preferred, background) >= 4.5)) return preferred;
        var blackMinimum = backgrounds.Min(background => Contrast(Colors.Black, background));
        var whiteMinimum = backgrounds.Min(background => Contrast(Colors.White, background));
        var target = blackMinimum >= whiteMinimum ? Colors.Black : Colors.White;
        for (var step = 1; step <= 100; step++)
        {
            var candidate = Mix(preferred, target, step / 100d);
            if (backgrounds.All(background => Contrast(candidate, background) >= 4.5)) return candidate;
        }
        return target;
    }

    /// <summary>按钮实底优先保留品牌深蓝文字，其他颜色选高对比的黑白文字并留出悬停余量。</summary>
    private static Color ButtonForeground(Color background)
    {
        var navy = Hex("0C111C");
        if (Contrast(navy, background) >= 7) return navy;
        return Contrast(Colors.Black, background) >= Contrast(Colors.White, background) ? Colors.Black : Colors.White;
    }

    /// <summary>高对比度模式交由 Windows 配色，关闭半透明装饰以保证系统可访问性。</summary>
    private static IReadOnlyDictionary<string, Color> HighContrast()
    {
        var colors = new Dictionary<string, Color>(Create(false), StringComparer.Ordinal);
        foreach (var key in colors.Keys.ToArray()) colors[key] = SystemColors.WindowColor;
        foreach (var key in new[] { "TextBrush", "MutedTextBrush", "AccentTextBrush", "SecondaryAccentBrush", "BorderBrush", "SelectionBorderBrush", "HoverBorderBrush", "FocusSoftBrush", "ScrollThumbBrush", "TextSelectionForegroundBrush", "DangerBrush", "DangerBorderBrush", "SuccessBrush", "SuccessBorderBrush", "WarningBrush", "WarningBorderBrush" })
            colors[key] = SystemColors.WindowTextColor;
        colors["AccentBrush"] = SystemColors.HighlightColor;
        colors["AccentForegroundBrush"] = SystemColors.HighlightTextColor;
        colors["TextSelectionBrush"] = SystemColors.HighlightColor;
        colors["TextSelectionForegroundBrush"] = SystemColors.HighlightTextColor;
        colors["HoverTintBrush"] = Colors.Transparent;
        colors["PressedTintBrush"] = Colors.Transparent;
        return colors;
    }

    /// <summary>将已知的内置色值转换为颜色，统一内置色板和用户色值的解析路径。</summary>
    private static Color Hex(string value) => TryParseHex(value, out var color) ? color : throw new ArgumentException("内置主题色值无效。", nameof(value));

    /// <summary>在不透明颜色之间插值，用于派生柔和底色、边框和状态层次。</summary>
    private static Color Mix(Color first, Color second, double amount) => Color.FromRgb(
        (byte)Math.Round(first.R + (second.R - first.R) * amount),
        (byte)Math.Round(first.G + (second.G - first.G) * amount),
        (byte)Math.Round(first.B + (second.B - first.B) * amount));

    /// <summary>只改变装饰层透明度，不改变其来源颜色。</summary>
    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>派生底色保持在统一明暗区间，防止中间灰背景让同一组文字无法兼顾所有表面。</summary>
    private static Color FitSurface(Color color, bool dark)
    {
        var target = dark ? Colors.Black : Colors.White;
        for (var step = 0; step <= 100; step++)
        {
            var candidate = Mix(color, target, step / 100d);
            var luminance = Luminance(candidate);
            if (dark ? luminance <= 0.16 : luminance >= 0.20) return candidate;
        }
        return target;
    }

    /// <summary>先线性化 sRGB 通道，再按人眼敏感度计算相对亮度。</summary>
    private static double Luminance(Color color)
    {
        static double Channel(byte value) { var fraction = value / 255d; return fraction <= 0.04045 ? fraction / 12.92 : Math.Pow((fraction + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
}
