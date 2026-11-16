using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Mods;

/// <summary>按当前实例版本和加载器限制扫描，不遍历缓存、其他版本模组目录或整个磁盘。</summary>
internal static class LocalModPaths
{
    /// <summary>返回实例相对的游戏目录前缀，根目录游戏不额外加入点号。</summary>
    internal static string Prefix(InstanceImportManifest manifest) => manifest.GameDirectory == "." ? "" : manifest.GameDirectory + "/";

    /// <summary>只有已识别的模组加载器开放写操作，未知实例仍允许查看 mods 根目录。</summary>
    internal static bool CanWrite(InstanceImportManifest manifest) => manifest.Components.Any(component =>
        component.Id.ToLowerInvariant() is "forge" or "net.minecraftforge" or "neoforge" or "net.neoforged" or
            "fabric" or "fabric-loader" or "net.fabricmc.fabric-loader" or "quilt" or "quilt-loader" or "org.quiltmc.quilt-loader");

    /// <summary>仅旧 Forge 使用当前游戏版本子目录，其他加载器只扫描 mods 根层。</summary>
    internal static IReadOnlyList<string> Directories(InstanceImportManifest manifest)
    {
        var result = new List<string> { Prefix(manifest) + "mods" };
        var forge = manifest.Components.Any(component => component.Id.Equals("forge", StringComparison.OrdinalIgnoreCase) ||
            component.Id.Equals("net.minecraftforge", StringComparison.OrdinalIgnoreCase));
        if (forge && Version.TryParse(manifest.MinecraftVersion, out var version) && version.Major == 1 && version.Minor < 13)
            result.Add(Prefix(manifest) + "mods/" + manifest.MinecraftVersion);
        return result;
    }

    /// <summary>文件必须属于本版本实际扫描目录，并通过统一 JAR/ZIP 与禁用后缀规则。</summary>
    internal static bool Allows(InstanceImportManifest manifest, string path)
    {
        if (!LocalModPathPolicy.Allows(path, manifest.GameDirectory, manifest.MinecraftVersion)) return false;
        var slash = path.LastIndexOf('/');
        return slash > 0 && Directories(manifest).Contains(path[..slash], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>新增文件固定放入 mods 根层，文件名先按导入规则验证。</summary>
    internal static string NewPath(InstanceImportManifest manifest, string fileName)
    {
        var safe = InstanceImportPaths.Relative(fileName);
        if (safe.Contains('/')) throw new InvalidDataException("新增模组名称不能包含目录。");
        var path = Prefix(manifest) + "mods/" + safe;
        if (!Allows(manifest, path)) throw new InvalidDataException("仅支持本地 JAR/ZIP 模组，或带 .disabled 后缀的模组文件。");
        return path;
    }
}
