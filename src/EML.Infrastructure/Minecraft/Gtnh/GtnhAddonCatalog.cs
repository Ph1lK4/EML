using EML.Core.Minecraft.Gtnh;

namespace EML.Infrastructure.Minecraft.Gtnh;

/// <summary>按用户提供的 Wiki 分类维护固定项目目录；收录不等于兼容或星门规则资格保证。</summary>
internal static class GtnhAddonCatalog
{
    private const string CompatibilityNote = "请选择具体发行文件，并核对作者说明中的 GTNH、Java、前置依赖和客户端/服务端要求；不会自动选择最新版。";
    private const string NonStargateNote = "此项目属于非星门规则分类，会改变原整合包的流程、难度或平衡；使用前请备份存档，并确认服务器允许。";

    internal static IReadOnlyList<GtnhAddon> Items { get; } =
    [
        Github("worldedit", "WorldEdit · 建筑编辑", "复制、粘贴与编辑建筑，适合测试世界和建造。", "功能增强",
            "GTNewHorizons/worldedit-gtnh", "多人游戏需要服务端支持及相应权限；客户端安装不能获得服务器编辑权限。"),
        Github("ae2-auto-pattern-upload", "AE2 自动样板上传", "辅助 AE2 样板上传操作。", "功能增强",
            "NOOB2RUSH/AE2-Auto-Pattern-Upload_2.9.0", "此仓库明确带有 2.9.0 标识，请核对目标 GTNH 版本，不向旧版自动套用。"),
        Github("nech", "拼音搜索 · Never Enough Characters Rework", "在 NEI 中通过拼音查找物品。", "功能增强",
            "asdflj/NeverEnoughCharacters-Rework", "核对已有拼音搜索模组，避免重复安装旧版；此功能不等于完整汉化。"),
        Github("mouse-side-button", "鼠标侧键修复 · MouseSideButtonFix", "改善终端快捷键使用鼠标侧键时的操作。", "功能增强",
            "asdflj/MouseSideButtonFix", ""),
        Github("omniocular", "OmniOcular 非官方版", "扩展观察与信息提示功能。", "功能增强",
            "wohaopa/OmniOcular-Unofficial", ""),
        Github("nei-recipe-tree", "NEI 配方树 · NEI-RecipeTree", "以配方树辅助查看合成流程。", "功能增强",
            "XSana/NEI-RecipeTree", ""),
        Github("applied-cooking", "Applied Cooking · 烹饪辅助", "提供烹饪与存储网络相关辅助。", "功能增强",
            "asdflj/AppliedCooking", ""),
        Github("jdk-jar-version-enforcer", "JdkJarVersion21Enforcer", "Java 与 JAR 版本相关兼容辅助。", "功能增强",
            "HOMEFTW/JdkJarVersion21Enforcer", "这是特定 Java 环境的辅助模组，不会安装或升级 Java；请先阅读作者的运行版本要求。"),
        Github("pico-8", "PICO-8 GTNH", "PICO-8 相关游戏内扩展。", "功能增强",
            "YueLengM/pico-8-GTNH", ""),
        Github("yes-steve-model", "Yes Steve Model 非官方版", "扩展玩家模型表现。", "功能增强",
            "czqwq/YesSteveModel-Unofficial", ""),
        Github("immersive-paintings", "Immersive Paintings NH · 沉浸式绘画", "提供绘画与装饰相关功能。", "功能增强",
            "czqwq/ImmersivePaintingsNH", ""),
        Github("gtnh-bq-ext", "GTNH BQ 扩展", "扩展任务系统相关功能。", "功能增强",
            "YueLengM/gtnh-bq-ext", ""),
        Github("qz-uilib", "Qz-UILib · 界面库", "界面处理相关的基础库。", "性能优化",
            "QuanhuZeYu/Qz-UILib", "它也是其他模组可能依赖的库；请核对所需版本，不重复添加已有库。"),
        CurseForge("fps-reducer", "FPS Reducer · 后台帧率调节", "按使用状态调节帧率，减少空闲时的资源消耗。", "性能优化",
            "fps-reducer", ""),
        Github("lag-goggles", "LagGoggles Legacy · 性能诊断", "辅助定位世界中的性能开销。", "性能优化",
            "FalsePattern/LagGogglesLegacy", "性能分析工具不等于自动修复卡顿；多人环境须核对服务端要求。"),
        Github("gtnh-extra-nerf", "GTNH Extra Nerf · 额外削弱", "提高整合包的挑战难度。", "难度提高",
            "kuba6000/GTNH-Extra-Nerf", "按用户提供的 Wiki 分类列入难度提高；具体版本的星门规则资格仍需另行核对。"),
        Github("mineshot", "Mineshot · 截图辅助", "提供游戏画面截图相关功能。", "视听增强",
            "GTNotLeisure/mineshot", "原 ABKQPO 仓库已迁移，使用原项目迁移后的发行来源。"),
        Github("small-phone", "SmallPhone · 小手机", "提供游戏内小手机相关界面。", "视听增强",
            "RealSilverMoon/SmallPhone", ""),
        Github("fmusic", "FMusic · 音乐扩展", "提供游戏内音乐相关功能。", "视听增强",
            "czqwq/FMusic", ""),
        Github("myctmlib", "MyCTMLib · 纹理连接支持", "纹理连接效果相关基础库。", "视听增强",
            "GTNotLeisure/MyCTMLib", "原 ABKQPO 仓库已迁移。请按 GTNH 版本选择对应分支的发行文件，并核对资源包及其他渲染模组的前置要求与冲突说明。"),
        Github("dark-text-fix", "DarkTextFix · 深色文字修复", "改善深色文字的显示问题。", "视听增强",
            "eudesjuniorr/DarkTextFix", "Modernity 相关适配可另查 AdminXiaoMai/DarkTextFix；不要同时安装同名修复模组，请按资源包说明选择。"),
        CurseForge("world-tooltips", "World Tooltips · 世界提示", "扩展世界中的提示信息。", "视听增强",
            "world-tooltips", ""),
        CurseForge("better-foliage", "Better Foliage · 植被效果", "改善树叶、草地等植被的视觉表现。", "视听增强",
            "better-foliage", ""),
        External("better-rain", "Better Rain · 雨景增强", "改善降雨的视觉表现。", "视听增强",
            "https://www.minecraftforum.net/forums/mapping-and-modding-java-edition/minecraft-mods/2134067-better-rain-v0-15-bug-fixes", "旧论坛发行没有已核实的固定文件与官方摘要，请在作者页面确认 1.7.10 文件后手动处理。"),
        Github("better-tooltip-box", "BetterTooltipBox · 提示框美化", "改善物品提示框的显示效果。", "视听增强",
            "xiaoxing2005/BetterTooltipBox", ""),
        CurseForge("chat-bubbles", "Chat Bubbles · 聊天气泡", "在游戏中显示聊天气泡。", "视听增强",
            "chat-bubbles", ""),
        Github("dynamic-surroundings", "Dynamic Surroundings · 动态环境", "扩展环境音效与视觉表现。", "视听增强",
            "mist475/DynamicSurroundings", ""),
        CurseForge("extra-player-render", "Extra Player Render · 玩家渲染", "扩展玩家模型的渲染表现。", "视听增强",
            "extra-player-render", ""),
        CurseForge("fancy-block-particles", "Fancy Block Particles · 方块粒子", "改善方块粒子的视觉效果。", "视听增强",
            "fancy-block-particles", ""),
        CurseForge("itemphysic-lite", "ItemPhysic Lite · 物品物理效果", "改善掉落物的视觉表现。", "视听增强",
            "itemphysic-lite", ""),
        CurseForge("matmos", "MAtmos · 环境音效", "扩展世界的环境声音。", "视听增强",
            "matmos", ""),
        Github("distant-horizons", "Distant Horizons · 远景渲染", "提供远距离地形显示。", "视听增强",
            "GTNewHorizons/DistantHorizons", "原 DarkShadow44/DistantHorizonsStandalone 已迁至此仓库，已通过 GitHub 官方 API 核对。远景渲染有额外性能与兼容要求，请核对 GTNH 分支和渲染模组兼容性。"),
        Github("better-line-break", "BetterLineBreak · 换行优化", "改善游戏中文字的换行表现。", "视听增强",
            "KatatsumuriPan/BetterLineBreak", "仓库同时发布其他 Minecraft 版本；本入口只列出文件名明确标注 1.7.10 的 JAR，不采用 1.12.2 文件。")
            with { AssetNamePattern = @"^BetterLineBreak[-_.]1\.7\.10(?:[-_.].*)?\.jar$" },
        Github("crosshair-selection", "Crosshair Selection · 准星选择", "提供准星外观选择。", "视听增强",
            "kanami2024520-jpg/Crosshair-Selection", ""),
        Github("twist-space-technology", "Twist Space Technology · 扭曲空间科技", "扩展机器与科技流程。", "流程修改",
            "Nxer/Twist-Space-Technology-Mod", ""),
        Github("programmable-hatches", "Programmable Hatches · 可编程仓室", "扩展 GregTech 与 AE2 的配方输入和自动化设备。", "流程修改",
            "reobf/Programmable-Hatches-Mod", "作者按 GTNH 版本维护不同适配；Beta、RC 与正式版可能不能互换，请核对原作者版本表。"),
        Github("gt-not-leisure", "GT Not Leisure · 格雷不休闲", "增加整合包科技内容与流程扩展。", "流程修改",
            "GTNotLeisure/GT-Not-Leisure", "原 ABKQPO 仓库已迁移，使用原项目迁移后的发行来源。"),
        Github("box-plus-plus", "BoxPlusPlus · 存储扩展", "扩展箱体与存储相关内容。", "流程修改",
            "RealSilverMoon/BoxPlusPlus", ""),
        Github("123-technology", "123Technology", "提供额外科技内容与流程修改。", "流程修改",
            "CallmeSHaobe/123Technology", ""),
        Github("mess-tech", "MessTech", "提供机器与科技相关扩展。", "流程修改",
            "czqwq/MessTech", ""),
        Github("gtnh-no-nerf", "GTNH No Nerf · 削弱调整", "调整整合包中的削弱与平衡限制。", "流程修改",
            "ElytraServers/gtnh-no-nerf", ""),
        Github("nh-utilities", "NH Utilities · 实用扩展", "提供整合包实用内容与流程辅助。", "流程修改",
            "Keriils/NH-Utilities", ""),
        Github("tako-tech", "TakoTech", "扩展整合包的科技流程。", "流程修改",
            "XSana/TakoTech", ""),
        Github("think-tech", "ThinkTech", "提供科技内容与流程扩展。", "流程修改",
            "Ol925/ThinkTech", ""),
        Github("cheaper-void-miners", "Cheaper Void Miners · 虚空采矿调整", "调整虚空采矿相关成本。", "流程修改",
            "Tungsten-W-74/CheaperVoidMiners", "原 Jonodonozym 仓库已迁移，使用原项目迁移后的发行来源。"),
        Github("gt-not-hard", "GT Not Hard · 格雷不难", "调整整合包流程与难度。", "流程修改",
            "z1564058782/GT-Not-Hard", ""),
        Github("void-miner-tweak", "Void Miner Tweak · 虚空采矿调整", "修改虚空采矿相关机制。", "流程修改",
            "reobf/Void-Miner-Tweak-Mod", ""),
        Github("gt-simple-wireless", "GT Simple Wireless Network · 简易无线网络", "扩展无线网络相关内容。", "流程修改",
            "MIAOKATZE/GT-Simple-Wireless-Network", ""),
        Github("gt-interesting-thing", "GT Interesting Thing · 趣味扩展", "提供额外内容与流程修改。", "流程修改",
            "MIAOKATZE/GT-Interesting-Thing", ""),
        Github("gt-steam-reborn", "GT Steam Reborn · 蒸汽扩展", "扩展蒸汽阶段相关内容。", "流程修改",
            "MIAOKATZE/GT-Steam-Reborn", ""),
        Github("gt-miao-blueprint", "GT Miao Blueprint · 蓝图扩展", "提供蓝图与建造流程辅助。", "流程修改",
            "MIAOKATZE/GT-Miao-Blueprint", ""),
        Github("fission-evolved", "Fission Evolved · 裂变扩展", "扩展裂变相关内容。", "流程修改",
            "shenFNX/Fission-Evolved", ""),
        Github("mcphone-gtnh", "MCPhone GTNH · 游戏内手机", "提供游戏内手机与实用功能。", "流程修改",
            "skyc10/mcphone-gtnh", ""),
        Github("ok-backpack", "OKBackpack · 背包扩展", "扩展背包与物品携带能力。", "流程修改",
            "Shigure-Ruiseki/OKBackpack", ""),
        Github("gt-production-line", "GregTech 产线简化", "简化部分 GregTech 生产流程。", "流程修改",
            "QionSi/GregTech-Production-line-simplification", ""),
        Github("tenkei-tech", "TenkeiTech", "扩展科技内容与生产流程。", "流程修改",
            "gitudH5R8/TenkeiTech", ""),
        Github("gt-never-reset", "GT Never Reset", "提供额外科技内容与流程修改。", "流程修改",
            "ikkkkkkkuuu/GT-Never-Reset", "请检查当前发行要求的基础库，完整安装作者列出的前置依赖。"),
        Github("gtrh", "GTRH", "提供整合包额外内容与流程修改。", "流程修改",
            "lonei-G/GTRH", ""),
        Github("libertas", "Libertas", "提供整合包流程与平衡扩展。", "流程修改",
            "nimeng1299/libertas", ""),
        Github("ez-miner", "EZMiner · 挖矿辅助", "减少采矿中的重复操作。", "流程优化",
            "czqwq/EZMiner", ""),
        Github("bandit-legacy", "Bandit Legacy", "提供整合包流程便利性调整。", "流程优化",
            "ElytraServers/Bandit-Legacy", ""),
        Github("qz-miner", "Qz-Miner · 挖矿辅助", "提供采矿操作辅助。", "流程优化",
            "QuanhuZeYu/Qz-Miner", ""),
        Github("ez-nuclear", "EZNuclear · 核能辅助", "提供核能流程相关辅助。", "流程优化",
            "czqwq/EZNuclear", ""),
        Github("torcherino-gtnh", "Torcherino GTNH · 时间加速", "提供时间加速相关功能。", "流程优化",
            "czqwq/Torcherino-GTNH", "加速机制可能加重服务器负载，使用前确认服务器规则与作者的限制说明。"),
        Github("time-crystal", "Time Crystal · 时间水晶", "提供时间与流程相关扩展。", "流程优化",
            "kuolemax/time-crystal", ""),
        Github("nh-kantan", "NH Kantan · 流程简化", "减少整合包流程中的重复工作。", "流程优化",
            "YueLengM/nh-kantan", ""),
        Github("enigmatic-legacy", "Enigmatic Legacy 1.7.10 · 神秘遗物", "提供遗物及相关玩法扩展。", "流程优化",
            "nimeng1299/Enigmatic-Legacy-1.7.10", ""),
        Github("ae2-things", "AE2 Things · AE2 扩展", "扩展 AE2 的实用功能。", "AE2增强",
            "asdflj/AE2Things", ""),
        Github("ae2-pattern-gen", "AE2 Pattern Gen · 样板生成", "辅助生成 AE2 样板。", "AE2增强",
            "Ch4oooooooLL/AE2PatternGen", ""),
        Github("wildcard-pattern", "Wildcard Pattern · 通配样板", "扩展 AE2 通配样板能力。", "AE2增强",
            "clfpwp/WildcardPatternforGTNH-1.7.10", ""),
        Github("ae2-infinity-cell", "AE2 Infinity Cell · 无限存储元件", "扩展 AE2 存储元件。", "AE2增强",
            "DancingSnow0517/AE2InfinityCell", ""),
        Github("ae-eu-network", "Applied Energistics EU Network · EU 网络", "扩展 AE2 与 EU 网络的交互。", "AE2增强",
            "DancingSnow0517/Applied-Energistics-EU-Network", ""),
        Github("advanced-memory-card", "Advanced Memory Card · 高级内存卡", "扩展 AE2 配置与内存卡相关操作。", "AE2增强",
            "suntide-20210418/AdvancedMemoryCard", ""),
        Github("wtcts", "WTCTS 1.7.10", "提供 AE2 相关实用扩展。", "AE2增强",
            "233max258/Wtcts-1.7.10", ""),
        Github("gtnh-eco", "GTNH ECO 1.7.10", "提供 AE2 与自动化流程扩展。", "AE2增强",
            "Yang120231/GTNH-ECO-1.7.10", ""),
        Github("ae2-convenience", "AE2 Convenience Features · 便利功能", "增加 AE2 操作便利性。", "AE2增强",
            "zeroCNM/AE2-Convenience-Features", ""),
        Github("eye-of-harmony-buffer", "Eye of Harmony Buffer · 和谐之眼扩展", "大幅调整和谐之眼相关流程。", "流程毁灭",
            "SafariXiu/EyeOfHarmonyBuffer", ""),
        Github("gtnh-nyx", "GTNH Nyx", "大幅调整整合包流程与平衡。", "流程毁灭",
            "rhnu/GTNH-Nyx", ""),
        Github("gtnh-cut-corners", "GTNH Cut Corners · 流程捷径", "大幅减少原整合包的部分流程。", "流程毁灭",
            "ElytraServers/GTNH-CutCorners", ""),
        Github("gtnh-modify", "GTNHModify · 修改版", "大幅修改原整合包的机制与流程。", "流程毁灭",
            "QuanhuZeYu/GTNHModify-forkBy_wohaopa", ""),

        Resource("dylan-gtnh-resourcepack", "GTNH ResourcePack · 实用纹理", "为 GTNH 提供实用纹理调整。", "无障碍与实用性",
            "DylanTaylor1/GTNH-ResourcePack"),
        CurseForgeContent("colored-gregtech-tools", "彩色 GregTech 螺丝刀与锉刀", "通过不同配色辅助区分工具。", "无障碍与实用性",
            "colored-gregtech-screwdriver-and-file", 412833, GtnhAddonKind.ResourcePack, 3078376),
        Resource("usernm-machines", "Usernm 机器纹理", "提供独立机器纹理。", "无障碍与实用性",
            "imTheSupremeOne/GTNH-Textures-Usernm", "machines"),
        CurseForgeContent("gtnh-16x-machine-textures", "GTNH 16x 机器纹理", "提供 16x 风格的机器纹理。", "无障碍与实用性",
            "gtnh-16x-machine-textures", 977463, GtnhAddonKind.ResourcePack),
        Resource("shadow-ui-invisible-healing-axe", "Shadow UI · 隐藏治愈之斧", "独立的治愈之斧显示调整。", "无障碍与实用性",
            "Ranzuu/Shadow-UI", "Just-InvisHealingAxe") with { AssetNamePattern = @"^Just-InvisHealingAxe\.zip$" },
        Resource("outlined-ores-modern", "Outlined Ores Modern · 矿石描边", "使用现代风格描边辅助辨认矿石。", "无障碍与实用性",
            "Ranzuu/GTNH-OutlinedOres", note: "原仓库已合并到 GTNH-OutlinedOres；本入口只列出适用于 Modernity 的纹理变体。")
            with { AssetNamePattern = @"^GTNH-OutlinedOres\.[0-9][A-Za-z0-9._-]*\.Modernity\.version\.zip$" },
        Resource("gtnh-outlined-ores", "GTNH Outlined Ores · 矿石描边", "为 GTNH 矿石增加描边。", "无障碍与实用性",
            "Ranzuu/GTNH-OutlinedOres") with { AssetNamePattern = @"^GTNH-OutlinedOres\.[0-9][0-9.]*\.zip$" },

        Resource("modernity-dark-ui", "Modernity GTNH · 深色界面", "为 Modernity GTNH 提供深色界面。", "深色模式",
            "ModernityGTNH/Modernity-GTNH-Dark-UI", note:
            "建议搭配 Modernity 主资源包，并置于主包上方；浅色、深色界面按需选择。文字不清楚时按作者说明安装 DarkTextFix。")
            with { AssetNamePattern = @"^(?:Modernity-GTNH-Dark-UI(?:-[A-Za-z0-9._-]+)?|default)\.zip$" },
        Resource("shadow-ui", "Shadow UI · 深色界面", "提供深色界面外观。", "深色模式", "Ranzuu/Shadow-UI")
            with { AssetNamePrefix = "Shadow.UI." },
        Resource("ae2-dark-mode", "AE2 Dark Mode · 深色界面", "为 AE2 提供深色界面。", "深色模式", "Ranzuu/AE2-Dark-Mode"),
        Resource("dark-gtnh-reimagined", "Dark GTNH Reimagined", "重新设计 GTNH 的深色界面。", "深色模式",
            "Kuliiuu/Dark-GTNH-Reimagined"),
        Resource("scuffed-gtnh-dark", "Scuffed GTNH Dark Continued", "延续维护的 GTNH 深色界面。", "深色模式",
            "Kuliiuu/Scuffed-GTNH-Dark-Continued"),
        Resource("transparent-ui", "Glass UI · 透明界面", "原 Transparent UI，提供透明界面外观。", "深色模式", "S4mpsa/Glass-UI"),

        Resource("modernity-gtnh", "Modernity GTNH · 主资源包", "以现代风格重绘 GTNH 的方块、物品及纹理。", "升级与重制",
            "ModernityGTNH/Modernity-GTNH", note:
            "本入口只安装主资源包，请置于资源包加载顺序底部；界面包另行选择。连接纹理及部分模型需要适配当前 GTNH 版本的 MyCTMLib，可在推荐模组中选择。周构建也需核对 GTNH 版本。")
            with { AssetNamePattern = @"^Modernity-(?:GTNH-)?v?[0-9][A-Za-z0-9._-]*\.zip$" },
        Resource("modernity-ui", "Modernity GTNH · 浅色界面", "为 GTNH 提供现代 AE2 风格的浅色界面。", "升级与重制",
            "ModernityGTNH/Modernity-GTNH-UI", note:
            "建议搭配 Modernity 主资源包，并置于主包上方；与深色界面按需选择，安装后在游戏内启用。")
            with { AssetNamePattern = @"^(?:Modernity-GTNH-UI(?:-[A-Za-z0-9._-]+)?|default)\.zip$" },
        CurseForgeContent("zedtech-gtnh", "ZedTech GTNH", "提供 GTNH 纹理重制。", "升级与重制",
            "zedtech-gtnh", 1333595, GtnhAddonKind.ResourcePack),
        Resource("gtnh-faithful-textures", "GTNH Faithful Textures", "提供 Faithful 风格的 GTNH 纹理。", "升级与重制",
            "Ethryan/GTNH-Faithful-Textures"),
        Resource("gtnh-fti-faithful", "GTNH FTI Faithful", "提供 Faithful 风格的模组纹理。", "升级与重制",
            "Eldrinn-Elantey/GTNH-FTI-Faithful-ResourcePack"),
        Resource("usernm-textures", "Usernm GTNH Textures", "提供 Usernm 风格的 GTNH 纹理。", "升级与重制",
            "Usernm0/GTNH-Textures-Usernm"),
        Resource("gtnh-refreshed", "GTNH Refreshed", "更新 GTNH 的纹理外观。", "升级与重制", "BlueHero233/GTNHRefreshed"),
        Resource("jstr-alk", "JSTR Alk Modded 1.7.10", "提供 1.7.10 模组纹理。", "升级与重制",
            "alkcorp/JSTR-Alk-Modded-1.7.10"),
        Resource("minimalistic-gtnh", "Minimalistic GTNH Repair", "提供简约风格的 GTNH 纹理。", "升级与重制",
            "Fogy-F/Minimalistic-GTNH-repair"),
        External("stellar-fusion", "Stellar Fusion · GregTech 32x", "查看旧版 Stellar Fusion 纹理的作者介绍。", "升级与重制",
            "https://web.archive.org/web/20230430014009/https://www.gtnewhorizons.com/forum/m/36844562/viewthread/32547244-stellar-fusion-gregtech-32x32-v034",
            "原作者页面仅有历史存档，未核实可用的固定发行文件，请手动核对来源和适用版本。") with { Kind = GtnhAddonKind.ResourcePack },
        Resource("uu-tex", "UU-Tex", "提供 GTNH 纹理扩展。", "升级与重制", "Jimbno/UU-Tex"),
        Resource("wily-bits", "Wily-bit's GTNH Textures", "提供 Wily-bit 的 GTNH 纹理。", "升级与重制",
            "Wilyb/Wily-bit-s-GTNH-tex"),
        External("discord-resourcepack", "Discord 社区资源包", "打开用户提供的资源包讨论消息。", "升级与重制",
            "https://discord.com/channels/181078474394566657/224191655375273985/889345648204316673",
            "此项目按要求仅提供原始 Discord 消息入口，请在原帖确认资源包、版本和下载说明。") with { Kind = GtnhAddonKind.ResourcePack },
        External("ivelitex", "Ivelitex", "查看 Ivelitex 的 GitLab 发行版本。", "升级与重制",
            "https://gitlab.com/ivelieu/ivelitex/-/releases",
            "当前自动安装不支持 GitLab 发行资产，请在原作者发行页手动选择资源包。") with { Kind = GtnhAddonKind.ResourcePack },

        Github("angelica", "Angelica · 渲染与光影支持", "为 GTNH 提供渲染与光影支持。", "光影加载器",
            "GTNewHorizons/Angelica", "Angelica 与 OptiFine 不兼容；已有 Angelica 的实例不会自动替换。不同 GTNH 内置版本支持的光影可能不同，请核对该发行版本说明。")
            with { Kind = GtnhAddonKind.ShaderLoader },
        External("optifine", "OptiFine", "打开 OptiFine 官方下载页面。", "光影加载器", "https://optifine.net/downloads",
            "仅提供官方下载入口。OptiFine 与 Angelica 不兼容，不应向已使用 Angelica 的 GTNH 实例直接添加。")
            with { Kind = GtnhAddonKind.ShaderLoader },
        Modrinth("complementary-unbound", "Complementary Unbound", "提供光照、阴影与环境视觉效果。", "光影包",
            "complementary-unbound", GtnhAddonKind.Shader),
        Modrinth("bsl-shaders", "BSL Shaders", "提供光照与环境视觉效果。", "光影包", "bsl-shaders", GtnhAddonKind.Shader),
        CurseForgeContent("sildurs-vibrant-shaders", "Sildur's Vibrant Shaders", "提供光照、阴影与环境效果。", "光影包",
            "sildurs-vibrant-shaders", 544096, GtnhAddonKind.Shader),
        Modrinth("euphoria-patches", "Euphoria Patches · 光影补丁", "为 Complementary 光影提供额外补丁。", "光影补丁",
            "euphoria-patches", GtnhAddonKind.ShaderLoader)
    ];

