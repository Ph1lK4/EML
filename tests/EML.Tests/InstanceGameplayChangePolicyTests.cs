using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.CurseForge;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Modrinth;

namespace EML.Tests;

/// <summary>用隔离实例验证游戏正常修改、一次性标记和备份往返，不启动游戏或连接网络。</summary>
public sealed class InstanceGameplayChangePolicyTests : IDisposable
{
    private const string Marker = ".minecraft/config/modpack-update";
    private const string Config = ".minecraft/serverutilities/serverutilities.cfg";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-gameplay-policy-" + Guid.NewGuid().ToString("N"));

    /// <summary>精确开放模组配置与典型世界状态，配置目录中的归档和数据包仍为严格内容。</summary>
    [Theory]
    [InlineData(".minecraft/serverutilities/aurora.cfg", true)]
    [InlineData(".minecraft/serverutilities/serverutilities.cfg", true)]
    [InlineData(".minecraft/serverutilities/client/serverutilities.cfg", true)]
    [InlineData(".minecraft/serverutilities/server/players.txt", false)]
    [InlineData(".minecraft/serverutilities/unknown.cfg", false)]
    [InlineData("other/serverutilities/aurora.cfg", false)]
    [InlineData(".minecraft/logs/latest.log", true)]
    [InlineData(".minecraft/logs/2026-11-05-1.log.gz", true)]
    [InlineData(".minecraft/saves/World/level.dat", true)]
    [InlineData(".minecraft/saves/World/DIM-1/region/r.0.0.mca", true)]
    [InlineData(".minecraft/saves/World/playerdata/player.dat", true)]
    [InlineData(".minecraft/saves/World/advancements/player.json", true)]
    [InlineData(".minecraft/saves/World/datapacks/tweaks.zip", false)]
    [InlineData(".minecraft/saves/World/datapacks/tweaks/data/rules.json", false)]
    [InlineData(".minecraft/saves/World/unknown.bin", false)]
    [InlineData(".minecraft/config/library.jar", false)]
    [InlineData(".minecraft/config/paxi/datapacks/tweaks.zip", false)]
    [InlineData(".minecraft/config/tweaks.zip", false)]
    [InlineData(".minecraft/config/native.dll", false)]
    [InlineData(".minecraft/config/setup.ps1", false)]
    [InlineData(".minecraft/config/settings.cfg", true)]
    public void MutablePathsAreNarrowAndProtectExecutableContent(string path, bool expected)
    {
        var file = Embedded(path, "original");
        Assert.Equal(expected, InstanceFileChangePolicy.IsMutableFile(file, ".minecraft", "Prism"));
        Assert.False(InstanceFileChangePolicy.IsMutableFile(file with { Role = InstanceFileRole.Content }, ".minecraft", "Prism"));
        Assert.False(InstanceFileChangePolicy.IsMutableFile(file with { Origin = InstanceFileOrigin.EmbeddedOverride }, ".minecraft", "Prism"));
    }

    /// <summary>旧备份可保留宽泛配置用途，但执行文件及数据包不因此取得放行许可。</summary>
    [Theory]
    [InlineData(".minecraft/config/test.jar")]
    [InlineData(".minecraft/config/paxi/datapacks/test.zip")]
    public void LegacyConfigurationRoleCannotWaiveProtectedContent(string path)
    {
        var file = Embedded(path, "original") with { Role = InstanceFileRole.UserConfiguration };
        Assert.Equal(InstanceFileRole.UserConfiguration, InstanceFileChangePolicy.GetRole(file, ".minecraft"));
        Assert.False(InstanceFileChangePolicy.IsMutableFile(file, ".minecraft"));
    }

