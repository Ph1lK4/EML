namespace EML.Infrastructure.Minecraft.Importing;

/// <summary>导入专用路径约束：压缩包和清单中的路径永远按不可信相对路径处理。</summary>
internal static class InstanceImportPaths
{
    /// <summary>将包内路径统一为斜杠分隔的相对路径，拒绝越界路径及 Windows 非法文件名。</summary>
    internal static string Relative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.StartsWith('/') || value.StartsWith('\\'))
            throw new InvalidDataException("无效的包内相对路径：" + value);
        var normalized = value.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (parts.Length > 64) throw new InvalidDataException("包内路径层级过深。");
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 255 || part is "." or ".." || part.EndsWith('.') ||
                part != part.Trim() || part.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
                throw new InvalidDataException("包内路径含非法文件名：" + value);
            var stem = part.Split('.')[0].TrimEnd().ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
                throw new InvalidDataException("包内路径包含 Windows 保留名称：" + value);
        }
        return normalized;
    }

    /// <summary>验证并返回单个实例文件夹名称，非法输入抛出参数异常。</summary>
    internal static string InstanceName(string value)
    {
        try
        {
            var name = Relative(value);
            if (name.Contains('/') || name.Length > 128) throw new InvalidDataException("实例名称须为不超过 128 字符的单个文件夹名。");
            return name;
        }
        catch (InvalidDataException ex) { throw new ArgumentException(ex.Message, nameof(value), ex); }
    }

    /// <summary>替换整合包名称中的非法字符，生成可编辑的实例建议名。</summary>
    internal static string SuggestName(string value)
    {
        var result = string.Concat(value.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (result.Length > 100) result = result[..100];
        try { return InstanceName(result); }
        catch (ArgumentException) { return "导入实例"; }
    }

    /// <summary>组合根目录和相对路径，确认结果位于根目录内，再返回绝对路径。</summary>
    internal static string Within(string root, string relative)
    {
        var safe = Relative(relative);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var result = Path.GetFullPath(Path.Combine(fullRoot, safe.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("路径超出目标目录。");
        return result;
    }

    /// <summary>检查目标及其父目录，拒绝符号链接和目录联接；尚不存在的路径允许通过。</summary>
    internal static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("导入路径含符号链接或目录联接：" + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>检查文件布局，拒绝忽略大小写后的重名，以及把文件当作父目录的冲突。</summary>
    internal static void ValidateLayout(IEnumerable<(string Path, bool Directory)> entries)
    {
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, directory) in entries)
        {
            var safe = Relative(path);
            if (!paths.TryAdd(safe, directory)) throw new InvalidDataException("压缩包有重复目标路径：" + safe);
        }
        foreach (var path in paths.Keys)
        {
            var parent = path;
            while (parent.LastIndexOf('/') is var index && index >= 0)
            {
                parent = parent[..index];
                if (paths.TryGetValue(parent, out var directory) && !directory)
                    throw new InvalidDataException("同一路径同时被用作文件和目录：" + parent);
            }
        }
    }
}
