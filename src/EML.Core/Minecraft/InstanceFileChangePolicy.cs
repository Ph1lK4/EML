namespace EML.Core.Minecraft;

/// <summary>集中定义允许玩家修改的配置文件，运行依赖和模组仍须匹配原始内容。</summary>
public static class InstanceFileChangePolicy
{
    /// <summary>按照清单内的游戏目录识别包内配置；不放行任意目录中的同名文件。</summary>
    public static bool IsMutableConfiguration(InstanceFileRequirement file, string gameDirectory)
    {
        if (file.Source != InstanceFileSource.Embedded || file.Path is null) return false;
        var prefix = gameDirectory == "." ? "" : gameDirectory + "/";
        if (!file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = file.Path[prefix.Length..];
        return relative.StartsWith("config/", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith("defaultconfigs/", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("options.txt", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("optionsof.txt", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("optionsshaders.txt", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("servers.dat", StringComparison.OrdinalIgnoreCase);
    }
}
