using System.IO.Compression;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Tests;

/// <summary>验证共享额度、独立元数据限制及长度溢出保护，不创建超大游戏文件。</summary>
public sealed class InstanceArchiveLimitsTests
{
    /// <summary>生产保护沿用十万条目、50 GiB；需求数量和清单长度不随测试额度改变。</summary>
    [Fact]
    public void ProductionLimitsRemainExplicitAndIndependent()
    {
        Assert.Equal(100_000, InstanceArchiveLimits.Default.MaximumEntries);
        Assert.Equal(50L * 1024 * 1024 * 1024, InstanceArchiveLimits.Default.MaximumUncompressedBytes);
        Assert.Equal(100_000, InstanceArchiveLimits.MaximumFileRequirements);
        Assert.Equal(64 * 1024 * 1024, InstanceArchiveLimits.MaximumInstanceManifestBytes);
        Assert.Equal(8 * 1024 * 1024, InstanceArchiveLimits.MaximumPlatformManifestBytes);
        var small = new InstanceArchiveLimits(1, 0);
        Assert.Equal(1, small.MaximumEntries);
        Assert.Equal(100_000, InstanceArchiveLimits.MaximumFileRequirements);
    }

    /// <summary>条目数恰好到达生产边界可以接受，多一项或空包则明确拒绝。</summary>
    [Fact]
    public void EntryBoundaryIsInclusive()
    {
        InstanceArchiveLimits.Default.ValidateEntryCount(100_000);
        Assert.Throws<InvalidDataException>(() => InstanceArchiveLimits.Default.ValidateEntryCount(100_001));
        Assert.Throws<InvalidDataException>(() => InstanceArchiveLimits.Default.ValidateEntryCount(0));
    }

    /// <summary>用声明长度覆盖 50 GiB 边界，不需要在测试机写入 50 GiB 文件。</summary>
    [Fact]
    public void ByteBoundaryIsInclusive()
    {
        var limits = InstanceArchiveLimits.Default;
        var almostFull = limits.AddLength(0, limits.MaximumUncompressedBytes - 1, false, "world/data.bin");
        Assert.Equal(limits.MaximumUncompressedBytes, limits.AddLength(almostFull, 1, false, "last.bin"));
        Assert.Throws<InvalidDataException>(() => limits.AddLength(almostFull, 2, false, "over.bin"));
    }

    /// <summary>减法比较必须在加法之前，极大声明长度不能通过有符号溢出绕过检查。</summary>
    [Fact]
    public void OversizedLengthsCannotWrapAround()
    {
        var limits = new InstanceArchiveLimits(10, long.MaxValue);
        Assert.Equal(long.MaxValue, limits.AddLength(long.MaxValue - 1, 1, false, "last.bin"));
        Assert.Throws<InvalidDataException>(() => limits.AddLength(long.MaxValue - 1, 2, false, "overflow.bin"));
        Assert.Throws<InvalidDataException>(() => InstanceArchiveLimits.Default.AddLength(1, long.MaxValue, false, "huge.bin"));
        Assert.Throws<InvalidDataException>(() => new InstanceArchiveLimits(1, 10).AddLength(11, 0, false, "over.bin"));
    }

    /// <summary>负长度与带内容的目录不可用于额度统计。</summary>
    [Theory]
    [InlineData(-1L, 0L, false)]
    [InlineData(0L, -1L, false)]
    [InlineData(0L, 1L, true)]
    public void InvalidLengthsAreRejected(long total, long length, bool directory)
    {
        Assert.Throws<InvalidDataException>(() => InstanceArchiveLimits.Default.AddLength(total, length, directory, "entry"));
    }

    /// <summary>零长度文件和空目录不占未压缩字节额度，但仍由条目额度约束。</summary>
    [Fact]
    public void EmptyFilesAndDirectoriesUseZeroBytes()
    {
        var limits = new InstanceArchiveLimits(2, 0);
        Assert.Equal(0, limits.AddLength(0, 0, true, "empty"));
        Assert.Equal(0, limits.AddLength(0, 0, false, "empty.txt"));
        limits.ValidateEntryCount(2);
        Assert.Throws<InvalidDataException>(() => limits.ValidateEntryCount(3));
    }

    /// <summary>通过真实 ZIP 读取器验证十万空目录计数，追加一项立即拒绝。</summary>
    [Fact]
    public void RealZipDirectoryCountUsesProductionBoundary()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            for (var i = 0; i < 100_000; i++) zip.CreateEntry($"folder-{i}/");
        buffer.Position = 0;
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true))
            Assert.Equal(100_000, SafeInstanceArchive.ReadEntries(zip, CancellationToken.None).Count);
        buffer.Position = 0;
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true)) zip.CreateEntry("one-too-many/");
        buffer.Position = 0;
        using var oversized = new ZipArchive(buffer, ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => SafeInstanceArchive.ReadEntries(oversized, CancellationToken.None));
    }
}
