using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.CurseForge;
using EML.Infrastructure.Minecraft.Modrinth;
using EML.Infrastructure.Minecraft.Prism;
using EML.Infrastructure.Minecraft.Standard;

namespace EML.Infrastructure.Minecraft.Runtime;

/// <summary>检查和启动共用格式选择，所有解析器只读取本地组件元数据。</summary>
internal sealed class InstanceRuntimeResolver : IPrismRuntimeResolver
{
    /// <summary>按导入清单选择对应解析器，不因缺少加载器信息退回原版。</summary>
    public PrismRuntimePlan Resolve(string instancePath, InstanceImportManifest manifest)
    {
        IPrismRuntimeResolver resolver = manifest.SourceFormat.ToUpperInvariant() switch
        {
            "PRISM" => new PrismRuntimeResolver(),
            "MODRINTH" => new ModrinthRuntimeResolver(),
            "CURSEFORGE" => new CurseForgeRuntimeResolver(),
            "ZIP" => new StandardMinecraftRuntimeResolver(),
            _ => throw new InvalidDataException("此实例格式尚未接入运行依赖：" + manifest.SourceFormat)
        };
        return resolver.Resolve(instancePath, manifest);
    }
}
