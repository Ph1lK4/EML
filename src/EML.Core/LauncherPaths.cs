using System.Text.Json;

namespace EML.Core;

/// <summary>区分程序目录与玩家数据根目录；发布布局不依赖启动时的工作目录。</summary>
public static class LauncherPaths
{
    public const string LayoutFileName = "eml-layout.json";
    private const int MaximumLayoutBytes = 1024;

    /// <summary>明确的 app 布局优先；否则兼容平铺发布和 Debug 向上查找解决方案。</summary>
    public static string ResolveDataRoot(string applicationDirectory, bool developmentMode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        if (!Path.IsPathFullyQualified(applicationDirectory))
            throw new ArgumentException("程序目录必须是绝对路径，不能依赖当前工作目录。", nameof(applicationDirectory));

        var application = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        var marker = Path.Combine(application, LayoutFileName);
        try
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(marker); }
            catch (FileNotFoundException) { return ResolveLegacyRoot(application, developmentMode); }
            catch (DirectoryNotFoundException) { return ResolveLegacyRoot(application, developmentMode); }

            // 标记存在但损坏时停止，不能悄悄改用 app/instances，造成数据“消失”的假象。
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw InvalidLayout(marker);
            if ((File.GetAttributes(application) & FileAttributes.ReparsePoint) != 0)
                throw InvalidLayout(marker);
            using var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumLayoutBytes) throw InvalidLayout(marker);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidLayout(marker);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!keys.Add(property.Name)) throw InvalidLayout(marker);
            if (keys.Count != 3 ||
                !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
                !root.TryGetProperty("layout", out var layout) || layout.ValueKind != JsonValueKind.String ||
                layout.GetString() != "app-directory" ||
                !root.TryGetProperty("dataDirectory", out var data) || data.ValueKind != JsonValueKind.String ||
                data.GetString() != "..")
                throw InvalidLayout(marker);

            var directory = new DirectoryInfo(application);
            if (!directory.Name.Equals("app", StringComparison.OrdinalIgnoreCase) || directory.Parent is null)
                throw InvalidLayout(marker);
            var executable = Path.Combine(directory.Parent.FullName, "EML.exe");
            var executableAttributes = File.GetAttributes(executable);
            if ((executableAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw InvalidLayout(marker);
            return directory.Parent.FullName;
        }
        catch (Exception ex) when (ex is JsonException or UnauthorizedAccessException)
        {
            throw new IOException($"无法读取 EML 发布布局，请完整解压发布包后重试：{marker}", ex);
        }
    }

    /// <summary>无布局标记时保留旧版规则；只在开发模式寻找解决方案目录。</summary>
    private static string ResolveLegacyRoot(string applicationDirectory, bool developmentMode)
    {
        if (developmentMode)
            for (var directory = new DirectoryInfo(applicationDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "EML.slnx"))) return directory.FullName;
        return applicationDirectory;
    }

    /// <summary>错误只包含布局文件路径，不读取或输出账号、实例和设置内容。</summary>
    private static IOException InvalidLayout(string marker) =>
        new($"EML 发布布局不完整或不受支持，请完整解压同一版本发布包：{marker}");
}
