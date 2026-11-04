using System.Text.Json;
using EML.Core.Minecraft;

namespace EML.Infrastructure.Minecraft.Importing;

/// <summary>只在本机保存原整合包位置，公开导入清单仍只记录文件名。</summary>
internal static class InstanceImportSourceStore
{
    internal const string RelativePath = ".eml-runtime/import-source.json";
    private const int MaximumBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 8 };

    /// <summary>本机定位状态绑定完整清单，不能由压缩包内同名文件提供可信来源。</summary>
    private sealed record ImportSource(int SchemaVersion, string ManifestFingerprint, string ArchivePath);

    /// <summary>判断是否为必须重新生成且不能随导出传播的本机定位文件。</summary>
    internal static bool IsLocalSourcePath(string relativePath) =>
        string.Equals(relativePath.Replace('\\', '/'), RelativePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>导入提交前保存经过确认的原包绝对位置，不把它放进原始文件需求。</summary>
    internal static async Task WriteAsync(string instanceRoot, InstanceImportManifest manifest, string archivePath,
        CancellationToken token)
    {
        var fullPath = ValidateArchivePath(Path.GetFullPath(archivePath), manifest);
        InstanceImportPaths.EnsureNoLinks(fullPath);
        var path = InstanceImportPaths.Within(instanceRoot, RelativePath);
        InstanceImportPaths.EnsureNoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ImportSource(1,
            EmbeddedFileBaseline.ManifestFingerprint(manifest), fullPath), Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("原包位置记录过长。");
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    /// <summary>读取与当前清单相符的本机位置；旧实例没有该记录时交由有限目录回退查找。</summary>
    internal static string? Read(string instanceRoot, InstanceImportManifest manifest)
    {
        var path = InstanceImportPaths.Within(instanceRoot, RelativePath);
        InstanceImportPaths.EnsureNoLinks(path);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumBytes) throw new InvalidDataException("原包位置记录超过 32 KiB。");
            var source = JsonSerializer.Deserialize<ImportSource>(input, Options)
                ?? throw new InvalidDataException("原包位置记录为空。");
            if (source.SchemaVersion != 1 || source.ManifestFingerprint is null ||
                source.ManifestFingerprint.Length != 64 || !source.ManifestFingerprint.All(Uri.IsHexDigit))
                throw new InvalidDataException("原包位置记录格式无效。");
            if (source.ManifestFingerprint != EmbeddedFileBaseline.ManifestFingerprint(manifest)) return null;
            return ValidateArchivePath(source.ArchivePath, manifest);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException ex) { throw new InvalidDataException("原包位置记录 JSON 损坏。", ex); }
    }

    /// <summary>原包位置必须是与导入文件名一致的本地绝对 ZIP/mrpack 路径。</summary>
    private static string ValidateArchivePath(string path, InstanceImportManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 16_384 || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(path), manifest.SourceArchive, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("原包位置不是与导入记录对应的本地绝对路径。");
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("原包位置必须指向 .zip 或 .mrpack 文件。");
        return Path.GetFullPath(path);
    }
}
