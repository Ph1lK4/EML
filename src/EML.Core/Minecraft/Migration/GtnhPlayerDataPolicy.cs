namespace EML.Core.Minecraft.Migration;

/// <summary>迁移与后续检查共用窄范围白名单，不把 mods、脚本或整片 config 变成可变文件。</summary>
public static class GtnhPlayerDataPolicy
{
    /// <summary>只认可本版规则允许的游戏内相对文件；世界内部作为整体保留。</summary>
    public static bool Allows(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/') ||
            path.Split('/').Any(part => part is "" or "." or ".." || part.Contains(':'))) return false;
        if (path.StartsWith("saves/", StringComparison.OrdinalIgnoreCase) && path.Split('/').Length >= 3) return true;
        if (new[] { "backups/", "journeymap/", "visualprospecting/", "TCNodeTracker/", "schematics/", "screenshots/", "shaderpacks/" }
            .Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return true;
        if (path.Equals("config/vendingmachine/favourites", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("config/vendingmachine/favourites/", StringComparison.OrdinalIgnoreCase)) return true;
        return new[] { "options.txt", "optionsnf.txt", "servers.dat", "localconfig.cfg", "BotaniaVars.dat", "config/shaders.properties" }
            .Contains(path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>确认迁入来源与路径同时匹配；普通原包文件不会因同名获得新权限。</summary>
    public static bool IsMigrated(InstanceFileRequirement file, string gameDirectory)
    {
        if (file.Origin != InstanceFileOrigin.MigratedPlayerData || file.Source != InstanceFileSource.Embedded || file.Path is null) return false;
        var prefix = gameDirectory == "." ? "" : gameDirectory + "/";
        return file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Allows(file.Path[prefix.Length..]);
    }
}
