using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;
using EML.Core.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Mods;

namespace EML.Tests;

public sealed partial class LocalModManagementTests
{
    /// <summary>目标玩家模组被移除或意外缺失后仍保留身份，迁移不能用同名新内容覆盖授权记录。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationRespectsAbsentManagedTargetIdentity(bool removed)
    {
        var source = await CreateAsync("Old"); var target = await CreateAsync("New");
        Mod(source.Game, "mods/custom.jar", version: "old");
        var targetSource = Mod(_root, "incoming/custom.jar", version: "target");
        await Service.AddAsync(target.Path, targetSource);
        var path = Relative(target, "mods/custom.jar");
        if (removed) await Service.RemoveAsync(target.Path, path);
        else File.Delete(Path.Combine(target.Path, path));
        var statePath = Path.Combine(target.Path, ModManagementStore.RelativePath);
        var beforeState = File.ReadAllBytes(statePath);
        var migration = new GtnhInstanceMigrationService(_root);
        var plan = await migration.PreviewAsync(new(source.Path, target.Path, GtnhMigrationMode.FullClient,
            ["mod:mods/custom.jar"], true, true) { ExtraModsRiskAccepted = true });
        Assert.Equal(MigrationFileAction.Skip, Assert.Single(plan.Files).Action);
        Assert.Equal("NoChanges", (await migration.MigrateAsync(plan, true)).Status);
        Assert.Equal(beforeState, File.ReadAllBytes(statePath));
        Assert.False(File.Exists(Path.Combine(target.Path, path)));
        Assert.Empty(MigrationDataStore.Read(target.Path, Manifest(target)).Files);
        var entry = Assert.Single((await Service.ListAsync(target.Path)).Files);
        Assert.Equal(removed ? LocalModState.Removed : LocalModState.Enabled, entry.State);
        if (removed) Assert.True(entry.CanRestore);
        else Assert.False(entry.IsPresent);
        ModOperationJournal.EnsureSettled(target.Path);
    }

    /// <summary>源已管理模组的启用别名重新出现时，不能经迁移把未知内容变成新可信来源。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationRejectsUnregisteredCopyOfDisabledOrRemovedSource(bool removed)
    {
        var source = await CreateAsync("Old"); var target = await CreateAsync("New");
        var original = Mod(_root, "incoming/custom.jar", version: "original");
        await Service.AddAsync(source.Path, original);
        var path = Relative(source, "mods/custom.jar");
        if (removed) await Service.RemoveAsync(source.Path, path);
        else await Service.SetEnabledAsync(source.Path, path, false);
        var unregistered = Mod(source.Game, "mods/custom.jar", version: "external replacement");
        var unregisteredBytes = File.ReadAllBytes(unregistered);
        var beforeState = File.ReadAllBytes(Path.Combine(source.Path, ModManagementStore.RelativePath));
        var migration = new GtnhInstanceMigrationService(_root);
        await Assert.ThrowsAsync<IOException>(() => migration.PreviewAsync(new(source.Path, target.Path,
            GtnhMigrationMode.FullClient, ["mod:mods/custom.jar"], true, true) { ExtraModsRiskAccepted = true }));
        Assert.Equal(unregisteredBytes, File.ReadAllBytes(unregistered));
        Assert.Equal(beforeState, File.ReadAllBytes(Path.Combine(source.Path, ModManagementStore.RelativePath)));
        Assert.Empty(InstanceEffectiveRequirements.Build(target.Path, Manifest(target)).Requirements);
        Assert.False(File.Exists(Path.Combine(target.Game, "mods/custom.jar")));
        Assert.Null(await migration.GetRecoveryAsync(target.Path));
    }

    /// <summary>迁移完成后新增的启禁或回收选择阻止旧迁移撤销，避免留下失去来源的管理记录。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationRollbackPreservesSubsequentModManagement(bool removed)
    {
        var source = await CreateAsync("Old"); var target = await CreateAsync("New");
        var original = Mod(source.Game, "mods/custom.jar");
        var migration = new GtnhInstanceMigrationService(_root);
        var plan = await migration.PreviewAsync(new(source.Path, target.Path, GtnhMigrationMode.FullClient,
            ["mod:mods/custom.jar"], true, true) { ExtraModsRiskAccepted = true });
        var migrated = await migration.MigrateAsync(plan, false);
        Assert.Equal("Completed", migrated.Status);
        var path = Relative(target, "mods/custom.jar");
        if (removed) await Service.RemoveAsync(target.Path, path);
        else await Service.SetEnabledAsync(target.Path, path, false);
        var statePath = Path.Combine(target.Path, ModManagementStore.RelativePath);
        var migrationPath = Path.Combine(target.Path, MigrationDataStore.RelativePath);
        var beforeState = File.ReadAllBytes(statePath); var beforeMigration = File.ReadAllBytes(migrationPath);
        var result = await migration.RestoreAsync(target.Path, migrated.Id);
        Assert.Equal("RecoveryRequired", result.Status);
        Assert.Equal(beforeState, File.ReadAllBytes(statePath));
        Assert.Equal(beforeMigration, File.ReadAllBytes(migrationPath));
        Assert.Equal("Completed", (await migration.GetRecoveryAsync(target.Path))?.Status);
        var entry = Assert.Single((await Service.ListAsync(target.Path)).Files);
        Assert.Equal(removed ? LocalModState.Removed : LocalModState.Disabled, entry.State);
        if (removed)
        {
            Assert.True(entry.CanRestore);
            await Service.RestoreAsync(target.Path, path);
            Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(Path.Combine(target.Path, path)));
        }
        else Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(Path.Combine(target.Path, entry.CurrentPath!)));
        MigrationJournalStore.EnsureSettled(target.Path);
        ModOperationJournal.EnsureSettled(target.Path);
    }

    /// <summary>普通禁用目标冲突在记录事务前拒绝，两份文件保留且不会让实例进入待恢复阻断。</summary>
    [Fact]
    public async Task DisableCollisionDoesNotCreatePendingOperation()
    {
        var instance = await CreateAsync(); var source = Mod(_root, "incoming/custom.jar");
        await Service.AddAsync(instance.Path, source);
        var path = Relative(instance, "mods/custom.jar");
        var conflicting = Mod(instance.Game, "mods/custom.jar.disabled", version: "conflict");
        var beforeConflict = File.ReadAllBytes(conflicting);
        var beforeState = File.ReadAllBytes(Path.Combine(instance.Path, ModManagementStore.RelativePath));
        await Assert.ThrowsAnyAsync<IOException>(() => Service.SetEnabledAsync(instance.Path, path, false));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(instance.Path, path)));
        Assert.Equal(beforeConflict, File.ReadAllBytes(conflicting));
        Assert.Equal(beforeState, File.ReadAllBytes(Path.Combine(instance.Path, ModManagementStore.RelativePath)));
        ModOperationJournal.EnsureSettled(instance.Path);
        Assert.Equal(Sha256(source), Assert.Single(InstanceEffectiveRequirements.Build(instance.Path, Manifest(instance)).Requirements).Hashes["sha256"]);
    }
}
