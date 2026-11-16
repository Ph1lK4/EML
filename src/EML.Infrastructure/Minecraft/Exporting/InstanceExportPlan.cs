using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Exporting;

/// <summary>固定一个待导出条目的源文件状态；生成的清单按真实字节计费，不使用旧清单长度。</summary>
internal sealed record InstanceExportEntry(string SourcePath, string RelativePath, bool Directory,
    long SourceLength, DateTime LastWriteTimeUtc, byte[]? GeneratedContent = null)
{
    internal long Length => GeneratedContent?.LongLength ?? SourceLength;
}

/// <summary>有界导出计划，只收录实际进入 ZIP 的内容，不改变来源、摘要或用户修改策略。</summary>
internal sealed record InstanceExportPlan(IReadOnlyList<InstanceExportEntry> Entries, long TotalBytes)
{
    /// <summary>压缩前固定备份清单、检查路径和额度；超限时尚未创建临时 ZIP。</summary>
    internal static InstanceExportPlan Create(string source, InstanceBackupManifest.Snapshot? backup,
        InstanceArchiveLimits limits, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var manifestBytes = backup is null ? null : InstanceManifestStore.Serialize(backup.Manifest);
        var entries = new List<InstanceExportEntry>();
        long total = 0;
        foreach (var original in Enumerate(source, token))
        {
            limits.ValidateEntryCount(entries.Count + 1);
            var entry = manifestBytes is not null && original.RelativePath.Equals(InstanceImportManifest.FileName, StringComparison.OrdinalIgnoreCase)
                ? original with { GeneratedContent = manifestBytes } : original;
            total = limits.AddLength(total, entry.Length, entry.Directory, entry.RelativePath);
            entries.Add(entry);
        }
        limits.ValidateEntryCount(entries.Count);
        InstanceImportPaths.ValidateLayout(entries.Select(entry => (entry.RelativePath, entry.Directory)));
        return new(entries, total);
    }

    /// <summary>发布前检查源文件增删、长度及写入时间变化；不能代替用户正常关闭外部游戏。</summary>
    internal void EnsureUnchanged(string source, CancellationToken token)
    {
        var remaining = Entries.ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var current in Enumerate(source, token))
        {
            if (!remaining.Remove(current.RelativePath, out var expected) ||
                current.Directory != expected.Directory || !current.Directory &&
                (current.SourceLength != expected.SourceLength || current.LastWriteTimeUtc != expected.LastWriteTimeUtc))
                throw new IOException("实例在导出期间发生变化，未发布备份。请关闭游戏后重试：" + current.SourcePath);
        }
        if (remaining.Count > 0)
            throw new IOException("实例文件在导出期间被移除，未发布备份：" + remaining.Values.First().SourcePath);
    }

    /// <summary>逐项枚举普通文件与目录，尽早拒绝链接和非法路径；不遍历不导出的 EML 派生状态。</summary>
    private static IEnumerable<InstanceExportEntry> Enumerate(string source, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            InstanceImportPaths.EnsureNoLinks(directory);
            foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("实例包含链接文件或目录，请先处理后再导出：" + path);
                var relative = InstanceImportPaths.Relative(Path.GetRelativePath(source, path).Replace('\\', '/'));
                if (InstanceFileChangePolicy.IsDerivedStatePath(relative)) continue;
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var info = isDirectory ? null : new FileInfo(path);
                yield return new(path, relative, isDirectory, info?.Length ?? 0, info?.LastWriteTimeUtc ?? default);
                if (isDirectory) pending.Push(path);
            }
        }
    }
}
