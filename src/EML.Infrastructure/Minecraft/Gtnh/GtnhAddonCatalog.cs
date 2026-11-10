using EML.Core.Minecraft.Gtnh;

namespace EML.Infrastructure.Minecraft.Gtnh;

/// <summary>按 GTNH Wiki 与原作者说明维护的适配项目；发行文件仍需用户核对目标版本。</summary>
internal static class GtnhAddonCatalog
{
    internal static IReadOnlyList<GtnhAddon> Items { get; } =
    [
        new("worldedit", "WorldEdit · 建筑编辑", "复制、粘贴和编辑建筑，适合测试世界与建造。", "建筑工具",
            "GTNewHorizons/worldedit-gtnh",
            "这是 GTNH 的 Minecraft 1.7.10 适配分支。多人游戏需要服务端安装及相应权限；客户端安装不能获得服务器编辑权限。请核对具体发行版的 Java 与依赖要求。"),
        new("nech", "拼音搜索 · Never Enough Characters Rework", "在 NEI 中使用拼音查找物品，也提供搜索选项与脚本扩展。", "中文体验",
            "asdflj/NeverEnoughCharacters-Rework",
            "作者明确面向 GTNH 的重写版本。请核对实例内是否已有其他拼音搜索模组及所需 NEI 版本；不要重复安装相同功能的旧版。此功能不等同于整合包完整汉化。"),
        new("programmable-hatches", "可编程仓室 · Programmable Hatches", "扩展 GregTech 与 AE2 的配方输入和自动化设备。", "自动化扩展",
            "reobf/Programmable-Hatches-Mod",
            "非官方内容扩展，会新增方块和设备，多人游戏须与服务端一致。作者按 GTNH 版本发布不同分支：2.8.0–2.8.4 对应 0.1.3p57；2.9.0 的 Beta、RC 与正式版也分别适配。请先查看作者主页的版本表，再选择固定发行文件，不要直接使用最新版本。"),
        new("mouse-side-button", "鼠标侧键修复 · MouseSideButtonFix", "修复部分终端快捷键设为鼠标侧键时不能直接触发的问题。", "操作体验",
            "asdflj/MouseSideButtonFix",
            "作者说明针对 AE2FC、AE2WCT 等终端侧键问题。请先确认当前实例仍存在此问题，以及现有模组是否已包含修复；旧发行若没有官方 SHA-256，本页仅展示说明与作者链接。")
    ];
}
