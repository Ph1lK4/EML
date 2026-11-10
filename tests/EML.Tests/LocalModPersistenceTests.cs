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
    /// <summary>备份往返保留管理状态、玩家基线和官方 SHA-1，不把当前磁盘内容重新当作可信来源。</summary>
    [Theory]
    [InlineData(LocalModState.Enabled)]
    [InlineData(LocalModState.Disabled)]
    [InlineData(LocalModState.Removed)]
    public async Task BackupRoundTripPreservesManagementAndOfficialBaseline(LocalModState state)
    {
        var instance = await CreateAsync();
        var official = await DeclareOfficialAsync(instance, "mods/official.jar");
        Write(instance.Game, "mods/official.jar", "official");
        var source = Mod(_root, "incoming/custom.jar");
        await Service.AddAsync(instance.Path, source);
        var path = Relative(instance, "mods/custom.jar");
        if (state == LocalModState.Disabled) await Service.SetEnabledAsync(instance.Path, path, false);
        if (state == LocalModState.Removed) await Service.RemoveAsync(instance.Path, path);
        var zip = Path.Combine(_root, "backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(instance.Path, zip);
        var imported = await new MinecraftInstanceImporter(_root).ImportAsync(zip, "Restored");
        var entry = Assert.Single((await Service.ListAsync(imported.InstancePath)).Files,
            item => item.Path == path);
        Assert.Equal(state, entry.State);
        var record = Assert.Single(imported.Manifest.ManagedMods!);
        Assert.Equal(state, record.State); Assert.True(record.Added);
        Assert.Equal(Sha256(source), record.Baseline.Hashes["sha256"]);
        Assert.Equal(InstanceFileOrigin.UserLocalImport, record.Baseline.Origin);
        var savedOfficial = Assert.Single(imported.Manifest.Files, file => file.Path == official.Path);
        Assert.Equal(InstanceFileOrigin.CurseForgeDownload, savedOfficial.Origin);
        Assert.Equal(official.Hashes["sha1"], savedOfficial.Hashes["sha1"]);
        if (state == LocalModState.Removed)
        {
            Assert.False(entry.CanRestore); // 回收区不随备份导出，不承诺不存在的恢复副本。
            Assert.DoesNotContain(InstanceEffectiveRequirements.Build(imported.InstancePath, imported.Manifest).Requirements,
                file => file.Origin == InstanceFileOrigin.UserLocalImport);
        }
        else
        {
            Assert.True(entry.CanManage);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(imported.InstancePath, entry.CurrentPath!)));
            await Service.SetEnabledAsync(imported.InstancePath, path, state != LocalModState.Enabled);
            Assert.Equal(state == LocalModState.Enabled ? LocalModState.Disabled : LocalModState.Enabled,
                Assert.Single((await Service.ListAsync(imported.InstancePath)).Files, item => item.Path == path).State);
        }
    }

    /// <summary>未接受的磁盘修改即使导出再导入，也不能通过备份获得新的可信哈希。</summary>
    [Fact]
    public async Task BackupDoesNotBlessModifiedDisabledMod()
    {
        var instance = await CreateAsync(); var source = Mod(_root, "incoming/custom.jar");
        await Service.AddAsync(instance.Path, source);
        var path = Relative(instance, "mods/custom.jar");
        await Service.SetEnabledAsync(instance.Path, path, false);
        var entry = Assert.Single((await Service.ListAsync(instance.Path)).Files);
        var current = Path.Combine(instance.Path, entry.CurrentPath!);
        var bytes = File.ReadAllBytes(current); bytes[^1] ^= 1; File.WriteAllBytes(current, bytes);
        var zip = Path.Combine(_root, "modified-backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(instance.Path, zip);
        var imported = await new MinecraftInstanceImporter(_root).ImportAsync(zip, "Restored");
        Assert.Equal(Sha256(source), Assert.Single(imported.Manifest.ManagedMods!).Baseline.Hashes["sha256"]);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(imported.InstancePath);
        Assert.True(InstanceFileStatePolicy.IsBlocking(Assert.Single(report.Files,
            file => file.Requirement.Origin == InstanceFileOrigin.UserLocalImport)));
        Assert.False(report.ContentReady);
    }

    /// <summary>GTNH 新版本迁移保留玩家额外模组的禁用状态，且目标继续使用严格用户来源。</summary>
    [Fact]
    public async Task GtnhMigrationCarriesDisabledManagedExtraMod()
    {
        var source = await CreateAsync("Old"); var target = await CreateAsync("New", "minecraft");
        await Service.AddAsync(source.Path, Mod(_root, "incoming/custom.jar"));
        await Service.SetEnabledAsync(source.Path, Relative(source, "mods/custom.jar"), false);
        var migration = new GtnhInstanceMigrationService(_root);
        var groups = await migration.GetGroupsAsync(source.Path, GtnhMigrationMode.FullClient);
        var group = Assert.Single(groups, item => item.Id.StartsWith("mod:", StringComparison.Ordinal));
        Assert.EndsWith(".disabled", group.Id);
        var plan = await migration.PreviewAsync(new(source.Path, target.Path, GtnhMigrationMode.FullClient,
            [group.Id], true, true) { ExtraModsRiskAccepted = true });
        Assert.Equal("Completed", (await migration.MigrateAsync(plan, false)).Status);
        var entry = Assert.Single((await Service.ListAsync(target.Path)).Files);
        Assert.Equal(LocalModState.Disabled, entry.State); Assert.True(entry.CanManage);
        Assert.False(File.Exists(Path.Combine(target.Game, "mods/custom.jar")));
        var requirement = Assert.Single(InstanceEffectiveRequirements.Build(target.Path, Manifest(target)).Requirements);
        Assert.Equal(InstanceFileOrigin.UserLocalImport, requirement.Origin); Assert.True(requirement.Required);
        await Service.SetEnabledAsync(target.Path, entry.Path, true);
        Assert.True(File.Exists(Path.Combine(target.Game, "mods/custom.jar")));
        var after = await new MinecraftInstanceChecker(_root).CheckAsync(target.Path);
        Assert.Equal(InstanceFileState.Verified, Assert.Single(after.Files).State);
    }

    /// <summary>历史迁入模组改为禁用后，备份重导入仍保留迁入身份并能重新启用。</summary>
    [Fact]
    public async Task ManagedMigratedModSurvivesBackupRoundTrip()
    {
        var source = await CreateAsync("Old"); var target = await CreateAsync("New");
        Mod(source.Game, "mods/custom.jar");
        var migration = new GtnhInstanceMigrationService(_root);
        var plan = await migration.PreviewAsync(new(source.Path, target.Path, GtnhMigrationMode.FullClient,
            ["mod:mods/custom.jar"], true, true) { ExtraModsRiskAccepted = true });
        await migration.MigrateAsync(plan, false);
        var entry = Assert.Single((await Service.ListAsync(target.Path)).Files);
        await Service.SetEnabledAsync(target.Path, entry.Path, false);
        var zip = Path.Combine(_root, "migrated-backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(target.Path, zip);
        var imported = await new MinecraftInstanceImporter(_root).ImportAsync(zip, "Restored");
        var record = Assert.Single(imported.Manifest.ManagedMods!);
        Assert.False(record.Added); Assert.Equal(LocalModState.Disabled, record.State);
        Assert.Single(imported.Manifest.MigratedPlayerFiles!);
        var restored = Assert.Single((await Service.ListAsync(imported.InstancePath)).Files);
        Assert.True(restored.CanManage); Assert.Equal(LocalModState.Disabled, restored.State);
        await Service.SetEnabledAsync(imported.InstancePath, restored.Path, true);
        var check = await new MinecraftInstanceChecker(_root).CheckAsync(imported.InstancePath);
        Assert.Equal(InstanceFileState.Verified, Assert.Single(check.Files,
            file => file.Requirement.Path == Relative(target, "mods/custom.jar") && file.Requirement.Origin == InstanceFileOrigin.UserLocalImport).State);
    }

    /// <summary>管理状态改变使正在使用的旧有效需求快照失效，不能沿用旧就绪结论。</summary>
    [Fact]
    public async Task ChangingModIntentInvalidatesEffectiveRequirementSnapshot()
    {
        var instance = await CreateAsync();
        await Service.AddAsync(instance.Path, Mod(_root, "incoming/custom.jar"));
        var manifest = Manifest(instance);
        var before = InstanceEffectiveRequirements.Build(instance.Path, manifest);
        await Service.SetEnabledAsync(instance.Path, Relative(instance, "mods/custom.jar"), false);
        Assert.Throws<IOException>(() => InstanceEffectiveRequirements.EnsureUnchanged(instance.Path, manifest, before));
    }

    /// <summary>可移植记录不能借用户添加身份把原包文件标记成主动移除。</summary>
    [Fact]
    public async Task PortableManagementCannotSuppressOfficialRequirement()
    {
        var instance = await CreateAsync();
        var official = await DeclareOfficialAsync(instance, "mods/official.jar");
        var bytes = Write(_root, "user.jar", "user");
        var record = new ManagedModRecord
        {
            Id = Guid.NewGuid().ToString("N"), Added = true, State = LocalModState.Removed,
            Baseline = new()
            {
                Path = official.Path, Source = InstanceFileSource.Embedded, Origin = InstanceFileOrigin.UserLocalImport,
                Role = InstanceFileRole.Content, Required = true, Size = 4,
                Hashes = new Dictionary<string, string> { ["sha256"] = Sha256(bytes) }
            }
        };
        Assert.Throws<InvalidDataException>(() => InstanceManifestStore.Validate(Manifest(instance) with { ManagedMods = [record] }));
        Assert.Equal(official.Hashes["sha1"], Assert.Single(Manifest(instance).Files).Hashes["sha1"]);
    }

    /// <summary>清单变化后旧的本地管理授权不能套用到另一个包或修改后的来源集合。</summary>
    [Fact]
    public async Task StaleManagementStateCannotApplyToChangedManifest()
    {
        var instance = await CreateAsync();
        await Service.AddAsync(instance.Path, Mod(_root, "incoming/custom.jar"));
        await InstanceManifestStore.WriteAsync(Path.Combine(instance.Path, InstanceImportManifest.FileName),
            Manifest(instance) with { SourceArchive = "different-pack.zip" }, default);
        Assert.Throws<InvalidDataException>(() => InstanceEffectiveRequirements.Build(instance.Path, Manifest(instance)));
        Assert.True(File.Exists(Path.Combine(instance.Game, "mods/custom.jar")));
    }
}
