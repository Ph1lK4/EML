namespace EML.Infrastructure.Minecraft.Importing;

/// <summary>导入与导出共同使用的资源额度；仅内部测试可注入小额度，不接受归档声明放宽保护。</summary>
internal sealed class InstanceArchiveLimits
{
    internal const int DefaultMaximumEntries = 100_000;
    internal const long DefaultMaximumUncompressedBytes = 50L * 1024 * 1024 * 1024;
    internal const int MaximumFileRequirements = 100_000;
    internal const int MaximumInstanceManifestBytes = 64 * 1024 * 1024;
    internal const int MaximumPlatformManifestBytes = 8 * 1024 * 1024;

    internal static InstanceArchiveLimits Default { get; } = new(DefaultMaximumEntries, DefaultMaximumUncompressedBytes);
    internal int MaximumEntries { get; }
    internal long MaximumUncompressedBytes { get; }

    /// <summary>限制 ZIP 的条目数和解压字节数；文件需求数量与 JSON 大小另有独立限制。</summary>
    internal InstanceArchiveLimits(int maximumEntries, long maximumUncompressedBytes)
    {
        if (maximumEntries < 1) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumUncompressedBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumUncompressedBytes));
        MaximumEntries = maximumEntries;
        MaximumUncompressedBytes = maximumUncompressedBytes;
    }

    /// <summary>检查全部 ZIP 条目，包括文件、目录和清单；恰好达到上限仍允许。</summary>
    internal void ValidateEntryCount(int count)
    {
        if (count <= 0) throw new InvalidDataException("压缩包为空。");
        if (count > MaximumEntries)
            throw new InvalidDataException($"压缩包包含至少 {count:N0} 个文件及目录，超过当前上限 {MaximumEntries:N0}；无法生成或导入可恢复的实例备份。");
    }

    /// <summary>按未压缩长度累计，先做减法比较，避免超大或伪造的长度导致整数溢出。</summary>
    internal long AddLength(long total, long length, bool directory, string path)
    {
        if (total < 0 || length < 0 || directory && length != 0)
            throw new InvalidDataException("压缩包条目长度无效：" + path);
        if (total > MaximumUncompressedBytes || length > MaximumUncompressedBytes - total)
            throw new InvalidDataException($"解压后的文件总量超过当前上限 {MaximumUncompressedBytes:N0} 字节（{MaximumUncompressedBytes / (1024d * 1024 * 1024):0.##} GiB）；无法生成或导入可恢复的实例备份。超限条目：{path}");
        return total + length;
    }
}
