using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.CurseForge;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Core.Modrinth;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.CurseForge;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Modrinth;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Tests;

/// <summary>验证真实导出重导入不会改变可信来源，API、下载和运行阶段均不访问外部网络。</summary>
public sealed class InstanceBackupIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-backup-integrity-" + Guid.NewGuid().ToString("N"));
    private const string ModPath = ".minecraft/mods/test.jar";
    private const string EmbeddedEntry = "overrides/mods/test.jar";
    private const string OfficialContent = "good";
    private const string EmbeddedContent = "pack";

    /// <summary>导入、改名和备份改变普通 Prism 显示名时保留原始声明，统一策略认可元数据变化。</summary>
    [Fact]
    public async Task PrismNameChangesPreserveOriginalBaselineAcrossBackup()
    {
        const string config = "[General]\nname=Package name\n";
        var archive = CreateArchive(("mmc-pack.json", "{\"formatVersion\":1,\"components\":[]}"), ("instance.cfg", config));
        var original = await new MinecraftInstanceImporter(_root).ImportAsync(archive, "Original");
        var baseline = Assert.Single(original.Manifest.Files, file => file.Path == "instance.cfg");
        Assert.Equal(Sha256(config), baseline.Hashes["sha256"]);
        Assert.Equal("instance.cfg", baseline.ArchiveEntry);
        var renamed = await new MinecraftInstanceManager(_root).RenameAsync(original.InstancePath, "Renamed");
        var (restored, _) = await RoundTripAsync(original with { InstancePath = renamed });

        var selected = Assert.Single(restored.Manifest.Files, file => file.Path == "instance.cfg");
        Assert.Equal(baseline.Size, selected.Size);
        Assert.Equal(baseline.Hashes["sha256"], selected.Hashes["sha256"]);
        Assert.Null(selected.Origin);
        Assert.Equal(InstanceFileRole.LauncherMetadata, selected.Role);
        Assert.Contains("name=Restored", File.ReadAllText(Path.Combine(restored.InstancePath, "instance.cfg")));
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(restored.InstancePath);
        Assert.Equal(InstanceFileState.ChangedSinceImport, Assert.Single(report.Files, file => file.Requirement.Path == "instance.cfg").State);
        Assert.Empty(report.ContentIssues);
    }

    /// <summary>严格来源的 Prism 配置不被备份导入隐式修改或重建哈希，改名也须在写入前拒绝。</summary>
    [Theory]
    [InlineData(InstanceFileOrigin.EmbeddedOverride)]
    [InlineData(InstanceFileOrigin.UserLocalImport)]
    [InlineData(null)]
    public async Task StrictPrismMetadataPreservesBytesAndRejectsImplicitRename(InstanceFileOrigin? origin)
    {
        const string config = "[General]\nname=Protected name\n";
        var requirement = new InstanceFileRequirement
        {
            Path = "instance.cfg", Source = InstanceFileSource.Embedded, Origin = origin,
            Role = origin is null ? InstanceFileRole.Content : null,
            Size = Encoding.UTF8.GetByteCount(config), Hashes = new Dictionary<string, string> { ["sha256"] = Sha256(config) },
            ArchiveEntry = origin == InstanceFileOrigin.UserLocalImport ? null : "instance.cfg"
        };
        var manifest = new InstanceImportManifest { SourceFormat = "Prism", Name = "Protected", Files = [requirement] };
        var archive = CreateArchive((InstanceImportManifest.FileName, Encoding.UTF8.GetString(InstanceManifestStore.Serialize(manifest))),
            ("mmc-pack.json", "{\"formatVersion\":1,\"components\":[]}"), ("instance.cfg", config));

        var imported = await new MinecraftInstanceImporter(_root).ImportAsync(archive, "Imported");

        var selected = Assert.Single(imported.Manifest.Files);
        Assert.Equal(requirement.Origin, selected.Origin);
        Assert.Equal(requirement.Role, selected.Role);
        Assert.Equal(requirement.Hashes["sha256"], selected.Hashes["sha256"]);
        Assert.Equal(config, File.ReadAllText(Path.Combine(imported.InstancePath, "instance.cfg")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MinecraftInstanceManager(_root).RenameAsync(imported.InstancePath, "Renamed"));
        Assert.True(Directory.Exists(imported.InstancePath));
        Assert.False(Directory.Exists(Path.Combine(_root, "instances", "Renamed")));
        Assert.Equal(config, File.ReadAllText(Path.Combine(imported.InstancePath, "instance.cfg")));
    }

    /// <summary>本机明确接受的 Prism 配置也属于严格来源，管理器必须读取用户记录后决定是否可改名。</summary>
    [Fact]
    public async Task AcceptedPrismConfigIsNotChangedByRename()
    {
        var archive = CreateArchive(("mmc-pack.json", "{\"formatVersion\":1,\"components\":[]}"),
            ("instance.cfg", "[General]\nname=Package name\n"));
        var imported = await new MinecraftInstanceImporter(_root).ImportAsync(archive, "Imported");
        const string acceptedConfig = "[General]\nname=Chosen name\n";
        WriteFile(imported.InstancePath, "instance.cfg", acceptedConfig);
        await new InstanceLocalReplacementService(_root).AcceptExistingAsync(imported.InstancePath, "instance.cfg");
        var recordPath = Path.Combine(imported.InstancePath, InstanceLocalReplacementService.RelativePath);
        var record = File.ReadAllBytes(recordPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new MinecraftInstanceManager(_root).RenameAsync(imported.InstancePath, "Renamed"));

        Assert.Equal(acceptedConfig, File.ReadAllText(Path.Combine(imported.InstancePath, "instance.cfg")));
        Assert.Equal(record, File.ReadAllBytes(recordPath));
        Assert.True(Directory.Exists(imported.InstancePath));
    }

    /// <summary>普通官方文件导出后仍使用原 SHA-1；同大小坏字节不能重新成为包内可信基线。</summary>
    [Theory]
    [InlineData(OfficialContent, true)]
    [InlineData("evil", false)]
    public async Task OfficialFileIntegritySurvivesBackupRoundTrip(string content, bool valid)
    {
        var (original, _) = await ImportCurseForgeAsync(includeOfficialReference: true);
        var resolver = new CurseForgeFileResolver(new FakeCurseForgeApi());
        await resolver.ResolveAsync(original.InstancePath, original.Manifest);
        WriteFile(original.InstancePath, ModPath, content);

        var (restored, _) = await RoundTripAsync(original);

        var saved = Assert.Single(restored.Manifest.Files, file => file.Source == InstanceFileSource.CurseForge);
        Assert.Equal(ModPath, saved.Path);
        Assert.Equal(InstanceFileOrigin.CurseForgeDownload, saved.Origin);
        Assert.Equal(4, saved.Size);
        Assert.Equal(Sha1(OfficialContent), Assert.Single(saved.Hashes).Value);
        Assert.True(saved.Hashes.ContainsKey("sha1"));
        Assert.DoesNotContain(restored.Manifest.Files, file => file.Source == InstanceFileSource.Embedded && file.Path == ModPath);
        var resolution = await resolver.ResolveAsync(restored.InstancePath, restored.Manifest);
        var effective = Assert.Single(CurseForgeFileStateStore.EffectiveRequirements(restored.Manifest, resolution), file => file.Path == ModPath);
        Assert.Equal(InstanceFileOrigin.CurseForgeDownload, effective.Origin);
        if (valid)
            Assert.True(await MatchesAsync(restored, effective));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => MatchesAsync(restored, effective));
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(restored.InstancePath);
        Assert.Equal(valid ? InstanceFileState.Verified : InstanceFileState.HashMismatch,
            Assert.Single(report.Files, file => file.Requirement.Path == ModPath).State);
        Assert.Equal(content, File.ReadAllText(Target(restored)));
    }

    /// <summary>已损坏的原包嵌入文件在备份中保留损坏字节供修复，但完整性基线必须仍是原包摘要。</summary>
    [Fact]
    public async Task ChangedEmbeddedFileIsNotRebaselinedByBackup()
    {
        var (original, _) = await ImportCurseForgeAsync(embedded: EmbeddedContent);
        var baseline = Assert.Single(original.Manifest.Files, file => file.Path == ModPath);
        WriteFile(original.InstancePath, ModPath, "evil");

        var (restored, _) = await RoundTripAsync(original);

        var restoredRequirement = Assert.Single(restored.Manifest.Files, file => file.Path == ModPath);
        Assert.Equal(baseline.Size, restoredRequirement.Size);
        Assert.Equal(baseline.Hashes["sha256"], restoredRequirement.Hashes["sha256"]);
        Assert.NotEqual(Sha256("evil"), restoredRequirement.Hashes["sha256"]);
        await Assert.ThrowsAsync<InvalidDataException>(() => MatchesAsync(restored, restoredRequirement));
        Assert.Equal("evil", File.ReadAllText(Target(restored)));
    }

    /// <summary>mods 中真正的原包覆盖与官方内容不同也保持来源独立，不能依赖 Paxi 路径特判。</summary>
    [Fact]
    public async Task EmbeddedOverrideAndOfficialRequirementRemainIndependentAcrossBackup()
    {
        var (original, _) = await ImportCurseForgeAsync(includeOfficialReference: true, embedded: EmbeddedContent);
        var resolver = new CurseForgeFileResolver(new FakeCurseForgeApi());
        await resolver.ResolveAsync(original.InstancePath, original.Manifest);

        var (restored, _) = await RoundTripAsync(original);

        var embedded = Assert.Single(restored.Manifest.Files, file => file.Path == ModPath);
        Assert.Equal(InstanceFileOrigin.EmbeddedOverride, embedded.Origin);
        Assert.Equal(InstanceFileSource.Embedded, embedded.Source);
        Assert.Equal(Sha256(EmbeddedContent), embedded.Hashes["sha256"]);
        Assert.Single(embedded.Hashes);
        Assert.Empty(embedded.Downloads);
        Assert.Equal(10, embedded.CurseForgeOrigin!.ProjectId);
        Assert.Equal(20, embedded.CurseForgeOrigin.FileId);
        Assert.Equal(Sha1(OfficialContent), embedded.CurseForgeOrigin.Hashes["sha1"]);
        var officialReference = Assert.Single(restored.Manifest.Files, file => file.Source == InstanceFileSource.CurseForge);
        Assert.Equal(10, officialReference.ProjectId);
        Assert.Equal(20, officialReference.FileId);
        Assert.Null(officialReference.Path);

        var resolution = await resolver.ResolveAsync(restored.InstancePath, restored.Manifest);
        var effective = Assert.Single(CurseForgeFileStateStore.EffectiveRequirements(restored.Manifest, resolution), file => file.Path == ModPath);
        Assert.Equal(InstanceFileOrigin.EmbeddedOverride, effective.Origin);
        Assert.Single(effective.Hashes);
        Assert.True(await MatchesAsync(restored, effective));
    }

    /// <summary>用户确认的替换导出为可移植需求，重导入后检查器和对应补全器认可同一份选择。</summary>
    [Theory]
    [InlineData("CurseForge")]
    [InlineData("Modrinth")]
    public async Task AcceptedReplacementSurvivesBackupAndSubsequentCompletion(string sourceFormat)
    {
        var original = sourceFormat == "CurseForge"
            ? (await ImportCurseForgeAsync(includeOfficialReference: true, embedded: EmbeddedContent)).Instance
            : await ImportModrinthAsync();
        if (sourceFormat == "CurseForge")
            await new CurseForgeFileResolver(new FakeCurseForgeApi()).ResolveAsync(original.InstancePath, original.Manifest);
        WriteFile(original.InstancePath, ModPath, "user");
        var accepted = await new InstanceLocalReplacementService(_root).AcceptExistingAsync(original.InstancePath, ModPath);

        var (restored, backup) = await RoundTripAsync(original);

        var selected = Assert.Single(restored.Manifest.Files, file => file.Path == ModPath);
        Assert.Equal(InstanceFileOrigin.UserLocalImport, selected.Origin);
        Assert.Equal(accepted.Size, selected.Size);
        Assert.Equal(accepted.Hashes["sha256"], selected.Hashes["sha256"]);
        Assert.Single(selected.Hashes);
        Assert.Empty(selected.Downloads);
        using (var archive = ZipFile.OpenRead(backup))
            Assert.DoesNotContain(archive.Entries, entry => IsInternal(entry.FullName));
        Assert.False(File.Exists(Path.Combine(restored.InstancePath, InstanceLocalReplacementService.RelativePath)));
        if (sourceFormat == "CurseForge")
        {
            Assert.Equal(10, selected.CurseForgeOrigin!.ProjectId);
            Assert.Equal(Sha1(OfficialContent), selected.CurseForgeOrigin.Hashes["sha1"]);
            await new CurseForgeFileResolver(new FakeCurseForgeApi()).ResolveAsync(restored.InstancePath, restored.Manifest);
        }
        var checkedFile = Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(restored.InstancePath)).Files,
            file => file.Requirement.Path == ModPath);
        Assert.Equal(InstanceFileState.Verified, checkedFile.State);
        Assert.Equal(InstanceFileOrigin.UserLocalImport, checkedFile.Requirement.Origin);

        var runtime = new FakeRuntime(sourceFormat);
        var downloader = new RejectDownloads();
        var result = sourceFormat == "CurseForge"
            ? await new CurseForgeInstanceInstaller(_root, downloader, new FakeCurseForgeApi(), runtime,
                modrinthApiClient: new RejectModrinthApi()).InstallAsync(restored.InstancePath)
            : await new ModrinthInstanceInstaller(_root, downloader, runtimeInstaller: runtime).InstallAsync(restored.InstancePath);

        Assert.True(result.FilesReady, string.Join(Environment.NewLine, result.Issues));
        Assert.Empty(result.ContentIssues);
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(0, downloader.Calls);
        Assert.Equal("user", File.ReadAllText(Target(restored)));

        WriteFile(restored.InstancePath, ModPath, "evil");
        await Assert.ThrowsAsync<InvalidDataException>(() => MatchesAsync(restored, selected));
    }

    /// <summary>程序内部缓存不随备份成为内容；重导入后刷新解析缓存也不会产生内容完整性错误。</summary>
    [Fact]
    public async Task RuntimeCachesAreExcludedFromBackupAndContentRequirements()
    {
        var (original, _) = await ImportCurseForgeAsync(includeOfficialReference: true);
        var resolver = new CurseForgeFileResolver(new FakeCurseForgeApi());
        await resolver.ResolveAsync(original.InstancePath, original.Manifest);
        WriteFile(original.InstancePath, ModPath, OfficialContent);
        WriteFile(original.InstancePath, ".eml-runtime/temporary/state.json", "generated cache");

        var (restored, backup) = await RoundTripAsync(original);

        using (var archive = ZipFile.OpenRead(backup))
            Assert.DoesNotContain(archive.Entries, entry => IsInternal(entry.FullName));
        Assert.DoesNotContain(restored.Manifest.Files, file => file.Path is not null && IsInternal(file.Path));
        await resolver.ResolveAsync(restored.InstancePath, restored.Manifest);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(restored.InstancePath);
        Assert.DoesNotContain(report.Files, file => file.Requirement.Path is not null && IsInternal(file.Requirement.Path));
        Assert.Equal(InstanceFileState.Verified, Assert.Single(report.Files, file => file.Requirement.Path == ModPath).State);
    }

    /// <summary>无解析映射的旧备份保留未知 JAR 的字节，但不能把它升级为足以覆盖官方需求的 Embedded 来源。</summary>
    [Fact]
    public async Task LegacyBackupWithoutResolutionCannotPromoteUnlistedJarToEmbedded()
    {
        var (original, _) = await ImportCurseForgeAsync(includeOfficialReference: true);
        var legacyArchive = CreateArchive(
            (InstanceImportManifest.FileName, File.ReadAllText(Path.Combine(original.InstancePath, InstanceImportManifest.FileName))),
            (ModPath, "evil"), (".eml-runtime/obsolete-cache.json", "untrusted legacy cache"));

        var restored = await new MinecraftInstanceImporter(_root).ImportAsync(legacyArchive, "Legacy restored");

        Assert.Equal("evil", File.ReadAllText(Target(restored)));
        Assert.DoesNotContain(restored.Manifest.Files, file => file.Source == InstanceFileSource.Embedded && file.Path == ModPath);
        Assert.Null(Assert.Single(restored.Manifest.Files, file => file.Source == InstanceFileSource.CurseForge).Path);
        Assert.DoesNotContain(restored.Manifest.Files, file => file.Path is not null && IsInternal(file.Path));
        Assert.False(File.Exists(Path.Combine(restored.InstancePath, ".eml-runtime/obsolete-cache.json")));
        var resolution = await new CurseForgeFileResolver(new FakeCurseForgeApi()).ResolveAsync(restored.InstancePath, restored.Manifest);
        var effective = Assert.Single(CurseForgeFileStateStore.EffectiveRequirements(restored.Manifest, resolution), file => file.Path == ModPath);
        Assert.Equal(InstanceFileOrigin.CurseForgeDownload, effective.Origin);
        await Assert.ThrowsAsync<InvalidDataException>(() => MatchesAsync(restored, effective));
    }

    /// <summary>原下载包删除后，重导入备份的 Embedded 文件可从本次备份条目恢复，仍使用原始 SHA-256。</summary>
    [Fact]
    public async Task ReimportedEmbeddedFileRestoresFromBackupEntryWhenOriginalArchiveIsGone()
    {
        var (original, originalArchive) = await ImportCurseForgeAsync(includeOfficialReference: true, embedded: EmbeddedContent);
        await new CurseForgeFileResolver(new FakeCurseForgeApi()).ResolveAsync(original.InstancePath, original.Manifest);
        var originalHash = Assert.Single(original.Manifest.Files, file => file.Path == ModPath).Hashes["sha256"];
        var (restored, backup) = await RoundTripAsync(original);
        var embedded = Assert.Single(restored.Manifest.Files, file => file.Path == ModPath);
        Assert.Equal(ModPath, embedded.ArchiveEntry);
        Assert.Equal(originalHash, embedded.Hashes["sha256"]);
        Assert.Equal(backup, InstanceImportSourceStore.Read(restored.InstancePath, restored.Manifest));
        File.Delete(originalArchive);
        File.Delete(Target(restored));

        var restoredFile = await new EmbeddedFileRestorer(_root).RestoreAsync(restored.InstancePath, restored.Manifest,
            embedded, CancellationToken.None);

        Assert.True(restoredFile);
        Assert.Equal(EmbeddedContent, File.ReadAllText(Target(restored)));
        Assert.True(await MatchesAsync(restored, embedded));
    }

    /// <summary>创建带固定平台 ID 的真实 CurseForge 导入，可选加入同名原包覆盖文件。</summary>
    private async Task<(InstanceImportResult Instance, string Archive)> ImportCurseForgeAsync(
        bool includeOfficialReference = false, string? embedded = null)
    {
        var manifest = JsonSerializer.Serialize(new
        {
            manifestType = "minecraftModpack", manifestVersion = 1, name = "Fixture", version = "1",
            minecraft = new { version = "1.20.1", modLoaders = Array.Empty<object>() },
            files = includeOfficialReference ? new[] { new { projectID = 10, fileID = 20, required = true } } : [],
            overrides = "overrides"
        });
        var entries = new List<(string Path, string Content)> { ("manifest.json", manifest) };
        if (embedded is not null) entries.Add((EmbeddedEntry, embedded));
        var archive = CreateArchive(entries.ToArray());
        return (await new MinecraftInstanceImporter(_root).ImportAsync(archive, "Original"), archive);
    }

    /// <summary>真实导入包含包内模组的 Modrinth 清单，为替换跨平台组合测试准备来源。</summary>
    private Task<InstanceImportResult> ImportModrinthAsync() => new MinecraftInstanceImporter(_root).ImportAsync(CreateArchive(
        ("modrinth.index.json", """
            {"formatVersion":1,"game":"minecraft","versionId":"1","name":"Fixture","files":[],
             "dependencies":{"minecraft":"1.20.1","fabric-loader":"0.16.10"}}
            """), (EmbeddedEntry, EmbeddedContent)), "Original");

    /// <summary>通过生产管理器和导入器往返，并确认导出不改写原实例清单。</summary>
    private async Task<(InstanceImportResult Instance, string Archive)> RoundTripAsync(InstanceImportResult original)
    {
        var manifestPath = Path.Combine(original.InstancePath, InstanceImportManifest.FileName);
        var originalManifest = File.ReadAllBytes(manifestPath);
        var archive = Path.Combine(_root, "backup-" + Guid.NewGuid().ToString("N") + ".zip");
        await new MinecraftInstanceManager(_root).ExportAsync(original.InstancePath, archive);
        Assert.Equal(originalManifest, File.ReadAllBytes(manifestPath));
        return (await new MinecraftInstanceImporter(_root).ImportAsync(archive, "Restored"), archive);
    }

    /// <summary>创建唯一测试压缩包，不覆盖外部数据。</summary>
    private string CreateArchive(params (string Path, string Content)[] entries)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry.Path).Open(), new UTF8Encoding(false));
            writer.Write(entry.Content);
        }
        return path;
    }

    /// <summary>只写入当前测试实例内指定文件，自动建立父目录。</summary>
    private static void WriteFile(string instance, string relative, string content)
    {
        var path = Path.Combine(instance, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// <summary>定位当前测试模组，不扫描或访问其他实例。</summary>
    private static string Target(InstanceImportResult instance) => Path.Combine(instance.InstancePath, ModPath);

    /// <summary>调用生产完整性匹配器，保留校验不符时的真实异常契约。</summary>
    private static Task<bool> MatchesAsync(InstanceImportResult instance, InstanceFileRequirement requirement) =>
        InstanceRuntimeInstaller.ExistingFileMatchesAsync(Target(instance), requirement, CancellationToken.None);

    /// <summary>识别根目录 EML 自有缓存，不把游戏内容中相似目录名误算进去。</summary>
    private static bool IsInternal(string path) => path.TrimEnd('/').Equals(".eml-runtime", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(".eml-runtime/", StringComparison.OrdinalIgnoreCase);

    /// <summary>计算官方文件的测试 SHA-1。</summary>
    private static string Sha1(string content) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>计算包内及用户选择文件的测试 SHA-256。</summary>
    private static string Sha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>只移除当前测试自行创建的唯一临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>提供固定官方模组元数据，所有返回字节和摘要均为测试声明。</summary>
    private sealed class FakeCurseForgeApi : ICurseForgeApiClient
    {
        public CurseForgeRateLimit? LastRateLimit => null;

        /// <summary>仅为本测试请求的固定文件返回官方 SHA-1 和下载地址。</summary>
        public Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CurseForgeFile>>(ids.Select(id => new CurseForgeFile
            {
                Id = id, ModId = 10, GameId = 432, FileName = "test.jar", FileLength = 4, IsAvailable = true,
                DownloadUrl = "https://edge.forgecdn.net/files/20/test.jar",
                Hashes = [new() { Algo = 1, Value = Sha1(OfficialContent) }]
            }).ToArray());

        /// <summary>将项目固定为可分发的普通模组，避免依赖 Paxi 等特殊目录规则。</summary>
        public Task<IReadOnlyList<CurseForgeMod>> GetModsAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CurseForgeMod>>(ids.Select(id => new CurseForgeMod
            {
                Id = id, GameId = 432, ClassId = 6, AllowModDistribution = true
            }).ToArray());

        /// <summary>拒绝未在测试计划中使用的单文件查询。</summary>
        public Task<CurseForgeFile> GetFileAsync(long projectId, long fileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>拒绝未在测试计划中使用的单项目查询。</summary>
        public Task<CurseForgeMod> GetModAsync(long projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>拒绝搜索，防止回归测试意外依赖外部服务。</summary>
        public Task<CurseForgeSearchResult> SearchAsync(CurseForgeSearchOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>测试内容已在本地，任何下载请求都说明来源选择或校验流程发生回退。</summary>
    private sealed class RejectDownloads : IFileDownloader
    {
        public int Calls { get; private set; }

        /// <summary>记录并拒绝下载，不创建任何网络请求。</summary>
        public Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("备份已有完整测试文件，不应下载替代内容。");
        }
    }

    /// <summary>只确认内容阶段允许进入运行阶段，不真实下载依赖或启动游戏。</summary>
    private sealed class FakeRuntime(string sourceFormat) : IPrismInstanceInstaller
    {
        public int Calls { get; private set; }

        /// <summary>返回明确的运行阶段完成结果，方便验证内容来源不会被外层安装器重置。</summary>
        public Task<PrismInstallResult> InstallAsync(string instancePath, IProgress<InstanceDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PrismInstallResult { SourceFormat = sourceFormat, LaunchProfilePath = "fixture-profile.json" });
        }
    }

    /// <summary>本测试无需备用平台，注入拒绝客户端保证任何分支都不会连接网络。</summary>
    private sealed class RejectModrinthApi : IModrinthApiClient
    {
        public ModrinthRateLimit? LastRateLimit => null;

        /// <summary>拒绝未声明的项目搜索。</summary>
        public Task<ModrinthSearchResult> SearchAsync(ModrinthSearchOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>拒绝未声明的项目查询。</summary>
        public Task<ModrinthProject> GetProjectAsync(string projectIdOrSlug, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>拒绝未声明的版本查询。</summary>
        public Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>拒绝同内容备用查询，避免掩盖错误的原包或用户来源回退。</summary>
        public Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(IReadOnlyList<string> hashes,
            string algorithm = "sha1", CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>拒绝未声明的版本列表请求。</summary>
        public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectIdOrSlug, ModrinthVersionFilter? filter = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
