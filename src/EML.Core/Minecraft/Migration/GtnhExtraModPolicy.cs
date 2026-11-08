namespace EML.Core.Minecraft.Migration;

/// <summary>额外模组是用户明确选择的严格内容，不能获得玩家数据的可变或可删除许可。</summary>
public static class GtnhExtraModPolicy
{
    public const string GroupPrefix = "mod:";
    public const string Rule = "gtnh-extra-mod-v1";
    public const string Warning = "迁移的模组可能与新版 GTNH、加载器或其他模组不兼容，导致崩溃、无法加入服务器或存档异常。" +
        "仅勾选自己额外添加的模组；不会自动升级模组、补齐其依赖或迁移配置。请保留旧实例并先测试副本。";

    /// <summary>只接收 mods 根目录或 1.7.10 子目录中的 JAR/ZIP，保留 .disabled 状态。</summary>
    public static bool Allows(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\')) return false;
        var parts = path.Split('/');
        if (parts.Length is not (2 or 3) || !parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase) ||
            parts.Length == 3 && parts[1] != "1.7.10" ||
            parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':'))) return false;
        var name = parts[^1];
        if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) name = name[..^9];
        return name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>数据组始终对应一个由预览重新确认的模组路径，不接受整个 mods 目录。</summary>
    public static bool IsGroup(string group) => group.StartsWith(GroupPrefix, StringComparison.Ordinal) && Allows(group[GroupPrefix.Length..]);
}
