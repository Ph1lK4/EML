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
    private LocalModManager Service => new(_root);

    /// <summary>多模组归档只占一个文件行，读取名称版本但不能把外部文件自动认作可信新增。</summary>
    [Fact]
    public async Task CatalogReadsMultipleIdentitiesWithoutAdoptingExternalFile()
    {
        var instance = await CreateAsync();
        Mod(instance.Game, "mods/multiple.jar", metadata: "[{\"modid\":\"first\",\"name\":\"First\",\"version\":\"1.2\"},{\"modid\":\"second\",\"name\":\"Second\",\"version\":\"2.0\"}]");
        var entry = Assert.Single((await Service.ListAsync(instance.Path)).Files);
        Assert.Equal(Relative(instance, "mods/multiple.jar"), entry.Path);
        Assert.Equal(new[] { "first", "second" }, entry.Identities.Select(value => value.Id).Order());
        Assert.Contains(entry.Identities, value => value.Name == "First" && value.Version == "1.2");
        Assert.True(entry.IsPresent); Assert.True(entry.CanAdopt); Assert.False(entry.CanManage);
        Assert.Empty(InstanceEffectiveRequirements.Build(instance.Path, Manifest(instance)).Requirements);
    }

    /// <summary>旧 Forge 的 modList 形式同样可读，一个文件仍是一个管理单位。</summary>
    [Fact]
    public async Task CatalogSupportsWrappedLegacyMetadata()
    {
        var instance = await CreateAsync();
        Mod(instance.Game, "mods/wrapped.jar", metadata: "{\"modList\":[{\"modid\":\"wrapped\",\"name\":\"Wrapped Mod\",\"version\":\"2.1\"}]}");
        var entry = Assert.Single((await Service.ListAsync(instance.Path)).Files);
        var identity = Assert.Single(entry.Identities);
        Assert.Equal("wrapped", identity.Id); Assert.Equal("Wrapped Mod", identity.Name); Assert.Equal("2.1", identity.Version);
    }

    /// <summary>错误、缺失或超限描述不隐藏磁盘文件，也不让整个实例列表失败。</summary>
    [Theory]
    [InlineData("malformed")]
    [InlineData("absent")]
    [InlineData("oversized")]
    public async Task UnreadableMetadataFallsBackToFilename(string kind)
    {
        var instance = await CreateAsync();
        if (kind == "malformed") Mod(instance.Game, "mods/broken.jar", metadata: "{invalid json");
        else if (kind == "absent") Mod(instance.Game, "mods/broken.jar", metadata: "text", metadataEntry: "README.txt");
        else Mod(instance.Game, "mods/broken.jar", metadata: new string(' ', 1024 * 1024 + 1));
        var entry = Assert.Single((await Service.ListAsync(instance.Path)).Files);
        Assert.Equal("broken.jar", entry.FileName); Assert.True(entry.IsPresent); Assert.Empty(entry.Identities);
        Assert.False(string.IsNullOrWhiteSpace(entry.Name));
    }

    /// <summary>GTNH 只枚举真实模组目录，不把其他版本、缓存或普通文本当模组。</summary>
    [Fact]
    public async Task CatalogUsesCurrentLoaderDirectoriesAndDisabledSuffix()
    {
        var instance = await CreateAsync();
        Mod(instance.Game, "mods/base.jar", "base");
        Mod(instance.Game, "mods/1.7.10/legacy.zip.disabled", "legacy");
        Mod(instance.Game, "mods/1.12.2/foreign.jar", "foreign");
        Mod(instance.Game, "mods/cache/cache.jar", "cache");
        Write(instance.Game, "mods/readme.txt", "readme");
        var files = (await Service.ListAsync(instance.Path)).Files;
        Assert.Equal(2, files.Count);
        Assert.Contains(files, file => file.FileName == "legacy.zip.disabled" && file.State == LocalModState.Disabled);
        Assert.DoesNotContain(files, file => file.FileName is "foreign.jar" or "cache.jar" or "readme.txt");
    }

    /// <summary>添加在真实游戏目录建立严格用户基线，并保留外部原件和原清单字节。</summary>
    [Theory]
    [InlineData(".")]
    [InlineData("minecraft")]
    [InlineData(".minecraft")]
    public async Task AddCreatesStrictUserBaselineWithoutChangingOriginalManifest(string game)
    {
        var instance = await CreateAsync(game: game);
        var source = Mod(_root, "incoming/custom.jar");
        var originalBytes = File.ReadAllBytes(Path.Combine(instance.Path, InstanceImportManifest.FileName));
        await Service.AddAsync(instance.Path, source);
        var entry = Assert.Single((await Service.ListAsync(instance.Path)).Files);
        Assert.True(entry.CanManage); Assert.False(entry.CanAdopt); Assert.Equal(LocalModState.Enabled, entry.State);
        Assert.Equal("Example Mod", entry.Name); Assert.Equal("1.0", entry.Version);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(instance.Path, entry.CurrentPath!)));
        var requirement = Assert.Single(InstanceEffectiveRequirements.Build(instance.Path, Manifest(instance)).Requirements);
        Assert.Equal(InstanceFileOrigin.UserLocalImport, requirement.Origin); Assert.True(requirement.Required);
        Assert.Equal(new FileInfo(source).Length, requirement.Size);
        Assert.Equal(Sha256(source), Assert.Single(requirement.Hashes).Value);
        Assert.Equal("sha256", Assert.Single(requirement.Hashes).Key);
        Assert.Empty(requirement.Downloads); Assert.Null(requirement.ArchiveEntry);
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(instance.Path, InstanceImportManifest.FileName)));
        Assert.True(File.Exists(source));
    }

    /// <summary>同名、同内容和相同 modId 的候选均不覆盖已有玩家模组。</summary>
    [Theory]
    [InlineData("name")]
    [InlineData("content")]
    [InlineData("id")]
    public async Task DuplicateAddsPreserveExistingFile(string collision)
    {
        var instance = await CreateAsync();
        var source = Mod(_root, "first/custom.jar", "custom", "1");
        await Service.AddAsync(instance.Path, source);
        var existing = Path.Combine(instance.Game, "mods/custom.jar");
        var original = File.ReadAllBytes(existing);
        string candidate;
        if (collision == "content")
        {
            candidate = Path.Combine(_root, "second-name.jar");
            File.Copy(source, candidate);
        }
        else candidate = Mod(_root, "second/" + (collision == "name" ? "custom.jar" : "different.jar"),
            collision == "id" ? "custom" : "other", "2");
        await Assert.ThrowsAnyAsync<Exception>(() => Service.AddAsync(instance.Path, candidate));
        Assert.Equal(original, File.ReadAllBytes(existing));
        Assert.Single((await Service.ListAsync(instance.Path)).Files);
        Assert.Single(InstanceEffectiveRequirements.Build(instance.Path, Manifest(instance)).Requirements);
    }

    /// <summary>禁用后完整检查与补全使用禁用文件的原基线，不重新生成启用文件。</summary>
    [Fact]
    public async Task DisabledModRemainsDisabledAfterCheckAndCompletion()
    {
        var instance = await CreateAsync();
        var source = Mod(_root, "incoming/custom.jar");
        await Service.AddAsync(instance.Path, source);
        var path = Relative(instance, "mods/custom.jar");
        await Service.SetEnabledAsync(instance.Path, path, false);
        var entry = Assert.Single((await Service.ListAsync(instance.Path)).Files);
        Assert.Equal(path, entry.Path); Assert.Equal(LocalModState.Disabled, entry.State);
        Assert.False(File.Exists(Path.Combine(instance.Path, path)));
        Assert.True(File.Exists(Path.Combine(instance.Path, entry.CurrentPath!)));
        var checker = new MinecraftInstanceChecker(_root);
        var check = await checker.CheckAsync(instance.Path);
        Assert.Equal(InstanceFileState.Verified, Assert.Single(check.Files, file => file.Requirement.Path == entry.CurrentPath).State);
        await new MinecraftInstanceDownloader(_root, checker, new NoDownload(), prismInstaller: new EmptyInstaller())
            .DownloadMissingAsync(instance.Path);
        Assert.False(File.Exists(Path.Combine(instance.Path, path)));
        Assert.Equal(LocalModState.Disabled, Assert.Single((await Service.ListAsync(instance.Path)).Files).State);
        await Service.SetEnabledAsync(instance.Path, path, true);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(instance.Path, path)));
    }

    /// <summary>禁用不豁免完整性：同长度内容变化与实际文件丢失仍被检查器阻断。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledModDamageStillFailsIntegrity(bool missing)
    {
        var instance = await CreateAsync();
        await Service.AddAsync(instance.Path, Mod(_root, "incoming/custom.jar"));
        var path = Relative(instance, "mods/custom.jar");
        await Service.SetEnabledAsync(instance.Path, path, false);
        var entry = Assert.Single((await Service.ListAsync(instance.Path)).Files);
        var current = Path.Combine(instance.Path, entry.CurrentPath!);
        if (missing) File.Delete(current);
        else { var bytes = File.ReadAllBytes(current); bytes[^1] ^= 1; File.WriteAllBytes(current, bytes); }
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(instance.Path);
        var result = Assert.Single(report.Files, file => file.Requirement.Path == entry.CurrentPath);
        Assert.True(InstanceFileStatePolicy.IsBlocking(result)); Assert.False(report.ContentReady);
        Assert.False(File.Exists(Path.Combine(instance.Path, path)));
    }

    /// <summary>主动回收取消对应玩家文件需求，补全不会还原；恢复保留回收前禁用状态。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedModIsNotRestoredByCompletionAndCanBeRecovered(bool disabled)
    {
        var instance = await CreateAsync();
        var source = Mod(_root, "incoming/custom.jar");
        await Service.AddAsync(instance.Path, source);
        var path = Relative(instance, "mods/custom.jar");
        if (disabled) await Service.SetEnabledAsync(instance.Path, path, false);
        await Service.RemoveAsync(instance.Path, path);
        var removed = Assert.Single((await Service.ListAsync(instance.Path)).Files);
        Assert.Equal(LocalModState.Removed, removed.State); Assert.True(removed.CanRestore);
        Assert.Empty(InstanceEffectiveRequirements.Build(instance.Path, Manifest(instance)).Requirements);
        var checker = new MinecraftInstanceChecker(_root);
        await new MinecraftInstanceDownloader(_root, checker, new NoDownload(), prismInstaller: new EmptyInstaller())
            .DownloadMissingAsync(instance.Path);
        Assert.False(File.Exists(Path.Combine(instance.Path, path)));
        Assert.False(File.Exists(Path.Combine(instance.Path, path + ".disabled")));
        await Service.RestoreAsync(instance.Path, path);
        var restored = Assert.Single((await Service.ListAsync(instance.Path)).Files);
        Assert.Equal(disabled ? LocalModState.Disabled : LocalModState.Enabled, restored.State);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(instance.Path, restored.CurrentPath!)));
    }

    /// <summary>未执行移除操作却丢失文件时，仍保留必要内容需求和原校验依据。</summary>
    [Fact]
    public async Task AccidentalLossIsNotInterpretedAsUserRemoval()
    {
        var instance = await CreateAsync();
        await Service.AddAsync(instance.Path, Mod(_root, "incoming/custom.jar"));
        File.Delete(Path.Combine(instance.Game, "mods/custom.jar"));
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(instance.Path);
        Assert.True(InstanceFileStatePolicy.IsBlocking(Assert.Single(report.Files)));
        Assert.False(report.ContentReady);
        var entry = Assert.Single((await Service.ListAsync(instance.Path)).Files);
        Assert.Equal(LocalModState.Enabled, entry.State); Assert.False(entry.IsPresent);
    }

    /// <summary>官方清单声明的模组不能经接管、启禁或移除转换成用户内容。</summary>
    [Fact]
    public async Task DeclaredPackModIsProtectedAcrossMutationEntrypoints()
    {
        var instance = await CreateAsync();
        var official = await DeclareOfficialAsync(instance, "mods/official.jar");
        var full = Write(instance.Game, "mods/official.jar", "official");
        var entry = Assert.Single((await Service.ListAsync(instance.Path)).Files);
        Assert.False(entry.CanManage); Assert.False(entry.CanAdopt);
        await Assert.ThrowsAnyAsync<Exception>(() => Service.AdoptAsync(instance.Path, official.Path!));
        await Assert.ThrowsAnyAsync<Exception>(() => Service.SetEnabledAsync(instance.Path, official.Path!, false));
        await Assert.ThrowsAnyAsync<Exception>(() => Service.RemoveAsync(instance.Path, official.Path!));
        Assert.Equal("official", File.ReadAllText(full));
        var effective = Assert.Single(InstanceEffectiveRequirements.Build(instance.Path, Manifest(instance)).Requirements);
        Assert.Equal(official.Path, effective.Path); Assert.Equal(official.Origin, effective.Origin);
        Assert.Equal(official.Size, effective.Size); Assert.Equal(official.Hashes["sha1"], effective.Hashes["sha1"]);
    }

    /// <summary>已声明但缺失的官方文件位置仍被保留，不允许同名添加占位。</summary>
    [Fact]
    public async Task MissingDeclaredModCannotBeReplacedByLocalAdd()
    {
        var instance = await CreateAsync();
        var official = await DeclareOfficialAsync(instance, "mods/custom.jar");
        var source = Mod(_root, "incoming/custom.jar");
        await Assert.ThrowsAnyAsync<Exception>(() => Service.AddAsync(instance.Path, source));
        Assert.False(File.Exists(Path.Combine(instance.Path, official.Path!)));
        Assert.Equal(official.Hashes["sha1"], Assert.Single(Manifest(instance).Files).Hashes["sha1"]);
    }

    /// <summary>普通官方模组仍使用 SHA-1 严格校验，不受额外模组管理状态影响。</summary>
    [Fact]
    public async Task OfficialHashMismatchStillBlocksAfterManagingAnotherMod()
    {
        var instance = await CreateAsync();
        var official = await DeclareOfficialAsync(instance, "mods/official.jar");
        Write(instance.Game, "mods/official.jar", "modified");
        await Service.AddAsync(instance.Path, Mod(_root, "incoming/custom.jar"));
        await Service.SetEnabledAsync(instance.Path, Relative(instance, "mods/custom.jar"), false);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(instance.Path);
        var result = Assert.Single(report.Files, file => file.Requirement.Path == official.Path);
        Assert.True(InstanceFileStatePolicy.IsBlocking(result)); Assert.False(report.ContentReady);
        Assert.Equal(official.Hashes["sha1"], result.Requirement.Hashes["sha1"]);
    }

    /// <summary>未登记文件必须明确接管后才允许修改，接管不会改写文件本身。</summary>
    [Fact]
    public async Task ExternalFileRequiresExplicitAdoption()
    {
        var instance = await CreateAsync();
        var full = Mod(instance.Game, "mods/external.jar"); var original = File.ReadAllBytes(full);
        var path = Relative(instance, "mods/external.jar");
        await Assert.ThrowsAnyAsync<Exception>(() => Service.SetEnabledAsync(instance.Path, path, false));
        Assert.Empty(InstanceEffectiveRequirements.Build(instance.Path, Manifest(instance)).Requirements);
        await Service.AdoptAsync(instance.Path, path);
        Assert.True(Assert.Single((await Service.ListAsync(instance.Path)).Files).CanManage);
        Assert.Equal(original, File.ReadAllBytes(full));
        await Service.SetEnabledAsync(instance.Path, path, false);
        Assert.Equal(LocalModState.Disabled, Assert.Single((await Service.ListAsync(instance.Path)).Files).State);
    }

    /// <summary>恢复时同名文件冲突必须保留新旧两份，不覆盖玩家后续添加的文件。</summary>
    [Fact]
    public async Task RestoreCollisionPreservesNewFileAndRecoveryCopy()
    {
        var instance = await CreateAsync();
        await Service.AddAsync(instance.Path, Mod(_root, "incoming/custom.jar"));
        var path = Relative(instance, "mods/custom.jar");
        await Service.RemoveAsync(instance.Path, path);
        Write(instance.Game, "mods/custom.jar", "new user file");
        await Assert.ThrowsAnyAsync<Exception>(() => Service.RestoreAsync(instance.Path, path));
        Assert.Equal("new user file", File.ReadAllText(Path.Combine(instance.Game, "mods/custom.jar")));
        Assert.Contains((await Service.ListAsync(instance.Path)).Files,
            file => file.Path == path && file.State == LocalModState.Removed && file.CanRestore);
    }

    /// <summary>已有实例操作锁阻止模组写入；释放后同一请求可以正常完成。</summary>
    [Fact]
    public async Task InstanceLeaseExcludesConcurrentModWrites()
    {
        var instance = await CreateAsync(); var source = Mod(_root, "incoming/custom.jar");
        using (InstanceOperationLease.Acquire(instance.Path))
            await Assert.ThrowsAsync<IOException>(() => Service.AddAsync(instance.Path, source));
        Assert.False(File.Exists(Path.Combine(instance.Game, "mods/custom.jar")));
        await Service.AddAsync(instance.Path, source);
        Assert.Single((await Service.ListAsync(instance.Path)).Files);
    }

    /// <summary>已取消的请求不会创建启用文件或可信管理记录。</summary>
    [Fact]
    public async Task PreCanceledAddHasNoSideEffects()
    {
        var instance = await CreateAsync(); var source = Mod(_root, "incoming/custom.jar");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.AddAsync(instance.Path, source, cancel.Token));
        Assert.Empty((await Service.ListAsync(instance.Path)).Files);
        Assert.Empty(InstanceEffectiveRequirements.Build(instance.Path, Manifest(instance)).Requirements);
        Assert.True(File.Exists(source));
    }

    /// <summary>管理入口拒绝越界与非模组路径，避免把运行文件或配置当成玩家模组。</summary>
    [Theory]
    [InlineData("../outside.jar")]
    [InlineData(".minecraft/mods/../../outside.jar")]
    [InlineData(".minecraft/config/custom.jar")]
    [InlineData(".minecraft/mods/cache/custom.jar")]
    [InlineData(".minecraft/mods/custom.txt")]
    public async Task UnsafeOrNonModPathsCannotBeAdopted(string path)
    {
        var instance = await CreateAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => Service.AdoptAsync(instance.Path, path));
        Assert.Empty(InstanceEffectiveRequirements.Build(instance.Path, Manifest(instance)).Requirements);
    }

    /// <summary>没有中断记录时恢复可重复调用，不改变正常模组状态与文件。</summary>
    [Fact]
    public async Task RecoveryWithoutPendingTransactionIsIdempotent()
    {
        var instance = await CreateAsync(); var source = Mod(_root, "incoming/custom.jar");
        await Service.AddAsync(instance.Path, source);
        await Service.RecoverAsync(instance.Path); await Service.RecoverAsync(instance.Path);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(instance.Game, "mods/custom.jar")));
        Assert.True(Assert.Single((await Service.ListAsync(instance.Path)).Files).CanManage);
    }
}
