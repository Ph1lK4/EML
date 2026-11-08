using System.IO.Compression;
using System.Text;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;

namespace EML.Tests;

public sealed partial class GtnhMigrationTests
{
    /// <summary>导入包内模组不被自动认定为玩家新增；所有候选均须单独勾选。</summary>
    [Fact]
    public async Task ExtraModCandidatesDistinguishImportedAndAddedWithoutRecommendingEither()
    {
        var old = await CreateAsync("old");
        Write(old.Game, "mods/imported.jar", "imported");
        await AddOfficialBaselineAsync(old, "mods/imported.jar", "imported");
        Write(old.Game, "mods/added.jar.disabled", "added");
        Write(old.Game, "mods/1.7.10/legacy.zip", "legacy");
        Write(old.Game, "mods/cache/cache.jar", "cache"); Write(old.Game, "mods/readme.txt", "readme");
        foreach (var mode in Enum.GetValues<GtnhMigrationMode>())
        {
            var groups = await Service.GetGroupsAsync(old.Path, mode);
            Assert.Equal(3, groups.Count);
            Assert.All(groups, group => Assert.False(group.Recommended));
            Assert.Contains(groups, group => group.Id == "mod:mods/imported.jar" && group.Name.Contains("需确认是自己添加"));
            Assert.Contains(groups, group => group.Id == "mod:mods/added.jar.disabled" && group.Name.Contains("导入后新增"));
        }
    }

