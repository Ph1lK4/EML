using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;

namespace EML.Infrastructure.Minecraft.Mods;

/// <summary>按大小与写入时间缓存少量归档描述；刷新列表不会计算整份模组 SHA-256。</summary>
internal sealed class LocalModFileCatalog
{
    internal sealed record Item(string Path, long Size, DateTime Modified, ModMetadataReader.Metadata Metadata);
    private readonly Dictionary<string, Item> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>只读枚举当前加载器使用的目录，拒绝链接并限制数量。</summary>
    internal IReadOnlyList<Item> Scan(string root, InstanceImportManifest manifest, CancellationToken token)
    {
        var result = new List<Item>();
        foreach (var directory in LocalModPaths.Directories(manifest))
        {
            var full = MigrationIO.SafePath(root, directory);
            if (File.Exists(full)) throw new IOException("模组目录被文件占用：" + directory);
            if (!Directory.Exists(full)) continue;
            var entries = 0;
            foreach (var file in Directory.EnumerateFileSystemEntries(full))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 8192) throw new IOException("模组目录中的条目过多，请先整理实例。");
                var path = InstanceImportPaths.Relative(Path.GetRelativePath(root, file).Replace('\\', '/'));
                if (!LocalModPaths.Allows(manifest, path)) continue;
                MigrationIO.SafePath(root, path);
                if (Directory.Exists(file)) continue;
                result.Add(Read(root, path));
                if (result.Count > 4096) throw new IOException("模组数量超过 4096，请先整理实例。");
            }
        }
        return result.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>读取一个已验证位置的显示元数据，文件变化时丢弃旧缓存并保留问题提示。</summary>
    internal Item Read(string root, string relative)
    {
        var path = MigrationIO.SafePath(root, relative);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("模组已不存在：" + relative, path);
        if (_cache.TryGetValue(path, out var cached) && cached.Size == info.Length && cached.Modified == info.LastWriteTimeUtc)
            return cached with { Path = relative };
        var size = info.Length;
        var time = info.LastWriteTimeUtc;
        var metadata = ModMetadataReader.Read(path);
        info.Refresh();
        if (!info.Exists || info.Length != size || info.LastWriteTimeUtc != time)
            metadata = metadata with { Warning = "读取期间文件发生变化，请刷新后再操作。" };
        var item = new Item(relative, size, time, metadata);
        if (_cache.Count > 8192) _cache.Clear();
        _cache[path] = item;
        return item;
    }

    /// <summary>写操作完成后主动移除该实例的描述缓存，不把旧文件信息沿用到新位置。</summary>
    internal void Invalidate(string root)
    {
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        foreach (var path in _cache.Keys.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray()) _cache.Remove(path);
    }
}
