using System.Security.Cryptography;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods;
using EML.Core.Minecraft.Migration;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Migration;

namespace EML.Tests;

public sealed partial class LocalModManagementTests
{
    /// <summary>平台来源保留官方摘要，管理事务自身的 SHA-256 不被加入文件校验要求。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlineRegistrationPreservesOfficialSource(bool modrinth)
    {
        var instance = await CreateAsync();
        var source = Mod(_root, "incoming/online.jar");
        var baseline = OnlineRequirement(instance, source, modrinth);
        await Service.RegisterDownloadedAsync(instance.Path, source, baseline);
        var selected = Assert.Single(InstanceEffectiveRequirements.Build(instance.Path, Manifest(instance)).Requirements);
        Assert.Equal(baseline.Origin, selected.Origin);
        Assert.Equal(baseline.Source, selected.Source);
        Assert.Equal(baseline.Hashes, selected.Hashes);
        Assert.False(selected.Hashes.ContainsKey("sha256"));
        Assert.Equal(baseline.ModrinthOrigin, selected.ModrinthOrigin);
        Assert.True(Assert.Single((await Service.ListAsync(instance.Path)).Files).CanManage);
        Assert.Equal(InstanceFileState.Verified, Assert.Single((await new MinecraftInstanceChecker(_root)
            .CheckAsync(instance.Path)).Files).State);
    }

