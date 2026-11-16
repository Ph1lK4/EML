using System.IO.Compression;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Tests;

/// <summary>验证 ZIP 内清单可读取，不代表恢复时新生成的清单仍能保存。</summary>
public sealed class InstanceRestoreMetadataBoundaryTests
{
    /// <summary>原清单恰好 64 MiB 时，恢复说明造成的增长必须在提取文件前被拒绝。</summary>
    [Fact]
    public void RestoreNotesGrowthCannotBypassManifestByteLimit()
    {
        var template = new InstanceImportManifest { Name = "Boundary", SourceArchive = "backup.zip", Notes = [""] };
        var overhead = InstanceManifestStore.Serialize(template).Length;
        var original = template with { Notes = [new string('a', InstanceManifestStore.MaximumBytes - overhead)] };
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var bytes = InstanceManifestStore.Serialize(original);
            Assert.Equal(InstanceManifestStore.MaximumBytes, bytes.Length);
            using var output = zip.CreateEntry(InstanceImportManifest.FileName, CompressionLevel.Fastest).Open();
            output.Write(bytes);
        }
        memory.Position = 0;
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var plan = InstanceImportPlanValidator.Prepare(InstanceArchiveReader.Read(archive, "backup.zip", CancellationToken.None));

        Assert.Equal(2, plan.Manifest.Notes.Count);
        Assert.Throws<InvalidDataException>(() => InstanceImportPlanValidator.ValidateRestoreMetadata(plan));

        Assert.Single(original.Notes);
        Assert.Equal(InstanceManifestStore.MaximumBytes, archive.Entries[0].Length);
    }
}
