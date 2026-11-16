using System.Diagnostics;
using System.IO;
using System.Windows;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods;
using Microsoft.Win32;

namespace EML.App.Services;

/// <summary>提供本地模组选择、变更确认和文件夹入口。</summary>
public sealed class ModManagementDialogs(Window owner) : IModManagementDialogs
{
    /// <summary>只选择本地 JAR、ZIP 或已禁用的同类文件，不执行模组代码。</summary>
    public IReadOnlyList<string> ChooseModFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加本地模组", Multiselect = true, CheckFileExists = true,
            Filter = "模组文件 (*.jar;*.zip;*.jar.disabled;*.zip.disabled)|*.jar;*.zip;*.jar.disabled;*.zip.disabled"
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileNames : [];
    }

    /// <summary>添加前说明兼容性由用户确认，批量添加逐个提交且保留已完成文件。</summary>
    public bool ConfirmAdd(MinecraftInstance instance, IReadOnlyList<string> paths) => DialogPrompt.Confirm(owner,
        $"添加 {paths.Count} 个本地模组？",
        "请确认文件来源可信，并适用于此实例的 Minecraft 版本与模组加载器。" +
        "额外模组可能缺少依赖或与整合包不兼容；列表中的识别信息不代表兼容性验证。" +
        "\n\n同名文件不会被覆盖。多个文件逐个添加，取消或失败时保留已完成的添加。",
        "添加模组", DialogTone.Warning, instance.Name,
        string.Join("\n", paths.Take(8).Select(Path.GetFileName)) + (paths.Count > 8 ? "\n……" : ""));

    /// <summary>只有明确属于玩家额外添加的模组才允许接管，不把来源不明自动当成用户文件。</summary>
    public bool ConfirmAdopt(MinecraftInstance instance, LocalModEntry mod) => DialogPrompt.Confirm(owner,
        "接管这个额外模组？",
        "请确认这是你自行添加、希望由 EML 管理的额外模组。接管后可以禁用、移入回收区或恢复。" +
        "\n\n这不会确认模组与整合包兼容，也不会更改原包文件的官方完整性要求。",
        "接管模组", DialogTone.Warning, instance.Name, $"{mod.FileName}\n{mod.Path}");

    /// <summary>告知依赖风险与恢复途径；移除仅作用于当前实例中的选中文件。</summary>
    public bool ConfirmRemove(MinecraftInstance instance, LocalModEntry mod) => DialogPrompt.Confirm(owner,
        "移入模组回收区？",
        "移除后可能影响其他模组或存档，请先正常退出游戏。文件将移入此实例的回收区，" +
        "可在“已移除”筛选中选择它并点击“恢复”。",
        "移入回收区", DialogTone.Danger, instance.Name, $"{mod.Name}\n{mod.FileName}");

    /// <summary>仅打开已存在的模组目录，不为浏览动作创建或修改实例文件。</summary>
    public void OpenModsFolder(MinecraftInstance instance)
    {
        var path = Path.Combine(instance.GameDirectory, "mods");
        if (!Directory.Exists(path))
        {
            DialogPrompt.Inform(owner, "模组文件夹", "此实例还没有 mods 文件夹。添加模组时会按需创建。");
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
