using System.IO.Compression;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;
using EML.Infrastructure.Minecraft.CurseForge;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Migration;

/// <summary>只读枚举可手动选择的额外模组，并保护新版已有模组；不猜测或下载模组版本。</summary>
internal static class GtnhExtraModCatalog
{
    internal sealed record Mod(string Path, long Size, string Sha256, IReadOnlyList<string> Ids);

    /// <summary>保留已声明但暂缺的模组位置，包括平台解析和此前迁入记录，不能将其当成空位。</summary>
    internal static IReadOnlyList<string> DeclaredPaths(string root, InstanceImportManifest manifest)
    {
        var prefix = manifest.GameDirectory == "." ? "" : manifest.GameDirectory + "/";
        var resolution = manifest.SourceFormat.Equals("CurseForge", StringComparison.OrdinalIgnoreCase)
            ? CurseForgeFileStateStore.Read(root, manifest) : null;
        var requirements = resolution is null ? manifest.Files : CurseForgeFileStateStore.EffectiveRequirements(manifest, resolution);
        return requirements.Select(file => file.Path).Concat(MigrationDataStore.Read(root, manifest).Files.Select(file => file.Path))
            .Where(path => path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(path => path![prefix.Length..]).Where(GtnhExtraModPolicy.Allows)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>导入记录只能区分导入后新增；原包内文件须由用户确认，全部默认不选。</summary>
    internal static IReadOnlyList<MigrationGroup> Groups(GtnhInstanceInfo source, CancellationToken token)
    {
        var manifest = InstanceManifestStore.Read(Path.Combine(source.InstancePath, InstanceImportManifest.FileName));
        var prefix = manifest.GameDirectory == "." ? "" : manifest.GameDirectory + "/";
        var imported = manifest.Files.Where(file => file.Path is not null).Select(file => file.Path!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var migrated = MigrationDataStore.Read(source.InstancePath, manifest).Files.Where(file => file.Rule == GtnhExtraModPolicy.Rule)
            .Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Paths(source.GameDirectory, token).Select(path => new MigrationGroup(GtnhExtraModPolicy.GroupPrefix + path,
            path + (migrated.Contains(prefix + path) ? "（此前手动迁入）" : imported.Contains(prefix + path)
                ? "（导入时已存在，需确认是自己添加）" : "（导入后新增）"), false)).ToArray();
    }

    /// <summary>目标模组的内容和标识均参与预览指纹，防止预览后安装同模组另一版本。</summary>
    internal static async Task<IReadOnlyList<Mod>> SnapshotAsync(string game, CancellationToken token)
    {
        var result = new List<Mod>();
        foreach (var path in Paths(game, token)) result.Add(await ReadAsync(game, path, token));
        return result;
    }

    /// <summary>读取选中文件的稳定摘要和有限元数据，既不加载 Java 类也不执行模组代码。</summary>
    internal static async Task<Mod> ReadAsync(string game, string relative, CancellationToken token)
    {
        if (!GtnhExtraModPolicy.Allows(relative)) throw new InvalidDataException("不支持的额外模组路径。");
        var path = MigrationIO.SafePath(game, relative);
        var before = await MigrationIO.SnapshotAsync(path, token) ?? throw new IOException("选中模组已不存在：" + relative);
        var ids = ReadIds(path);
        var after = await MigrationIO.SnapshotAsync(path, token);
        if (after != before) throw new IOException("读取时模组发生变化：" + relative);
        return new(relative, before.Size, before.Hash, ids);
    }

    /// <summary>按文件名（忽略禁用后缀）、相同内容和已知 modid 拦截重复，不用文件名推断版本兼容性。</summary>
    internal static string? Collision(Mod candidate, IEnumerable<Mod> others, IEnumerable<string> declaredPaths)
    {
        var name = Name(candidate.Path);
        if (declaredPaths.Any(path => Name(path).Equals(name, StringComparison.OrdinalIgnoreCase)))
            return "新版已有同名模组记录，保留新版来源（即使文件暂时缺失）。";
        foreach (var other in others)
        {
            if (Name(other.Path).Equals(name, StringComparison.OrdinalIgnoreCase)) return "已有同名模组：" + other.Path;
            if (candidate.Sha256.Equals(other.Sha256, StringComparison.OrdinalIgnoreCase)) return "已有相同内容的模组：" + other.Path;
            if (candidate.Ids.Intersect(other.Ids, StringComparer.OrdinalIgnoreCase).Any()) return "已有相同 modid 的模组：" + other.Path;
        }
        return null;
    }

    /// <summary>只遍历 Forge 当前游戏版本使用的位置，不遍历 mods 内的缓存或其他版本目录。</summary>
    private static IReadOnlyList<string> Paths(string game, CancellationToken token)
    {
        var result = new List<string>();
        foreach (var directory in new[] { "mods", "mods/1.7.10" })
        {
            var full = MigrationIO.SafePath(game, directory);
            if (File.Exists(full)) throw new IOException("模组目录被文件占用：" + directory);
            if (!Directory.Exists(full)) continue;
            foreach (var path in Directory.EnumerateFiles(full))
            {
                token.ThrowIfCancellationRequested();
                var relative = InstanceImportPaths.Relative(Path.GetRelativePath(game, path).Replace('\\', '/'));
                MigrationIO.SafePath(game, relative);
                if (GtnhExtraModPolicy.Allows(relative)) result.Add(relative);
                if (result.Count > 4096) throw new IOException("模组数量过多，请先整理实例。");
            }
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>沿用 FML 的数组或 modList 对象形式；缺少可读标识时不宣称已经排除重复模组。</summary>
    private static IReadOnlyList<string> ReadIds(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("mcmod.info");
            if (entry is null || entry.Length > 1024 * 1024) return [];
            using var input = entry.Open();
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                if (memory.Length + count > 1024 * 1024) return [];
                memory.Write(buffer, 0, count);
            }
            memory.Position = 0;
            using var document = JsonDocument.Parse(memory, new JsonDocumentOptions { MaxDepth = 32 });
            var list = document.RootElement;
            if (list.ValueKind == JsonValueKind.Object && !list.TryGetProperty("modList", out list)) return [];
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 256) return [];
            return list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("modid", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("modid").GetString()!)
                .Where(id => id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException) { return []; }
        catch (InvalidDataException) { return []; }
    }

    /// <summary>启用/禁用版本和两个 Forge 模组目录都不能形成重复安装。</summary>
    private static string Name(string path)
    {
        var name = path.Split('/')[^1];
        return name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? name[..^9] : name;
    }
}
