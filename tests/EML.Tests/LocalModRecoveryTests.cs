using System.Text.Json;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;
using EML.Core.Minecraft.Mods;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Mods;

namespace EML.Tests;

public sealed partial class LocalModManagementTests
{
    /// <summary>提交前、移动后和状态写入后的中断均可继续完成，恢复不会覆盖文件且可重复执行。</summary>
    [Theory]
    [InlineData("beforeMove")]
    [InlineData("afterMove")]
    [InlineData("afterState")]
    public async Task InterruptedDisableRecoversAtEachCommitBoundary(string stage)
    {
        var instance = await CreateAsync(); var source = Mod(_root, "incoming/custom.jar");
        await Service.AddAsync(instance.Path, source);
        var statePath = Path.Combine(instance.Path, ModManagementStore.RelativePath);
        var beforeState = File.ReadAllBytes(statePath);
        var path = Relative(instance, "mods/custom.jar");
        await Service.SetEnabledAsync(instance.Path, path, false);
        var current = Assert.Single((await Service.ListAsync(instance.Path)).Files).CurrentPath!;
        if (stage != "afterState") File.WriteAllBytes(statePath, beforeState);
        if (stage == "beforeMove") File.Move(Path.Combine(instance.Path, current), Path.Combine(instance.Path, path));
        MakeJournalPending(instance);
        Assert.Throws<InvalidOperationException>(() => InstanceEffectiveRequirements.Build(instance.Path, Manifest(instance)));
        await Service.RecoverAsync(instance.Path);
        await Service.RecoverAsync(instance.Path);
        ModOperationJournal.EnsureSettled(instance.Path);
        var entry = Assert.Single((await Service.ListAsync(instance.Path)).Files);
        Assert.Equal(LocalModState.Disabled, entry.State);
        Assert.False(File.Exists(Path.Combine(instance.Path, path)));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(instance.Path, entry.CurrentPath!)));
        Assert.Equal(Sha256(source), Assert.Single(InstanceEffectiveRequirements.Build(instance.Path, Manifest(instance)).Requirements).Hashes["sha256"]);
    }

    /// <summary>中断后目标被外部修改时保留修改内容和待恢复标记，不把新字节偷偷接受成基线。</summary>
    [Fact]
    public async Task InterruptedOperationRefusesExternallyModifiedDestination()
    {
        var instance = await CreateAsync(); var source = Mod(_root, "incoming/custom.jar");
        await Service.AddAsync(instance.Path, source);
        var statePath = Path.Combine(instance.Path, ModManagementStore.RelativePath);
        var beforeState = File.ReadAllBytes(statePath);
        var path = Relative(instance, "mods/custom.jar");
        await Service.SetEnabledAsync(instance.Path, path, false);
        var current = Assert.Single((await Service.ListAsync(instance.Path)).Files).CurrentPath!;
        File.WriteAllBytes(statePath, beforeState); MakeJournalPending(instance);
        Write(instance.Path, current, "external user modification");
        await Assert.ThrowsAsync<IOException>(() => Service.RecoverAsync(instance.Path));
        Assert.Equal("external user modification", File.ReadAllText(Path.Combine(instance.Path, current)));
        Assert.Equal(beforeState, File.ReadAllBytes(statePath));
        Assert.Throws<InvalidOperationException>(() => ModOperationJournal.EnsureSettled(instance.Path));
        Assert.True(File.Exists(source));
    }

    /// <summary>中断时源与目标同时出现文件视为冲突，两份文件均保留，不自动选一个覆盖。</summary>
    [Fact]
    public async Task InterruptedOperationWithTwoCopiesPreservesBoth()
    {
        var instance = await CreateAsync(); var source = Mod(_root, "incoming/custom.jar");
        await Service.AddAsync(instance.Path, source);
        var statePath = Path.Combine(instance.Path, ModManagementStore.RelativePath);
        var beforeState = File.ReadAllBytes(statePath);
        var path = Relative(instance, "mods/custom.jar");
        await Service.SetEnabledAsync(instance.Path, path, false);
        var current = Assert.Single((await Service.ListAsync(instance.Path)).Files).CurrentPath!;
        File.WriteAllBytes(statePath, beforeState); MakeJournalPending(instance);
        Write(instance.Path, path, "new active file");
        await Assert.ThrowsAsync<IOException>(() => Service.RecoverAsync(instance.Path));
        Assert.Equal("new active file", File.ReadAllText(Path.Combine(instance.Path, path)));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(instance.Path, current)));
        Assert.Throws<InvalidOperationException>(() => ModOperationJournal.EnsureSettled(instance.Path));
    }

    /// <summary>显式接受禁用模组的新内容后，重新启用及备份都沿用该选择，不能回退到旧 SHA-256。</summary>
    [Fact]
    public async Task AcceptedDisabledReplacementSurvivesEnableAndBackup()
    {
        var instance = await CreateAsync();
        await Service.AddAsync(instance.Path, Mod(_root, "incoming/custom.jar", version: "1"));
        var path = Relative(instance, "mods/custom.jar");
        await Service.SetEnabledAsync(instance.Path, path, false);
        var current = Assert.Single((await Service.ListAsync(instance.Path)).Files).CurrentPath!;
        var replacement = Mod(_root, "replacement/custom.jar", version: "2");
        File.Copy(replacement, Path.Combine(instance.Path, current), overwrite: true);
        await new InstanceLocalReplacementService(_root).AcceptExistingAsync(instance.Path, current);
        Assert.Equal(Sha256(replacement), Assert.Single(InstanceEffectiveRequirements.Build(instance.Path, Manifest(instance)).Requirements).Hashes["sha256"]);
        await Service.SetEnabledAsync(instance.Path, path, true);
        Assert.Equal(File.ReadAllBytes(replacement), File.ReadAllBytes(Path.Combine(instance.Path, path)));
        var zip = Path.Combine(_root, "replacement-backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(instance.Path, zip);
        var imported = await new MinecraftInstanceImporter(_root).ImportAsync(zip, "Restored");
        var requirement = Assert.Single(InstanceEffectiveRequirements.Build(imported.InstancePath, imported.Manifest).Requirements,
            file => file.Path == path && file.Origin == InstanceFileOrigin.UserLocalImport);
        Assert.Equal(Sha256(replacement), requirement.Hashes["sha256"]);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(imported.InstancePath);
        Assert.Equal(InstanceFileState.Verified, Assert.Single(report.Files, file => file.Requirement.Path == path).State);
        await Service.SetEnabledAsync(imported.InstancePath, path, false);
        Assert.Equal(LocalModState.Disabled, Assert.Single((await Service.ListAsync(imported.InstancePath)).Files).State);
    }

    /// <summary>迁入模组经管理、明确替换和备份后仍可启用，迁入身份不与旧基线产生冲突。</summary>
    [Fact]
    public async Task AcceptedManagedMigrationReplacementSurvivesBackupAndEnable()
    {
        var source = await CreateAsync("Old"); var target = await CreateAsync("New");
        Mod(source.Game, "mods/custom.jar", version: "1");
        var migration = new GtnhInstanceMigrationService(_root);
        var plan = await migration.PreviewAsync(new(source.Path, target.Path, GtnhMigrationMode.FullClient,
            ["mod:mods/custom.jar"], true, true) { ExtraModsRiskAccepted = true });
        await migration.MigrateAsync(plan, false);
        var path = Relative(target, "mods/custom.jar");
        await Service.SetEnabledAsync(target.Path, path, false);
        var current = Assert.Single((await Service.ListAsync(target.Path)).Files).CurrentPath!;
        var replacement = Mod(_root, "replacement/custom.jar", version: "2");
        File.Copy(replacement, Path.Combine(target.Path, current), overwrite: true);
        await new InstanceLocalReplacementService(_root).AcceptExistingAsync(target.Path, current);
        var zip = Path.Combine(_root, "migration-replacement.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(target.Path, zip);
        var imported = await new MinecraftInstanceImporter(_root).ImportAsync(zip, "Restored");
        Assert.False(Assert.Single(imported.Manifest.ManagedMods!).Added);
        await Service.SetEnabledAsync(imported.InstancePath, path, true);
        var requirement = Assert.Single(InstanceEffectiveRequirements.Build(imported.InstancePath, imported.Manifest).Requirements,
            file => file.Path == path && file.Origin == InstanceFileOrigin.UserLocalImport);
        Assert.Equal(Sha256(replacement), requirement.Hashes["sha256"]);
        Assert.Equal(File.ReadAllBytes(replacement), File.ReadAllBytes(Path.Combine(imported.InstancePath, requirement.Path!)));
        Assert.Equal(InstanceFileState.Verified, Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(imported.InstancePath)).Files,
            file => file.Requirement.Path == path).State);
    }

    /// <summary>模拟进程在持久化步骤之间退出，只改测试临时目录内的真实生产日志。</summary>
    private static void MakeJournalPending(Instance instance)
    {
        var path = Path.Combine(instance.Path, ModOperationJournal.RelativePath);
        var journal = JsonSerializer.Deserialize<ModOperationJournal.Journal>(File.ReadAllBytes(path), MigrationIO.Json)!;
        MigrationIO.WriteJson(path, journal with { Status = "Pending" });
    }
}
