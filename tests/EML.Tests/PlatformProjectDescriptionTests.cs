using EML.Infrastructure.Minecraft.Catalog;

namespace EML.Tests;

/// <summary>平台介绍仅转换排版，确保中文原文、英文回退和安全纯文本边界。</summary>
public sealed class PlatformProjectDescriptionTests
{
    /// <summary>HTML 标题、段落与列表保留分隔，多语言文字不会被当成标签删除。</summary>
    [Fact]
    public void HtmlPreservesParagraphsAndAuthorLanguages()
    {
        var text = PlatformProjectDescription.FromHtml("<h2>中文介绍</h2><p>科技 &amp; 冒险。</p><ul><li>中文功能</li><li>English feature</li></ul><p>繁體中文說明。</p>");
        Assert.Equal("中文介绍\n\n科技 & 冒险。\n\n• 中文功能\n\n• English feature\n\n繁體中文說明。", text);
    }

    /// <summary>脚本、样式、嵌入网页和 HTML 注释不作为介绍呈现。</summary>
    [Theory]
    [InlineData("<script>alert('hidden')</script>")]
    [InlineData("<style>.hidden {display:block}</style>")]
    [InlineData("<iframe src='https://example.test'>hidden</iframe>")]
    [InlineData("<object>hidden</object>")]
    [InlineData("<!-- hidden -->")]
    public void HtmlDropsExecutableAndNonContentBlocks(string block) =>
        Assert.Equal("作者正文", PlatformProjectDescription.FromHtml("<p>作者正文</p>" + block));

    /// <summary>缺少闭合标签的脚本和样式也不在文本中泄露成正文。</summary>
    [Theory]
    [InlineData("<script>hidden")]
    [InlineData("<style>hidden")]
    [InlineData("<!-- hidden")]
    public void UnterminatedNonContentBlockIsRemoved(string block) =>
        Assert.Equal("作者正文", PlatformProjectDescription.FromHtml("<p>作者正文</p>" + block));

    /// <summary>图片不会加载，Markdown 链接只保留作者可读标签，并保留中文段落。</summary>
    [Fact]
    public void MarkdownRemovesFormattingWithoutTranslatingText()
    {
        var body = "# 中文介绍\n\n**科技**与 __冒险__，`1.20.1`。\n\n[说明](https://example.test/docs)\n\n![截图](https://example.test/picture.png)\n\n## English\n\nOriginal English body.";
        var text = PlatformProjectDescription.FromMarkdown(body);
        Assert.Equal("中文介绍\n\n科技与 冒险，1.20.1。\n\n说明\n\n截图\n\nEnglish\n\nOriginal English body.", text);
        Assert.DoesNotContain("https://", text);
    }

    /// <summary>正文中用于解释标签的编码文字只解码一次，作为普通文字保留。</summary>
    [Fact]
    public void EncodedMarkupRemainsPlainText() =>
        Assert.Equal("示例 <tag> &lt;tag&gt;", PlatformProjectDescription.FromHtml("<p>示例 &lt;tag&gt; &amp;lt;tag&amp;gt;</p>"));

    /// <summary>没有介绍时返回空文本，让页面显示平台摘要而不是生成内容。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n \t ")]
    public void MissingBodyStaysEmpty(string? body)
    { Assert.Equal("", PlatformProjectDescription.FromHtml(body)); Assert.Equal("", PlatformProjectDescription.FromMarkdown(body)); }

    /// <summary>英文正文逐字保留，不因是否包含中文而套用其他项目说明。</summary>
    [Fact]
    public void EnglishOnlyBodyStaysEnglish() =>
        Assert.Equal("Explore a world of technology and adventure.", PlatformProjectDescription.FromMarkdown("Explore a world of technology and adventure."));

    /// <summary>输入与显示输出分别限长，避免远端正文让 WPF 页面处理过量文字。</summary>
    [Theory]
    [InlineData(PlatformProjectDescription.MaximumInputLength + 1)]
    [InlineData(PlatformProjectDescription.MaximumTextLength + 1)]
    public void OversizedBodyIsRejected(int length)
    {
        var text = new string('x', length);
        Assert.Throws<InvalidDataException>(() => PlatformProjectDescription.FromHtml(text));
        Assert.Throws<InvalidDataException>(() => PlatformProjectDescription.FromMarkdown(text));
    }
}