    /// <summary>配置和世界状态变化由检查器与安装后验证共同认可，同时不改写原清单。</summary>
    [Theory]
    [InlineData(Config, "user")]
    [InlineData(".minecraft/serverutilities/aurora.cfg", "longer user configuration")]
    [InlineData(".minecraft/serverutilities/client/serverutilities.cfg", "user")]
    [InlineData(".minecraft/logs/latest.log", "new log")]
    [InlineData(".minecraft/saves/World/level.dat", "new world state")]
    public async Task NormalGameplayChangesHaveConsistentContentReadiness(string path, string current)
    {
        var requirement = Embedded(path, "base");
        var fixture = await CreateAsync("Zip", [requirement]);
        Write(fixture.Root, path, current);
        var before = File.ReadAllBytes(Path.Combine(fixture.Root, InstanceImportManifest.FileName));

        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Root);
        var result = Assert.Single(report.Files);
        Assert.Equal(InstanceFileState.ChangedSinceImport, result.State);
        Assert.True(result.IsAllowedConfigurationChange);
        Assert.True(report.ContentReady);
        Assert.Empty(await InstanceContentVerifier.VerifyRequiredAsync(fixture.Root, fixture.Manifest, [requirement], CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(fixture.Root, InstanceImportManifest.FileName)));
        Assert.Equal(current, File.ReadAllText(Path.Combine(fixture.Root, path)));
    }

    /// <summary>显式原包覆盖、用户替代或内容角色不能借普通配置与日志规则解除完整性要求。</summary>
    [Theory]
    [InlineData(Config, InstanceFileOrigin.EmbeddedOverride)]
    [InlineData(Config, InstanceFileOrigin.UserLocalImport)]
    [InlineData(".minecraft/logs/latest.log", InstanceFileOrigin.EmbeddedOverride)]
    [InlineData(".minecraft/saves/World/level.dat", InstanceFileOrigin.UserLocalImport)]
    [InlineData(Config, null)]
    public async Task ExplicitSourceOrContentRoleStillRejectsChangedBytes(string path, InstanceFileOrigin? origin)
    {
        var requirement = Embedded(path, "base") with { Origin = origin, Role = origin is null ? InstanceFileRole.Content : null };
        var fixture = await CreateAsync("Zip", [requirement]);
        Write(fixture.Root, path, "user");
        Assert.False((await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Root)).ContentReady);
        Assert.NotEmpty(await InstanceContentVerifier.VerifyRequiredAsync(fixture.Root, fixture.Manifest, [requirement], CancellationToken.None));
        Assert.Equal("user", File.ReadAllText(Path.Combine(fixture.Root, path)));
    }

    /// <summary>新用途标签也必须匹配已知路径，不能给任意模组声明用户数据或消耗标记用途。</summary>
    [Theory]
    [InlineData(InstanceFileRole.UserData)]
    [InlineData(InstanceFileRole.ConsumableMarker)]
    public void NewRolesCannotWaiveAnArbitraryMod(InstanceFileRole role) =>
        Assert.Throws<InvalidDataException>(() => InstanceFileChangePolicy.GetRole(
            Embedded(".minecraft/mods/test.jar", "base") with { Role = role }, ".minecraft"));

    /// <summary>运行文件和严格来源即便被上层误设允许标志，也不能借新状态绕过最终启动检查。</summary>
    [Fact]
    public void ResultFlagsCannotWaiveRuntimeOrStrictSourceRequirements()
    {
        var marker = Embedded(Marker, "") with { Role = InstanceFileRole.ConsumableMarker };
        var consumed = new InstanceFileCheckResult(marker, InstanceFileState.Consumed, "consumed") { IsAllowedMissing = true };
        Assert.False(InstanceFileStatePolicy.IsBlocking(consumed));
        Assert.True(InstanceFileStatePolicy.IsBlocking(consumed with { IsRuntimeFile = true }));
        Assert.True(InstanceFileStatePolicy.IsBlocking(consumed with { Requirement = marker with { Origin = InstanceFileOrigin.EmbeddedOverride } }));
        Assert.True(InstanceFileStatePolicy.IsBlocking(consumed with { Requirement = marker with { Role = InstanceFileRole.Content } }));
        var changed = new InstanceFileCheckResult(Embedded(Config, "base"), InstanceFileState.ChangedSinceImport, "changed")
            { IsAllowedConfigurationChange = true, IsRuntimeFile = true };
        Assert.True(InstanceFileStatePolicy.IsBlocking(changed));
    }

    /// <summary>零字节标记真正缺失时返回已消耗，不将普通包内配置缺失一并放行。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(InstanceFileRole.UserConfiguration)]
    [InlineData(InstanceFileRole.ConsumableMarker)]
    public async Task ConsumedMarkerHasExplicitStateAndKeepsOriginalIntegrity(InstanceFileRole? role)
    {
        var requirement = Embedded(Marker, "") with { Role = role };
        var fixture = await CreateAsync("Zip", [requirement]);
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Root, InstanceImportManifest.FileName));
        var checker = new MinecraftInstanceChecker(_root);
        foreach (var mode in new[] { InstanceCheckMode.Quick, InstanceCheckMode.Full })
        {
            var report = await checker.CheckAsync(fixture.Root, mode);
            var result = Assert.Single(report.Files);
            Assert.Equal(InstanceFileState.Consumed, result.State);
            Assert.True(result.IsAllowedMissing);
            Assert.False(InstanceFileStatePolicy.IsBlocking(result));
            Assert.Equal(mode == InstanceCheckMode.Full, report.ContentReady);
        }
        Assert.Empty(await InstanceContentVerifier.VerifyRequiredAsync(fixture.Root, fixture.Manifest, [requirement], CancellationToken.None));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Root, InstanceImportManifest.FileName)));
        Assert.False(File.Exists(Path.Combine(fixture.Root, Marker)));
    }

    /// <summary>标记必须来自原包且有精确空内容依据；不能把名字相似或不可信声明当作正常消耗。</summary>
    [Theory]
    [InlineData("wrong-size")]
    [InlineData("wrong-hash")]
    [InlineData("no-entry")]
    [InlineData("extra-hash")]
    [InlineData("strict-role")]
    [InlineData("explicit-origin")]
    [InlineData("different-path")]
    public async Task MissingMarkerExceptionRequiresTrustedEmptyBaseline(string variation)
    {
        var file = Embedded(Marker, "");
        file = variation switch
        {
            "wrong-size" => file with { Size = 1 },
            "wrong-hash" => file with { Hashes = new Dictionary<string, string> { ["sha256"] = Sha256("other") } },
            "no-entry" => file with { ArchiveEntry = null },
            "extra-hash" => file with { Hashes = new Dictionary<string, string>(file.Hashes) { ["sha1"] = new string('a', 40) } },
            "strict-role" => file with { Role = InstanceFileRole.Content },
            "explicit-origin" => file with { Origin = InstanceFileOrigin.EmbeddedOverride },
            _ => file with { Path = ".minecraft/config/modpack-update-other" }
        };
        Assert.False(InstanceFileChangePolicy.AllowsMissing(file, ".minecraft"));
        var fixture = await CreateAsync("Zip", [file]);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Root);
        Assert.False(report.ContentReady);
        Assert.True(InstanceFileStatePolicy.IsBlocking(Assert.Single(report.Files)));
        Assert.NotEmpty(await InstanceContentVerifier.VerifyRequiredAsync(fixture.Root, fixture.Manifest, [file], CancellationToken.None));
    }

    /// <summary>现存非空标记不能获得可变配置豁免，错误字节继续阻断而且不会被删除。</summary>
    [Fact]
    public async Task PresentChangedMarkerRemainsStrict()
    {
        var requirement = Embedded(Marker, "");
        var fixture = await CreateAsync("Zip", [requirement]);
        Write(fixture.Root, Marker, "not empty");
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Root);
        Assert.False(report.ContentReady);
        Assert.False(Assert.Single(report.Files).IsAllowedConfigurationChange);
        Assert.NotEmpty(await InstanceContentVerifier.VerifyRequiredAsync(fixture.Root, fixture.Manifest, [requirement], CancellationToken.None));
        Assert.Equal("not empty", File.ReadAllText(Path.Combine(fixture.Root, Marker)));
    }

    /// <summary>可修改文件仍必须存在，日志或存档不因用途变更而掩盖真实丢失。</summary>
    [Theory]
    [InlineData(Config)]
    [InlineData(".minecraft/logs/latest.log")]
    [InlineData(".minecraft/saves/World/level.dat")]
    public async Task MissingMutableFilesStillBlock(string path)
    {
        var requirement = Embedded(path, "base");
        var fixture = await CreateAsync("Zip", [requirement]);
        Assert.False((await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Root)).ContentReady);
        Assert.NotEmpty(await InstanceContentVerifier.VerifyRequiredAsync(fixture.Root, fixture.Manifest, [requirement], CancellationToken.None));
    }

    /// <summary>目录占位或父路径是文件属于访问结构错误，不能被缺失许可吞掉。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MarkerDirectoryAndParentFileAreUnreadable(bool targetDirectory)
    {
        var requirement = Embedded(Marker, "");
        var fixture = await CreateAsync("Zip", [requirement]);
        if (targetDirectory) Directory.CreateDirectory(Path.Combine(fixture.Root, Marker));
        else Write(fixture.Root, ".minecraft/config", "parent is file");
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Root);
        Assert.Equal(InstanceFileState.Unreadable, Assert.Single(report.Files).State);
        Assert.False(report.ContentReady);
        Assert.NotEmpty(await InstanceContentVerifier.VerifyRequiredAsync(fixture.Root, fixture.Manifest, [requirement], CancellationToken.None));
    }

    /// <summary>文件被独占打开导致无法读取时，正常修改许可不能把访问失败当成成功。</summary>
    [Fact]
    public async Task LockedConfigurationIsNotAcceptedAsUserModification()
    {
        var requirement = Embedded(Config, "base");
        var fixture = await CreateAsync("Zip", [requirement]);
        Write(fixture.Root, Config, "user");
        using var locked = new FileStream(Path.Combine(fixture.Root, Config), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Root);
        Assert.Equal(InstanceFileState.Unreadable, Assert.Single(report.Files).State);
        Assert.False(report.ContentReady);
        Assert.NotEmpty(await InstanceContentVerifier.VerifyRequiredAsync(fixture.Root, fixture.Manifest, [requirement], CancellationToken.None));
    }

    /// <summary>两个平台安装器保留玩家配置、不恢复已消耗标记，运行阶段独立完成。</summary>
    [Theory]
    [InlineData("CurseForge")]
    [InlineData("Modrinth")]
    public async Task PlatformInstallersUseTheSameGameplayPolicyAndDoNotRestoreMarker(string format)
    {
        var requirements = new[] { Embedded(Marker, ""), Embedded(Config, "base") };
        var fixture = await CreateAsync(format, requirements);
        Write(fixture.Root, Config, "player settings");
        CreateOriginalArchive(fixture.Manifest, (Marker, ""), (Config, "base"));
        var downloader = new RejectDownloader();
        var runtime = new ReadyRuntime();
        var result = format == "CurseForge"
            ? await new CurseForgeInstanceInstaller(_root, downloader, runtimeInstaller: runtime).InstallAsync(fixture.Root)
            : await new ModrinthInstanceInstaller(_root, downloader, runtimeInstaller: runtime).InstallAsync(fixture.Root);

        Assert.True(result.LaunchReady, string.Join(Environment.NewLine, result.Issues));
        Assert.Empty(result.ContentIssues);
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(0, downloader.Calls);
        Assert.DoesNotContain(result.Files, file => file.State == InstanceDownloadState.RestoredEmbeddedFile);
        Assert.Contains(result.Notes, note => note.Contains("已消耗"));
        Assert.False(File.Exists(Path.Combine(fixture.Root, Marker)));
        Assert.Equal("player settings", File.ReadAllText(Path.Combine(fixture.Root, Config)));
        Assert.True((await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Root)).ContentReady);
    }

    /// <summary>通用补全依据已消耗状态跳过标记，也不能生成一条缺失失败记录。</summary>
    [Fact]
    public async Task GenericDownloaderDoesNotTreatConsumedMarkerAsMissing()
    {
        var fixture = await CreateAsync("Zip", [Embedded(Marker, "")]);
        var downloader = new RejectDownloader();
        var report = await new MinecraftInstanceDownloader(_root, new MinecraftInstanceChecker(_root), downloader).DownloadMissingAsync(fixture.Root);
        Assert.Empty(report.Files);
        Assert.True(report.ContentReady);
        Assert.Equal(0, downloader.Calls);
        Assert.False(File.Exists(Path.Combine(fixture.Root, Marker)));
    }

    /// <summary>备份往返保留旧角色与原始哈希，玩家变化继续允许，已消耗标记保持缺失。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(InstanceFileRole.UserConfiguration)]
    [InlineData(InstanceFileRole.Content)]
    public async Task BackupRoundTripKeepsOriginalBaselineAndMarkerPolicy(InstanceFileRole? markerRole)
    {
        var files = new[] { Embedded(Marker, "") with { Role = markerRole }, Embedded(Config, "base") };
        var fixture = await CreateAsync("Zip", files);
        Write(fixture.Root, Config, "player settings");
        var originalManifest = File.ReadAllBytes(Path.Combine(fixture.Root, InstanceImportManifest.FileName));
        var backup = Path.Combine(_root, "backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(fixture.Root, backup);
        var restored = await new MinecraftInstanceImporter(_root).ImportAsync(backup, "Restored");
        Assert.Equal(originalManifest, File.ReadAllBytes(Path.Combine(fixture.Root, InstanceImportManifest.FileName)));
        foreach (var original in files)
        {
            var file = Assert.Single(restored.Manifest.Files, file => file.Path == original.Path);
            Assert.Equal(original.Source, file.Source);
            Assert.Equal(original.Origin, file.Origin);
            Assert.Equal(original.Size, file.Size);
            Assert.Equal(original.Hashes["sha256"], file.Hashes["sha256"]);
        }
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(restored.InstancePath);
        Assert.Equal(markerRole != InstanceFileRole.Content, report.ContentReady);
        Assert.Equal(markerRole == InstanceFileRole.Content ? InstanceFileState.Missing : InstanceFileState.Consumed,
            Assert.Single(report.Files, file => file.Requirement.Path == Marker).State);
        Assert.False(File.Exists(Path.Combine(restored.InstancePath, Marker)));
        Assert.Equal("player settings", File.ReadAllText(Path.Combine(restored.InstancePath, Config)));
    }

    /// <summary>用户明确替代已分类的标记后，新来源被认可并保持严格，备份后也不能再按消耗规则放行。</summary>
    [Fact]
    public async Task AcceptedMarkerReplacementRetainsItsOwnStrictBaselineAcrossBackup()
    {
        var requirement = Embedded(Marker, "") with { Role = InstanceFileRole.ConsumableMarker };
        var fixture = await CreateAsync("Zip", [requirement]);
        Write(fixture.Root, Marker, "user chosen content");
        await new InstanceLocalReplacementService(_root).AcceptExistingAsync(fixture.Root, Marker);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Root);
        Assert.True(report.ContentReady);
        Assert.Equal(InstanceFileOrigin.UserLocalImport, Assert.Single(report.Files).Requirement.Origin);
        var backup = Path.Combine(_root, "accepted-backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(fixture.Root, backup);
        var restored = await new MinecraftInstanceImporter(_root).ImportAsync(backup, "Restored");
        var selected = Assert.Single(restored.Manifest.Files);
        Assert.Equal(InstanceFileOrigin.UserLocalImport, selected.Origin);
        Assert.False(InstanceFileChangePolicy.AllowsMissing(selected, ".minecraft"));
        Assert.True((await new MinecraftInstanceChecker(_root).CheckAsync(restored.InstancePath)).ContentReady);
        File.Delete(Path.Combine(restored.InstancePath, Marker));
        Assert.False((await new MinecraftInstanceChecker(_root).CheckAsync(restored.InstancePath)).ContentReady);
        Assert.NotEmpty(await InstanceContentVerifier.VerifyRequiredAsync(restored.InstancePath, restored.Manifest, [selected], CancellationToken.None));
    }

    /// <summary>生成独立实例与清单，后续所有磁盘操作均限制在测试临时根目录。</summary>
    private async Task<(string Root, InstanceImportManifest Manifest)> CreateAsync(string format, IReadOnlyList<InstanceFileRequirement> files)
    {
        var root = Path.Combine(_root, "instances", "Fixture");
        Directory.CreateDirectory(Path.Combine(root, ".minecraft"));
        var manifest = new InstanceImportManifest { Name = "Fixture", SourceFormat = format, SourceArchive = "original.zip", Files = files };
        await InstanceManifestStore.WriteAsync(Path.Combine(root, InstanceImportManifest.FileName), manifest, CancellationToken.None);
        return (root, manifest);
    }

    /// <summary>构造普通包内文件的原始快照，不把当前磁盘内容重新授权为可信基线。</summary>
    private static InstanceFileRequirement Embedded(string path, string original) => new()
    {
        Path = path, ArchiveEntry = path, Source = InstanceFileSource.Embedded,
        Size = Encoding.UTF8.GetByteCount(original), Hashes = new Dictionary<string, string> { ["sha256"] = Sha256(original) }
    };

    /// <summary>准备可被恢复器找到的原 ZIP，确保“不恢复标记”不是因为原包恰好不存在。</summary>
    private void CreateOriginalArchive(InstanceImportManifest manifest, params (string Path, string Content)[] files)
    {
        using var archive = ZipFile.Open(Path.Combine(_root, manifest.SourceArchive), ZipArchiveMode.Create);
        foreach (var (path, content) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    /// <summary>只在当前测试实例中写入模拟用户内容。</summary>
    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// <summary>计算固定测试内容摘要。</summary>
    private static string Sha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>仅清理本测试创建的唯一临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>测试无需下载；意外请求会明确失败并留下计数。</summary>
    private sealed class RejectDownloader : IFileDownloader
    {
        public int Calls { get; private set; }

        /// <summary>拒绝所有网络下载，不创建或改写目标文件。</summary>
        public Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("本测试不允许下载。");
        }
    }

    /// <summary>隔离运行依赖阶段，以检查内容策略是否正确传递就绪结果。</summary>
    private sealed class ReadyRuntime : IPrismInstanceInstaller
    {
        public int Calls { get; private set; }

        /// <summary>返回已经准备好的测试运行配置，不启动游戏或产生外部请求。</summary>
        public Task<PrismInstallResult> InstallAsync(string instancePath, IProgress<InstanceDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PrismInstallResult { LaunchProfilePath = "fixture.json" });
        }
    }
}
