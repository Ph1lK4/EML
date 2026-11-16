using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Tests;

/// <summary>使用小测试额度验证备份边界及真实回导；只操作本测试的 GUID 临时目录。</summary>
public sealed class InstanceExportBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-export-boundaries-" + Guid.NewGuid().ToString("N"));

    /// <summary>三个目录和一个四字节文件恰好达到两个额度时，导出并通过同额度解析。</summary>
    [Fact]
    public async Task ExactEntryAndByteLimitsAllowExportAndRestore()
    {
        var source = CreatePlainInstance();
        var destination = Path.Combine(_root, "exact.zip");
        var limits = new InstanceArchiveLimits(4, 4);

        await Manager(limits).ExportAsync(source, destination);

        using (var zip = ZipFile.OpenRead(destination))
        {
            var entries = SafeInstanceArchive.ReadEntries(zip, CancellationToken.None, limits);
            Assert.Equal(4, entries.Count);
            Assert.Equal(3, entries.Count(entry => entry.Directory));
            Assert.Equal(4, entries.Sum(entry => entry.Entry.Length));
        }
        var restored = await new MinecraftInstanceImporter(_root).ImportAsync(destination, "恢复（中文）");
        Assert.Equal("keep", File.ReadAllText(Path.Combine(restored.GameDirectory, "options.txt")));
        Assert.True(Directory.Exists(Path.Combine(restored.GameDirectory, "saves", "empty")));
        AssertNoTemporaryArchive();
    }

    /// <summary>文件加目录超一项，或未压缩字节超一字节，均在开始写 ZIP 前失败。</summary>
    [Theory]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    public async Task OverLimitRejectsBeforeWritingWithoutChangingSource(int entries, long bytes)
    {
        var source = CreatePlainInstance();
        var destination = Path.Combine(_root, "over.zip");
        var wroteArchive = false;
        var progress = new InlineProgress(_ => wroteArchive |= Directory.GetFiles(_root, ".eml-export-*.tmp").Length != 0);

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Manager(new(entries, bytes)).ExportAsync(source, destination, progress));

        Assert.False(wroteArchive);
        Assert.False(File.Exists(destination));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(source, ".minecraft", "options.txt")));
        Assert.True(Directory.Exists(Path.Combine(source, ".minecraft", "saves", "empty")));
        AssertNoTemporaryArchive();
    }

    /// <summary>已排除的运行缓存不计入导出额度，也不能随 ZIP 泄露本机派生状态。</summary>
    [Fact]
    public async Task ExcludedRuntimeFilesDoNotConsumeArchiveBudget()
    {
        var source = CreatePlainInstance();
        Directory.CreateDirectory(Path.Combine(source, ".eml-runtime", "unrelated-cache"));
        File.WriteAllBytes(Path.Combine(source, ".eml-runtime", "unrelated-cache", "cache.bin"), new byte[256]);
        var destination = Path.Combine(_root, "without-cache.zip");

        await Manager(new(4, 4)).ExportAsync(source, destination);

        using var zip = ZipFile.OpenRead(destination);
        Assert.Equal(4, zip.Entries.Count);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.StartsWith(".eml-runtime", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(source, ".eml-runtime", "unrelated-cache", "cache.bin")));
    }

    /// <summary>备份清单新增来源用途字段后更大，不能用原清单长度误判为没有超限。</summary>
    [Fact]
    public async Task RegeneratedManifestBytesAreCountedBeforeWriting()
    {
        var (source, original, _) = CreateEmlInstance();
        var originalBytes = File.ReadAllBytes(Path.Combine(source, InstanceImportManifest.FileName));
        var backupBytes = InstanceManifestStore.Serialize(InstanceBackupManifest.Create(source, original).Manifest);
        Assert.True(backupBytes.Length > originalBytes.Length);
        var destination = Path.Combine(_root, "manifest-too-large.zip");
        var beganWriting = false;
        var progress = new InlineProgress(_ => beganWriting |= Directory.GetFiles(_root, ".eml-export-*.tmp").Length != 0);

        await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
            Manager(new(4, originalBytes.Length + 4L)).ExportAsync(source, destination, progress));

        Assert.False(beganWriting);
        Assert.False(File.Exists(destination));
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(source, InstanceImportManifest.FileName)));
        AssertNoTemporaryArchive();
    }

    /// <summary>重新生成清单的真实大小刚好达到额度时可备份，进度与 ZIP 未压缩总量一致。</summary>
    [Fact]
    public async Task ExactRegeneratedManifestBudgetAndProgressMatchWrittenArchive()
    {
        var (source, original, _) = CreateEmlInstance();
        var backupBytes = InstanceManifestStore.Serialize(InstanceBackupManifest.Create(source, original).Manifest);
        var total = backupBytes.Length + 4L;
        var destination = Path.Combine(_root, "manifest-exact.zip");
        var updates = new List<InstanceExportProgress>();
        var limits = new InstanceArchiveLimits(4, total);

        await Manager(limits).ExportAsync(source, destination, new InlineProgress(updates.Add));

        using var zip = ZipFile.OpenRead(destination);
        Assert.Equal(4, SafeInstanceArchive.ReadEntries(zip, CancellationToken.None, limits).Count);
        Assert.Equal(total, zip.Entries.Sum(entry => entry.Length));
        Assert.Equal(backupBytes.Length, zip.GetEntry(InstanceImportManifest.FileName)!.Length);
        Assert.Equal(total, updates[^1].TotalBytes);
        Assert.Equal(total, updates[^1].BytesCopied);
        AssertNoTemporaryArchive();
    }

    /// <summary>压缩前发现文件变长或变短时拒绝发布，不覆盖用户刚修改的源文件。</summary>
    [Theory]
    [InlineData("expanded")]
    [InlineData("")]
    public async Task SourceLengthChangedAfterPreflightDoesNotPublish(string changed)
    {
        var source = CreatePlainInstance();
        var path = Path.Combine(source, ".minecraft", "options.txt");
        var destination = Path.Combine(_root, "changed.zip");
        var modified = false;
        var progress = new InlineProgress(_ =>
        {
            if (modified || Directory.GetFiles(_root, ".eml-export-*.tmp").Length == 0) return;
            modified = true;
            File.WriteAllText(path, changed);
        });

        await Assert.ThrowsAnyAsync<IOException>(() => Manager(new(4, 4)).ExportAsync(source, destination, progress));

        Assert.True(modified);
        Assert.Equal(changed, File.ReadAllText(path));
        Assert.False(File.Exists(destination));
        AssertNoTemporaryArchive();
    }

    /// <summary>写入开始后取消必须清理临时 ZIP，保留完整源实例且不报告成功。</summary>
    [Fact]
    public async Task CancellationAfterPreflightLeavesNoPublishedBackup()
    {
        var source = CreatePlainInstance();
        var destination = Path.Combine(_root, "cancelled.zip");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(_ =>
        {
            if (Directory.GetFiles(_root, ".eml-export-*.tmp").Length != 0) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Manager(new(4, 4)).ExportAsync(source, destination, progress, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(File.Exists(destination));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(source, ".minecraft", "options.txt")));
        AssertNoTemporaryArchive();
    }

    /// <summary>目标已有备份时，即使源实例超限，也不能修改或删除旧备份。</summary>
    [Fact]
    public async Task ExistingBackupIsNeverOverwritten()
    {
        var source = CreatePlainInstance();
        var destination = Path.Combine(_root, "existing.zip");
        File.WriteAllText(destination, "existing backup bytes");

        await Assert.ThrowsAnyAsync<IOException>(() => Manager(new(1, 1)).ExportAsync(source, destination));

        Assert.Equal("existing backup bytes", File.ReadAllText(destination));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(source, ".minecraft", "options.txt")));
        AssertNoTemporaryArchive();
    }

    /// <summary>导出过程中其他操作占用了最终路径时，提交失败但保留竞争创建的文件。</summary>
    [Fact]
    public async Task DestinationCreatedDuringExportIsPreserved()
    {
        var source = CreatePlainInstance();
        var destination = Path.Combine(_root, "raced.zip");
        var created = false;
        var progress = new InlineProgress(_ =>
        {
            if (created || Directory.GetFiles(_root, ".eml-export-*.tmp").Length == 0) return;
            created = true;
            File.WriteAllText(destination, "concurrent backup");
        });

        await Assert.ThrowsAnyAsync<IOException>(() => Manager(new(4, 4)).ExportAsync(source, destination, progress));

        Assert.True(created);
        Assert.Equal("concurrent backup", File.ReadAllText(destination));
        AssertNoTemporaryArchive();
    }

    /// <summary>普通目录与原生 Prism 目录都能导出后真实导入，保留内容、空目录和中文名称。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericAndPrismBackupsRoundTripWithoutDownloads(bool prism)
    {
        var source = CreatePlainInstance();
        if (prism)
        {
            File.WriteAllText(Path.Combine(source, "mmc-pack.json"), "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}");
            File.WriteAllText(Path.Combine(source, "instance.cfg"), "[General]\nname=Original\n");
        }
        var destination = Path.Combine(_root, "roundtrip.zip");
        await Manager(new(20, 4096)).ExportAsync(source, destination);

        var restored = await new MinecraftInstanceImporter(_root).ImportAsync(destination, "恢复（中文）");

        Assert.Equal(prism ? "Prism" : "Zip", restored.Manifest.SourceFormat);
        Assert.Equal("恢复（中文）", restored.Manifest.Name);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(restored.GameDirectory, "options.txt")));
        Assert.True(Directory.Exists(Path.Combine(restored.GameDirectory, "saves", "empty")));
        if (prism) Assert.Contains("name=恢复（中文）", File.ReadAllText(Path.Combine(restored.InstancePath, "instance.cfg")));
        AssertNoTemporaryArchive();
    }

    /// <summary>即使玩家文件不同于可信基线，也允许保存备份；回导不把修改字节升级为官方或原包摘要。</summary>
    [Fact]
    public async Task ChangedPayloadIsBackedUpWithoutReplacingTrustedBaseline()
    {
        var (source, original, requirement) = CreateEmlInstance();
        File.WriteAllText(Path.Combine(source, ".minecraft", "mods", "sample.jar"), "evil");
        var backup = InstanceManifestStore.Serialize(InstanceBackupManifest.Create(source, original).Manifest);
        var destination = Path.Combine(_root, "changed-payload.zip");

        await Manager(new(4, backup.Length + 4L)).ExportAsync(source, destination);
        var restored = await new MinecraftInstanceImporter(_root).ImportAsync(destination, "Restored");

        Assert.Equal("evil", File.ReadAllText(Path.Combine(restored.GameDirectory, "mods", "sample.jar")));
        var actual = Assert.Single(restored.Manifest.Files);
        Assert.Equal(requirement.Origin, actual.Origin);
        Assert.Equal(requirement.Size, actual.Size);
        Assert.Equal(requirement.Hashes["sha256"], actual.Hashes["sha256"]);
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("evil"))), actual.Hashes["sha256"]);
        AssertNoTemporaryArchive();
    }

    /// <summary>创建三个目录和一个四字节文件，便于精确计算 ZIP 条目数量。</summary>
    private string CreatePlainInstance()
    {
        var root = Path.Combine(_root, "instances", "Original");
        Directory.CreateDirectory(Path.Combine(root, ".minecraft", "saves", "empty"));
        File.WriteAllText(Path.Combine(root, ".minecraft", "options.txt"), "keep");
        return root;
    }

    /// <summary>构造带严格包内来源的最小 EML 实例；备份会增加明确用途字段但不改变基线。</summary>
    private (string Root, InstanceImportManifest Manifest, InstanceFileRequirement Requirement) CreateEmlInstance()
    {
        var root = Path.Combine(_root, "instances", "Original");
        Directory.CreateDirectory(Path.Combine(root, ".minecraft", "mods"));
        File.WriteAllText(Path.Combine(root, ".minecraft", "mods", "sample.jar"), "good");
        var requirement = new InstanceFileRequirement
        {
            Path = ".minecraft/mods/sample.jar", ArchiveEntry = ".minecraft/mods/sample.jar",
            Source = InstanceFileSource.Embedded, Origin = InstanceFileOrigin.EmbeddedOverride,
            Size = 4, Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("good"))) }
        };
        var manifest = new InstanceImportManifest { Name = "Original", Files = [requirement] };
        File.WriteAllBytes(Path.Combine(root, InstanceImportManifest.FileName), InstanceManifestStore.Serialize(manifest));
        return (root, manifest, requirement);
    }

    /// <summary>注入仅供测试的较小额度，拒绝意外调用系统回收操作。</summary>
    private MinecraftInstanceManager Manager(InstanceArchiveLimits limits) =>
        new(_root, _ => throw new InvalidOperationException("测试不允许回收实例。"), limits);

    /// <summary>确保失败或成功都没有遗留可被误认成备份的临时 ZIP。</summary>
    private void AssertNoTemporaryArchive() => Assert.Empty(Directory.GetFiles(_root, ".eml-export-*.tmp"));

    /// <summary>同步报告测试进度，使取消、文件变化和竞争创建的时机可重复。</summary>
    private sealed class InlineProgress(Action<InstanceExportProgress> callback) : IProgress<InstanceExportProgress>
    {
        /// <summary>直接执行注入回调，不依赖 UI 调度。</summary>
        public void Report(InstanceExportProgress value) => callback(value);
    }

    /// <summary>仅清理当前测试创建的唯一临时根目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
