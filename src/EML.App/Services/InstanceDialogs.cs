using System.IO;
using System.Windows;
using EML.Core.Minecraft;
using Microsoft.Win32;

namespace EML.App.Services;

/// <summary>通过 WPF 窗口完成导入预览、删除确认和文件选择。</summary>
/// <param name="owner">对话框所属的主窗口。</param>
public sealed class InstanceDialogs(Window owner) : IInstanceDialogs
{
    /// <summary>选择本地 ZIP 或 mrpack 压缩包，取消时返回 null。</summary>
    public string? ChooseImportPath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入实例包", Filter = "实例包 (*.zip;*.mrpack)|*.zip;*.mrpack",
            CheckFileExists = true, Multiselect = false
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    /// <summary>展示预览并返回用户确认的名称；关闭或取消时返回 null。</summary>
    public string? ConfirmImport(InstanceImportPreview preview)
    {
        var dialog = new InstanceImportDialog(preview) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.InstanceName : null;
    }

    /// <summary>显示删除范围并返回是否确认，默认选择取消。</summary>
    public bool ConfirmDelete(MinecraftInstance instance) => DialogPrompt.Confirm(owner,
        "删除这个实例？",
        "此操作包含实例内的存档、模组和配置，请先正常关闭游戏。\n\n" +
        "EML 将请求 Windows 回收站；若系统提示永久删除，请谨慎选择。",
        "删除实例", DialogTone.Danger, instance.Name, instance.InstancePath);

    /// <summary>选择未使用的 ZIP 保存路径；重名时重新选择，取消时返回 null。</summary>
    public string? ChooseExportPath(MinecraftInstance instance)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出实例完整备份（含存档和配置）", Filter = "ZIP 压缩包|*.zip",
            DefaultExt = ".zip", AddExtension = true, CheckPathExists = true,
            OverwritePrompt = false, FileName = Path.GetFileName(instance.InstancePath) + ".zip"
        };
        while (dialog.ShowDialog(owner) == true)
        {
            if (!File.Exists(dialog.FileName) && !Directory.Exists(dialog.FileName)) return dialog.FileName;
            DialogPrompt.Inform(owner, "无法覆盖备份", "目标已存在，请选择新的文件名。");
        }
        return null;
    }
}
