namespace EML.Core.Minecraft;

/// <summary>共享文件用途和允许变化规则；用途不会覆盖可信来源的完整性要求。</summary>
public static class InstanceFileChangePolicy
{
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

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
            IsMarkerPath(file.Path, gameDirectory) ? InstanceFileRole.ConsumableMarker :
            IsKnownUserData(file.Path, gameDirectory) ? InstanceFileRole.UserData :
            IsKnownConfiguration(file.Path, gameDirectory) ? InstanceFileRole.UserConfiguration : InstanceFileRole.Content;
        if (file.Role is not { } declared) return inferred;
        // 明确 Content 可收紧配置策略，但声明不能扩大缓存或可变元数据的路径范围。
        if (declared == InstanceFileRole.Content && inferred != InstanceFileRole.DerivedState) return declared;
        // 旧备份曾把一次性标记归为配置；兼容原声明，不重写原清单或来源指纹。
        if (declared == InstanceFileRole.UserConfiguration && inferred == InstanceFileRole.ConsumableMarker) return declared;
        // 旧备份的配置目录分类较宽；保留声明，但可变许可仍受后缀与来源约束。
        if (declared == InstanceFileRole.UserConfiguration && IsConfigurationLocation(file.Path, gameDirectory)) return declared;
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

    /// <summary>普通包内配置、日志和世界状态允许正常变化；明确来源及内容用途始终严格校验。</summary>
    public static bool IsMutableFile(InstanceFileRequirement file, string gameDirectory, string? sourceFormat = null) =>
        IsMutableConfiguration(file, gameDirectory, sourceFormat) ||
        file.Source == InstanceFileSource.Embedded && file.Origin is null &&
        GetRole(file, gameDirectory, sourceFormat) == InstanceFileRole.UserData && IsKnownUserData(file.Path, gameDirectory);

    /// <summary>仅允许有原包空内容依据的一次性更新标记被游戏消耗，不能放行其他配置缺失。</summary>
    public static bool AllowsMissing(InstanceFileRequirement file, string gameDirectory, string? sourceFormat = null) =>
        IsConsumableMarker(file, gameDirectory) && GetRole(file, gameDirectory, sourceFormat) != InstanceFileRole.Content;

    /// <summary>DefaultKeys 会删除这个空标记；必须同时匹配普通包内来源、原条目和唯一空内容摘要。</summary>
    private static bool IsConsumableMarker(InstanceFileRequirement file, string gameDirectory) =>
        file.Source == InstanceFileSource.Embedded && file.Origin is null && file.Size == 0 &&
        !string.IsNullOrWhiteSpace(file.ArchiveEntry) && file.Downloads.Count == 0 && file.Hashes.Count == 1 &&
        file.Hashes.TryGetValue("sha256", out var hash) && hash.Equals(EmptySha256, StringComparison.OrdinalIgnoreCase) &&
        IsMarkerPath(file.Path, gameDirectory);

    /// <summary>用途只描述已知路径；是否允许缺失仍由原包来源及空内容校验依据决定。</summary>
    private static bool IsMarkerPath(string? path, string gameDirectory) =>
        string.Equals(GameRelative(path, gameDirectory), "config/modpack-update", StringComparison.OrdinalIgnoreCase);

    /// <summary>Prism 的根配置保存名称和本机设置；其他目录或来源的同名文件不采用此规则。</summary>
    private static bool IsPrismMetadata(InstanceFileRequirement file, string? sourceFormat) =>
        string.Equals(sourceFormat, "Prism", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(file.Path, "instance.cfg", StringComparison.OrdinalIgnoreCase);

    /// <summary>仅采用已支持的配置范围；缺失或无法读取不会被可变策略放行。</summary>
    private static bool IsKnownConfiguration(string? path, string gameDirectory)
    {
        var relative = GameRelative(path, gameDirectory);
        // 配置目录也能包含 datapack、原生库或脚本，这些内容不能因所在目录获得可变许可。
        if (relative is null || IsProtectedContent(relative) || relative.Equals("config/modpack-update", StringComparison.OrdinalIgnoreCase)) return false;
        return IsConfigurationLocation(path, gameDirectory);
    }

    /// <summary>定位已支持的配置文件，ServerUtilities 仅开放三个由模组配置管理器保存的 CFG。</summary>
    private static bool IsConfigurationLocation(string? path, string gameDirectory)
    {
        var relative = GameRelative(path, gameDirectory);
        if (relative is null) return false;
        return relative.StartsWith("config/", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith("defaultconfigs/", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("options.txt", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("optionsof.txt", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("optionsshaders.txt", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("servers.dat", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("serverutilities/aurora.cfg", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("serverutilities/serverutilities.cfg", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("serverutilities/client/serverutilities.cfg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>只识别常见日志与世界状态格式，不把 saves 中的数据包或任意二进制文件视为用户数据。</summary>
    private static bool IsKnownUserData(string? path, string gameDirectory)
    {
        var relative = GameRelative(path, gameDirectory);
        if (relative is null) return false;
        if (relative.StartsWith("logs/", StringComparison.OrdinalIgnoreCase))
            return relative.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase);
        if (IsProtectedContent(relative)) return false;
        var parts = relative.Split('/');
        if (parts.Length < 3 || !parts[0].Equals("saves", StringComparison.OrdinalIgnoreCase) ||
            parts.Any(part => part.Equals("datapacks", StringComparison.OrdinalIgnoreCase))) return false;
        var file = parts[^1];
        if (parts.Length == 3 && (file.Equals("level.dat", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("level.dat_old", StringComparison.OrdinalIgnoreCase) || file.Equals("session.lock", StringComparison.OrdinalIgnoreCase))) return true;
        var directory = parts[^2];
        return (directory.Equals("region", StringComparison.OrdinalIgnoreCase) || directory.Equals("entities", StringComparison.OrdinalIgnoreCase) ||
                directory.Equals("poi", StringComparison.OrdinalIgnoreCase)) && file.EndsWith(".mca", StringComparison.OrdinalIgnoreCase) ||
            (directory.Equals("data", StringComparison.OrdinalIgnoreCase) || directory.Equals("playerdata", StringComparison.OrdinalIgnoreCase)) &&
                file.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
            (directory.Equals("stats", StringComparison.OrdinalIgnoreCase) || directory.Equals("advancements", StringComparison.OrdinalIgnoreCase)) &&
                file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>执行文件、归档和数据包内容保持严格，不因放在配置目录中改变用途许可。</summary>
    private static bool IsProtectedContent(string relative) =>
        relative.Split('/').Any(part => part.Equals("datapacks", StringComparison.OrdinalIgnoreCase)) ||
        Path.GetExtension(relative).ToLowerInvariant() is ".jar" or ".zip" or ".mrpack" or ".7z" or ".rar" or ".tar" or ".gz" or
            ".tgz" or ".bz2" or ".xz" or ".dll" or ".exe" or ".class" or ".so" or ".dylib" or ".bat" or ".cmd" or
            ".ps1" or ".sh" or ".py" or ".js" or ".lua" or ".zs";

    /// <summary>以游戏目录为边界返回相对路径，不让实例外的同名配置命中规则。</summary>
    private static string? GameRelative(string? path, string gameDirectory)
    {
        if (path is null) return null;
        var prefix = gameDirectory == "." ? "" : gameDirectory + "/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : null;
    }
}
