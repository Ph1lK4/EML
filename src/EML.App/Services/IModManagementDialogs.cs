using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods;

namespace EML.App.Services;

/// <summary>本地模组管理的文件选择与确认；不在界面模型中直接操作窗口或磁盘。</summary>
public interface IModManagementDialogs
{
    /// <summary>选择要添加的本地模组文件，取消时返回空集合。</summary>
    IReadOnlyList<string> ChooseModFiles();
    /// <summary>说明加载器、版本和来源风险，由用户确认添加范围。</summary>
    bool ConfirmAdd(MinecraftInstance instance, IReadOnlyList<string> paths);
    /// <summary>确认将一个未登记的文件作为玩家额外模组纳入管理。</summary>
    bool ConfirmAdopt(MinecraftInstance instance, LocalModEntry mod);
    /// <summary>确认把选中模组移入当前实例的可恢复区域。</summary>
    bool ConfirmRemove(MinecraftInstance instance, LocalModEntry mod);
    /// <summary>打开当前实例的 mods 目录；目录不存在时显示提示。</summary>
    void OpenModsFolder(MinecraftInstance instance);
}
