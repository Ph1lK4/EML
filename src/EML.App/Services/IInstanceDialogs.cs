using EML.Core.Minecraft;

namespace EML.App.Services;

/// <summary>界面交互独立于磁盘操作，便于测试和替换窗口。</summary>
public interface IInstanceDialogs
{
    /// <summary>显示实例名称和路径，让用户确认删除范围。</summary>
    bool ConfirmDelete(MinecraftInstance instance);
    /// <summary>选择新的 ZIP 文件位置；取消时返回 null。</summary>
    string? ChooseExportPath(MinecraftInstance instance);
}
