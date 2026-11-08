using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Launching;
using EML.Core.Minecraft.Migration;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Launching;
using EML.Infrastructure.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Prism;

namespace EML.Tests;

public sealed partial class GtnhMigrationTests
{
    /// <summary>迁移取代先前已接受的个人文件，而迁移后新确认的替换仍获严格校验并随备份保留。</summary>
    [Fact]
    public async Task ExplicitReplacementAfterMigrationTakesPrecedenceAndSurvivesBackup()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "servers.dat", "migrated"); Write(target.Game, "servers.dat", "initial");
        var manifestPath = Path.Combine(target.Path, InstanceImportManifest.FileName);
        var manifest = InstanceManifestStore.Read(manifestPath) with { Files = [new()
        {
            Path = ".minecraft/servers.dat", Source = InstanceFileSource.Embedded, Size = 7,
            Hashes = new Dictionary<string, string> { ["sha256"] = Hash("initial") }, ArchiveEntry = "overrides/servers.dat"
        }] };
        await InstanceManifestStore.WriteAsync(manifestPath, manifest, default);
        var replacements = new InstanceLocalReplacementService(_root);
        Write(target.Game, "servers.dat", "previous chosen"); await replacements.AcceptExistingAsync(target.Path, ".minecraft/servers.dat");
        var migration = await Service.MigrateAsync(await PlanAsync(old, target, "servers"), true);
        Assert.Equal("Completed", migration.Status);
        Assert.Equal(InstanceFileOrigin.MigratedPlayerData, Assert.Single(InstanceEffectiveRequirements.Build(target.Path, manifest).Requirements).Origin);
        Write(target.Game, "servers.dat", "later chosen"); await replacements.AcceptExistingAsync(target.Path, ".minecraft/servers.dat");
        Assert.Equal("RecoveryRequired", (await Service.RestoreAsync(target.Path, migration.Id)).Status);
        Assert.Equal("later chosen", Read(target.Game, "servers.dat"));
        var selected = Assert.Single(InstanceEffectiveRequirements.Build(target.Path, manifest).Requirements);
        Assert.Equal(InstanceFileOrigin.UserLocalImport, selected.Origin);
        Assert.Equal(Hash("later chosen"), selected.Hashes["sha256"]);
        var zip = Path.Combine(_root, "explicit-backup.zip"); await new MinecraftInstanceManager(_root).ExportAsync(target.Path, zip);
        var restored = await new MinecraftInstanceImporter(_root).ImportAsync(zip, "Restored");
        Assert.Equal(InstanceFileOrigin.UserLocalImport, Assert.Single(InstanceEffectiveRequirements.Build(restored.InstancePath, restored.Manifest).Requirements,
            file => file.Path == ".minecraft/servers.dat").Origin);
        Write(restored.GameDirectory, "servers.dat", "changed again");
        await Assert.ThrowsAsync<InvalidDataException>(() => InstanceContentVerifier.CheckExistingAsync(restored.InstancePath, restored.Manifest, selected, default));
    }

    /// <summary>迁入后正常变化及主动删除不再触发旧包恢复，原始官方哈希仍在清单中。</summary>
    [Fact]
    public async Task MigratedPlayerFilesUseMutablePolicyWithoutLosingBaseline()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "servers.dat", "personal"); Write(target.Game, "servers.dat", "official");
        var baseline = await AddOfficialBaselineAsync(target, "servers.dat", "official");
        await Service.MigrateAsync(await PlanAsync(old, target, "servers"), true);
        Write(target.Game, "servers.dat", "changed after playing");
        var manifest = InstanceManifestStore.Read(Path.Combine(target.Path, InstanceImportManifest.FileName));
        Assert.Equal(baseline.Hashes["sha1"], Assert.Single(manifest.Files).Hashes["sha1"]);
        var snapshot = InstanceEffectiveRequirements.Build(target.Path, manifest);
        var file = Assert.Single(snapshot.Requirements);
        Assert.Equal(InstanceFileOrigin.MigratedPlayerData, file.Origin);
        Assert.False(file.Required);
        Assert.Single(file.Hashes); Assert.Equal(Hash("personal"), file.Hashes["sha256"]);
        Assert.Equal(InstanceFileState.ChangedSinceImport, await InstanceContentVerifier.CheckExistingAsync(target.Path, manifest, file, default));
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(target.Path);
        var checkedFile = Assert.Single(report.Files, f => f.Requirement.Path == file.Path);
        Assert.False(InstanceFileStatePolicy.IsBlocking(checkedFile)); Assert.True(checkedFile.IsAllowedConfigurationChange);
        Assert.Empty(report.ContentIssues);
        File.Delete(Path.Combine(target.Game, "servers.dat"));
        Assert.Equal(InstanceFileState.Consumed, await InstanceContentVerifier.CheckExistingAsync(target.Path, manifest, file, default));
        Assert.Empty(await InstanceContentVerifier.VerifyRequiredAsync(target.Path, manifest, snapshot.Requirements, default));
    }

    /// <summary>仅同名而未经迁移选择的官方文件仍严格校验，不能因玩家路径放宽。</summary>
    [Fact]
    public async Task NonMigratedOfficialFilesStayStrict()
    {
        var target = await CreateAsync("new");
        var official = await AddOfficialBaselineAsync(target, "servers.dat", "official");
        Write(target.Game, "servers.dat", "modified");
        var manifest = InstanceManifestStore.Read(Path.Combine(target.Path, InstanceImportManifest.FileName));
        Assert.False(InstanceFileChangePolicy.IsMutableFile(official, manifest.GameDirectory));
        await Assert.ThrowsAsync<InvalidDataException>(() => InstanceContentVerifier.CheckExistingAsync(target.Path, manifest, official, default));
    }

    /// <summary>生产备份与重导入保留官方要求及独立玩家来源，不携带本机事务备份。</summary>
    [Fact]
    public async Task ExportImportPreservesPlayerPolicyAndOfficialIntegrity()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "servers.dat", "personal"); Write(target.Game, "servers.dat", "official");
        var official = await AddOfficialBaselineAsync(target, "servers.dat", "official");
        await Service.MigrateAsync(await PlanAsync(old, target, "servers"), true);
        Write(target.Game, "servers.dat", "progress after migration");
        var zip = Path.Combine(_root, "backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(target.Path, zip);
        var imported = await new MinecraftInstanceImporter(_root).ImportAsync(zip, "Restored");
        Assert.Equal("progress after migration", Read(imported.GameDirectory, "servers.dat"));
        var saved = Assert.Single(imported.Manifest.Files, f => f.Path == official.Path);
        Assert.Equal(InstanceFileOrigin.CurseForgeDownload, saved.Origin);
        Assert.Equal(official.Hashes["sha1"], saved.Hashes["sha1"]); Assert.Equal(official.Size, saved.Size);
        Assert.Single(imported.Manifest.MigratedPlayerFiles!);
        Assert.False(Directory.Exists(Path.Combine(imported.InstancePath, MigrationJournalStore.Root)));
        var effective = InstanceEffectiveRequirements.Build(imported.InstancePath, imported.Manifest);
        var file = Assert.Single(effective.Requirements, f => f.Path == official.Path);
        Assert.Equal(InstanceFileOrigin.MigratedPlayerData, file.Origin);
        Assert.Equal(InstanceFileState.ChangedSinceImport,
            await InstanceContentVerifier.CheckExistingAsync(imported.InstancePath, imported.Manifest, file, default));
    }

    /// <summary>补全入口只读取迁入用途记录，不向下载器请求已删除的玩家文件。</summary>
    [Fact]
    public async Task DownloaderDoesNotReinstallDeletedMigratedFile()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "servers.dat", "personal"); Write(target.Game, "servers.dat", "official");
        await AddOfficialBaselineAsync(target, "servers.dat", "official");
        await Service.MigrateAsync(await PlanAsync(old, target, "servers"), true);
        File.Delete(Path.Combine(target.Game, "servers.dat"));
        var report = await new MinecraftInstanceDownloader(_root, new MinecraftInstanceChecker(_root), new NoDownload(),
            prismInstaller: new EmptyInstaller()).DownloadMissingAsync(target.Path);
        Assert.False(File.Exists(Path.Combine(target.Game, "servers.dat")));
        Assert.DoesNotContain(report.Files, f => f.Requirement.Path == ".minecraft/servers.dat");
    }

    /// <summary>模拟提交后、完成标记前崩溃，后续启动和补全均停止，恢复可重复执行。</summary>
    [Fact]
    public async Task InterruptedCommitBlocksLaunchAndRecovers()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "servers.dat", "old"); Write(target.Game, "servers.dat", "new");
        var result = await Service.MigrateAsync(await PlanAsync(old, target, "servers"), true);
        var journal = MigrationJournalStore.Read(target.Path, result.Id);
        journal.Status = "Applying"; MigrationJournalStore.Write(target.Path, journal);
        Assert.Throws<InvalidOperationException>(() => InstanceEffectiveRequirements.Build(target.Path,
            InstanceManifestStore.Read(Path.Combine(target.Path, InstanceImportManifest.FileName))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MinecraftGameLauncher(_root).RunAsync(target.Path, new MinecraftLaunchSettings()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MinecraftInstanceDownloader(_root,
            new MinecraftInstanceChecker(_root), new NoDownload()).DownloadMissingAsync(target.Path));
        var restored = await Service.RestoreAsync(target.Path, result.Id);
        Assert.Equal("RolledBack", restored.Status); Assert.Equal("new", Read(target.Game, "servers.dat"));
        Assert.Null(await Service.GetRecoveryAsync(target.Path));
    }

    /// <summary>中断标记缺少小型摘要时仍被检测到，不忽略已有日志。</summary>
    [Fact]
    public async Task JournalWithoutSummaryRequiresRecovery()
    {
        var target = await CreateAsync("new");
        var manifest = InstanceManifestStore.Read(Path.Combine(target.Path, InstanceImportManifest.FileName));
        var journal = new MigrationJournal { Id = Guid.NewGuid().ToString("N"), GameDirectory = manifest.GameDirectory,
            ManifestFingerprint = EmbeddedFileBaseline.ManifestFingerprint(manifest), SourceName = "old" };
        MigrationJournalStore.Write(target.Path, journal);
        File.Delete(Path.Combine(MigrationJournalStore.DirectoryPath(target.Path, journal.Id), "state.json"));
        Assert.Throws<InvalidOperationException>(() => MigrationJournalStore.EnsureSettled(target.Path));
        Assert.Equal("RecoveryRequired", (await Service.GetRecoveryAsync(target.Path))?.Status);
        Assert.Equal("RolledBack", (await Service.RestoreAsync(target.Path, journal.Id)).Status);
    }

    /// <summary>运行中的实例锁阻止迁移；迁移期间启动入口也不能进入准备阶段。</summary>
    [Fact]
    public async Task OperationLeaseExcludesConcurrentLaunchAndMigration()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new"); Write(old.Game, "servers.dat", "old");
        var plan = await PlanAsync(old, target, "servers");
        using (InstanceOperationLease.Acquire(target.Path))
        {
            await Assert.ThrowsAsync<IOException>(() => Service.MigrateAsync(plan, false));
            await Assert.ThrowsAsync<IOException>(() => new MinecraftGameLauncher(_root).RunAsync(target.Path, new MinecraftLaunchSettings()));
        }
        Assert.Equal("Completed", (await Service.MigrateAsync(plan, false)).Status);
    }

    /// <summary>篡改记录为任意模组路径或无效摘要时不能获得可变许可。</summary>
    [Theory]
    [InlineData(".minecraft/mods/a.jar", true)]
    [InlineData(".minecraft/servers.dat", false)]
    public async Task InvalidPortableMigrationRecordsAreRejected(string path, bool validHash)
    {
        var target = await CreateAsync("new");
        var manifest = InstanceManifestStore.Read(Path.Combine(target.Path, InstanceImportManifest.FileName));
        Assert.Throws<InvalidDataException>(() => InstanceManifestStore.Validate(manifest with
        { MigratedPlayerFiles = [new(path, 3, validHash ? Hash("abc") : "invalid", Guid.NewGuid().ToString("N"))] }));
    }

    /// <summary>恢复日志不能伪造目标指向模组或任意清单之外的文件。</summary>
    [Fact]
    public async Task TamperedJournalCannotOverwritePackFiles()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new"); Write(old.Game, "servers.dat", "old");
        var result = await Service.MigrateAsync(await PlanAsync(old, target, "servers"), false);
        var journal = MigrationJournalStore.Read(target.Path, result.Id);
        journal.Files = [new(".minecraft/mods/a.jar", 3, Hash("old"), null, null)];
        MigrationJournalStore.Write(target.Path, journal);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.RestoreAsync(target.Path, result.Id));
        Assert.False(File.Exists(Path.Combine(target.Game, "mods/a.jar")));
    }

    /// <summary>文件占用目录时停止预览，不能把访问错误当作不存在。</summary>
    [Fact]
    public async Task FileAsDirectoryConflictIsRejected()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "journeymap/map", "map"); Write(target.Game, "journeymap", "file");
        await Assert.ThrowsAsync<IOException>(() => PlanAsync(old, target, "maps"));
        Assert.Equal("file", Read(target.Game, "journeymap"));
    }

    /// <summary>多个世界同时命中另存命名规则时，预览为每个世界保留独立目录。</summary>
    [Fact]
    public async Task MultipleWorldRenameTargetsCannotOverlap()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "saves/A/level.dat", "first"); Write(old.Game, "saves/A (迁入 1)/level.dat", "second");
        Write(target.Game, "saves/A/level.dat", "existing");
        var result = await Service.MigrateAsync(await PlanAsync(old, target, "world:A", "world:A (迁入 1)"), false);
        Assert.Equal("Completed", result.Status);
        Assert.Equal("first", Read(target.Game, "saves/A (迁入 1)/level.dat"));
        Assert.Equal("second", Read(target.Game, "saves/A (迁入 1) (迁入 1)/level.dat"));
    }

    /// <summary>建立与个人内容不同的官方固定文件，用来验证来源未合并。</summary>
    private static async Task<InstanceFileRequirement> AddOfficialBaselineAsync(Instance target, string gamePath, string content)
    {
        var path = target.Relative == "." ? gamePath : target.Relative + "/" + gamePath;
        var file = new InstanceFileRequirement { Path = path, Source = InstanceFileSource.CurseForge,
            Origin = InstanceFileOrigin.CurseForgeDownload, ProjectId = 1, FileId = 2, Size = System.Text.Encoding.UTF8.GetByteCount(content),
            Hashes = new Dictionary<string, string> { ["sha1"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(content))) } };
        var manifestPath = Path.Combine(target.Path, InstanceImportManifest.FileName);
        var manifest = InstanceManifestStore.Read(manifestPath);
        await InstanceManifestStore.WriteAsync(manifestPath, manifest with { Files = [file] }, default);
        return file;
    }
    /// <summary>任何意外下载都立即使测试失败。</summary>
    private sealed class NoDownload : IFileDownloader
    {
        public Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("测试不允许网络下载。");
    }
    /// <summary>隔离运行依赖安装，只验证补全入口对玩家数据的处理。</summary>
    private sealed class EmptyInstaller : IPrismInstanceInstaller
    {
        public Task<PrismInstallResult> InstallAsync(string instancePath, IProgress<InstanceDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new PrismInstallResult { SourceFormat = "Prism" });
    }
}
