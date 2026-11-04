using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using EML.Core.CurseForge;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.CurseForge;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Tests;

/// <summary>在独立临时目录验证原包恢复和用户明确接受的替换，不接触真实实例或网络。</summary>
public sealed class EmbeddedFileRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-embedded-recovery-" + Guid.NewGuid().ToString("N"));
    private const string ArchiveEntry = "overrides/config/paxi/datapacks/test.zip";
    private const string RelativePath = ".minecraft/config/paxi/datapacks/test.zip";
    private const string OriginalContent = "pack original content";
    private const string ManifestJson = """
        {"manifestType":"minecraftModpack","manifestVersion":1,"name":"Recovery test","version":"1",
         "minecraft":{"version":"1.20.1","modLoaders":[{"id":"fabric-0.19.3","primary":true}]},
         "files":[],"overrides":"overrides"}
        """;

    /// <summary>创建真实导入记录；原包可放在任意嵌套目录，验证精确路径恢复而不是扫描磁盘。</summary>
    private async Task<(InstanceImportResult Instance, InstanceFileRequirement File, string Archive)> ImportAsync(string? archiveFolder = null)
    {
        Directory.CreateDirectory(_root);
        var folder = archiveFolder ?? _root;
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".zip");
        WriteArchive(archive, ("manifest.json", ManifestJson), (ArchiveEntry, OriginalContent));
        var instance = await new MinecraftInstanceImporter(_root).ImportAsync(archive, "Imported");
        return (instance, Assert.Single(instance.Manifest.Files, file => file.Path == RelativePath), archive);
    }

    /// <summary>写入测试 ZIP；只覆盖当前测试目录中专门创建的压缩包。</summary>
    private static void WriteArchive(string path, params (string Path, string Content)[] entries)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    /// <summary>定位测试实例内的单一载荷文件。</summary>
    private static string Target(InstanceImportResult instance) => InstanceImportPaths.Within(instance.InstancePath, RelativePath);

    /// <summary>为有效嵌入需求附加不同官方内容的来源信息，不将官方摘要当作嵌入校验条件。</summary>
    private static InstanceFileRequirement WithOfficialMetadata(InstanceFileRequirement file) => file with
    {
        Origin = InstanceFileOrigin.EmbeddedOverride,
        CurseForgeOrigin = new()
        {
            ProjectId = 1527486, FileId = 8003803, Size = 100,
            Hashes = new Dictionary<string, string> { ["sha1"] = new('a', 40) }
        }
    };

    /// <summary>原包位于普通查找范围外时，仍能依据导入时的精确记录恢复并通过原基线校验。</summary>
    [Fact]
    public async Task MissingEmbeddedRestoresFromRecordedOriginalArchive()
    {
        var (instance, file, archive) = await ImportAsync(Path.Combine(_root, "packages", "nested"));
        File.Delete(Target(instance));
        Assert.Equal(archive, InstanceImportSourceStore.Read(instance.InstancePath, instance.Manifest));

        Assert.True(await new EmbeddedFileRestorer(_root).RestoreAsync(instance.InstancePath, instance.Manifest,
            WithOfficialMetadata(file), CancellationToken.None));

        Assert.Equal(OriginalContent, await File.ReadAllTextAsync(Target(instance)));
        Assert.True(await InstanceRuntimeInstaller.ExistingFileMatchesAsync(Target(instance), WithOfficialMetadata(file), CancellationToken.None));
        AssertNoTemporaryFiles(instance);
    }

    /// <summary>旧实例没有本机路径记录时，可在启动器根目录找到同名原包并验证实际条目。</summary>
    [Fact]
    public async Task LegacyInstanceFindsSameNamedArchiveInLauncherRoot()
    {
        var (instance, file, _) = await ImportAsync();
        File.Delete(InstanceImportPaths.Within(instance.InstancePath, InstanceImportSourceStore.RelativePath));
        File.Delete(Target(instance));
        Assert.True(await new EmbeddedFileRestorer(_root).RestoreAsync(instance.InstancePath, instance.Manifest, file, CancellationToken.None));
        Assert.Equal(OriginalContent, File.ReadAllText(Target(instance)));
    }

    /// <summary>原包已经不存在时明确返回未恢复，既不创建载荷文件也不下载官方替代内容。</summary>
    [Fact]
    public async Task MissingArchiveLeavesEmbeddedFileMissing()
    {
        var (instance, file, archive) = await ImportAsync();
        File.Delete(Target(instance));
        File.Delete(archive);
        Assert.False(await new EmbeddedFileRestorer(_root).RestoreAsync(instance.InstancePath, instance.Manifest,
            WithOfficialMetadata(file), CancellationToken.None));
        Assert.False(File.Exists(Target(instance)));
        AssertNoTemporaryFiles(instance);
    }

    /// <summary>同名包的条目大小或摘要改变时不能将它当作原包恢复，临时数据也会清理。</summary>
    [Theory]
    [InlineData("PACK original content")]
    [InlineData("short")]
    public async Task ChangedArchiveContentCannotBecomeImportedBaseline(string changed)
    {
        var (instance, file, archive) = await ImportAsync();
        File.Delete(Target(instance));
        WriteArchive(archive, (ArchiveEntry, changed));
        await Assert.ThrowsAsync<InvalidDataException>(() => new EmbeddedFileRestorer(_root)
            .RestoreAsync(instance.InstancePath, instance.Manifest, file, CancellationToken.None));
        Assert.False(File.Exists(Target(instance)));
        AssertNoTemporaryFiles(instance);
    }

    /// <summary>恢复前验证整个压缩包，不能忽略无关条目中的路径穿越或重复条目。</summary>
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(ArchiveEntry)]
    public async Task UnsafeArchiveLayoutIsRejectedBeforeRecovery(string unsafeEntry)
    {
        var (instance, file, archive) = await ImportAsync();
        File.Delete(Target(instance));
        WriteArchive(archive, (ArchiveEntry, OriginalContent), (unsafeEntry, "unsafe"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new EmbeddedFileRestorer(_root)
            .RestoreAsync(instance.InstancePath, instance.Manifest, file, CancellationToken.None));
        Assert.False(File.Exists(Target(instance)));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
        AssertNoTemporaryFiles(instance);
    }

    /// <summary>压缩包中的符号链接不能被当作普通文件恢复。</summary>
    [Fact]
    public async Task SymbolicLinkArchiveEntryIsRejected()
    {
        var (instance, file, archivePath) = await ImportAsync();
        File.Delete(Target(instance));
        using (var output = new FileStream(archivePath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(ArchiveEntry);
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(OriginalContent);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new EmbeddedFileRestorer(_root)
            .RestoreAsync(instance.InstancePath, instance.Manifest, file, CancellationToken.None));
        Assert.False(File.Exists(Target(instance)));
    }

    /// <summary>用户现有内容不匹配时保留原文件，不通过恢复操作强行覆盖。</summary>
    [Fact]
    public async Task ExistingChangedFileIsNeverOverwritten()
    {
        var (instance, file, _) = await ImportAsync();
        await File.WriteAllTextAsync(Target(instance), "user edits");
        await Assert.ThrowsAsync<InvalidDataException>(() => new EmbeddedFileRestorer(_root)
            .RestoreAsync(instance.InstancePath, instance.Manifest, file, CancellationToken.None));
        Assert.Equal("user edits", await File.ReadAllTextAsync(Target(instance)));
        AssertNoTemporaryFiles(instance);
    }

    /// <summary>已有原包正确内容无需恢复，也不因元数据里不同的官方 SHA-1 被拒绝。</summary>
    [Fact]
    public async Task ExistingOriginalFileIgnoresOfficialProvenanceHash()
    {
        var (instance, file, _) = await ImportAsync();
        Assert.True(await new EmbeddedFileRestorer(_root).RestoreAsync(instance.InstancePath, instance.Manifest,
            WithOfficialMetadata(file), CancellationToken.None));
        Assert.Equal(OriginalContent, File.ReadAllText(Target(instance)));
    }

    /// <summary>已取消的恢复不创建目标文件或残留临时文件。</summary>
    [Fact]
    public async Task CancelledRecoveryLeavesMissingFileUnchanged()
    {
        var (instance, file, _) = await ImportAsync();
        File.Delete(Target(instance));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EmbeddedFileRestorer(_root)
            .RestoreAsync(instance.InstancePath, instance.Manifest, file, cancellation.Token));
        Assert.False(File.Exists(Target(instance)));
        AssertNoTemporaryFiles(instance);
    }

    /// <summary>原清单发生改变后旧本机来源定位失效，不能沿用另一份导入的精确路径。</summary>
    [Fact]
    public async Task SourceLocationIsBoundToImportedManifest()
    {
        var (instance, file, _) = await ImportAsync(Path.Combine(_root, "packages", "nested"));
        File.Delete(Target(instance));
        var changedManifest = instance.Manifest with { MinecraftVersion = "1.21.1" };
        await InstanceManifestStore.WriteAsync(Path.Combine(instance.InstancePath, InstanceImportManifest.FileName), changedManifest, CancellationToken.None);
        Assert.Null(InstanceImportSourceStore.Read(instance.InstancePath, changedManifest));
        Assert.False(await new EmbeddedFileRestorer(_root).RestoreAsync(instance.InstancePath, changedManifest, file, CancellationToken.None));
    }

    /// <summary>导出包含实际游戏内容和公共清单，但不泄露原包的本机绝对路径定位文件。</summary>
    [Fact]
    public async Task ExportDoesNotIncludeLocalArchiveLocation()
    {
        var (instance, _, archivePath) = await ImportAsync();
        var publicManifest = await File.ReadAllTextAsync(Path.Combine(instance.InstancePath, InstanceImportManifest.FileName));
        Assert.DoesNotContain(archivePath, publicManifest);
        var destination = Path.Combine(_root, "export.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(instance.InstancePath, destination);
        using var archive = ZipFile.OpenRead(destination);
        Assert.DoesNotContain(archive.Entries, entry => InstanceImportSourceStore.IsLocalSourcePath(entry.FullName));
        Assert.Contains(archive.Entries, entry => entry.FullName == RelativePath);
    }

    /// <summary>重新导入备份时丢弃其中旧机器的定位文件，重建指向本次备份的恢复记录。</summary>
    [Fact]
    public async Task ImportRebuildsLocalSourceRecordInsteadOfTrustingArchiveCopy()
    {
        var (instance, _, _) = await ImportAsync();
        var backup = Path.Combine(_root, "backup.zip");
        WriteArchive(backup,
            (InstanceImportManifest.FileName, File.ReadAllText(Path.Combine(instance.InstancePath, InstanceImportManifest.FileName))),
            (RelativePath, OriginalContent), (InstanceImportSourceStore.RelativePath, "untrusted record"));
        var reimported = await new MinecraftInstanceImporter(_root).ImportAsync(backup, "Reimported");
        Assert.Equal(backup, InstanceImportSourceStore.Read(reimported.InstancePath, reimported.Manifest));
        Assert.DoesNotContain(reimported.Manifest.Files, file => file.Path == InstanceImportSourceStore.RelativePath);
        var restoredFile = Assert.Single(reimported.Manifest.Files, file => file.Path == RelativePath);
        File.Delete(Target(reimported));
        Assert.True(await new EmbeddedFileRestorer(_root).RestoreAsync(reimported.InstancePath, reimported.Manifest, restoredFile, CancellationToken.None));
    }

    /// <summary>记录只在用户明确调用接受操作时创建，并保留官方来源作为元数据。</summary>
    [Fact]
    public async Task ExplicitUserReplacementHasItsOwnIntegrityAndRetainsProvenance()
    {
        var (instance, file, _) = await ImportAsync();
        await File.WriteAllTextAsync(Target(instance), "explicit user choice");
        var service = new InstanceLocalReplacementService(_root);
        var accepted = await service.AcceptExistingAsync(instance.InstancePath, RelativePath);
        var applied = Assert.Single(InstanceLocalReplacementService.Apply(instance.InstancePath, instance.Manifest, [WithOfficialMetadata(file)]));
        Assert.Equal(InstanceFileOrigin.UserLocalImport, accepted.Origin);
        Assert.Equal(accepted.Hashes["sha256"], applied.Hashes["sha256"]);
        Assert.Single(applied.Hashes);
        Assert.Null(applied.ArchiveEntry);
        Assert.Empty(applied.Downloads);
        Assert.Equal(1527486, applied.CurseForgeOrigin!.ProjectId);
        Assert.True(await InstanceRuntimeInstaller.ExistingFileMatchesAsync(Target(instance), applied, CancellationToken.None));
    }

    /// <summary>选择后再次修改文件仍必须失败，不能把主动替换当作永久关闭该路径的校验。</summary>
    [Fact]
    public async Task UserReplacementChangedAfterSelectionIsRejected()
    {
        var (instance, file, _) = await ImportAsync();
        await File.WriteAllTextAsync(Target(instance), "user choice one");
        await new InstanceLocalReplacementService(_root).AcceptExistingAsync(instance.InstancePath, RelativePath);
        await File.WriteAllTextAsync(Target(instance), "user choice two");
        var applied = Assert.Single(InstanceLocalReplacementService.Apply(instance.InstancePath, instance.Manifest, [WithOfficialMetadata(file)]));
        await Assert.ThrowsAsync<InvalidDataException>(() => InstanceRuntimeInstaller.ExistingFileMatchesAsync(Target(instance), applied, CancellationToken.None));
        Assert.Equal("user choice two", File.ReadAllText(Target(instance)));
    }

    /// <summary>运行阶段撤回或改动替换记录，即使文件本身未变也不能沿用旧授权返回可启动。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementRecordChangedDuringRuntimeBlocksReadiness(bool remove)
    {
        var (instance, _, _) = await ImportAsync();
        await File.WriteAllTextAsync(Target(instance), "explicit user choice");
        await new InstanceLocalReplacementService(_root).AcceptExistingAsync(instance.InstancePath, RelativePath);
        var runtime = new ReplacementChangingRuntime(() => ChangeReplacementRecord(instance.InstancePath, remove));
        var installer = new CurseForgeInstanceInstaller(_root, new UnexpectedDownloader(), new UnexpectedCurseForgeApi(), runtime);

        var result = await installer.InstallAsync(instance.InstancePath);

        Assert.Equal(1, runtime.Calls);
        Assert.False(result.FilesReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.Contains(result.Issues, issue => issue.Contains("本地替换记录在操作期间发生变化"));
        Assert.Equal("explicit user choice", File.ReadAllText(Target(instance)));
    }

    /// <summary>完整检查报告必须注明扫描期间变化的替换记录，不能只检查未改变的文件和原清单。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementRecordChangedDuringFullCheckInvalidatesSnapshot(bool remove)
    {
        var (instance, _, _) = await ImportAsync();
        await File.WriteAllTextAsync(Target(instance), "explicit user choice");
        await new InstanceLocalReplacementService(_root).AcceptExistingAsync(instance.InstancePath, RelativePath);
        var changed = false;
        var progress = new SynchronousProgress<InstanceCheckProgress>(_ =>
        {
            if (changed) return;
            changed = true;
            ChangeReplacementRecord(instance.InstancePath, remove);
        });

        var result = await new MinecraftInstanceChecker(_root).CheckAsync(instance.InstancePath,
            InstanceCheckMode.Full, progress);

        Assert.True(changed);
        Assert.Equal(InstanceFileState.Verified, Assert.Single(result.Files, file => file.Requirement.Path == RelativePath).State);
        Assert.Contains(result.ContentIssues, issue => issue.Contains("本地替换记录在操作期间发生变化"));
        Assert.False(result.ContentReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.Equal("explicit user choice", File.ReadAllText(Target(instance)));
    }

    /// <summary>没有主动替换记录的只读检查不改变需求，不创建 .eml-runtime 目录。</summary>
    [Fact]
    public async Task ApplyingMissingReplacementStateDoesNotWriteFiles()
    {
        var (instance, file, _) = await ImportAsync();
        File.Delete(InstanceImportPaths.Within(instance.InstancePath, InstanceImportSourceStore.RelativePath));
        Directory.Delete(Path.Combine(instance.InstancePath, ".eml-runtime"));
        IReadOnlyList<InstanceFileRequirement> requirements = [WithOfficialMetadata(file)];
        Assert.Same(requirements, InstanceLocalReplacementService.Apply(instance.InstancePath, instance.Manifest, requirements));
        Assert.False(Directory.Exists(Path.Combine(instance.InstancePath, ".eml-runtime")));
    }

    /// <summary>用户替换记录包含原需求指纹，不能伪造记录后复用为另一份原始内容。</summary>
    [Fact]
    public async Task ReplacementWithMismatchedOriginalFingerprintIsRejected()
    {
        var (instance, file, _) = await ImportAsync();
        await new InstanceLocalReplacementService(_root).AcceptExistingAsync(instance.InstancePath, RelativePath);
        var path = InstanceImportPaths.Within(instance.InstancePath, InstanceLocalReplacementService.RelativePath);
        var state = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        state["files"]![0]!["originalFingerprint"] = new string('0', 64);
        await File.WriteAllTextAsync(path, state.ToJsonString());
        Assert.Throws<InvalidDataException>(() => InstanceLocalReplacementService.Apply(instance.InstancePath, instance.Manifest, [file]));
    }

    /// <summary>删除用户已经选定的替换后，恢复器不得悄悄恢复原包版本。</summary>
    [Fact]
    public async Task UserSelectedReplacementCannotBeRestoredAsOriginal()
    {
        var (instance, _, _) = await ImportAsync();
        var accepted = await new InstanceLocalReplacementService(_root).AcceptExistingAsync(instance.InstancePath, RelativePath);
        File.Delete(Target(instance));
        await Assert.ThrowsAsync<InvalidDataException>(() => new EmbeddedFileRestorer(_root)
            .RestoreAsync(instance.InstancePath, instance.Manifest, accepted, CancellationToken.None));
        Assert.False(File.Exists(Target(instance)));
    }

    /// <summary>恢复和主动替换都拒绝访问启动器 instances 范围外的路径。</summary>
    [Fact]
    public async Task RecoveryAndReplacementRejectOutsideInstanceRoot()
    {
        var (instance, file, _) = await ImportAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EmbeddedFileRestorer(Path.Combine(_root, "other-launcher"))
            .RestoreAsync(instance.InstancePath, instance.Manifest, file, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new InstanceLocalReplacementService(Path.Combine(_root, "other-launcher"))
            .AcceptExistingAsync(instance.InstancePath, RelativePath));
    }

    /// <summary>验证恢复失败或取消后没有残留该操作创建的临时文件。</summary>
    private static void AssertNoTemporaryFiles(InstanceImportResult instance) =>
        Assert.Empty(Directory.EnumerateFiles(instance.InstancePath, "*.eml-restore.tmp", SearchOption.AllDirectories));

    /// <summary>只修改当前测试目录的选择记录，保持载荷和导入清单不变以重现旧快照问题。</summary>
    private static void ChangeReplacementRecord(string instanceRoot, bool remove)
    {
        var path = InstanceImportPaths.Within(instanceRoot, InstanceLocalReplacementService.RelativePath);
        if (remove) File.Delete(path);
        else
        {
            var state = JsonNode.Parse(File.ReadAllText(path))!;
            state["files"] = new JsonArray();
            File.WriteAllText(path, state.ToJsonString());
        }
    }

    /// <summary>在运行依赖返回成功前变更选择记录，不启动游戏、不访问网络。</summary>
    private sealed class ReplacementChangingRuntime(Action change) : IPrismInstanceInstaller
    {
        internal int Calls { get; private set; }

        /// <summary>模拟较晚完成的运行准备，使最终就绪保护必须重新核对内容来源。</summary>
        public Task<PrismInstallResult> InstallAsync(string instancePath, IProgress<InstanceDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            change();
            return Task.FromResult(new PrismInstallResult { SourceFormat = "CurseForge", LaunchProfilePath = "test-launch.json" });
        }
    }

    /// <summary>测试只有现成包内文件，任何下载都表示流程意外回退。</summary>
    private sealed class UnexpectedDownloader : IFileDownloader
    {
        /// <summary>拒绝网络下载，防止测试把平台文件当作替换来源。</summary>
        public Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("此测试不得下载文件。");
    }

    /// <summary>本测试清单没有平台引用，所有 API 请求都视为意外行为。</summary>
    private sealed class UnexpectedCurseForgeApi : ICurseForgeApiClient
    {
        public CurseForgeRateLimit? LastRateLimit => null;
        /// <summary>拒绝批量文件请求。</summary>
        public Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(IReadOnlyList<long> fileIds, CancellationToken cancellationToken = default) => throw new InvalidOperationException("不得请求 API。");
        /// <summary>拒绝批量项目请求。</summary>
        public Task<IReadOnlyList<CurseForgeMod>> GetModsAsync(IReadOnlyList<long> projectIds, CancellationToken cancellationToken = default) => throw new InvalidOperationException("不得请求 API。");
        /// <summary>拒绝单文件请求。</summary>
        public Task<CurseForgeFile> GetFileAsync(long projectId, long fileId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("不得请求 API。");
        /// <summary>拒绝单项目请求。</summary>
        public Task<CurseForgeMod> GetModAsync(long projectId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("不得请求 API。");
        /// <summary>拒绝搜索请求。</summary>
        public Task<CurseForgeSearchResult> SearchAsync(CurseForgeSearchOptions options, CancellationToken cancellationToken = default) => throw new InvalidOperationException("不得请求 API。");
    }

    /// <summary>同步触发检查进度，精确在读取状态后变更测试记录。</summary>
    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        /// <summary>直接执行回调，避免后台同步上下文改变测试时序。</summary>
        public void Report(T value) => callback(value);
    }

    /// <summary>清理当前测试创建的 GUID 目录，与用户实例完全隔离。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
