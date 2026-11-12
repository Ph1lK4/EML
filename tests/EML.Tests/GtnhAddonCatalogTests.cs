using EML.Core.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Gtnh;

namespace EML.Tests;

/// <summary>固定用户提供的分类、项目身份和专用发行入口，避免扩充目录时漏项或串源。</summary>
public sealed class GtnhAddonCatalogTests
{
    /// <summary>资源与光影不混入 80 个模组中，重复的描边资源链接只收录一次。</summary>
    [Fact]
    public void CatalogKeepsDistinctContentKindsAndUniqueIds()
    {
        var items = GtnhAddonCatalog.Items;
        Assert.Equal(113, items.Count);
        Assert.Equal(items.Count, items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(80, items.Count(x => x.Kind == GtnhAddonKind.Mod));
        Assert.Equal(27, items.Count(x => x.Kind == GtnhAddonKind.ResourcePack));
        Assert.Equal(3, items.Count(x => x.Kind == GtnhAddonKind.Shader));
        Assert.Equal(2, items.Count(x => x.Kind == GtnhAddonKind.ShaderLoader && x.Category == "光影加载器"));
        Assert.Single(items, x => x.Kind == GtnhAddonKind.ShaderLoader && x.Category == "光影补丁");
        Assert.Single(items, x => x.Id == "outlined-ores-modern");
        Assert.DoesNotContain(items, x => x.Repository == "Ranzuu/OutlinedOres-Modern");
    }

    /// <summary>逐类检查用户给出的模组数量，防止编辑分类时隐式丢失项目。</summary>
    [Theory]
    [InlineData("功能增强", 12)]
    [InlineData("性能优化", 3)]
    [InlineData("难度提高", 1)]
    [InlineData("视听增强", 18)]
    [InlineData("流程修改", 25)]
    [InlineData("流程优化", 8)]
    [InlineData("AE2增强", 9)]
    [InlineData("流程毁灭", 4)]
    public void ModCategoryContainsAllSuppliedEntries(string category, int count) =>
        Assert.Equal(count, GtnhAddonCatalog.Items.Count(x => x.Kind == GtnhAddonKind.Mod && x.Category == category));

    /// <summary>资源包按自己的分类显示，不与模组规则标记混用。</summary>
    [Theory]
    [InlineData("无障碍与实用性", 7)]
    [InlineData("深色模式", 6)]
    [InlineData("升级与重制", 14)]
    public void ResourceCategoriesAreSeparate(string category, int count) =>
        Assert.Equal(count, GtnhAddonCatalog.Items.Count(x => x.Kind == GtnhAddonKind.ResourcePack && x.Category == category));

    /// <summary>后四类明确提示非星门规则，其余项目不能反向宣称所有版本已获规则认可。</summary>
    [Fact]
    public void NonStargateWarningsFollowUserClassification()
    {
        foreach (var item in GtnhAddonCatalog.Items)
        {
            var expected = item.Kind == GtnhAddonKind.Mod && item.Category is "流程修改" or "流程优化" or "AE2增强" or "流程毁灭";
            Assert.Equal(expected, item.IsNonStargate);
            if (expected) Assert.Contains("非星门规则", item.Notes);
            else Assert.Contains("不代表", item.RuleDescription);
        }
        Assert.Equal(46, GtnhAddonCatalog.Items.Count(x => x.IsNonStargate));
    }

    /// <summary>保留已发布的项目 ID 与首项，避免旧选择或现有调用链指向新项目。</summary>
    [Fact]
    public void ExistingProjectIdsRemainStable()
    {
        Assert.Equal("worldedit", GtnhAddonCatalog.Items[0].Id);
        Assert.Equal("GTNewHorizons/worldedit-gtnh", Find("worldedit").Repository);
        Assert.Equal("asdflj/NeverEnoughCharacters-Rework", Find("nech").Repository);
        Assert.Equal("reobf/Programmable-Hatches-Mod", Find("programmable-hatches").Repository);
        Assert.Equal("asdflj/MouseSideButtonFix", Find("mouse-side-button").Repository);
    }

    /// <summary>所有自动入口都有明确平台身份，手动入口只展示可访问的作者页面。</summary>
    [Fact]
    public void EveryEntryHasOneUsableSourceIdentity()
    {
        foreach (var item in GtnhAddonCatalog.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.False(string.IsNullOrWhiteSpace(item.Summary));
            Assert.False(string.IsNullOrWhiteSpace(item.Notes));
            Assert.True(Uri.TryCreate(item.ProjectUrl, UriKind.Absolute, out var uri));
            Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
            Assert.Equal(item.Provider != GtnhAddonProvider.External, item.CanQueryReleases);
            switch (item.Provider)
            {
                case GtnhAddonProvider.Github:
                    Assert.Equal(2, item.Repository.Split('/').Length);
                    Assert.Null(item.CurseForgeProjectId); Assert.Null(item.ModrinthProjectId);
                    break;
                case GtnhAddonProvider.CurseForge:
                    Assert.True(item.CurseForgeProjectId > 0);
                    Assert.Empty(item.Repository); Assert.Null(item.ModrinthProjectId);
                    break;
                case GtnhAddonProvider.Modrinth:
                    Assert.False(string.IsNullOrWhiteSpace(item.ModrinthProjectId));
                    Assert.Empty(item.Repository); Assert.Null(item.CurseForgeProjectId);
                    break;
                case GtnhAddonProvider.External:
                    Assert.False(string.IsNullOrWhiteSpace(item.ManualReason));
                    Assert.Empty(item.Repository); Assert.Null(item.CurseForgeProjectId); Assert.Null(item.ModrinthProjectId);
                    break;
            }
        }
    }

    /// <summary>平台 ID 来自官方 API 的精确 slug 查询，不用名称相似的现代版替换 1.7.10 项目。</summary>
    [Theory]
    [InlineData("fps-reducer", 280294)]
    [InlineData("world-tooltips", 241545)]
    [InlineData("better-foliage", 228529)]
    [InlineData("chat-bubbles", 235520)]
    [InlineData("extra-player-render", 481042)]
    [InlineData("fancy-block-particles", 254441)]
    [InlineData("itemphysic-lite", 270441)]
    [InlineData("matmos", 366256)]
    [InlineData("colored-gregtech-tools", 412833)]
    [InlineData("gtnh-16x-machine-textures", 977463)]
    [InlineData("zedtech-gtnh", 1333595)]
    [InlineData("sildurs-vibrant-shaders", 544096)]
    public void CurseForgeProjectIdentityIsPinned(string id, long projectId)
    {
        var item = Find(id);
        Assert.Equal(GtnhAddonProvider.CurseForge, item.Provider);
        Assert.Equal(projectId, item.CurseForgeProjectId);
    }

    /// <summary>同仓库专用纹理与完整资源包保持不同的固定身份。</summary>
    [Fact]
    public void DedicatedResourceReleasesDoNotExpandToUnrelatedAssets()
    {
        var machines = Find("usernm-machines");
        Assert.Equal("imTheSupremeOne/GTNH-Textures-Usernm", machines.Repository);
        Assert.Equal("machines", machines.ReleaseTag);
        Assert.Equal("Usernm0/GTNH-Textures-Usernm", Find("usernm-textures").Repository);
        var axe = Find("shadow-ui-invisible-healing-axe");
        Assert.Equal("Ranzuu/Shadow-UI", axe.Repository);
        Assert.Equal("Just-InvisHealingAxe", axe.ReleaseTag);
        Assert.Null(Find("shadow-ui").ReleaseTag);
        Assert.Equal(3078376, Find("colored-gregtech-tools").CurseForgeFileId);
    }

    /// <summary>未提供直接发行来源的入口不能自动下载源码归档、论坛附件或聊天链接。</summary>
    [Theory]
    [InlineData("better-rain", "www.minecraftforum.net")]
    [InlineData("stellar-fusion", "web.archive.org")]
    [InlineData("discord-resourcepack", "discord.com")]
    [InlineData("ivelitex", "gitlab.com")]
    [InlineData("optifine", "optifine.net")]
    public void AuthorPageOnlyEntriesStayManual(string id, string host)
    {
        var item = Find(id);
        Assert.Equal(GtnhAddonProvider.External, item.Provider);
        Assert.False(item.CanQueryReleases);
        Assert.Equal(host, new Uri(item.ProjectUrl).Host);
    }

    /// <summary>Modernity 三个独立入口使用对应仓库，不能把组织主页或主包附带资产当作界面包来源。</summary>
    [Theory]
    [InlineData("modernity-gtnh", "Modernity-GTNH")]
    [InlineData("modernity-ui", "Modernity-GTNH-UI")]
    [InlineData("modernity-dark-ui", "Modernity-GTNH-Dark-UI")]
    public void ModernityResourcePacksHaveDistinctInstallableSources(string id, string repository)
    {
        var item = Find(id);
        Assert.Equal(GtnhAddonProvider.Github, item.Provider);
        Assert.Equal(GtnhAddonKind.ResourcePack, item.Kind);
        Assert.Equal("ModernityGTNH/" + repository, item.Repository);
        Assert.True(item.CanQueryReleases);
        Assert.Null(item.ManualReason);
        Assert.NotNull(item.AssetNamePattern);
        Assert.Single(GtnhAddonCatalog.Items, other => other.Repository == item.Repository);
    }

    /// <summary>已核实的仓库迁移保留稳定项目 ID，现代矿石纹理与普通纹理仍独立筛选。</summary>
    [Fact]
    public void VerifiedRepositoryMovesPreserveStableEntriesAndVariantFilters()
    {
        Assert.Equal("GTNotLeisure/MyCTMLib", Find("myctmlib").Repository);
        Assert.Equal("GTNotLeisure/mineshot", Find("mineshot").Repository);
        Assert.Equal("GTNotLeisure/GT-Not-Leisure", Find("gt-not-leisure").Repository);
        Assert.Equal("Tungsten-W-74/CheaperVoidMiners", Find("cheaper-void-miners").Repository);
        Assert.Equal("S4mpsa/Glass-UI", Find("transparent-ui").Repository);
        Assert.Equal("Ranzuu/GTNH-OutlinedOres", Find("outlined-ores-modern").Repository);
        Assert.NotEqual(Find("outlined-ores-modern").AssetNamePattern, Find("gtnh-outlined-ores").AssetNamePattern);
        Assert.Equal("Shadow.UI.", Find("shadow-ui").AssetNamePrefix);
    }

    /// <summary>补丁仍按模组安装，光影包单独登记；两者均不自动选择最新发行。</summary>
    [Theory]
    [InlineData("complementary-unbound", GtnhAddonKind.Shader)]
    [InlineData("bsl-shaders", GtnhAddonKind.Shader)]
    [InlineData("euphoria-patches", GtnhAddonKind.ShaderLoader)]
    public void ModrinthShadersAndPatchHaveExplicitKinds(string id, GtnhAddonKind kind)
    {
        var item = Find(id);
        Assert.Equal(GtnhAddonProvider.Modrinth, item.Provider);
        Assert.Equal(id, item.ModrinthProjectId);
        Assert.Equal(kind, item.Kind);
        Assert.Contains("不表示最新版", item.Notes);
    }

    /// <summary>目录中保留渲染加载器冲突提示，不把已有 Angelica 替换为 OptiFine。</summary>
    [Fact]
    public void ShaderLoaderConflictIsVisibleBeforeInstallation()
    {
        Assert.Contains("OptiFine 不兼容", Find("angelica").Notes);
        Assert.Contains("Angelica 不兼容", Find("optifine").Notes);
        Assert.Equal("GTNewHorizons/Angelica", Find("angelica").Repository);
    }

    /// <summary>保留用户提供的项目身份；仓库迁移仅使用已核实的官方目标，不能按名称猜测。</summary>
    [Fact]
    public void GithubModRepositorySetMatchesSuppliedList()
    {
        const string repositories = """
            NOOB2RUSH/AE2-Auto-Pattern-Upload_2.9.0
            asdflj/NeverEnoughCharacters-Rework
            asdflj/MouseSideButtonFix
            GTNewHorizons/worldedit-gtnh
            wohaopa/OmniOcular-Unofficial
            XSana/NEI-RecipeTree
            asdflj/AppliedCooking
            HOMEFTW/JdkJarVersion21Enforcer
            YueLengM/pico-8-GTNH
            czqwq/YesSteveModel-Unofficial
            czqwq/ImmersivePaintingsNH
            YueLengM/gtnh-bq-ext
            QuanhuZeYu/Qz-UILib
            FalsePattern/LagGogglesLegacy
            kuba6000/GTNH-Extra-Nerf
            GTNotLeisure/mineshot
            RealSilverMoon/SmallPhone
            czqwq/FMusic
            GTNotLeisure/MyCTMLib
            eudesjuniorr/DarkTextFix
            xiaoxing2005/BetterTooltipBox
            mist475/DynamicSurroundings
            GTNewHorizons/DistantHorizons
            KatatsumuriPan/BetterLineBreak
            kanami2024520-jpg/Crosshair-Selection
            Nxer/Twist-Space-Technology-Mod
            reobf/Programmable-Hatches-Mod
            GTNotLeisure/GT-Not-Leisure
            RealSilverMoon/BoxPlusPlus
            CallmeSHaobe/123Technology
            czqwq/MessTech
            ElytraServers/gtnh-no-nerf
            Keriils/NH-Utilities
            XSana/TakoTech
            Ol925/ThinkTech
            Tungsten-W-74/CheaperVoidMiners
            z1564058782/GT-Not-Hard
            reobf/Void-Miner-Tweak-Mod
            MIAOKATZE/GT-Simple-Wireless-Network
            MIAOKATZE/GT-Interesting-Thing
            MIAOKATZE/GT-Steam-Reborn
            MIAOKATZE/GT-Miao-Blueprint
            shenFNX/Fission-Evolved
            skyc10/mcphone-gtnh
            Shigure-Ruiseki/OKBackpack
            QionSi/GregTech-Production-line-simplification
            gitudH5R8/TenkeiTech
            ikkkkkkkuuu/GT-Never-Reset
            lonei-G/GTRH
            nimeng1299/libertas
            czqwq/EZMiner
            ElytraServers/Bandit-Legacy
            QuanhuZeYu/Qz-Miner
            czqwq/EZNuclear
            czqwq/Torcherino-GTNH
            kuolemax/time-crystal
            YueLengM/nh-kantan
            nimeng1299/Enigmatic-Legacy-1.7.10
            asdflj/AE2Things
            Ch4oooooooLL/AE2PatternGen
            clfpwp/WildcardPatternforGTNH-1.7.10
            DancingSnow0517/AE2InfinityCell
            DancingSnow0517/Applied-Energistics-EU-Network
            suntide-20210418/AdvancedMemoryCard
            233max258/Wtcts-1.7.10
            Yang120231/GTNH-ECO-1.7.10
            zeroCNM/AE2-Convenience-Features
            SafariXiu/EyeOfHarmonyBuffer
            rhnu/GTNH-Nyx
            ElytraServers/GTNH-CutCorners
            QuanhuZeYu/GTNHModify-forkBy_wohaopa
            """;
        var expected = repositories.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal).ToArray();
        var actual = GtnhAddonCatalog.Items.Where(x => x.Kind == GtnhAddonKind.Mod && x.Provider == GtnhAddonProvider.Github)
            .Select(x => x.Repository).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    /// <summary>按稳定 ID 读取单个目录项，重复或遗漏会使调用处直接失败。</summary>
    private static GtnhAddon Find(string id) => Assert.Single(GtnhAddonCatalog.Items, x => x.Id == id);
}
