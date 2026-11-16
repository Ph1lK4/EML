using EML.Core.Minecraft.Mods.Online;

namespace EML.App.Services;

/// <summary>中文展示只发生在页面层，平台名称、固定编号和下载文件保持原始值。</summary>
public static class OnlineModDisplayText
{
    /// <summary>只有固定项目编号被收录时才显示中文名，并保留平台原名便于核对。</summary>
    public static string Name(OnlineProjectId? project, string original, IOnlineModLocalization? localization)
    {
        var translated = project is null ? null : localization?.Find(project)?.ChineseName;
        if (string.IsNullOrWhiteSpace(translated) || string.Equals(translated, original, StringComparison.Ordinal)) return original;
        return string.IsNullOrWhiteSpace(original) ? translated : $"{translated}（{original}）";
    }

    /// <summary>已收录项目使用整理的中文简介，缺少简介时仍显示平台原文。</summary>
    public static string Summary(OnlineProjectId? project, string original, IOnlineModLocalization? localization) =>
        TranslationSummary(project, localization) ?? original;

    /// <summary>明确区分 EML 整理的中文简介与平台作者原文，避免被当作官方译文。</summary>
    public static string SummaryNote(OnlineProjectId? project, IOnlineModLocalization? localization) =>
        TranslationSummary(project, localization) is null ? "" : "中文简介由 EML 整理";

    /// <summary>仅转换包含中文且明确收录的搜索词，英文及未知输入原样交给平台。</summary>
    public static string SearchTerm(string input, IOnlineModLocalization? localization)
    {
        var text = input.Trim();
        if (!text.Any(character => character is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff')) return text;
        var translated = localization?.ResolveSearchTerm(text);
        return string.IsNullOrWhiteSpace(translated) ? text : translated;
    }

    /// <summary>版本通道采用中文标签，不修改官方版本字符串。</summary>
    public static string Channel(string? value) => value?.ToLowerInvariant() switch
    {
        "release" => "正式版", "beta" => "测试版", "alpha" => "早期测试版", _ => "未标注"
    };

    /// <summary>加载器保留通用品牌名称，空选项显示为全部加载器。</summary>
    public static string Loader(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" => "全部加载器", "forge" => "Forge", "fabric" => "Fabric", "neoforge" => "NeoForge",
        "quilt" => "Quilt", "liteloader" => "LiteLoader", _ => value ?? "全部加载器"
    };

    /// <summary>空白翻译不掩盖原始简介。</summary>
    private static string? TranslationSummary(OnlineProjectId? project, IOnlineModLocalization? localization)
    {
        var summary = project is null ? null : localization?.Find(project)?.Summary;
        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }
}