    /// <summary>仅选择模组不等于接受风险，服务端也拒绝未经确认的请求。</summary>
    [Fact]
    public async Task ExtraModsRequireSeparateRiskAcceptance()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "mods/custom.jar", "custom");
        await Assert.ThrowsAsync<InvalidOperationException>(() => PlanAsync(old, target, "mod:mods/custom.jar"));
        Assert.False(Directory.Exists(Path.Combine(target.Game, "mods")));
        Assert.Null(await Service.GetRecoveryAsync(target.Path));
    }

    /// <summary>只复制选中条目，保留启用状态和版本目录；配置及其他模组不会跟随复制。</summary>
    [Theory]
    [InlineData("mods/custom.jar")]
    [InlineData("mods/custom.zip")]
    [InlineData("mods/custom.jar.disabled")]
    [InlineData("mods/1.7.10/custom.zip.disabled")]
    public async Task SelectedExtraModIsStrictUserContent(string path)
    {
        var old = await CreateAsync("old", "minecraft"); var target = await CreateAsync("new", ".");
        Write(old.Game, path, "custom mod"); Write(old.Game, "mods/unselected.jar", "not selected");
        Write(old.Game, "config/custom.cfg", "old configuration");
        var plan = await ModPlanAsync(old, target, path);
        Assert.Contains(plan.Notes, note => note.Contains("不兼容"));
        Assert.Equal(MigrationFileAction.Copy, Assert.Single(plan.Files).Action);
        var result = await Service.MigrateAsync(plan, false);
        Assert.Equal("Completed", result.Status); Assert.Equal(1, result.CopiedFiles);
        Assert.Equal("custom mod", Read(target.Game, path)); Assert.Equal("custom mod", Read(old.Game, path));
        Assert.False(File.Exists(Path.Combine(target.Game, "mods/unselected.jar")));
        Assert.False(File.Exists(Path.Combine(target.Game, "config/custom.cfg")));
        var manifest = InstanceManifestStore.Read(Path.Combine(target.Path, InstanceImportManifest.FileName));
        var requirement = Assert.Single(InstanceEffectiveRequirements.Build(target.Path, manifest).Requirements);
        Assert.Equal(InstanceFileOrigin.UserLocalImport, requirement.Origin);
        Assert.Equal(InstanceFileRole.Content, requirement.Role); Assert.True(requirement.Required);
        Assert.Equal(10, requirement.Size); Assert.Equal(Hash("custom mod"), requirement.Hashes["sha256"]);
        Assert.False(InstanceFileChangePolicy.IsMutableFile(requirement, manifest.GameDirectory));
        Assert.Equal(InstanceFileState.Verified, await InstanceContentVerifier.CheckExistingAsync(target.Path, manifest, requirement, default));
        Assert.Equal(GtnhExtraModPolicy.Rule, Assert.Single(MigrationDataStore.Read(target.Path, manifest).Files).Rule);
        Assert.Empty(manifest.Files);
    }

    /// <summary>同名、禁用后缀别名及版本子目录重复不能通过覆盖开关替换新版。</summary>
    [Theory]
    [InlineData("mods/custom.jar")]
    [InlineData("mods/CUSTOM.jar.disabled")]
    [InlineData("mods/1.7.10/custom.jar")]
    public async Task ExtraModFilenameCollisionNeverOverwrites(string targetPath)
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "mods/custom.jar", "old mod"); Write(target.Game, targetPath, "new mod");
        var plan = await ModPlanAsync(old, target, "mods/custom.jar");
        Assert.Equal(MigrationFileAction.Skip, Assert.Single(plan.Files).Action);
        Assert.Contains("同名", plan.Files[0].Detail);
        Assert.Equal("NoChanges", (await Service.MigrateAsync(plan, true)).Status);
        Assert.Equal("new mod", Read(target.Game, targetPath)); Assert.Null(await Service.GetRecoveryAsync(target.Path));
    }

    /// <summary>重命名同内容文件仍视为重复；不靠文件名判断模组版本。</summary>
    [Fact]
    public async Task ExtraModIdenticalContentWithDifferentNameIsSkipped()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "mods/old-name.jar", "same mod"); Write(target.Game, "mods/new-name.jar", "same mod");
        var file = Assert.Single((await ModPlanAsync(old, target, "mods/old-name.jar")).Files);
        Assert.Equal(MigrationFileAction.Skip, file.Action); Assert.Contains("相同内容", file.Detail);
    }

    /// <summary>支持两种 mcmod.info 形式；不同内容但相同 modid 时保留新版。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtraModIdCollisionSkipsDifferentVersion(bool wrapped)
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        WriteMod(old.Game, "mods/custom-v1.jar", "custom", "v1");
        WriteMod(target.Game, "mods/custom-v2.jar", "custom", "v2", wrapped);
        var plan = await ModPlanAsync(old, target, "mods/custom-v1.jar");
        Assert.Equal(MigrationFileAction.Skip, Assert.Single(plan.Files).Action); Assert.Contains("modid", plan.Files[0].Detail);
        Assert.Equal("NoChanges", (await Service.MigrateAsync(plan, true)).Status);
        Assert.False(File.Exists(Path.Combine(target.Game, "mods/custom-v1.jar")));
    }

    /// <summary>同次选择的两个版本也会去重，只保留排序后第一个并在预览解释。</summary>
    [Fact]
    public async Task SelectedExtraModsCannotDuplicateEachOther()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        WriteMod(old.Game, "mods/a.jar", "custom", "a"); WriteMod(old.Game, "mods/b.jar", "custom", "b");
        var plan = await ModPlanAsync(old, target, "mods/b.jar", "mods/a.jar");
        Assert.Equal(new[] { MigrationFileAction.Copy, MigrationFileAction.Skip }, plan.Files.Select(file => file.Action));
        var result = await Service.MigrateAsync(plan, true);
        Assert.Equal(1, result.CopiedFiles); Assert.Equal(1, result.SkippedFiles);
        Assert.False(File.Exists(Path.Combine(target.Game, "mods/b.jar")));
    }

    /// <summary>新版官方清单中的缺失模组不能借迁移变成用户模组。</summary>
    [Fact]
    public async Task MissingDeclaredModStillProtectsOfficialSource()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "mods/custom.jar", "old mod");
        var official = await AddOfficialBaselineAsync(target, "mods/custom.jar", "official mod");
        var plan = await ModPlanAsync(old, target, "mods/custom.jar");
        Assert.Equal(MigrationFileAction.Skip, Assert.Single(plan.Files).Action);
        Assert.Equal("NoChanges", (await Service.MigrateAsync(plan, true)).Status);
        Assert.False(File.Exists(Path.Combine(target.Game, "mods/custom.jar")));
        Assert.Equal(official.Hashes["sha1"], Assert.Single(InstanceManifestStore.Read(Path.Combine(target.Path,
            InstanceImportManifest.FileName)).Files).Hashes["sha1"]);
    }

    /// <summary>预览之后新增或更改目标中的其他模组也使计划失效，避免同 modid 的竞争。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedTargetModSetInvalidatesExtraModPreview(bool existed)
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        WriteMod(old.Game, "mods/old-custom.jar", "custom", "old");
        if (existed) WriteMod(target.Game, "mods/new-custom.jar", "other", "before");
        var plan = await ModPlanAsync(old, target, "mods/old-custom.jar");
        WriteMod(target.Game, "mods/new-custom.jar", "custom", "after");
        await Assert.ThrowsAsync<IOException>(() => Service.MigrateAsync(plan, true));
        Assert.False(File.Exists(Path.Combine(target.Game, "mods/old-custom.jar")));
    }

    /// <summary>取消或撤销会把迁入模组移入事务备份，不影响源和目标原有模组。</summary>
    [Theory]
    [InlineData("暂存并校验")]
    [InlineData("提交迁移")]
    [InlineData(null)]
    public async Task ExtraModMigrationRollsBackWithItsProvenance(string? cancelStage)
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "mods/a.jar", "a"); Write(old.Game, "mods/b.jar", "b"); Write(target.Game, "mods/official.jar", "official");
        using var cancel = new CancellationTokenSource();
        var progress = new Callback(value => { if (value.Stage == cancelStage && value.Completed == 1) cancel.Cancel(); });
        var result = await Service.MigrateAsync(await ModPlanAsync(old, target, "mods/a.jar", "mods/b.jar"), true, progress, cancel.Token);
        if (cancelStage is null) result = await Service.RestoreAsync(target.Path, result.Id);
        Assert.Equal("RolledBack", result.Status);
        Assert.False(File.Exists(Path.Combine(target.Game, "mods/a.jar"))); Assert.False(File.Exists(Path.Combine(target.Game, "mods/b.jar")));
        Assert.False(File.Exists(Path.Combine(target.Path, MigrationDataStore.RelativePath)));
        Assert.Equal("official", Read(target.Game, "mods/official.jar")); Assert.Equal("a", Read(old.Game, "mods/a.jar"));
    }

    /// <summary>迁入模组的内容改变或丢失都不能享受玩家存档的可变策略。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtraModDamageBlocksContentAndCompletion(bool missing)
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new"); Write(old.Game, "mods/custom.jar", "original");
        await Service.MigrateAsync(await ModPlanAsync(old, target, "mods/custom.jar"), false);
        if (missing) File.Delete(Path.Combine(target.Game, "mods/custom.jar")); else Write(target.Game, "mods/custom.jar", "modified");
        var checker = new MinecraftInstanceChecker(_root);
        var report = await checker.CheckAsync(target.Path);
        var file = Assert.Single(report.Files, file => file.Requirement.Path == ".minecraft/mods/custom.jar");
        Assert.True(InstanceFileStatePolicy.IsBlocking(file)); Assert.False(file.IsAllowedConfigurationChange);
        var completion = await new MinecraftInstanceDownloader(_root, checker, new NoDownload(),
            prismInstaller: new EmptyInstaller()).DownloadMissingAsync(target.Path);
        Assert.False(completion.LaunchReady);
        Assert.Equal(!missing, File.Exists(Path.Combine(target.Game, "mods/custom.jar")));
        if (!missing) Assert.Equal("modified", Read(target.Game, "mods/custom.jar"));
    }

    /// <summary>真实导出重导入保留额外模组严格基线、旧官方哈希和后续显式接受的替换。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExtraModBackupPreservesTrustedSelection(bool modified, bool accepted)
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new"); Write(old.Game, "mods/custom.jar", "original");
        Write(target.Game, "mods/official.jar", "official");
        var official = await AddOfficialBaselineAsync(target, "mods/official.jar", "official");
        await Service.MigrateAsync(await ModPlanAsync(old, target, "mods/custom.jar"), false);
        if (modified) Write(target.Game, "mods/custom.jar", "modified");
        if (accepted) await new InstanceLocalReplacementService(_root).AcceptExistingAsync(target.Path, ".minecraft/mods/custom.jar");
        var zip = Path.Combine(_root, "extra-mod-backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(target.Path, zip);
        var imported = await new MinecraftInstanceImporter(_root).ImportAsync(zip, "Restored");
        var savedOfficial = Assert.Single(imported.Manifest.Files, file => file.Path == official.Path);
        Assert.Equal(InstanceFileOrigin.CurseForgeDownload, savedOfficial.Origin);
        Assert.Equal(official.Hashes["sha1"], savedOfficial.Hashes["sha1"]);
        var record = Assert.Single(imported.Manifest.MigratedPlayerFiles!);
        Assert.Equal(GtnhExtraModPolicy.Rule, record.Rule); Assert.Equal(Hash(accepted ? "modified" : "original"), record.Sha256);
        var requirement = Assert.Single(InstanceEffectiveRequirements.Build(imported.InstancePath, imported.Manifest).Requirements,
            file => file.Path == record.Path);
        Assert.Equal(InstanceFileOrigin.UserLocalImport, requirement.Origin); Assert.True(requirement.Required);
        if (modified && !accepted)
            await Assert.ThrowsAsync<InvalidDataException>(() => InstanceContentVerifier.CheckExistingAsync(imported.InstancePath,
                imported.Manifest, requirement, default));
        else
            Assert.Equal(InstanceFileState.Verified, await InstanceContentVerifier.CheckExistingAsync(imported.InstancePath,
                imported.Manifest, requirement, default));
        Assert.False(Directory.Exists(Path.Combine(imported.InstancePath, MigrationJournalStore.Root)));
        var groups = await Service.GetGroupsAsync(imported.InstancePath, GtnhMigrationMode.FullClient);
        Assert.Contains(groups, group => group.Id == "mod:mods/custom.jar" && group.Name.Contains("此前手动迁入"));
        // 备份重导入后仍可以接受新的版本，不依赖原电脑的本机状态文件。
        Write(imported.GameDirectory, "mods/custom.jar", "chosen after restore");
        await new InstanceLocalReplacementService(_root).AcceptExistingAsync(imported.InstancePath, record.Path);
        var updated = InstanceEffectiveRequirements.Build(imported.InstancePath, imported.Manifest).Requirements.Single(file => file.Path == record.Path);
        Assert.Equal(Hash("chosen after restore"), updated.Hashes["sha256"]);
    }

    /// <summary>已迁入但后来丢失的模组保持原记录，不能经第二次迁移静默接受不同内容。</summary>
    [Fact]
    public async Task MissingPreviouslyMigratedModCannotBeSilentlyRebased()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new"); Write(old.Game, "mods/custom.jar", "original");
        await Service.MigrateAsync(await ModPlanAsync(old, target, "mods/custom.jar"), false);
        File.Delete(Path.Combine(target.Game, "mods/custom.jar")); Write(old.Game, "mods/custom.jar", "modified");
        var plan = await ModPlanAsync(old, target, "mods/custom.jar");
        Assert.Equal(MigrationFileAction.Skip, Assert.Single(plan.Files).Action);
        Assert.Equal("NoChanges", (await Service.MigrateAsync(plan, true)).Status);
        var manifest = InstanceManifestStore.Read(Path.Combine(target.Path, InstanceImportManifest.FileName));
        Assert.Equal(Hash("original"), Assert.Single(MigrationDataStore.Read(target.Path, manifest).Files).Sha256);
    }

    /// <summary>额外模组规则不能用于配置、越界路径、其他版本或嵌套目录。</summary>
    [Theory]
    [InlineData("mods/../custom.jar")]
    [InlineData("mods/1.12.2/custom.jar")]
    [InlineData("mods/cache/custom.jar")]
    [InlineData("mods/custom.txt")]
    [InlineData("config/custom.jar")]
    [InlineData("mods/custom.jar:stream")]
    public void ExtraModRulesRemainNarrow(string path) => Assert.False(GtnhExtraModPolicy.Allows(path));

    /// <summary>可移植模组记录不能覆盖已有官方路径，也不能借玩家规则放松模组校验。</summary>
    [Fact]
    public async Task ExtraModRecordCannotOverrideOriginalRequirements()
    {
        var target = await CreateAsync("new"); await AddOfficialBaselineAsync(target, "mods/official.jar", "official");
        var manifest = InstanceManifestStore.Read(Path.Combine(target.Path, InstanceImportManifest.FileName));
        var record = new MigratedPlayerFile(".minecraft/mods/official.jar", 6, Hash("custom"), Guid.NewGuid().ToString("N"), GtnhExtraModPolicy.Rule);
        Assert.Throws<InvalidDataException>(() => InstanceManifestStore.Validate(manifest with { MigratedPlayerFiles = [record] }));
    }

    /// <summary>用真实服务构建显式接受风险的单文件模组计划。</summary>
    private Task<InstanceMigrationPlan> ModPlanAsync(Instance source, Instance target, params string[] paths) =>
        Service.PreviewAsync(new(source.Path, target.Path, GtnhMigrationMode.FullClient,
            paths.Select(path => GtnhExtraModPolicy.GroupPrefix + path).ToArray(), true, true) { ExtraModsRiskAccepted = true });

    /// <summary>仅写入元数据和无害文本，覆盖实际 JAR 元数据读取，不执行任何 Java 代码。</summary>
    private static void WriteMod(string root, string path, string id, string version, bool wrapped = false)
    {
        var full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = new FileStream(full, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("mcmod.info");
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        var list = System.Text.Json.JsonSerializer.Serialize(new[] { new { modid = id, version } });
        writer.Write(wrapped ? "{\"modList\":" + list + "}" : list);
    }
}
