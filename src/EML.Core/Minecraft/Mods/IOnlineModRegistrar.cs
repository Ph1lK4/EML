namespace EML.Core.Minecraft.Mods;

/// <summary>把已下载的模组交给现有事务管理器；保留官方完整性要求，不降级为本地导入来源。</summary>
public interface IOnlineModRegistrar
{
    /// <summary>重新校验暂存文件，拒绝原包和重复文件冲突，原子安装并登记实际平台来源。</summary>
    Task RegisterDownloadedAsync(string instancePath, string sourcePath, InstanceFileRequirement requirement,
        CancellationToken token = default);
}
