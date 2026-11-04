namespace EML.Core.Minecraft;

/// <summary>共享文件用途和允许变化规则；用途不会覆盖可信来源的完整性要求。</summary>
public static class InstanceFileChangePolicy
{
    /// <summary>只识别实例根目录的 EML 私有状态，不把游戏目录中的同名路径当成缓存。</summary>
    public static bool IsDerivedStatePath(string? path)
    {
        var relative = path?.Replace('\\', '/');
        return relative is not null && (relative.Equals(".eml-runtime", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith(".eml-runtime/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>读取用途或为旧清单推导兼容用途；从不根据路径推测文件内容来源。</summary>
    public static InstanceFileRole GetRole(InstanceFileRequirement file, string gameDirectory, string? sourceFormat = null)
    {
        if (file.Role is { } role && !Enum.IsDefined(role)) throw new InvalidDataException("未知的实例文件用途。");
        var inferred = IsDerivedStatePath(file.Path) ? InstanceFileRole.DerivedState :
            IsPrismMetadata(file, sourceFormat) ? InstanceFileRole.LauncherMetadata :
            IsKnownConfiguration(file.Path, gameDirectory) ? InstanceFileRole.UserConfiguration : InstanceFileRole.Content;
        if (file.Role is not { } declared) return inferred;
        // 明确 Content 可收紧配置策略，但声明不能扩大缓存或可变元数据的路径范围。
        if (declared == InstanceFileRole.Content && inferred != InstanceFileRole.DerivedState) return declared;
        if (declared != inferred) throw new InvalidDataException("文件用途与允许的路径或实例格式不一致。");
        return declared;
    }

    /// <summary>仅放行可读取的普通包内设置和 Prism 元数据；明确覆盖、用户替换和平台文件始终严格。</summary>
    public static bool IsMutableConfiguration(InstanceFileRequirement file, string gameDirectory, string? sourceFormat = null)
    {
        if (file.Source != InstanceFileSource.Embedded || file.Path is null || file.Origin is not null) return false;
        return GetRole(file, gameDirectory, sourceFormat) switch
        {
            InstanceFileRole.LauncherMetadata => IsPrismMetadata(file, sourceFormat),
            InstanceFileRole.UserConfiguration => IsKnownConfiguration(file.Path, gameDirectory),
            _ => false
        };
    }

    /// <summary>Prism 的根配置保存名称和本机设置；其他目录或来源的同名文件不采用此规则。</summary>
    private static bool IsPrismMetadata(InstanceFileRequirement file, string? sourceFormat) =>
        string.Equals(sourceFormat, "Prism", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(file.Path, "instance.cfg", StringComparison.OrdinalIgnoreCase);

    /// <summary>仅采用已支持的配置范围；缺失或无法读取不会被可变策略放行。</summary>
    private static bool IsKnownConfiguration(string? path, string gameDirectory)
    {
        if (path is null) return false;
        var prefix = gameDirectory == "." ? "" : gameDirectory + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = path[prefix.Length..];
        return relative.StartsWith("config/", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith("defaultconfigs/", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("options.txt", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("optionsof.txt", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("optionsshaders.txt", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("servers.dat", StringComparison.OrdinalIgnoreCase);
    }
}
