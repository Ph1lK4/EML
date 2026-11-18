using EML.Core.Minecraft;

namespace EML.App.Services;

/// <summary>将外部实例的目录选择和复制确认与导入服务分开，便于无窗口测试。</summary>
public interface IExternalInstanceDialogs
{
    /// <summary>发现常见位置并允许手选目录；关闭窗口返回 null，不执行任何复制。</summary>
    Task<ExternalMinecraftInstance?> ChooseInstanceAsync(IExternalInstanceDiscovery discovery,
        CancellationToken cancellationToken = default);

    /// <summary>显示源目录、复制量和目标位置，并要求用户确认游戏已关闭；取消返回 null。</summary>
    string? ConfirmCopy(FolderInstanceImportPreview preview);
}
