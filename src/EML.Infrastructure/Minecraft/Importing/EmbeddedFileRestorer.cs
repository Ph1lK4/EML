using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using EML.Core.Minecraft;

namespace EML.Infrastructure.Minecraft.Importing;

/// <summary>只从原包恢复缺失的嵌入文件，绝不以同名远端下载替换原始内容。</summary>
internal sealed class EmbeddedFileRestorer(string launcherRoot)
{
    /// <summary>按导入记录校验现有文件或恢复缺失条目；找不到原包时返回 false，损坏时抛出明确错误。</summary>
    internal async Task<bool> RestoreAsync(string instanceRoot, InstanceImportManifest manifest,
        InstanceFileRequirement requirement, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = EmbeddedFileBaseline.ValidateInstance(launcherRoot, instanceRoot);
        InstanceManifestStore.Validate(manifest);
        if (requirement.Origin == InstanceFileOrigin.UserLocalImport || requirement.Path is null)
            throw new InvalidDataException("用户主动替换的文件不能自动恢复为原包内容。");
        var baseline = EmbeddedFileBaseline.Find(manifest, requirement.Path);
        var destination = InstanceImportPaths.Within(root, baseline.Path!);
        InstanceImportPaths.EnsureNoLinks(destination);
        if (File.Exists(destination))
        {
            if (await MatchesAsync(destination, baseline, token).ConfigureAwait(false)) return true;
            throw new InvalidDataException("现有文件与原包完整性记录不符，已保留原文件：" + baseline.Path);
        }
        if (Directory.Exists(destination)) throw new IOException("恢复目标已被目录占用：" + baseline.Path);
        if (baseline.ArchiveEntry is null) return false;
        foreach (var candidate in ArchiveCandidates(root, manifest))
        {
            token.ThrowIfCancellationRequested();
            InstanceImportPaths.EnsureNoLinks(candidate);
            if (!File.Exists(candidate)) continue;
            await RestoreEntryAsync(candidate, root, destination, manifest, baseline, token).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    /// <summary>优先使用导入时的精确路径；旧实例只检查少量固定目录中的同名原包。</summary>
    private IEnumerable<string> ArchiveCandidates(string instanceRoot, InstanceImportManifest manifest)
    {
        var recorded = InstanceImportSourceStore.Read(instanceRoot, manifest);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (recorded is not null && paths.Add(recorded)) yield return recorded;
        var name = manifest.SourceArchive;
        if (string.IsNullOrWhiteSpace(name)) yield break;
        // SourceArchive 只接受一个文件名，不让旧清单借查找过程访问任意目录。
        if (InstanceImportPaths.Relative(name).Contains('/'))
            throw new InvalidDataException("导入记录的原包名称不是单个文件名。");
        var roots = new[]
        {
            Path.GetFullPath(launcherRoot), Path.GetDirectoryName(Path.GetFullPath(launcherRoot)), instanceRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };
        foreach (var directory in roots.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var candidate = Path.GetFullPath(Path.Combine(directory!, name));
            if (paths.Add(candidate)) yield return candidate;
        }
    }

    /// <summary>检查整个 ZIP 的安全布局，再提取唯一的记录条目并在原子提交前核对大小和 SHA-256。</summary>
    private static async Task RestoreEntryAsync(string archivePath, string instanceRoot, string destination,
        InstanceImportManifest manifest, InstanceFileRequirement baseline, CancellationToken token)
    {
        using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var entries = SafeInstanceArchive.ReadEntries(archive, token);
        var sourcePath = InstanceImportPaths.Relative(baseline.ArchiveEntry!);
        var entry = entries.SingleOrDefault(item => !item.Directory && item.Path.Equals(sourcePath, StringComparison.Ordinal))
            ?? throw new InvalidDataException("原包中没有导入记录对应的文件条目：" + sourcePath);
        if (entry.Entry.Length != baseline.Size)
            throw new InvalidDataException("原包条目大小与导入记录不符，未恢复文件：" + sourcePath);
        InstanceImportPaths.EnsureNoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".eml-restore.tmp";
        try
        {
            using var source = entry.Entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long copied = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                int count;
                while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    if (count > baseline.Size!.Value - copied)
                        throw new InvalidDataException("原包条目实际内容超过导入大小，已取消恢复。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    copied += count;
                }
            }
            if (copied != baseline.Size || !Convert.ToHexStringLower(hash.GetHashAndReset())
                    .Equals(baseline.Hashes["sha256"], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原包条目的 SHA-256 或大小与导入记录不符，未恢复文件：" + sourcePath);
            token.ThrowIfCancellationRequested();
            EmbeddedFileBaseline.EnsureManifestUnchanged(instanceRoot, manifest);
            InstanceImportPaths.EnsureNoLinks(destination);
            File.Move(temporary, destination, overwrite: false);
        }
        finally { DeleteTemporary(temporary); }
    }

    /// <summary>验证已有文件，只使用原包的大小和 SHA-256，不引入官方文件的不同摘要。</summary>
    private static async Task<bool> MatchesAsync(string path, InstanceFileRequirement baseline, CancellationToken token)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        if (input.Length != baseline.Size) return false;
        var hash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash).Equals(baseline.Hashes["sha256"], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>只删除本次生成的临时文件，清理失败不会掩盖原本的恢复错误。</summary>
    private static void DeleteTemporary(string temporary)
    {
        try
        {
            InstanceImportPaths.EnsureNoLinks(temporary);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("无法清理恢复临时文件 {0}：{1}", temporary, ex.Message); }
    }
}
