using System.Net;
using System.Text.RegularExpressions;

namespace EML.Infrastructure.Minecraft.Catalog;

/// <summary>把平台正文转换为有界纯文本，不加载图片、执行网页内容或生成新的译文。</summary>
internal static class PlatformProjectDescription
{
    internal const int MaximumInputLength = 1024 * 1024;
    internal const int MaximumTextLength = 200000;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>保留作者的 HTML 段落和列表内容，删除脚本、样式及标签，仅解码一次字符实体。</summary>
    public static string FromHtml(string? value) => Finish(RemoveHtml(Validate(value)));

    /// <summary>移除常见 Markdown 排版标记与图片地址，链接只显示作者标签，所有语言保持原来的顺序。</summary>
    public static string FromMarkdown(string? value)
    {
        var text = RemoveHtml(Validate(value));
        text = Replace(text, @"(?m)^\s*(```|~~~)[^\n]*$", "");
        text = Replace(text, @"(?m)^[ \t]{0,3}#{1,6}[ \t]+", "");
        text = Replace(text, @"(?m)^[ \t]*>[ \t]?", "");
        text = Replace(text, @"!?\[([^\]\n]*)\]\([^\n)]*\)", "$1");
        text = Replace(text, @"!?\[([^\]\n]*)\]\[[^\]\n]*\]", "$1");
        text = Replace(text, @"(?m)^[ \t]{0,3}\[[^\]\n]+\]:[^\n]*$", "");
        text = Replace(text, @"\*\*([^*\n]+)\*\*", "$1");
        text = Replace(text, @"__([^_\n]+)__", "$1");
        text = Replace(text, @"~~([^~\n]+)~~", "$1");
        text = Replace(text, @"`([^`\n]+)`", "$1");
        return Finish(text);
    }

    /// <summary>拒绝超大正文，在文本转换前限定内存和正则处理范围。</summary>
    private static string Validate(string? value)
    {
        if (value is null) return "";
        if (value.Length > MaximumInputLength) throw new InvalidDataException("平台介绍过长，请在项目网页查看完整内容。");
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>将 HTML 结构转成文本分隔；脚本或样式即使没有结束标签也不显示其内容。</summary>
    private static string RemoveHtml(string value)
    {
        var text = Replace(value, @"(?is)<!--.*?(?:-->|$)", "");
        text = Replace(text, @"(?is)<script\b[^>]*>.*?(?:</script\s*>|$)", "");
        text = Replace(text, @"(?is)<style\b[^>]*>.*?(?:</style\s*>|$)", "");
        text = Replace(text, @"(?is)<(?:iframe|object)\b[^>]*>.*?(?:</(?:iframe|object)\s*>|$)", "");
        text = Replace(text, @"(?i)<li\b[^>]*>", "\n• ");
        text = Replace(text, @"(?i)</?(?:p|div|section|article|h[1-6]|ul|ol|li|blockquote|pre|tr)\b[^>]*>|<br\b[^>]*>", "\n");
        text = Replace(text, @"(?i)</(?:td|th)\s*>", " \t ");
        return Replace(text, @"</?[A-Za-z][^>]*>", "");
    }

    /// <summary>解码正文并整理空行；输出只作为 WPF 文本显示，不再交给 HTML 解析器。</summary>
    private static string Finish(string value)
    {
        var text = WebUtility.HtmlDecode(value).Replace('\u00a0', ' ');
        text = Replace(text, @"[^\S\n]+", " ");
        text = Replace(text, @"(?m)^ +| +$", "");
        text = Replace(text, @"\n{3,}", "\n\n").Trim();
        if (text.Length > MaximumTextLength) throw new InvalidDataException("平台介绍过长，请在项目网页查看完整内容。");
        return text;
    }

    /// <summary>所有远端文本匹配都有时间上限，异常可由页面降级为平台摘要与网页入口。</summary>
    private static string Replace(string value, string pattern, string replacement) =>
        Regex.Replace(value, pattern, replacement, RegexOptions.CultureInvariant, MatchTimeout);
}
