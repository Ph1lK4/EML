using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Modrinth;

namespace EML.Infrastructure.Minecraft.CurseForge;

/// <summary>为 CurseForge 固定组件生成官方运行计划；缓存独立保存，解析和检查阶段不联网。</summary>
public sealed class CurseForgeRuntimeResolver : IPrismRuntimeResolver
{
    private readonly ModrinthRuntimeResolver _officialResolver;

    /// <summary>复用原版、Fabric、Quilt 的官方元数据解析，并由来源规则选择唯一主加载器。</summary>
    public CurseForgeRuntimeResolver(string? runtimeOs = null, string? runtimeArchitecture = null,
        IReadOnlyDictionary<string, bool>? features = null, string? runtimeOsVersion = null)
    {
        _officialResolver = new ModrinthRuntimeResolver("CurseForge", runtimeOs, runtimeArchitecture, features, runtimeOsVersion);
    }

    /// <summary>读取已缓存的固定版本；缺少元数据时返回下载需求，Forge 安装处理器未接入时明确阻断。</summary>
    public PrismRuntimePlan Resolve(string instancePath, InstanceImportManifest manifest) =>
        _officialResolver.Resolve(instancePath, manifest);
}