    /// <summary>保留用户指定的作者仓库，不按模组名称搜索并替换成其他同名项目。</summary>
    private static GtnhAddon Github(string id, string name, string summary, string category, string repository, string note) =>
        new(id, name, summary, category, repository, Notes(category, note))
        {
            IsNonStargate = NonStargate(category)
        };

    /// <summary>资源包保留独立种类；专用标签固定查询，避免把同仓库其他内容当作同一项目。</summary>
    private static GtnhAddon Resource(string id, string name, string summary, string category, string repository, string? tag = null, string? note = null) =>
        Github(id, name, summary, category, repository,
            (note is null ? "" : note + "\n") + "安装后请在游戏资源包设置中启用，并按作者说明调整叠放顺序；不会自动启用或替换已有资源包。")
            with { Kind = GtnhAddonKind.ResourcePack, ReleaseTag = tag };

    /// <summary>Modrinth 使用目录中固定的项目标识，不按名字模糊搜索其他来源。</summary>
    private static GtnhAddon Modrinth(string id, string name, string summary, string category, string project, GtnhAddonKind kind) =>
        new(id, name, summary, category, "", Notes(category,
            "光影与补丁必须同时适配实例已有的加载器和版本；目录收录不表示最新版可用于 GTNH。安装后需要在游戏中手动启用。"))
        {
            Provider = GtnhAddonProvider.Modrinth, Kind = kind, ModrinthProjectId = project,
            WebsiteUrl = "https://modrinth.com/" + (kind == GtnhAddonKind.Shader ? "shader/" : "mod/") + project
        };

