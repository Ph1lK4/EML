using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Tests;

/// <summary>验证导入与导出复核共用的计划检查，不访问真实实例、远端平台或账号数据。</summary>
public sealed class InstanceImportPlanValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-import-plan-" + Guid.NewGuid().ToString("N"));

    /// <summary>只清除根目录的派生状态，保留游戏目录中同名的用户文件。</summary>
    [Fact]
    public void PrepareRemovesOnlyRootDerivedState()
    {
        using var archive = OpenMemoryArchive((".eml-runtime/state.json", "derived"),
            (".minecraft/.eml-runtime/state.json", "user"));
        var original = new InstanceArchivePlan(new InstanceImportManifest
        {
            Files = archive.Entries.Select(entry => Requirement(entry.FullName, "baseline")).ToArray()
        }, archive.Entries.Select(entry => new InstanceArchiveCopy(entry, entry.FullName, false)).ToArray());

        var prepared = InstanceImportPlanValidator.Prepare(original);

        Assert.Equal(".minecraft/.eml-runtime/state.json", Assert.Single(prepared.Copies).TargetPath);
        Assert.Equal(".minecraft/.eml-runtime/state.json", Assert.Single(prepared.Manifest.Files).Path);
        Assert.Equal(2, original.Copies.Count);
        Assert.Equal(2, original.Manifest.Files.Count);
    }

    /// <summary>已下载文件和待下载需求必须使用同一布局检查，不能将文件同时作为父目录。</summary>
    [Fact]
    public void PrepareRejectsCopyAndMissingRequirementLayoutConflict()
    {
        using var archive = OpenMemoryArchive((".minecraft/mods", "file instead of directory"));
        var plan = new InstanceArchivePlan(new InstanceImportManifest
        {
            Files = [Requirement(".minecraft/mods/test.jar", "expected")]
        }, [new InstanceArchiveCopy(archive.Entries[0], ".minecraft/mods", false)]);

        Assert.Throws<InvalidDataException>(() => InstanceImportPlanValidator.Prepare(plan));
    }

    /// <summary>备份保留官方摘要、包内基线和用户主动替换，仅刷新可恢复的包内条目位置。</summary>
    [Fact]
    public void BuildManifestPreservesTrustWithoutAuthorizingUnknownCopiedFiles()
    {
        var official = new InstanceFileRequirement
        {
            Path = ".minecraft/mods/official.jar", Source = InstanceFileSource.CurseForge,
            Origin = InstanceFileOrigin.CurseForgeDownload, ProjectId = 12, FileId = 34, Size = 4,
            Hashes = new Dictionary<string, string> { ["sha1"] = new('a', 40) }
        };
        var embedded = Requirement(".minecraft/mods/embedded.jar", "original") with
        {
            Origin = InstanceFileOrigin.EmbeddedOverride, ArchiveEntry = "overrides/mods/embedded.jar"
        };
        var accepted = Requirement(".minecraft/mods/accepted.jar", "accepted") with
        {
            Origin = InstanceFileOrigin.UserLocalImport, ArchiveEntry = null
        };
        var plan = new InstanceArchivePlan(new InstanceImportManifest
        {
            Name = "Original", Files = [official, embedded, accepted]
        }, [], PreserveRemoteRequirements: true);
        var copied = new[] { official.Path!, embedded.Path!, accepted.Path!, ".minecraft/mods/unknown.jar" }
            .ToDictionary(path => path, path => Requirement(path, "changed") with { ArchiveEntry = path },
                StringComparer.OrdinalIgnoreCase);

        var restored = InstanceImportPlanValidator.BuildManifest(plan, copied, "Restored", "backup.zip");

        Assert.Equal("Restored", restored.Name);
        Assert.Equal("backup.zip", restored.SourceArchive);
        Assert.Equal("NotChecked", restored.IntegrityStatus);
        Assert.Equal(3, restored.Files.Count);
        Assert.Same(official, restored.Files[0]);
        Assert.Equal(embedded.Hashes["sha256"], restored.Files[1].Hashes["sha256"]);
        Assert.Equal(embedded.Size, restored.Files[1].Size);
        Assert.Equal(embedded.Path, restored.Files[1].ArchiveEntry);
        Assert.Same(accepted, restored.Files[2]);
        Assert.DoesNotContain(restored.Files, file => file.Path == ".minecraft/mods/unknown.jar");
        Assert.Equal("overrides/mods/embedded.jar", embedded.ArchiveEntry);
    }

    /// <summary>首次导入仍以已提取的覆盖文件建立基线，不把被覆盖来源的摘要合并进去。</summary>
    [Fact]
    public void BuildManifestForNewImportKeepsOneBaselinePerCopiedPath()
    {
        var remote = new InstanceFileRequirement
        {
            Path = ".minecraft/mods/test.jar", Source = InstanceFileSource.DirectDownload,
            Downloads = ["https://example.invalid/test.jar"], Hashes = new Dictionary<string, string> { ["sha1"] = new('a', 40) }
        };
        var embedded = Requirement(remote.Path!, "pack") with { ArchiveEntry = "overrides/mods/test.jar" };
        var plan = new InstanceArchivePlan(new InstanceImportManifest { Files = [remote] }, []);

        var manifest = InstanceImportPlanValidator.BuildManifest(plan,
            new Dictionary<string, InstanceFileRequirement>(StringComparer.OrdinalIgnoreCase) { [remote.Path!] = embedded },
            "Imported", "pack.zip");

        Assert.Same(embedded, Assert.Single(manifest.Files));
        Assert.Single(manifest.Files[0].Hashes);
        Assert.True(manifest.Files[0].Hashes.ContainsKey("sha256"));
        Assert.True(remote.Hashes.ContainsKey("sha1"));
    }

    /// <summary>清单大小预测只使用临时占位摘要，不改写来源清单或将当前文件重新授权。</summary>
    [Fact]
    public void MetadataPreflightDoesNotMutateExistingBaselines()
    {
        using var archive = OpenMemoryArchive((".minecraft/mods/test.jar", "changed"));
        var requirement = Requirement(".minecraft/mods/test.jar", "original") with
        {
            Origin = InstanceFileOrigin.EmbeddedOverride, ArchiveEntry = "overrides/mods/test.jar"
        };
        var plan = new InstanceArchivePlan(new InstanceImportManifest
        {
            Name = "Original", SourceArchive = "backup.zip", Files = [requirement], Notes = ["original note"]
        }, [new InstanceArchiveCopy(archive.Entries[0], requirement.Path!, false)], PreserveRemoteRequirements: true);
        var originalBytes = InstanceManifestStore.Serialize(plan.Manifest);

        InstanceImportPlanValidator.ValidateRestoreMetadata(plan, "Restored");

        Assert.Equal(originalBytes, InstanceManifestStore.Serialize(plan.Manifest));
        Assert.Equal(Sha256("original"), requirement.Hashes["sha256"]);
    }

    /// <summary>Prism 首次导入缺少配置时生成名称文件，实际保存的摘要不是预测用占位值。</summary>
    [Fact]
    public async Task PrismImportWithoutConfigurationRecordsGeneratedRealBytes()
    {
        var path = CreateArchive(("mmc-pack.json", "{\"formatVersion\":1,\"components\":[]}"));
        var importer = new MinecraftInstanceImporter(_root);
        var preview = await importer.InspectAsync(path);

        var result = await importer.ImportAsync(path, "中文新实例");

        Assert.Equal("Prism", preview.Format);
        var config = Assert.Single(result.Manifest.Files, file => file.Path == "instance.cfg");
        var bytes = File.ReadAllBytes(Path.Combine(result.InstancePath, "instance.cfg"));
        Assert.Contains("name=中文新实例", Encoding.UTF8.GetString(bytes));
        Assert.Equal(bytes.LongLength, config.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), config.Hashes["sha256"]);
        Assert.NotEqual(new string('0', 64), config.Hashes["sha256"]);
        Assert.Null(config.ArchiveEntry);
    }

    /// <summary>恢复缺少配置记录的旧备份时，保留原配置内容并为生成后的文件记录真实摘要。</summary>
    [Fact]
    public async Task PrismBackupWithUntrackedConfigurationUsesActualGeneratedHash()
    {
        var manifest = new InstanceImportManifest { SourceFormat = "Prism", Name = "Old", Files = [] };
        var path = CreateArchive((InstanceImportManifest.FileName, Encoding.UTF8.GetString(InstanceManifestStore.Serialize(manifest))),
            ("instance.cfg", "[General]\nname=Old\nCustomSetting=值\n"));

        var result = await new MinecraftInstanceImporter(_root).ImportAsync(path, "Restored");

        var config = Assert.Single(result.Manifest.Files);
        var content = File.ReadAllText(Path.Combine(result.InstancePath, "instance.cfg"));
        Assert.Contains("CustomSetting=值", content);
        Assert.Contains("name=Restored", content);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), config.Size);
        Assert.Equal(Sha256(content), config.Hashes["sha256"]);
        Assert.Null(config.ArchiveEntry);
    }

    /// <summary>只读保留的严格配置不受名称编辑大小上限影响，复核不会隐式读取或改写它。</summary>
    [Fact]
    public void MetadataPreflightAllowsLargeStrictPrismConfiguration()
    {
        var content = new string('x', 1024 * 1024 + 1);
        using var archive = OpenMemoryArchive(("instance.cfg", content));
        var requirement = Requirement("instance.cfg", content) with { Origin = InstanceFileOrigin.EmbeddedOverride };
        var plan = new InstanceArchivePlan(new InstanceImportManifest
        {
            SourceFormat = "Prism", Name = "Protected", Files = [requirement]
        }, [new InstanceArchiveCopy(archive.Entries[0], "instance.cfg", false)], PreserveRemoteRequirements: true);

        InstanceImportPlanValidator.ValidateRestoreMetadata(plan);

        Assert.Same(requirement, Assert.Single(plan.Manifest.Files));
    }

    /// <summary>会进行名称编辑的过大配置在提取前拒绝，不留下未完成的实例或临时目录。</summary>
    [Fact]
    public async Task OversizedMutablePrismConfigurationFailsBeforeExtraction()
    {
        var path = CreateArchive(("mmc-pack.json", "{\"formatVersion\":1,\"components\":[]}"),
            ("instance.cfg", new string('x', 1024 * 1024 + 1)));
        var importer = new MinecraftInstanceImporter(_root);

        await Assert.ThrowsAsync<InvalidDataException>(() => importer.InspectAsync(path));
        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(path, "Too large"));

        Assert.False(Directory.Exists(Path.Combine(_root, ".eml-imports")));
        Assert.False(Directory.Exists(Path.Combine(_root, "instances")));
    }

    /// <summary>恢复新生成配置后会增加一个需求，因此原清单正好达到上限时必须提前拒绝。</summary>
    [Fact]
    public void GeneratedPrismConfigurationCountsTowardManifestLimit()
    {
        var requirement = new InstanceFileRequirement { Source = InstanceFileSource.CurseForge, ProjectId = 1, FileId = 2 };
        var plan = new InstanceArchivePlan(new InstanceImportManifest
        {
            SourceFormat = "Prism", Name = "Full manifest",
            Files = Enumerable.Repeat(requirement, InstanceArchiveLimits.MaximumFileRequirements).ToArray()
        }, [], PreserveRemoteRequirements: true);
        InstanceManifestStore.Validate(plan.Manifest);

        Assert.Throws<InvalidDataException>(() => InstanceImportPlanValidator.ValidateRestoreMetadata(plan));

        Assert.Equal(InstanceArchiveLimits.MaximumFileRequirements, plan.Manifest.Files.Count);
    }

    /// <summary>清单预检遵守取消，不产生任何文件系统副作用。</summary>
    [Fact]
    public void MetadataPreflightHonorsCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var plan = new InstanceArchivePlan(new InstanceImportManifest { Name = "Cancelled" }, []);

        Assert.Throws<OperationCanceledException>(() =>
            InstanceImportPlanValidator.ValidateRestoreMetadata(plan, token: cancelled.Token));

        Assert.False(Directory.Exists(_root));
    }

    /// <summary>生成独立测试 ZIP，使用真实导入器读取，禁止覆盖已有归档。</summary>
    private string CreateArchive(params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var output = new FileStream(path, FileMode.CreateNew);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        WriteEntries(archive, files);
        return path;
    }

    /// <summary>构造只在内存存在的可读 ZIP，归档释放时一并关闭底层缓冲。</summary>
    private static ZipArchive OpenMemoryArchive(params (string Path, string Content)[] files)
    {
        var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true)) WriteEntries(archive, files);
        memory.Position = 0;
        return new ZipArchive(memory, ZipArchiveMode.Read);
    }

    /// <summary>用无 BOM UTF-8 写入测试条目，使声明长度与摘要可精确断言。</summary>
    private static void WriteEntries(ZipArchive archive, IEnumerable<(string Path, string Content)> files)
    {
        foreach (var (path, content) in files)
        {
            using var output = archive.CreateEntry(path).Open();
            output.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    /// <summary>创建带确定大小和 SHA-256 的包内来源记录。</summary>
    private static InstanceFileRequirement Requirement(string path, string content) => new()
    {
        Path = path, Source = InstanceFileSource.Embedded, Size = Encoding.UTF8.GetByteCount(content),
        Hashes = new Dictionary<string, string> { ["sha256"] = Sha256(content) }
    };

    /// <summary>计算测试字符串的无 BOM UTF-8 摘要。</summary>
    private static string Sha256(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    /// <summary>仅清理本测试创建的唯一临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
