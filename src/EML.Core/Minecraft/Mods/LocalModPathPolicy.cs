namespace EML.Core.Minecraft.Mods;

/// <summary>模组操作只允许游戏目录内的加载位置，不能借管理记录访问其他内容或运行依赖。</summary>
public static class LocalModPathPolicy
{
    /// <summary>去掉唯一的禁用后缀，得到同一模组的启用路径。</summary>
    public static string EnabledPath(string path) => path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^9] : path;

    /// <summary>接受 mods 根目录及当前 Minecraft 版本子目录中的 JAR/ZIP。</summary>
    public static bool Allows(string path, string gameDirectory, string? minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\')) return false;
        var prefix = gameDirectory == "." ? "" : gameDirectory + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = path[prefix.Length..].Split('/');
        if (parts.Length is not (2 or 3) || !parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase) ||
            parts.Length == 3 && (string.IsNullOrWhiteSpace(minecraftVersion) || parts[1] != minecraftVersion) ||
            parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':'))) return false;
        var name = EnabledPath(parts[^1]);
        return name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }
}