    /// <summary>资源与光影使用已核实的平台项目 ID；原文指定单个文件时不扩展为整个项目。</summary>
    private static GtnhAddon CurseForgeContent(string id, string name, string summary, string category, string slug,
        long projectId, GtnhAddonKind kind, long? fileId = null) =>
        new(id, name, summary, category, "", Notes(category,
            "请核对作者的适用版本、资源叠放顺序或光影加载器要求；安装后在游戏中手动启用。"))
        {
            Provider = GtnhAddonProvider.CurseForge, Kind = kind, CurseForgeProjectId = projectId, CurseForgeFileId = fileId,
            WebsiteUrl = "https://www.curseforge.com/minecraft/" +
                (kind == GtnhAddonKind.Shader ? "shaders/" : "texture-packs/") + slug +
                (fileId is null ? "" : "/download/" + fileId)
        };

    /// <summary>使用固定平台身份读取文件列表，网页链接只供用户查看作者说明。</summary>
    private static GtnhAddon CurseForge(string id, string name, string summary, string category, string slug, string note)
    {
        var projectId = slug switch
        {
            "fps-reducer" => 280294, "world-tooltips" => 241545, "better-foliage" => 228529,
            "chat-bubbles" => 235520, "extra-player-render" => 481042, "fancy-block-particles" => 254441,
            "itemphysic-lite" => 270441, "matmos" => 366256,
            _ => throw new ArgumentException("未登记的 CurseForge 固定项目。", nameof(slug))
        };
        return new(id, name, summary, category, "", Notes(category, note))
        {
            Provider = GtnhAddonProvider.CurseForge, CurseForgeProjectId = projectId,
            WebsiteUrl = "https://www.curseforge.com/minecraft/mc-mods/" + slug,
            IsNonStargate = NonStargate(category)
        };
    }

    /// <summary>论坛入口仅供查看作者说明，不推测旧附件或第三方镜像的下载地址。</summary>
    private static GtnhAddon External(string id, string name, string summary, string category, string website, string reason) =>
        new(id, name, summary, category, "", Notes(category, reason))
        {
            Provider = GtnhAddonProvider.External, WebsiteUrl = website,
            IsNonStargate = NonStargate(category), ManualReason = reason
        };

    /// <summary>只按用户提供的后四类标记非星门规则，不反推其他项目已获得规则认可。</summary>
    private static bool NonStargate(string category) => category is "流程修改" or "流程优化" or "AE2增强" or "流程毁灭";

    /// <summary>将兼容性提示与分类风险放在具体项目说明中，便于安装前核对。</summary>
    private static string Notes(string category, string note) =>
        (note.Length == 0 ? "" : note + "\n") + CompatibilityNote + (NonStargate(category) ? "\n" + NonStargateNote : "");
}
