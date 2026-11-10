using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EML.Core.Minecraft.Mods;

namespace EML.Infrastructure.Minecraft.Mods;

/// <summary>只读取模组描述文件，不加载 Java 类，也不执行模组中的任何代码。</summary>
internal static class ModMetadataReader
{
    private const int MaximumMetadataBytes = 1024 * 1024;
    internal sealed record Metadata(string Name, string? Version, string? Loader, IReadOnlyList<string> Ids, string? Warning = null)
    {
        public IReadOnlyList<LocalModIdentity> Identities { get; init; } = [];
    }

    /// <summary>轻量读取单个归档的显示信息，损坏或未知格式仍保留文件名供列表展示。</summary>
    internal static Metadata Read(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(input, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(Path.GetFileName(path), null, null, [], "无法读取模组描述：" + ex.Message);
        }
    }

    /// <summary>在调用方持有的只读句柄上读取有限元数据；不解压嵌套归档。</summary>
    internal static Metadata Read(Stream input, string fallbackName)
    {
        try
        {
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > 100_000) return Unknown(fallbackName, "归档条目过多，已跳过描述读取。");
            if (Text(archive, "fabric.mod.json") is { } fabric) return ReadJson(fabric, fallbackName, "Fabric");
            if (Text(archive, "quilt.mod.json") is { } quilt) return ReadJson(quilt, fallbackName, "Quilt");
            if (Text(archive, "META-INF/neoforge.mods.toml") is { } neo) return ReadToml(neo, fallbackName, "NeoForge");
            if (Text(archive, "META-INF/mods.toml") is { } forge) return ReadToml(forge, fallbackName, "Forge");
            if (Text(archive, "mcmod.info") is { } legacy) return ReadLegacy(legacy, fallbackName);
            return Unknown(fallbackName, "未找到可识别的模组描述；仍可管理文件，兼容性需自行确认。");
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or DecoderFallbackException)
        {
            return Unknown(fallbackName, "模组描述无法解析：" + ex.Message);
        }
    }

    /// <summary>严格限制单个描述文件的解压字节数，避免把大型资源读入内存。</summary>
    private static string? Text(ZipArchive archive, string name)
    {
        var entries = archive.Entries.Where(entry => entry.FullName == name).Take(2).ToArray();
        if (entries.Length > 1) throw new InvalidDataException("模组描述条目重复。");
        var entry = entries.SingleOrDefault();
        if (entry is null) return null;
        if (entry.Length > MaximumMetadataBytes) throw new InvalidDataException("模组描述超过 1 MiB。");
        using var input = entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            if (memory.Length + count > MaximumMetadataBytes) throw new InvalidDataException("模组描述超过 1 MiB。");
            memory.Write(buffer, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(memory.ToArray()).TrimStart('\uFEFF');
    }

    /// <summary>读取 Fabric 或 Quilt 的顶层标识和别名；嵌套模组不作为独立磁盘文件展示。</summary>
    private static Metadata ReadJson(string text, string fallback, string loader)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Unknown(fallback, "模组 JSON 未提供描述对象。");
        if (loader == "Quilt")
        {
            if (!root.TryGetProperty("quilt_loader", out var quilt) || quilt.ValueKind != JsonValueKind.Object)
                return Unknown(fallback, "Quilt 描述未提供 quilt_loader 对象。");
            root = quilt;
        }
        var ids = new List<string>();
        AddId(ids, String(root, "id"));
        if (root.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array && provides.GetArrayLength() <= 256)
            foreach (var provided in provides.EnumerateArray())
                AddId(ids, provided.ValueKind == JsonValueKind.String ? provided.GetString() : String(provided, "id"));
        var name = String(root, "name");
        if (name is null && root.TryGetProperty("metadata", out var metadata)) name = String(metadata, "name");
        var version = Version(String(root, "version"));
        var primary = String(root, "id");
        return new(name ?? fallback, version, loader, Distinct(ids))
        {
            Identities = primary is not null && ids.Contains(primary, StringComparer.OrdinalIgnoreCase)
                ? [new(primary, name ?? primary, version ?? "未知")] : []
        };
    }

    /// <summary>兼容旧 Forge 的数组和 modList 容器，一个 JAR 可声明多个 modid。</summary>
    private static Metadata ReadLegacy(string text, string fallback)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        var values = document.RootElement;
        if (values.ValueKind == JsonValueKind.Object && !values.TryGetProperty("modList", out values))
            return Unknown(fallback, "mcmod.info 未提供模组数组。");
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 256)
            return Unknown(fallback, "mcmod.info 的模组数组无效。");
        var ids = new List<string>();
        var identities = new List<LocalModIdentity>();
        string? name = null, version = null;
        foreach (var value in values.EnumerateArray())
        {
            var id = String(value, "modid");
            AddId(ids, id);
            if (id is not null && ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                identities.Add(new(id, String(value, "name") ?? id, Version(String(value, "version")) ?? "未知"));
            name ??= String(value, "name");
            version ??= Version(String(value, "version"));
        }
        return new(name ?? fallback, version, "Forge", Distinct(ids)) { Identities = identities.DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray() };
    }

    /// <summary>只提取 mods 表内的简单字符串；无法静态解释的 TOML 值保留未知，不猜测兼容版本。</summary>
    private static Metadata ReadToml(string text, string fallback, string loader)
    {
        var ids = new List<string>();
        var identities = new List<LocalModIdentity>();
        string? id = null, name = null, version = null;
        var inMod = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                AppendIdentity(identities, ids, id, name, version);
                id = name = version = null;
                inMod = line == "[[mods]]";
                continue;
            }
            if (!inMod || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = TomlString(line[(separator + 1)..].Trim());
            if (key == "modId") id = value;
            else if (key == "displayName") name ??= value;
            else if (key == "version") version ??= Version(value);
        }
        AppendIdentity(identities, ids, id, name, version);
        return new(identities.FirstOrDefault()?.Name ?? fallback, identities.FirstOrDefault()?.Version, loader, Distinct(ids),
            ids.Count == 0 ? "未能静态读取 modid，无法排除不同文件名的重复模组。" : null)
        {
            Identities = identities.DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    /// <summary>每个 TOML mods 表保留自己的名称和版本，不把首个模组的显示值套到整个归档。</summary>
    private static void AppendIdentity(List<LocalModIdentity> identities, List<string> ids, string? id, string? name, string? version)
    {
        AddId(ids, id);
        if (id is not null && ids.Contains(id, StringComparer.OrdinalIgnoreCase)) identities.Add(new(id, name ?? id, version ?? "未知"));
    }

    /// <summary>处理单行引号字符串和尾部注释，多行或动态表达式保持未知。</summary>
    private static string? TomlString(string value)
    {
        if (value.Length < 2 || value[0] is not ('\'' or '"') || value.StartsWith("\"\"\"", StringComparison.Ordinal) || value.StartsWith("'''", StringComparison.Ordinal)) return null;
        var quote = value[0];
        for (var i = 1; i < value.Length; i++)
        {
            if (quote == '"' && value[i] == '\\') { i++; continue; }
            if (value[i] != quote) continue;
            var trailing = value[(i + 1)..].Trim();
            if (trailing.Length > 0 && !trailing.StartsWith('#')) return null;
            if (quote == '\'') return Clean(value[1..i]);
            try { return Clean(JsonSerializer.Deserialize<string>(value[..(i + 1)])); }
            catch (JsonException) { return null; }
        }
        return null;
    }

    /// <summary>只接受有限长度的纯文本字段，避免畸形描述污染界面。</summary>
    private static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? Clean(property.GetString()) : null;

    /// <summary>去除首尾空白并拒绝控制字符和过长文本。</summary>
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl) ? null : value.Trim();

    /// <summary>构建时未替换的版本占位符不能作为真实版本显示。</summary>
    private static string? Version(string? value) => value is null || value.Contains("${", StringComparison.Ordinal) ? null : value;

    /// <summary>校验标识的长度和字符，避免用任意描述文本作冲突依据。</summary>
    private static void AddId(List<string> ids, string? value)
    {
        if (value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') && ids.Count < 256) ids.Add(value);
    }

    /// <summary>稳定去重模组标识，使列表和迁移使用同样的冲突依据。</summary>
    private static IReadOnlyList<string> Distinct(IEnumerable<string> ids) => ids.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>未知描述不让整个模组列表失败，也不宣称归档或依赖已通过验证。</summary>
    private static Metadata Unknown(string name, string warning) => new(name, null, null, [], warning);
}
