namespace Launcher.Metadata;

/// <summary>
/// 把 Forge 的版本 JSON 和它 inheritsFrom 的父版本合并成一份。
/// 注：1.7.10 的 Forge 只给差异字段，必须合并后才能启动。
/// </summary>
public static class VersionMerger
{
    public static ResolvedVersion Merge(VersionJson child, VersionJson? parent)
    {
        var mergedLibraries = new List<LibraryJson>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in (child.Libraries ?? new()).Concat(parent?.Libraries ?? new()))
        {
            var key = GroupArtifact(library.Name);
            if (key is not null && !seen.Add(key)) continue; // 子版本优先，父版本同坐标丢弃
            mergedLibraries.Add(library);
        }

        return new ResolvedVersion
        {
            Id = child.Id ?? parent?.Id ?? "unknown",
            // 客户端 jar 永远用继承链最底下的那个版本（Forge 没有自己的 client jar）
            ClientJarVersionId = parent?.Id ?? child.Id ?? "unknown",
            MainClass = child.MainClass ?? parent?.MainClass,
            MinecraftArguments = child.MinecraftArguments ?? parent?.MinecraftArguments,
            AssetIndex = child.AssetIndex ?? parent?.AssetIndex,
            Downloads = child.Downloads ?? parent?.Downloads,
            Logging = child.Logging ?? parent?.Logging,
            JavaVersion = child.JavaVersion ?? parent?.JavaVersion,
            Libraries = mergedLibraries,
        };
    }

    /// <summary>group:artifact:version[:classifier][@ext] → group:artifact，用来去重。</summary>
    public static string? GroupArtifact(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var parts = name.Split(':');
        return parts.Length >= 2 ? parts[0] + ":" + parts[1] : name;
    }
}
