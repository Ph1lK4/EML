using System.Security.Cryptography;
using System.Text;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;
using EML.Core.Minecraft.Migration;
using EML.Core.Minecraft.Mods;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.External;
using EML.Infrastructure.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Tests;

/// <summary>外部启动器保留 EML 来源时使用生产复制链路，所有载荷只存在于独立临时目录。</summary>
public sealed class PreservedFolderImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-preserved-folder-" + Guid.NewGuid().ToString("N"));
    private const string ModPath = ".minecraft/mods/original.jar";
    private const string OriginalArchive = "original-pack.zip";
    private const string OriginalEntry = "overrides/mods/original.jar";
    private string SourceLauncher => Path.Combine(_root, "prism");
    private string Source => Path.Combine(SourceLauncher, "instances", "Original");
    private string Destination => Path.Combine(_root, "eml");
    private ExternalMinecraftInstance Candidate => new("Original", "Prism", Source,
        Path.Combine(Source, ".minecraft"), "1.7.10", "Forge");
    private MinecraftFolderImporter Service => new(Destination);

    /// <summary>创建可识别 Prism 组件，不启动游戏、不访问真实账号或外部网络。</summary>
    public PreservedFolderImportTests()
    {
        Directory.CreateDirectory(Destination);
        Directory.CreateDirectory(Path.Combine(Source, ".minecraft"));
        Write("mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}""");
        Write("instance.cfg", "[General]\nname=Original\nJavaPath=C:\\private\\java.exe\nPreLaunchCommand=private-hook\n");
    }

    /// <summary>原包与官方摘要独立保留；即使字节已经损坏，复制也不能把它重新认证为可信内容。</summary>
    [Theory]
    [InlineData("pack", true)]
    [InlineData("evil", false)]
    public async Task EmbeddedOverrideKeepsOriginalIntegrityAndOfficialMetadata(string bytes, bool valid)
    {
        var baseline = Embedded(ModPath, "pack") with
        {
            CurseForgeOrigin = new() { ProjectId = 10, FileId = 20, Size = 4,
                Hashes = new Dictionary<string, string> { ["sha1"] = Sha1("good") } }
        };
        await SaveAsync(Manifest(baseline));
        Write(ModPath, bytes);
        var sourceManifest = File.ReadAllBytes(Path.Combine(Source, InstanceImportManifest.FileName));

        var result = await Service.ImportAsync(Candidate, "副本（保留来源）");

        var copied = Assert.Single(result.Manifest.Files);
        Assert.Equal(InstanceFileOrigin.EmbeddedOverride, copied.Origin);
        Assert.Equal(InstanceFileSource.Embedded, copied.Source);
        Assert.Equal(OriginalArchive, result.Manifest.SourceArchive);
        Assert.Equal(OriginalEntry, copied.ArchiveEntry);
        Assert.Equal(4, copied.Size);
        Assert.Equal(Sha256("pack"), Assert.Single(copied.Hashes).Value);
        Assert.Equal("sha256", Assert.Single(copied.Hashes).Key);
        Assert.Equal(10, copied.CurseForgeOrigin!.ProjectId);
        Assert.Equal(20, copied.CurseForgeOrigin.FileId);
        Assert.Equal(Sha1("good"), copied.CurseForgeOrigin.Hashes["sha1"]);
        Assert.Equal(sourceManifest, File.ReadAllBytes(Path.Combine(Source, InstanceImportManifest.FileName)));
        Assert.Equal(bytes, File.ReadAllText(Path.Combine(Source, ModPath)));
        Assert.Equal(bytes, File.ReadAllText(Path.Combine(result.InstancePath, ModPath)));
        if (valid)
            Assert.True(await InstanceRuntimeInstaller.ExistingFileMatchesAsync(Path.Combine(result.InstancePath, ModPath), copied, default));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => InstanceRuntimeInstaller.ExistingFileMatchesAsync(
                Path.Combine(result.InstancePath, ModPath), copied, default));
        var report = await new MinecraftInstanceChecker(Destination).CheckAsync(result.InstancePath);
        Assert.Equal(valid ? InstanceFileState.Verified : InstanceFileState.HashMismatch, Assert.Single(report.Files).State);
        Assert.Empty(Directory.EnumerateFiles(Destination, "*.zip", SearchOption.AllDirectories));
    }

    /// <summary>普通官方文件继续受官方 SHA-1 约束，目录复制不为其添加本地 SHA-256。</summary>
    [Fact]
    public async Task OfficialRequirementIsNotRebaselinedFromCorruptBytes()
    {
        var baseline = new InstanceFileRequirement
        {
            Path = ModPath, Source = InstanceFileSource.CurseForge, Origin = InstanceFileOrigin.CurseForgeDownload,
            ProjectId = 10, FileId = 20, Size = 4,
            Hashes = new Dictionary<string, string> { ["sha1"] = Sha1("good") },
            Downloads = ["https://edge.forgecdn.net/files/20/original.jar"]
        };
        await SaveAsync(Manifest(baseline));
        Write(ModPath, "evil");

        var result = await Service.ImportAsync(Candidate, "copy");

        var copied = Assert.Single(result.Manifest.Files);
        Assert.Equal(baseline.Origin, copied.Origin);
        Assert.Equal(baseline.Source, copied.Source);
        Assert.Equal(baseline.Downloads, copied.Downloads);
        Assert.Equal("sha1", Assert.Single(copied.Hashes).Key);
        Assert.Equal(Sha1("good"), copied.Hashes["sha1"]);
        await Assert.ThrowsAsync<InvalidDataException>(() => InstanceRuntimeInstaller.ExistingFileMatchesAsync(
            Path.Combine(result.InstancePath, ModPath), copied, default));
    }

    /// <summary>明确接受的替换折叠成可移植来源；不复制绑定旧实例的本机状态。</summary>
    [Fact]
    public async Task ExplicitLocalReplacementIsRecognizedAfterCopy()
    {
        await SaveAsync(Manifest(Embedded(ModPath, "pack")));
        Write(ModPath, "chosen by user");
        await new InstanceLocalReplacementService(SourceLauncher).AcceptExistingAsync(Source, ModPath);

        var result = await Service.ImportAsync(Candidate, "copy");

        var copied = Assert.Single(result.Manifest.Files);
        Assert.Equal(InstanceFileOrigin.UserLocalImport, copied.Origin);
        Assert.Equal(Sha256("chosen by user"), copied.Hashes["sha256"]);
        Assert.Null(copied.ArchiveEntry);
        Assert.False(Directory.Exists(Path.Combine(result.InstancePath, ".eml-runtime")));
        Assert.True(File.Exists(Path.Combine(Source, InstanceLocalReplacementService.RelativePath)));
        var report = await new MinecraftInstanceChecker(Destination).CheckAsync(result.InstancePath);
        Assert.True(report.ContentReady);
        Assert.Equal(InstanceFileState.Verified, Assert.Single(report.Files).State);
    }

    /// <summary>启用、禁用和移除记录均独立携带，不能把受管模组再次加入原包文件列表。</summary>
    [Theory]
    [InlineData(LocalModState.Enabled, false)]
    [InlineData(LocalModState.Disabled, false)]
    [InlineData(LocalModState.Removed, false)]
    [InlineData(LocalModState.Enabled, true)]
    [InlineData(LocalModState.Disabled, true)]
    [InlineData(LocalModState.Removed, true)]
    public async Task ManagedModStateSurvivesWithoutDuplicateRequirements(LocalModState state, bool localState)
    {
        var record = new ManagedModRecord
        {
            Id = Guid.NewGuid().ToString("N"), Added = true, State = state,
            Baseline = Online(".minecraft/mods/additional.jar", "official additional mod")
        };
        var manifest = Manifest() with { ManagedMods = localState ? null : [record] };
        await SaveAsync(manifest);
        if (localState) ModManagementStore.Write(Source, manifest, [record]);
        var activePath = ModManagementStore.CurrentPath(record);
        if (activePath is not null) Write(activePath, "official additional mod");
        else Write(ModManagementStore.TrashPath(record), "official additional mod");

        var result = await Service.ImportAsync(Candidate, "copy");

        Assert.Empty(result.Manifest.Files);
        var restored = Assert.Single(result.Manifest.ManagedMods!);
        Assert.Equal(record.Id, restored.Id);
        Assert.Equal(state, restored.State);
        Assert.Equal(InstanceFileOrigin.ModrinthDownload, restored.Baseline.Origin);
        Assert.Equal(record.Baseline.Hashes["sha1"], restored.Baseline.Hashes["sha1"]);
        Assert.False(Directory.Exists(Path.Combine(result.InstancePath, ".eml-runtime")));
        var effective = InstanceEffectiveRequirements.Build(result.InstancePath, result.Manifest).Requirements;
        if (activePath is null) Assert.Empty(effective);
        else
        {
            Assert.Equal(activePath, Assert.Single(effective).Path);
            Assert.Equal("official additional mod", File.ReadAllText(Path.Combine(result.InstancePath, activePath)));
        }
    }

    /// <summary>迁移用途和资源包官方来源各自保留，不因现有载荷被重复登记为本地文件。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationAndAddedContentKeepTheirIndependentRecords(bool localState)
    {
        const string mapPath = ".minecraft/journeymap/data/server/map.json";
        var migrated = new MigratedPlayerFile(mapPath, 8, Sha256("old map!"), Guid.NewGuid().ToString("N"));
        var resource = new AddedContentFile(GtnhAddonKind.ResourcePack, Online(".minecraft/resourcepacks/extra.zip", "resource"));
        var manifest = Manifest() with
        {
            MigratedPlayerFiles = localState ? null : [migrated],
            AddedContentFiles = localState ? null : [resource]
        };
        await SaveAsync(manifest);
        if (localState)
        {
            MigrationIO.WriteJson(Path.Combine(Source, MigrationDataStore.RelativePath), new MigrationDataStore.State(1,
                EmbeddedFileBaseline.ManifestFingerprint(manifest), [migrated]));
            AddedContentStore.Write(Source, manifest, [resource]);
        }
        Write(mapPath, "newer map changed during gameplay");
        Write(resource.Baseline.Path!, "resource");

        var result = await Service.ImportAsync(Candidate, "copy");

        Assert.Empty(result.Manifest.Files);
        Assert.Equal(migrated, Assert.Single(result.Manifest.MigratedPlayerFiles!));
        var savedResource = Assert.Single(result.Manifest.AddedContentFiles!);
        Assert.Equal(InstanceFileOrigin.ModrinthDownload, savedResource.Baseline.Origin);
        Assert.Equal(resource.Baseline.Hashes["sha1"], savedResource.Baseline.Hashes["sha1"]);
        Assert.False(Directory.Exists(Path.Combine(result.InstancePath, ".eml-runtime")));
        var report = await new MinecraftInstanceChecker(Destination).CheckAsync(result.InstancePath);
        Assert.True(report.ContentReady, string.Join(Environment.NewLine, report.ContentIssues));
        Assert.Equal(2, report.Files.Count);
    }

    /// <summary>缺失原包文件继续保留恢复依据，不能因复制时不存在而从清单消失。</summary>
    [Fact]
    public async Task MissingEmbeddedRequirementRemainsMissingWithOriginalArchiveMapping()
    {
        await SaveAsync(Manifest(Embedded(ModPath, "pack")));

        var result = await Service.ImportAsync(Candidate, "copy");

        var copied = Assert.Single(result.Manifest.Files);
        Assert.Equal(OriginalEntry, copied.ArchiveEntry);
        Assert.Equal(OriginalArchive, result.Manifest.SourceArchive);
        Assert.Null(InstanceImportSourceStore.Read(result.InstancePath, result.Manifest));
        var report = await new MinecraftInstanceChecker(Destination).CheckAsync(result.InstancePath);
        Assert.Equal(InstanceFileState.MissingEmbeddedFile, Assert.Single(report.Files).State);
        Assert.False(report.ContentReady);
    }

    /// <summary>未知载荷原样保留，但不会被提升为覆盖官方或用户既有声明的新来源。</summary>
    [Fact]
    public async Task UnlistedFilesAreCopiedWithoutInventingRequirements()
    {
        await SaveAsync(Manifest());
        Write(".minecraft/mods/unlisted.jar", "unlisted mod");

        var result = await Service.ImportAsync(Candidate, "copy");

        Assert.Empty(result.Manifest.Files);
        Assert.Equal("unlisted mod", File.ReadAllText(Path.Combine(result.GameDirectory, "mods", "unlisted.jar")));
    }

    /// <summary>旧 Prism 名称配置沿用允许变化策略，重建安全内容时不改写其原始基线。</summary>
    [Fact]
    public async Task MutablePrismConfigurationIsSanitizedWithoutRebaselining()
    {
        const string original = "[General]\nname=Pack\n";
        var baseline = Embedded("instance.cfg", original) with { Origin = null, ArchiveEntry = "instance.cfg" };
        await SaveAsync(Manifest(baseline));

        var result = await Service.ImportAsync(Candidate, "新名称");

        var config = File.ReadAllText(Path.Combine(result.InstancePath, "instance.cfg"));
        Assert.Contains("name=新名称", config);
        Assert.DoesNotContain("private", config);
        Assert.DoesNotContain("JavaPath", config);
        Assert.DoesNotContain("PreLaunchCommand", config);
        Assert.Equal(Sha256(original), Assert.Single(result.Manifest.Files).Hashes["sha256"]);
        Assert.True((await new MinecraftInstanceChecker(Destination).CheckAsync(result.InstancePath)).ContentReady);
    }

    /// <summary>独立严格来源的名称配置不能在清除启动钩子时被隐式改写。</summary>
    [Fact]
    public async Task StrictPrismConfigurationRejectsImplicitRewriting()
    {
        await SaveAsync(Manifest(Embedded("instance.cfg", "strict configuration")));

        await Assert.ThrowsAsync<InvalidDataException>(() => Service.ImportAsync(Candidate, "copy"));

        AssertNoCopy();
        Assert.Contains("private-hook", File.ReadAllText(Path.Combine(Source, "instance.cfg")));
    }

    /// <summary>目录复制之后再做 ZIP 备份，仍保留原始摘要，并仅在新备份中重定位恢复条目。</summary>
    [Fact]
    public async Task CopiedSourceSurvivesBackupRoundTripWithoutHashChanges()
    {
        await SaveAsync(Manifest(Embedded(ModPath, "pack")));
        Write(ModPath, "pack");
        var copied = await Service.ImportAsync(Candidate, "copy");
        var archive = Path.Combine(_root, "backup.zip");

        await new MinecraftInstanceManager(Destination).ExportAsync(copied.InstancePath, archive);
        var restored = await new MinecraftInstanceImporter(Destination).ImportAsync(archive, "restored");

        var requirement = Assert.Single(restored.Manifest.Files, file => file.Path == ModPath);
        Assert.Equal(InstanceFileOrigin.EmbeddedOverride, requirement.Origin);
        Assert.Equal(Sha256("pack"), requirement.Hashes["sha256"]);
        Assert.Equal(ModPath, requirement.ArchiveEntry);
        Assert.Equal(OriginalEntry, Assert.Single(copied.Manifest.Files).ArchiveEntry);
        Assert.True((await new MinecraftInstanceChecker(Destination).CheckAsync(restored.InstancePath)).ContentReady);
    }

    /// <summary>组件已经在 Prism 改版时明确拒绝旧清单，不能继续沿用旧来源伪称版本一致。</summary>
    [Fact]
    public async Task ConflictingPrismVersionIsRejectedBeforeCopy()
    {
        await SaveAsync(Manifest() with { Components = [new("net.minecraft", "1.12.2")], MinecraftVersion = "1.12.2" });

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service.ImportAsync(Candidate, "copy"));

        Assert.Contains("不一致", error.Message);
        AssertNoCopy();
    }

    /// <summary>复制完成前修改模组选择时拒绝发布，不因文件字节相同忽略来源状态变化。</summary>
    [Fact]
    public async Task ManagedStateMutationBeforeCommitRejectsCopy()
    {
        var manifest = Manifest();
        await SaveAsync(manifest);
        var record = new ManagedModRecord { Id = Guid.NewGuid().ToString("N"), Added = true, State = LocalModState.Disabled,
            Baseline = Online(".minecraft/mods/additional.jar", "content") };
        ModManagementStore.Write(Source, manifest, [record]);
        Write(ModManagementStore.CurrentPath(record)!, "content");
        var progress = new ImmediateProgress(value =>
        {
            if (value.FileName == "正在确认源文件未变化")
                ModManagementStore.Write(Source, manifest, [record with { State = LocalModState.Removed }]);
        });

        await Assert.ThrowsAsync<IOException>(() => Service.ImportAsync(Candidate, "copy", progress));

        AssertNoCopy();
    }

    /// <summary>凭证和机器缓存即使存在也不读取；安全状态由原清单和独立来源记录生成。</summary>
    [Fact]
    public async Task CredentialsAndDerivedFilesAreNotCopied()
    {
        await SaveAsync(Manifest());
        var credentials = Write("accounts.json", "secret sentinel");
        Write(".eml-runtime/cache/private.json", "private cache");
        using var locked = new FileStream(credentials, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await Service.ImportAsync(Candidate, "copy");

        Assert.False(File.Exists(Path.Combine(result.InstancePath, "accounts.json")));
        Assert.False(Directory.Exists(Path.Combine(result.InstancePath, ".eml-runtime")));
        Assert.Empty(result.Manifest.Files);
    }

    /// <summary>创建与真实 Prism 组件一致的最小 EML 声明。</summary>
    private static InstanceImportManifest Manifest(params InstanceFileRequirement[] files) => new()
    {
        Name = "Original", SourceFormat = "Prism", SourceArchive = OriginalArchive, GameDirectory = ".minecraft",
        MinecraftVersion = "1.7.10", Components = [new("net.minecraft", "1.7.10")], Files = files
    };

    /// <summary>按生产格式持久化来源，供读取器和状态指纹共同使用。</summary>
    private Task SaveAsync(InstanceImportManifest manifest) =>
        InstanceManifestStore.WriteAsync(Path.Combine(Source, InstanceImportManifest.FileName), manifest, default);

    /// <summary>构造独立的包内摘要，归档条目刻意与目标路径不同。</summary>
    private static InstanceFileRequirement Embedded(string path, string content) => new()
    {
        Path = path, Source = InstanceFileSource.Embedded, Origin = InstanceFileOrigin.EmbeddedOverride,
        Size = Encoding.UTF8.GetByteCount(content), Hashes = new Dictionary<string, string> { ["sha256"] = Sha256(content) },
        ArchiveEntry = OriginalEntry
    };

    /// <summary>构造测试官方 Modrinth 身份，避免使用任何真实远端请求。</summary>
    private static InstanceFileRequirement Online(string path, string content) => new()
    {
        Path = path, Source = InstanceFileSource.DirectDownload, Origin = InstanceFileOrigin.ModrinthDownload,
        Size = Encoding.UTF8.GetByteCount(content), Hashes = new Dictionary<string, string> { ["sha1"] = Sha1(content) },
        Downloads = ["https://cdn.modrinth.com/data/test/versions/v1/" + Path.GetFileName(path)],
        ModrinthOrigin = new() { ProjectId = "test", VersionId = "v1", FileName = Path.GetFileName(path) }
    };

    /// <summary>只写当前测试创建的源目录，返回路径供独占锁断言使用。</summary>
    private string Write(string relative, string content)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>失败或取消后不能留下可被识别的半成品实例。</summary>
    private void AssertNoCopy()
    {
        foreach (var folder in new[] { "instances", ".eml-imports" })
        {
            var path = Path.Combine(Destination, folder);
            if (Directory.Exists(path)) Assert.Empty(Directory.EnumerateFileSystemEntries(path));
        }
    }

    /// <summary>为原始来源生成确定的 SHA-256。</summary>
    private static string Sha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>为平台来源生成确定的 SHA-1。</summary>
    private static string Sha1(string content) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>仅删除本测试创建的随机临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>同步触发测试回调，精确覆盖提交前的状态变化窗口。</summary>
    private sealed class ImmediateProgress(Action<InstanceImportProgress> action) : IProgress<InstanceImportProgress>
    {
        /// <summary>不调度后台回调，确保测试修改发生在指定复制阶段。</summary>
        public void Report(InstanceImportProgress value) => action(value);
    }
}
