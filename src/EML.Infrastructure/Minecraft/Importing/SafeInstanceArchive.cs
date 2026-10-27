using System.IO.Compression;
using System.Text.Json;

namespace EML.Infrastructure.Minecraft.Importing;

/// <summary>已规范化并通过安全检查的 ZIP 条目，目录 Path 不包含末尾斜杠。</summary>
/// <param name="Entry">用于读取文件内容的原始 ZIP 条目。</param>
/// <param name="Path">规范化后的包内相对路径。</param>
/// <param name="Directory">是否为目录条目。</param>
internal sealed record SafeArchiveEntry(ZipArchiveEntry Entry, string Path, bool Directory);

/// <summary>统一保护所有包格式，不因识别为某平台而跳过 ZIP 路径检查。</summary>
internal static class SafeInstanceArchive
{
    internal const int MaxEntries = 100_000;
    internal const long MaxUncompressedBytes = 50L * 1024 * 1024 * 1024;
    internal const int MaxManifestBytes = 8 * 1024 * 1024;

    /// <summary>检查条目数量、总大小、链接和路径冲突，返回可供解析的条目列表。</summary>
    internal static IReadOnlyList<SafeArchiveEntry> ReadEntries(ZipArchive archive, CancellationToken token)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxEntries)
            throw new InvalidDataException("压缩包为空或条目数量超过 100000。");
        long total = 0;
        var entries = new List<SafeArchiveEntry>();
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((type != 0 && type != 0x8000 && type != 0x4000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("不支持压缩包中的符号链接或特殊文件：" + entry.FullName);
            var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || type == 0x4000;
            var path = InstanceImportPaths.Relative(directory ? entry.FullName.TrimEnd('/', '\\') : entry.FullName);
            if (entry.Length < 0 || entry.Length > MaxUncompressedBytes - total || (directory && entry.Length != 0))
                throw new InvalidDataException("解压后的文件总量超过 50 GiB，或条目长度无效。");
            total += entry.Length;
            entries.Add(new(entry, path, directory));
        }
        InstanceImportPaths.ValidateLayout(entries.Select(e => (e.Path, e.Directory)));
        return entries;
    }

    /// <summary>读取受大小和嵌套深度限制的 JSON 对象，返回关闭文档后仍可使用的副本。</summary>
    internal static JsonElement ReadJson(SafeArchiveEntry entry)
    {
        if (entry.Directory || entry.Entry.Length > MaxManifestBytes) throw new InvalidDataException("整合包清单超过 8 MiB 或不是文件。");
        using var input = entry.Entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + count > MaxManifestBytes) throw new InvalidDataException("整合包清单过大。");
            memory.Write(buffer, 0, count);
        }
        if (memory.Length != entry.Entry.Length) throw new InvalidDataException("清单条目长度与实际内容不符。");
        memory.Position = 0;
        using var document = JsonDocument.Parse(memory, new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("整合包清单必须是 JSON 对象。");
        return document.RootElement.Clone();
    }
}
