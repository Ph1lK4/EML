namespace EML.Infrastructure.Minecraft.Modrinth;

/// <summary>Modrinth 整合包安装策略，与 API 搜索参数和通用下载重试策略分别配置。</summary>
public sealed record ModrinthInstallOptions
{
    /// <summary>同时校验或下载的清单文件数量；默认四路，允许设置为 1 至 16。</summary>
    public int MaxConcurrentFiles { get; init; } = 4;

    /// <summary>拒绝无界或无效的并发设置，防止错误配置耗尽网络和文件句柄。</summary>
    internal void Validate()
    {
        if (MaxConcurrentFiles is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentFiles), "文件并发数必须为 1 至 16。");
    }
}
