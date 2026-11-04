using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Modrinth;
using EML.Infrastructure.Minecraft.CurseForge;

namespace EML.Tests;

/// <summary>使用独立临时实例验证检查结果、边界和只读性，不访问真实整合包。</summary>
public sealed class MinecraftInstanceCheckerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-check-tests-" + Guid.NewGuid().ToString("N"));
    private string InstancePath => Path.Combine(_root, "instances", "example");

    /// <summary>创建当前测试独占的实例目录。</summary>
    public MinecraftInstanceCheckerTests() => Directory.CreateDirectory(InstancePath);

    /// <summary>将测试清单写入临时实例，沿用产品验证器保证测试输入格式正确。</summary>
    private Task ManifestAsync(params InstanceFileRequirement[] requirements) =>
        InstanceManifestStore.WriteAsync(Path.Combine(InstancePath, InstanceImportManifest.FileName),
            new InstanceImportManifest
            {
                Name = "测试实例", Files = requirements,
                Components = [new InstanceComponentRequirement("minecraft", "1.20.1")]
            }, CancellationToken.None);

    /// <summary>生成具有全部三种哈希的下载文件需求，默认内容为 UTF-8 文本。</summary>
    private static InstanceFileRequirement Requirement(string path = ".minecraft/mods/test.jar", string content = "expected")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new InstanceFileRequirement
        {
            Path = path, Source = InstanceFileSource.DirectDownload, Size = bytes.Length,
            Downloads = ["https://example.invalid/test.jar"],
            Hashes = new Dictionary<string, string>
            {
                ["sha1"] = Convert.ToHexString(SHA1.HashData(bytes)),
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)),
                ["sha512"] = Convert.ToHexString(SHA512.HashData(bytes))
            }
        };
    }

    /// <summary>创建一个测试文件和它需要的父目录。</summary>
    private string WriteFile(string relative, string content)
    {
        var path = Path.Combine(InstancePath, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>文件内容与所有哈希一致时，完整检查返回已验证状态并保留组件声明。</summary>
    [Fact]
    public async Task FullCheckVerifiesEveryDeclaredHash()
    {
        var requirement = Requirement();
        WriteFile(requirement.Path!, "expected");
        await ManifestAsync(requirement);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath);
        Assert.True(report.HasManifest);
        Assert.Equal(InstanceFileState.Verified, Assert.Single(report.Files).State);
        Assert.Equal("minecraft", Assert.Single(report.Components).Id);
        Assert.Contains(report.Notes, note => note.Contains("不代表实例一定能启动"));
    }

    /// <summary>平台尚无元数据时只报告待下载组件，检查本身不创建缓存、不修改清单或模组。</summary>
    [Theory]
    [InlineData("Modrinth", "fabric-loader")]
    [InlineData("CurseForge", "fabric")]
    public async Task PlatformCheckReportsMissingMetadataWithoutWritingOrDownloading(string format, string loaderId)
    {
        var requirement = Requirement();
        WriteFile(requirement.Path!, "expected");
        var manifest = new InstanceImportManifest
        {
            Name = "Platform test", SourceFormat = format, MinecraftVersion = "1.20.1",
            Components = [new("minecraft", "1.20.1"), new(loaderId, "0.16.10")], Files = [requirement]
        };
        await InstanceManifestStore.WriteAsync(Path.Combine(InstancePath, InstanceImportManifest.FileName), manifest, CancellationToken.None);
        var before = Directory.GetFiles(InstancePath, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), Modified: File.GetLastWriteTimeUtc(path)));

        var report = await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath);

        Assert.Equal(format, report.RuntimeSourceFormat);
        Assert.False(report.IsPrismRuntime);
        Assert.Null(report.LaunchProfilePath);
        Assert.Contains(report.RuntimeIssues, issue => issue.Contains("minecraft-manifest"));
        Assert.Contains(report.RuntimeIssues, issue => issue.Contains("fabric-loader"));
        Assert.Equal(InstanceFileState.Verified, Assert.Single(report.Files).State);
        Assert.False(Directory.Exists(Path.Combine(InstancePath, ".eml-runtime")));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(InstancePath, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, state) in before)
        {
            Assert.Equal(state.Bytes, File.ReadAllBytes(path));
            Assert.Equal(state.Modified, File.GetLastWriteTimeUtc(path));
        }
    }

    /// <summary>各平台只读取自己的运行状态，即使旧平台状态使用相同指纹也不能混用。</summary>
    [Theory]
    [InlineData("Modrinth", ".eml-runtime/state.json", false)]
    [InlineData("Modrinth", ".eml-runtime/modrinth/state.json", true)]
    [InlineData("CurseForge", ".eml-runtime/modrinth/state.json", false)]
    [InlineData("CurseForge", ".eml-runtime/curseforge/state.json", true)]
    public async Task PlatformCheckerUsesFormatSpecificState(string format, string statePath, bool shouldRead)
    {
        var manifest = new InstanceImportManifest
        {
            SourceFormat = format, MinecraftVersion = "1.20.1", Components = [new("minecraft", "1.20.1")]
        };
        await InstanceManifestStore.WriteAsync(Path.Combine(InstancePath, InstanceImportManifest.FileName), manifest, CancellationToken.None);
        IPrismRuntimeResolver resolver = format == "Modrinth" ? new ModrinthRuntimeResolver() : new CurseForgeRuntimeResolver();
        var plan = resolver.Resolve(InstancePath, manifest);
        const string profilePath = ".minecraft/versions/test-profile/test-profile.json";
        const string profileContent = "{\"id\":\"test-profile\"}";
        WriteFile(profilePath, profileContent);
        var profile = Requirement(profilePath, profileContent) with { Source = InstanceFileSource.Embedded, Downloads = [] };
        WriteFile(statePath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, fingerprint = plan.Fingerprint, launchProfilePath = profilePath, generatedFiles = new[] { profile }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var report = await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath);

        Assert.Equal(format, report.RuntimeSourceFormat);
        if (shouldRead)
        {
            var generated = Assert.Single(report.Files);
            Assert.True(generated.IsRuntimeFile);
            Assert.Equal(InstanceFileState.Verified, generated.State);
        }
        else
        {
            Assert.Null(report.LaunchProfilePath);
            Assert.Empty(report.Files);
        }
        // 测试的元数据未下载，即使存在配置也仍保留阻断信息，不应伪称实际可启动。
        Assert.NotEmpty(report.RuntimeIssues);
        Assert.Null(report.LaunchProfilePath);
    }

    /// <summary>CurseForge 检查只读取固定文件缓存，无 API Key 时也可校验人工提供的受限文件。</summary>
    [Fact]
    public async Task CurseForgeCachedManualFileIsVerifiedWithoutApiOrManifestRewrite()
    {
        var reference = new InstanceFileRequirement { Source = InstanceFileSource.CurseForge, ProjectId = 10, FileId = 20 };
        var manifest = new InstanceImportManifest
        {
            SourceFormat = "CurseForge", Files = [reference], MinecraftVersion = "1.20.1",
            Components = [new("minecraft", "1.20.1")]
        };
        var manifestPath = Path.Combine(InstancePath, InstanceImportManifest.FileName);
        await InstanceManifestStore.WriteAsync(manifestPath, manifest, CancellationToken.None);
        var requirement = Requirement() with
        {
            Source = InstanceFileSource.CurseForge, Origin = InstanceFileOrigin.CurseForgeDownload,
            ProjectId = 10, FileId = 20, Downloads = [],
            Hashes = new Dictionary<string, string> { ["sha1"] = Requirement().Hashes["sha1"] }
        };
        WriteFile(requirement.Path!, "expected");
        await CurseForgeFileStateStore.WriteAsync(InstancePath, manifest, new CurseForgeFileResolution
        {
            Fingerprint = CurseForgeFileStateStore.Fingerprint(manifest), Files = [new()
            {
                Requirement = requirement, ClassId = 6, FileName = "test.jar",
                ManualDownloadUrl = "https://www.curseforge.com/minecraft/mc-mods/example/files/20"
            }]
        }, CancellationToken.None);
        var before = Directory.GetFiles(InstancePath, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);

        var report = await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath);

        var checkedFile = Assert.Single(report.Files);
        Assert.Equal(InstanceFileState.Verified, checkedFile.State);
        Assert.Equal(20, checkedFile.Requirement.FileId);
        Assert.False(checkedFile.IsRuntimeFile);
        Assert.Equal("CurseForge", report.RuntimeSourceFormat);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(InstancePath, "*", SearchOption.AllDirectories).Order());
    }

    /// <summary>篡改或损坏的 CurseForge 缓存仅产生问题提示，不能使未解析的引用看起来已校验。</summary>
    [Fact]
    public async Task CurseForgeCorruptCacheRemainsUnresolved()
    {
        var manifest = new InstanceImportManifest
        {
            SourceFormat = "CurseForge", Components = [new("minecraft", "1.20.1")],
            Files = [new() { Source = InstanceFileSource.CurseForge, ProjectId = 10, FileId = 20 }]
        };
        await InstanceManifestStore.WriteAsync(Path.Combine(InstancePath, InstanceImportManifest.FileName), manifest, CancellationToken.None);
        WriteFile(CurseForgeFileStateStore.RelativePath, "{broken");
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath);
        Assert.Equal(InstanceFileState.Unresolved, Assert.Single(report.Files).State);
        Assert.Contains(report.ContentIssues, issue => issue.Contains("文件来源") && issue.Contains("CurseForge"));
        Assert.DoesNotContain(report.RuntimeIssues, issue => issue.Contains("CurseForge 文件缓存"));
        Assert.False(report.ContentReady);
        Assert.Equal("{broken", File.ReadAllText(Path.Combine(InstancePath, CurseForgeFileStateStore.RelativePath)));
    }

    /// <summary>相同大小的内容变化必须由哈希发现，不能只靠长度判定通过。</summary>
    [Fact]
    public async Task FullCheckDetectsSameLengthCorruption()
    {
        var requirement = Requirement(content: "abc");
        WriteFile(requirement.Path!, "xyz");
        await ManifestAsync(requirement);
        var result = Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath)).Files);
        Assert.Equal(InstanceFileState.HashMismatch, result.State);
        Assert.Contains("sha1", result.Message);
        Assert.Contains("sha256", result.Message);
        Assert.Contains("sha512", result.Message);
    }

    /// <summary>即使前两个哈希匹配，最后一个声明哈希不符也必须失败。</summary>
    [Fact]
    public async Task FullCheckDoesNotIgnoreAdditionalDeclaredHashes()
    {
        var requirement = Requirement();
        var hashes = requirement.Hashes.ToDictionary(pair => pair.Key, pair => pair.Value);
        hashes["sha512"] = new string('0', 128);
        WriteFile(requirement.Path!, "expected");
        await ManifestAsync(requirement with { Hashes = hashes });
        var result = Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath)).Files);
        Assert.Equal(InstanceFileState.HashMismatch, result.State);
        Assert.Contains("sha512", result.Message);
        Assert.DoesNotContain("sha256", result.Message);
    }

    /// <summary>快速检查只确认路径与长度，不能把未读取的内容显示为校验通过。</summary>
    [Fact]
    public async Task QuickCheckNeverClaimsVerifiedContent()
    {
        var requirement = Requirement(content: "abc");
        WriteFile(requirement.Path!, "xyz");
        await ManifestAsync(requirement);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath, InstanceCheckMode.Quick);
        Assert.Equal(InstanceCheckMode.Quick, report.Mode);
        Assert.Equal(InstanceFileState.PresentUnverified, Assert.Single(report.Files).State);
    }

    /// <summary>下载文件大小不符在快速和完整检查中都必须被识别。</summary>
    [Theory]
    [InlineData(InstanceCheckMode.Quick)]
    [InlineData(InstanceCheckMode.Full)]
    public async Task BothModesDetectWrongSize(InstanceCheckMode mode)
    {
        var requirement = Requirement();
        WriteFile(requirement.Path!, "short");
        await ManifestAsync(requirement);
        Assert.Equal(InstanceFileState.SizeMismatch,
            Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath, mode)).Files).State);
    }

    /// <summary>包内文件变化只显示为导入后变化，配置和存档不会被武断标记为损坏。</summary>
    [Theory]
    [InlineData("same", "edit")]
    [InlineData("old", "new content")]
    public async Task EmbeddedChangesRemainBaselineChanges(string before, string after)
    {
        var requirement = Requirement(".minecraft/config/options.txt", before) with
        { Source = InstanceFileSource.Embedded, Downloads = [] };
        WriteFile(requirement.Path!, after);
        await ManifestAsync(requirement);
        var result = Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath)).Files);
        Assert.Equal(InstanceFileState.ChangedSinceImport, result.State);
        Assert.Contains("正常修改", result.Message);
        Assert.True(result.IsAllowedConfigurationChange);
    }

    /// <summary>包内模组的变化也属于基线变化，但最终准备状态不能把它当成允许的配置修改。</summary>
    [Theory]
    [InlineData(".minecraft/mods/test.jar")]
    [InlineData(".minecraft/libraries/test.jar")]
    [InlineData("outside/config/options.txt")]
    public async Task EmbeddedBinaryChangesAreNotAllowedConfigurationChanges(string path)
    {
        var requirement = Requirement(path, "original") with
        { Source = InstanceFileSource.Embedded, Downloads = [] };
        WriteFile(path, "modified");
        await ManifestAsync(requirement);
        var result = Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath)).Files);
        Assert.Equal(InstanceFileState.ChangedSinceImport, result.State);
        Assert.False(result.IsAllowedConfigurationChange);
    }

    /// <summary>可选文件缺失仍是缺失状态，但保留可选标记供汇总与下载计划排除。</summary>
    [Fact]
    public async Task MissingOptionalFileRemainsOptional()
    {
        await ManifestAsync(Requirement() with { Required = false });
        var result = Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath)).Files);
        Assert.Equal(InstanceFileState.Missing, result.State);
        Assert.False(result.Requirement.Required);
        Assert.Equal("可选", result.RequiredDisplay);
    }

    /// <summary>没有路径的 CurseForge 引用属于待解析，不是已知路径缺失。</summary>
    [Fact]
    public async Task UnresolvedCurseForgeIsNotReportedMissing()
    {
        await ManifestAsync(new InstanceFileRequirement
        { Source = InstanceFileSource.CurseForge, ProjectId = 123, FileId = 456 });
        var result = Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath)).Files);
        Assert.Equal(InstanceFileState.Unresolved, result.State);
        Assert.Equal("CurseForge 123/456", result.PathDisplay);
    }

    /// <summary>缺少清单时明确报告没有检查依据，不能从已有文件反推一切完整。</summary>
    [Fact]
    public async Task MissingManifestDoesNotInventRequirements()
    {
        WriteFile(".minecraft/mods/existing.jar", "file");
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath);
        Assert.False(report.HasManifest);
        Assert.Empty(report.Files);
        Assert.Contains(report.Notes, note => note.Contains("缺少预期文件清单"));
    }

    /// <summary>没有哈希的现有文件不能通过完整内容校验，即使长度符合要求。</summary>
    [Fact]
    public async Task NoHashesMeansPresentButUnverified()
    {
        var requirement = Requirement() with { Hashes = new Dictionary<string, string>() };
        WriteFile(requirement.Path!, "expected");
        await ManifestAsync(requirement);
        Assert.Equal(InstanceFileState.PresentUnverified,
            Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath)).Files).State);
    }

    /// <summary>文件位置变成目录时报告无法读取，而不是缺失或通过。</summary>
    [Fact]
    public async Task DirectoryAtFilePathIsUnreadable()
    {
        var requirement = Requirement();
        Directory.CreateDirectory(Path.Combine(InstancePath, requirement.Path!));
        await ManifestAsync(requirement);
        Assert.Equal(InstanceFileState.Unreadable,
            Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath)).Files).State);
    }

    /// <summary>父目录位置实际是文件时报告结构问题，不误报成普通缺失。</summary>
    [Fact]
    public async Task FileAtParentPathIsUnreadable()
    {
        WriteFile(".minecraft/mods", "not a directory");
        await ManifestAsync(Requirement());
        Assert.Equal(InstanceFileState.Unreadable,
            Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath)).Files).State);
    }

    /// <summary>被独占打开的文件属于无法读取，不属于不存在。</summary>
    [Fact]
    public async Task LockedFileIsNotReportedMissing()
    {
        var requirement = Requirement();
        var path = WriteFile(requirement.Path!, "expected");
        await ManifestAsync(requirement);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(InstanceFileState.Unreadable,
            Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath)).Files).State);
    }

    /// <summary>损坏清单中出现目录穿越时必须整体拒绝，不能读取实例之外的文件。</summary>
    [Fact]
    public async Task UnsafeManifestIsRejected()
    {
        WriteFile(InstanceImportManifest.FileName,
            """{"files":[{"path":"../outside.txt","source":"Embedded"}]}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => new MinecraftInstanceChecker(_root).CheckAsync(InstancePath));
    }

    /// <summary>清单位置变成文件夹属于无效输入，不能解释为缺少清单。</summary>
    [Fact]
    public async Task ManifestDirectoryIsRejected()
    {
        Directory.CreateDirectory(Path.Combine(InstancePath, InstanceImportManifest.FileName));
        await Assert.ThrowsAsync<IOException>(() => new MinecraftInstanceChecker(_root).CheckAsync(InstancePath));
    }

    /// <summary>实例根目录、嵌套目录与其他目录都不能作为合法直接子实例传入。</summary>
    [Fact]
    public async Task ScopeRejectsOutsideAndNestedInstances()
    {
        var checker = new MinecraftInstanceChecker(_root);
        foreach (var path in new[] { _root, Path.Combine(_root, "instances"), Path.Combine(InstancePath, "nested"), Path.Combine(_root, "elsewhere") })
            await Assert.ThrowsAsync<ArgumentException>(() => checker.CheckAsync(path));
    }

    /// <summary>检查不应写清单、改文件内容或时间，也不应删除未声明文件。</summary>
    [Fact]
    public async Task CheckingDoesNotModifyAnyInstanceFile()
    {
        var requirement = Requirement();
        WriteFile(requirement.Path!, "expected");
        WriteFile(".minecraft/extra.txt", "user data");
        await ManifestAsync(requirement);
        var before = Directory.GetFiles(InstancePath, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), Modified: File.GetLastWriteTimeUtc(path)));
        await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath);
        var afterPaths = Directory.GetFiles(InstancePath, "*", SearchOption.AllDirectories);
        Assert.Equal(before.Keys.Order(), afterPaths.Order());
        foreach (var (path, state) in before)
        {
            Assert.Equal(state.Bytes, File.ReadAllBytes(path));
            Assert.Equal(state.Modified, File.GetLastWriteTimeUtc(path));
        }
    }

    /// <summary>已取消的任务直接退出，不读取文件或返回误导性的完整报告。</summary>
    [Fact]
    public async Task AlreadyCanceledCheckDoesNotRun()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MinecraftInstanceChecker(_root).CheckAsync(InstancePath, cancellationToken: cancellation.Token));
    }

    /// <summary>读取清单后取消应立即结束，不能继续处理文件或发送完成进度。</summary>
    [Fact]
    public async Task CancellationAfterManifestStopsCheck()
    {
        await ManifestAsync(Requirement("first.jar"), Requirement("second.jar"));
        using var cancellation = new CancellationTokenSource();
        var observed = new List<InstanceCheckProgress>();
        var progress = new InlineProgress(value =>
        {
            observed.Add(value);
            if (value.FileName == "读取检查清单") cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MinecraftInstanceChecker(_root).CheckAsync(InstancePath, progress: progress, cancellationToken: cancellation.Token));
        Assert.DoesNotContain(observed, value => value.CompletedFiles == 2);
    }

    /// <summary>大量快速完成的小条目共享节流，回调总数应符合每秒约十次的上限。</summary>
    [Fact]
    public async Task ProgressIsThrottledAcrossSmallFiles()
    {
        await ManifestAsync(Enumerable.Range(0, 500).Select(index => Requirement($"missing/{index}.jar")).ToArray());
        var observed = new List<InstanceCheckProgress>();
        var elapsed = Stopwatch.StartNew();
        await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath,
            progress: new InlineProgress(observed.Add));
        elapsed.Stop();
        Assert.Equal(0, observed[0].CompletedFiles);
        Assert.Equal(500, observed[^1].CompletedFiles);
        // 开始与结束额外允许两次，留出少量计时分辨率余量，不依赖机器完成检查的绝对速度。
        Assert.True(observed.Count <= elapsed.ElapsedMilliseconds / 90 + 2);
    }

    /// <summary>进度从零开始并在所有清单条目处理完毕后达到百分之百。</summary>
    [Fact]
    public async Task ProgressCountsAllDeclaredFiles()
    {
        await ManifestAsync(Requirement("first.jar"), Requirement("second.jar"));
        var observed = new List<InstanceCheckProgress>();
        await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath, progress: new InlineProgress(observed.Add));
        Assert.Equal(0, observed[0].CompletedFiles);
        Assert.Equal(2, observed[^1].CompletedFiles);
        Assert.Equal(100, observed[^1].Percent);
    }

    /// <summary>实例内指向外部的目录链接必须被拒绝，不能校验链接目标的数据。</summary>
    [Fact]
    public async Task LinkedFileParentIsUnreadable()
    {
        var external = Path.Combine(_root, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "test.jar"), "expected", new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(InstancePath, ".minecraft"));
        var link = Path.Combine(InstancePath, ".minecraft", "mods");
        CreateDirectoryLink(link, external);
        try
        {
            await ManifestAsync(Requirement());
            var result = Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(InstancePath)).Files);
            Assert.Equal(InstanceFileState.Unreadable, result.State);
            Assert.Contains("链接", result.Message);
        }
        finally { Directory.Delete(link); }
    }

    /// <summary>即使实例本身不是链接，其启动器祖先包含目录联接也必须拒绝。</summary>
    [Fact]
    public async Task LinkedAncestorIsRejected()
    {
        var actual = Path.Combine(_root, "actual-launcher");
        Directory.CreateDirectory(Path.Combine(actual, "instances", "sample"));
        var link = Path.Combine(_root, "linked-launcher");
        CreateDirectoryLink(link, actual);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new MinecraftInstanceChecker(link)
                .CheckAsync(Path.Combine(link, "instances", "sample")));
        }
        finally { Directory.Delete(link); }
    }

    /// <summary>开始之后被替换为联接的实例父目录也必须拒绝，不能返回看似可信的最终报告。</summary>
    [Fact]
    public async Task AncestorReplacedDuringCheckCannotReturnTrustedReport()
    {
        await ManifestAsync(Requirement());
        var instances = Path.Combine(_root, "instances");
        var original = Path.Combine(_root, "original-instances");
        var replacement = Path.Combine(_root, "replacement");
        Directory.CreateDirectory(Path.Combine(replacement, "example"));
        var replaced = false;
        var progress = new InlineProgress(value =>
        {
            if (value.FileName != "读取检查清单" || replaced) return;
            Directory.Move(instances, original);
            replaced = true;
            CreateDirectoryLink(instances, replacement);
        });
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new MinecraftInstanceChecker(_root)
                .CheckAsync(InstancePath, progress: progress));
        }
        finally
        {
            if (replaced)
            {
                if (Directory.Exists(instances)) Directory.Delete(instances);
                Directory.Move(original, instances);
            }
        }
    }

    /// <summary>Windows 使用不需要符号链接权限的目录联接，其他平台创建普通目录符号链接。</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        var start = new ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    /// <summary>测试内同步转发进度，避免异步回调让取消断言出现竞态。</summary>
    private sealed class InlineProgress(Action<InstanceCheckProgress> callback) : IProgress<InstanceCheckProgress>
    {
        /// <summary>在报告进度的线程直接执行断言准备或取消动作。</summary>
        public void Report(InstanceCheckProgress value) => callback(value);
    }

    /// <summary>只清理本测试创建的 GUID 临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
