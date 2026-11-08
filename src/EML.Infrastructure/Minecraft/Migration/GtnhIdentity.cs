using System.Text.RegularExpressions;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Migration;

/// <summary>使用包内版本标识及 GTNH 配置特征确认范围，不凭文件夹名称猜测。</summary>
internal static partial class GtnhIdentity
{
    /// <summary>要求 Minecraft 1.7.10、明确 GTNH 版本文本和至少两个独立配置特征。</summary>
    internal static GtnhInstanceInfo? Read(string launcherRoot, string instancePath)
    {
        var root = EmbeddedFileBaseline.ValidateInstance(launcherRoot, instancePath);
        var manifest = InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName));
        if (manifest.MinecraftVersion != "1.7.10" && !manifest.Components.Any(c =>
            c.Id is "minecraft" or "net.minecraft" && c.Version == "1.7.10")) return null;
        var game = manifest.GameDirectory == "." ? root : InstanceImportPaths.Within(root, manifest.GameDirectory);
        var marker = MigrationIO.SafePath(game, "config/txloader/load/mainmenu/version.txt");
        if (!File.Exists(marker) || new FileInfo(marker).Length > 4096) return null;
        var match = VersionPattern().Match(File.ReadAllText(marker));
        if (!match.Success) return null;
        var features = new[] { "config/GregTech/GregTech.cfg", "config/GregTech/GT5U.cfg", "config/bartworks.cfg", "config/gtnhintergalactic.cfg" };
        if (features.Count(path => File.Exists(MigrationIO.SafePath(game, path))) < 2) return null;
        return new(root, Path.GetFileName(root), game, match.Groups[1].Value);
    }

    [GeneratedRegex(@"\bGTNH\s+(\d+\.\d+\.\d+(?:[.\-][A-Za-z0-9]+)*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
