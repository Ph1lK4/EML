namespace EML.Core.Minecraft;

/// <summary>约定扫描能力，让界面不必依赖具体目录解析实现。</summary>
public interface IMinecraftInstanceLocator
{
    /// <summary>异步返回实例列表，无实例时返回空集合；通过 token 接收取消请求。</summary>
    Task<IReadOnlyList<MinecraftInstance>> FindAllAsync(
        CancellationToken cancellationToken = default);
}
