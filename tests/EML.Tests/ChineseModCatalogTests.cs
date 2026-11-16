using System.Text;
using EML.Core.Minecraft.Mods.Online;
using EML.Infrastructure.Minecraft.Mods.Online;

namespace EML.Tests;

public sealed class ChineseModCatalogTests
{
    /// <summary>内置目录可离线读取，固定平台编号提供名称与原创简介。</summary>
    [Fact]
    public void EmbeddedCatalogContainsVerifiedCommonProjects()
    {
        var catalog = ChineseModCatalog.Default;
        Assert.InRange(catalog.EntryCount, 40, 10000);
        Assert.True(catalog.ProjectCount >= catalog.EntryCount);
        Assert.Same(catalog, ChineseModCatalog.Default);
        var jei = catalog.Find(new(ModProvider.Modrinth, "u6dRKJwZ"));
        Assert.NotNull(jei);
        Assert.Equal("JEI 物品管理器", jei.ChineseName);
        Assert.Contains("配方", jei.Summary);
        Assert.Equal(jei, catalog.Find(new(ModProvider.CurseForge, "238222")));
        Assert.Equal("旅行地图", catalog.Find(new(ModProvider.Modrinth, "lfHFW1mp"))?.ChineseName);
    }

    /// <summary>相同字符串属于不同平台时不能串用中文信息，未知编号也不按名称猜测。</summary>
    [Fact]
    public void ProjectLookupUsesExactProviderAndCaseSensitiveId()
    {
        Assert.Null(ChineseModCatalog.Default.Find(new(ModProvider.CurseForge, "u6dRKJwZ")));
        Assert.Null(ChineseModCatalog.Default.Find(new(ModProvider.Modrinth, "U6DRKJWZ")));
        Assert.Null(ChineseModCatalog.Default.Find(new(ModProvider.Modrinth, "jei")));
        Assert.Null(ChineseModCatalog.Default.Find(new(ModProvider.Modrinth, "unknown1")));
    }

    /// <summary>只解析完整中文别名，允许首尾空格与中文别名中英文字母的大小写差异。</summary>
    [Theory]
    [InlineData("旅行地图", "JourneyMap")]
    [InlineData("  苹果皮  ", "AppleSkin")]
    [InlineData("  jei物品管理器  ", "Just Enough Items")]
    [InlineData("机械动力", "Create")]
    [InlineData("经验球合并", "Clumps")]
    [InlineData("应用能源", "Applied Energistics 2")]
    public void ExactChineseAliasesResolveToOriginalSearchName(string text, string expected)
    {
        Assert.Equal(expected, ChineseModCatalog.Default.ResolveSearchTerm(text));
    }

    /// <summary>英文搜索、部分中文词和未收录词不被改写，避免命中相似但不同的项目。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("JEI")]
    [InlineData("JourneyMap")]
    [InlineData("机械")]
    [InlineData("地图")]
    [InlineData("未知模组")]
    public void UnknownOrEnglishSearchTermsRemainUnchanged(string text)
    {
        Assert.Null(ChineseModCatalog.Default.ResolveSearchTerm(text));
    }

    /// <summary>重复平台编号直接拒绝，防止目录维护失误覆盖已有翻译。</summary>
    [Fact]
    public void DuplicateProjectIdentityIsRejected()
    {
        using var stream = Json("""
            [
              {"chineseName":"甲模组","englishName":"Alpha","summary":"说明甲","modrinthId":"aaaaaaaa"},
              {"chineseName":"乙模组","englishName":"Beta","summary":"说明乙","modrinthId":"aaaaaaaa"}
            ]
            """);
        Assert.Throws<InvalidDataException>(() => new ChineseModCatalog(stream));
    }

    /// <summary>同一中文别名指向不同原名时保留用户输入，不取第一条或最后一条。</summary>
    [Fact]
    public void AmbiguousAliasesAreNeverResolved()
    {
        using var stream = Json("""
            [
              {"chineseName":"甲模组","englishName":"Alpha","summary":"说明甲","modrinthId":"aaaaaaaa","aliases":["通用名字"]},
              {"chineseName":"乙模组","englishName":"Beta","summary":"说明乙","modrinthId":"bbbbbbbb","aliases":["通用名字"]},
              {"chineseName":"丙模组","englishName":"Alpha","summary":"说明丙","modrinthId":"cccccccc","aliases":["通用名字"]}
            ]
            """);
        var catalog = new ChineseModCatalog(stream);
        Assert.Null(catalog.ResolveSearchTerm("通用名字"));
        Assert.Equal("Alpha", catalog.ResolveSearchTerm("甲模组"));
        Assert.Equal(3, catalog.EntryCount);
    }

    /// <summary>一个已确认的条目可分别登记两个平台身份，显示文案不改写身份。</summary>
    [Fact]
    public void OneTranslationMayHaveTwoIndependentlyVerifiedProviderIds()
    {
        using var stream = Json("""
            [{"chineseName":"测试模组","englishName":"Test Mod","summary":"测试说明","modrinthId":"abcdefgh","curseForgeId":"12345"}]
            """);
        var catalog = new ChineseModCatalog(stream);
        Assert.Equal(1, catalog.EntryCount);
        Assert.Equal(2, catalog.ProjectCount);
        Assert.Equal(catalog.Find(new(ModProvider.Modrinth, "abcdefgh")), catalog.Find(new(ModProvider.CurseForge, "12345")));
        Assert.Null(catalog.Find(new(ModProvider.Modrinth, "12345")));
    }

    /// <summary>无身份、非固定编号和缺少文案的目录不能静默进入显示层。</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"chineseName\":\"名称\",\"englishName\":\"Name\",\"summary\":\"说明\"}]")]
    [InlineData("[{\"chineseName\":\"名称\",\"englishName\":\"Name\",\"summary\":\"说明\",\"modrinthId\":\"jei\"}]")]
    [InlineData("[{\"chineseName\":\"名称\",\"englishName\":\"Name\",\"summary\":\"说明\",\"curseForgeId\":\"01\"}]")]
    [InlineData("[{\"chineseName\":\"Name\",\"englishName\":\"Name\",\"summary\":\"说明\",\"modrinthId\":\"abcdefgh\"}]")]
    public void InvalidCatalogEntriesAreRejected(string json)
    {
        using var stream = Json(json);
        Assert.Throws<InvalidDataException>(() => new ChineseModCatalog(stream));
    }

    /// <summary>测试目录只存在内存中，不修改内置数据或用户文件。</summary>
    private static MemoryStream Json(string value) => new(Encoding.UTF8.GetBytes(value));
}
