using System.Security.Cryptography;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Mods;

/// <summary>模组管理和 GTNH 迁移共用稳定摘要与重复检查，列表无需调用完整摘要读取。</summary>
internal static class ModArchiveInspector
{
    internal sealed record Snapshot(string Path, long Size, string Sha256, ModMetadataReader.Metadata Metadata);

    /// <summary>在同一只读句柄上读取描述并只计算一次 SHA-256，读取期间拒绝写入和替换。</summary>
    internal static async Task<Snapshot> ReadAsync(string path, string relative, CancellationToken token)
    {
        InstanceImportPaths.EnsureNoLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var size = input.Length;
        if (size > SafeInstanceArchive.MaxUncompressedBytes) throw new InvalidDataException("模组文件超过允许大小。");
        var time = File.GetLastWriteTimeUtc(path);
        var metadata = ModMetadataReader.Read(input, System.IO.Path.GetFileName(path));
        input.Position = 0;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
        if (input.Length != size || File.GetLastWriteTimeUtc(path) != time) throw new IOException("读取期间模组发生变化：" + relative);
        return new(relative, size, hash, metadata);
    }

    /// <summary>文件名、相同内容和已知标识任一冲突都不能通过覆盖旧文件解决。</summary>
    internal static string? Collision(string candidatePath, string? candidateHash, IReadOnlyList<string> candidateIds,
        IEnumerable<(string Path, string? Hash, IReadOnlyList<string> Ids)> others, IEnumerable<string> declaredPaths)
    {
        var name = Name(candidatePath);
        if (declaredPaths.Any(path => Name(path).Equals(name, StringComparison.OrdinalIgnoreCase)))
            return "已有同名模组声明，保留原来源（即使文件暂时缺失）。";
        foreach (var other in others)
        {
            if (Name(other.Path).Equals(name, StringComparison.OrdinalIgnoreCase)) return "已有同名模组：" + other.Path;
            if (candidateHash is not null && candidateHash.Equals(other.Hash, StringComparison.OrdinalIgnoreCase)) return "已有相同内容的模组：" + other.Path;
            if (candidateIds.Intersect(other.Ids, StringComparer.OrdinalIgnoreCase).Any()) return "已有相同 modid 的模组：" + other.Path;
        }
        return null;
    }

    /// <summary>启用和禁用副本共享文件名身份，两个 Forge 模组目录也不能绕过冲突检查。</summary>
    internal static string Name(string path)
    {
        var name = path.Replace('\\', '/').Split('/')[^1];
        return name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? name[..^9] : name;
    }
}
