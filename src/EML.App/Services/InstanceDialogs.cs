using System.IO;
using System.Windows;
using EML.Core.Minecraft;
using Microsoft.Win32;

namespace EML.App.Services;

/// <summary>通过 WPF 窗口提供确认和导出路径选择。</summary>
public sealed class InstanceDialogs(Window owner) : IInstanceDialogs
{
    /// <summary>默认选择取消；说明删除包含存档，并由 Windows 处理回收站请求。</summary>
    public bool ConfirmDelete(MinecraftInstance instance) => MessageBox.Show(owner,
        $"删除实例：{instance.Name}\n{instance.InstancePath}\n\n包含存档、模组和配置，请先关闭游戏。\n将请求 Windows 回收站；若系统提示永久删除，请谨慎选择。",
        "确认删除实例", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
        MessageBoxResult.Cancel) == MessageBoxResult.OK;

    /// <summary>只接受未使用的文件名，避免覆盖用户已有备份。</summary>
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
            MessageBox.Show(owner, "目标已存在，请选择新的文件名。", "无法覆盖备份");
        }
        return null;
    }
}