    /// <summary>即使下载器声称成功，落盘登记仍独立检查官方哈希和大小；失败不生成管理记录。</summary>
    [Theory]
    [InlineData("sha1")]
    [InlineData("sha512")]
    [InlineData("size")]
    public async Task OnlineRegistrationRejectsWrongOfficialIntegrity(string failure)
    {
        var instance = await CreateAsync();
        var source = Mod(_root, "incoming/online.jar");
        var baseline = OnlineRequirement(instance, source, failure == "sha512");
        baseline = failure == "size" ? baseline with { Size = baseline.Size + 1 } : baseline with
        {
            Hashes = baseline.Hashes.ToDictionary(pair => pair.Key,
                pair => pair.Key == failure ? new string('0', failure == "sha1" ? 40 : 128) : pair.Value)
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.RegisterDownloadedAsync(instance.Path, source, baseline));
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(instance.Path, baseline.Path!)));
        Assert.Empty(ModManagementStore.Read(instance.Path, Manifest(instance)).Files);
    }

    /// <summary>在线模组的禁用与主动移除经过补全仍生效，恢复也不改变原始平台来源。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OnlineManagementSurvivesCompletionAndRestore(bool modrinth, bool remove)
    {
        var instance = await CreateAsync();
        var source = Mod(_root, "incoming/online.jar");
        var baseline = OnlineRequirement(instance, source, modrinth);
        await Service.RegisterDownloadedAsync(instance.Path, source, baseline);
        await Service.SetEnabledAsync(instance.Path, baseline.Path!, false);
        if (remove) await Service.RemoveAsync(instance.Path, baseline.Path!);
        var checker = new MinecraftInstanceChecker(_root);
        await new MinecraftInstanceDownloader(_root, checker, new NoDownload(), prismInstaller: new EmptyInstaller())
            .DownloadMissingAsync(instance.Path);
        Assert.False(File.Exists(Path.Combine(instance.Path, baseline.Path!)));
        var record = Assert.Single(ModManagementStore.Read(instance.Path, Manifest(instance)).Files);
        Assert.Equal(remove ? LocalModState.Removed : LocalModState.Disabled, record.State);
        Assert.Equal(baseline.Origin, record.Baseline.Origin);
        Assert.Equal(baseline.Hashes, record.Baseline.Hashes);
        if (remove) await Service.RestoreAsync(instance.Path, baseline.Path!);
        Assert.Equal(LocalModState.Disabled, Assert.Single((await Service.ListAsync(instance.Path)).Files).State);
        await Service.SetEnabledAsync(instance.Path, baseline.Path!, true);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(instance.Path, baseline.Path!)));
    }

    /// <summary>备份往返保存在线来源、官方完整性与启禁/移除选择，不重新建立本地导入基线。</summary>
    [Theory]
    [InlineData(false, LocalModState.Enabled)]
    [InlineData(false, LocalModState.Disabled)]
    [InlineData(false, LocalModState.Removed)]
    [InlineData(true, LocalModState.Enabled)]
    [InlineData(true, LocalModState.Disabled)]
    [InlineData(true, LocalModState.Removed)]
    public async Task OnlineBackupRoundTripPreservesOfficialIdentity(bool modrinth, LocalModState state)
    {
        var instance = await CreateAsync();
        var source = Mod(_root, "incoming/online.jar");
        var baseline = OnlineRequirement(instance, source, modrinth);
        await Service.RegisterDownloadedAsync(instance.Path, source, baseline);
        if (state == LocalModState.Disabled) await Service.SetEnabledAsync(instance.Path, baseline.Path!, false);
        if (state == LocalModState.Removed) await Service.RemoveAsync(instance.Path, baseline.Path!);
        var archive = Path.Combine(_root, "online-backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(instance.Path, archive);
        var imported = await new MinecraftInstanceImporter(_root).ImportAsync(archive, "Restored");
        var record = Assert.Single(imported.Manifest.ManagedMods!);
        Assert.Equal(state, record.State);
        Assert.Equal(baseline.Origin, record.Baseline.Origin);
        Assert.Equal(baseline.Hashes, record.Baseline.Hashes);
        Assert.Equal(baseline.ProjectId, record.Baseline.ProjectId);
        Assert.Equal(baseline.FileId, record.Baseline.FileId);
        Assert.Equal(baseline.ModrinthOrigin, record.Baseline.ModrinthOrigin);
        var entry = Assert.Single((await Service.ListAsync(imported.InstancePath)).Files);
        Assert.Equal(state, entry.State);
        if (state != LocalModState.Removed)
            await Service.SetEnabledAsync(imported.InstancePath, baseline.Path!, state != LocalModState.Enabled);
        else Assert.False(entry.CanRestore);
    }

    /// <summary>备用下载保留两边的固定身份；实际内容只使用 Modrinth 文件的官方摘要。</summary>
    [Fact]
    public async Task OnlineFallbackPreservesBothProvidersAcrossBackup()
    {
        var instance = await CreateAsync();
        var source = Mod(_root, "incoming/online.jar");
        var baseline = OnlineRequirement(instance, source, true) with
        {
            ProjectId = 123, FileId = 456,
            CurseForgeOrigin = new() { ProjectId = 123, FileId = 456, Size = new FileInfo(source).Length,
                Hashes = new Dictionary<string, string> { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(source))) } }
        };
        await Service.RegisterDownloadedAsync(instance.Path, source, baseline);
        var backup = InstanceBackupManifest.Create(instance.Path, Manifest(instance)).Manifest;
        var restored = InstanceManifestStore.Parse(System.Text.Json.JsonDocument.Parse(InstanceManifestStore.Serialize(backup)).RootElement);
        var record = Assert.Single(restored.ManagedMods!);
        Assert.Equal(InstanceFileOrigin.ModrinthDownload, record.Baseline.Origin);
        Assert.Equal(baseline.ModrinthOrigin, record.Baseline.ModrinthOrigin);
        Assert.Equal(123, record.Baseline.CurseForgeOrigin!.ProjectId);
        Assert.Equal(baseline.Hashes, record.Baseline.Hashes);
    }

    /// <summary>备用来源关联被篡改时，注册和备份导入都拒绝，不能伪称已满足另一平台的固定文件。</summary>
    [Theory]
    [InlineData("size")]
    [InlineData("hash")]
    [InlineData("project")]
    [InlineData("file")]
    [InlineData("missing-origin")]
    public async Task OnlineFallbackRejectsInconsistentOriginLink(string mismatch)
    {
        var instance = await CreateAsync(); var source = Mod(_root, "incoming/online.jar");
        var baseline = OnlineRequirement(instance, source, true);
        var origin = new CurseForgeFileOrigin { ProjectId = 123, FileId = 456, Size = baseline.Size,
            Hashes = new Dictionary<string, string> { ["sha1"] = baseline.Hashes["sha1"] } };
        baseline = baseline with { ProjectId = 123, FileId = 456, CurseForgeOrigin = mismatch switch
        {
            "size" => origin with { Size = origin.Size + 1 },
            "hash" => origin with { Hashes = new Dictionary<string, string> { ["sha1"] = new string('0', 40) } },
            "project" => origin with { ProjectId = 999 },
            "file" => origin with { FileId = 999 },
            _ => null
        } };
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.RegisterDownloadedAsync(instance.Path, source, baseline));
        var backup = Manifest(instance) with { ManagedMods = [new() { Id = Guid.NewGuid().ToString("N"), Added = true,
            State = LocalModState.Enabled, Baseline = baseline }] };
        Assert.Throws<InvalidDataException>(() => InstanceManifestStore.Validate(backup));
        Assert.Empty((await Service.ListAsync(instance.Path)).Files);
    }

    /// <summary>不能用在线注册覆盖原包；调用者提供的路径同样限制在当前游戏的 mods 根目录。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlineRegistrationRejectsProtectedOrForeignTarget(bool foreign)
    {
        var instance = await CreateAsync();
        var source = Mod(_root, "incoming/online.jar");
        var baseline = OnlineRequirement(instance, source, false);
        if (foreign) baseline = baseline with { Path = ".minecraft/config/online.jar" };
        else await DeclareOfficialAsync(instance, "mods/online.jar");
        await Assert.ThrowsAnyAsync<Exception>(() => Service.RegisterDownloadedAsync(instance.Path, source, baseline));
        Assert.Empty(ModManagementStore.Read(instance.Path, Manifest(instance)).Files);
        Assert.True(File.Exists(source));
    }

    /// <summary>GTNH 版本迁移保留平台来源与禁用状态，目标目录不同也不降级为本地导入。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlineModMigrationPreservesOfficialHashesAndDisabledState(bool modrinth)
    {
        var sourceInstance = await CreateAsync("Old");
        var target = await CreateAsync("New", "minecraft");
        var source = Mod(_root, "incoming/online.jar");
        var baseline = OnlineRequirement(sourceInstance, source, modrinth);
        await Service.RegisterDownloadedAsync(sourceInstance.Path, source, baseline);
        await Service.SetEnabledAsync(sourceInstance.Path, baseline.Path!, false);
        var migration = new GtnhInstanceMigrationService(_root);
        var plan = await migration.PreviewAsync(new(sourceInstance.Path, target.Path, GtnhMigrationMode.FullClient,
            ["mod:mods/online.jar.disabled"], true, true) { ExtraModsRiskAccepted = true });
        Assert.Equal("Completed", (await migration.MigrateAsync(plan, false)).Status);
        var migrated = Assert.Single(MigrationDataStore.Read(target.Path, Manifest(target)).Files);
        Assert.Equal("minecraft/mods/online.jar.disabled", migrated.OnlineModBaseline!.Path);
        Assert.Equal(baseline.Origin, migrated.OnlineModBaseline.Origin);
        Assert.Equal(baseline.Hashes, migrated.OnlineModBaseline.Hashes);
        var entry = Assert.Single((await Service.ListAsync(target.Path)).Files);
        Assert.Equal(LocalModState.Disabled, entry.State);
        Assert.True(entry.CanManage);
        await Service.SetEnabledAsync(target.Path, entry.Path, true);
        var backup = InstanceBackupManifest.Create(target.Path, Manifest(target)).Manifest;
        Assert.Equal(baseline.Origin, Assert.Single(backup.ManagedMods!).Baseline.Origin);
        Assert.Equal(baseline.Hashes, Assert.Single(backup.ManagedMods!).Baseline.Hashes);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(target.Path);
        Assert.Equal(InstanceFileState.Verified, Assert.Single(report.Files).State);
    }

    /// <summary>已修改的在线模组不能通过 GTNH 迁移取得新的可信 SHA-256。</summary>
    [Fact]
    public async Task OnlineModMigrationRejectsModifiedOfficialContent()
    {
        var instance = await CreateAsync("Old");
        var target = await CreateAsync("New");
        var source = Mod(_root, "incoming/online.jar");
        var baseline = OnlineRequirement(instance, source, false);
        await Service.RegisterDownloadedAsync(instance.Path, source, baseline);
        var installed = Path.Combine(instance.Path, baseline.Path!);
        var bytes = File.ReadAllBytes(installed); bytes[^1] ^= 1; File.WriteAllBytes(installed, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => new GtnhInstanceMigrationService(_root).PreviewAsync(
            new(instance.Path, target.Path, GtnhMigrationMode.FullClient, ["mod:mods/online.jar"], true, true)
            { ExtraModsRiskAccepted = true }));
        Assert.False(File.Exists(Path.Combine(target.Game, "mods/online.jar")));
    }

    /// <summary>构造无网络的固定平台文件记录，使用真实测试归档计算独立的官方摘要。</summary>
    private static InstanceFileRequirement OnlineRequirement(Instance instance, string source, bool modrinth)
    {
        var bytes = File.ReadAllBytes(source);
        var name = Path.GetFileName(source);
        var hashes = new Dictionary<string, string> { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(bytes)) };
        if (modrinth) hashes["sha512"] = Convert.ToHexStringLower(SHA512.HashData(bytes));
        return new()
        {
            Path = Relative(instance, "mods/" + name), Required = true, Role = InstanceFileRole.Content,
            Size = bytes.LongLength, Hashes = hashes,
            Source = modrinth ? InstanceFileSource.DirectDownload : InstanceFileSource.CurseForge,
            Origin = modrinth ? InstanceFileOrigin.ModrinthDownload : InstanceFileOrigin.CurseForgeDownload,
            Downloads = [modrinth ? "https://cdn.modrinth.com/data/test/versions/one/" + name : "https://edge.forgecdn.net/files/1/2/" + name],
            ProjectId = modrinth ? null : 123, FileId = modrinth ? null : 456,
            ModrinthOrigin = modrinth ? new() { ProjectId = "project1", VersionId = "version1", FileName = name } : null
        };
    }
}
