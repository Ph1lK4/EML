using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EML.Core.CurseForge;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.CurseForge;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Tests;

/// <summary>用固定 API 和下载内容验证 CurseForge 引用、分发限制、缓存、并发及运行准备边界。</summary>
public sealed class CurseForgeInstanceInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-CurseForge-install-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>模组、资源包和光影按项目类别进入正确目录，完整校验后才调用运行安装器。</summary>
    [Fact]
    public async Task ResolvesPinnedFilesByClassAndPreservesOriginalManifest()
    {
        var fixture = await FixtureAsync(3);
        fixture.Api.Mods[101] = fixture.Api.Mods[101] with { ClassId = 12 };
        fixture.Api.Mods[102] = fixture.Api.Mods[102] with { ClassId = 6552 };
        var original = File.ReadAllBytes(fixture.ManifestPath);
        var runtime = new FakeRuntime();
        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Equal("CurseForge", result.SourceFormat);
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(3, result.Files.Count);
        Assert.True(File.Exists(Path.Combine(fixture.Instance, ".minecraft/mods/file-0.jar")));
        Assert.True(File.Exists(Path.Combine(fixture.Instance, ".minecraft/resourcepacks/file-1.jar")));
        Assert.True(File.Exists(Path.Combine(fixture.Instance, ".minecraft/shaderpacks/file-2.jar")));
        Assert.Equal(original, File.ReadAllBytes(fixture.ManifestPath));
        Assert.All(fixture.Downloader.Requests, request =>
        {
            Assert.Single(request.Hashes);
            Assert.True(request.Hashes.ContainsKey("sha1"));
            Assert.True(request.ExpectedSize > 0);
        });
    }

    /// <summary>大型清单以每批最多五十个固定 ID 请求 API，不请求最新版或递归改变包声明版本。</summary>
    [Fact]
    public async Task MetadataRequestsAreBatchedAtFifty()
    {
        var fixture = await FixtureAsync(101);
        var result = await Installer(fixture, new FakeRuntime()).InstallAsync(fixture.Instance);
        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Equal(new[] { 50, 50, 1 }, fixture.Api.FileRequests.Select(request => request.Length));
        Assert.Equal(new[] { 50, 50, 1 }, fixture.Api.ModRequests.Select(request => request.Length));
        Assert.Equal(101, fixture.Downloader.Requests.Count);
    }

    /// <summary>成功解析和下载后可离线复查，再次执行不会请求 API 或重复下载。</summary>
    [Fact]
    public async Task CompleteCacheAndExistingFilesCanBeReusedWithoutApi()
    {
        var fixture = await FixtureAsync(2);
        var runtime = new FakeRuntime();
        var installer = Installer(fixture, runtime);
        Assert.True((await installer.InstallAsync(fixture.Instance)).FilesReady);
        fixture.Api.FailIfCalled = true;
        var result = await installer.InstallAsync(fixture.Instance);
        Assert.True(result.FilesReady);
        Assert.Empty(result.Files);
        Assert.Equal(2, fixture.Downloader.Requests.Count);
        Assert.Equal(2, runtime.Calls);
    }

    /// <summary>作者禁用分发、缺少地址或文件不可用时不猜 CDN；人工放入精确文件后可离线继续。</summary>
    [Theory]
    [InlineData("restricted")]
    [InlineData("missing-url")]
    [InlineData("unavailable")]
    public async Task DistributionRestrictionsRequireManualExactFile(string reason)
    {
        var fixture = await FixtureAsync(1);
        if (reason == "restricted") fixture.Api.Mods[100] = fixture.Api.Mods[100] with { AllowModDistribution = false };
        if (reason == "missing-url") fixture.Api.Files[1000] = fixture.Api.Files[1000] with { DownloadUrl = null };
        if (reason == "unavailable") fixture.Api.Files[1000] = fixture.Api.Files[1000] with { IsAvailable = false };
        var runtime = new FakeRuntime();
        var installer = Installer(fixture, runtime);

        var first = await installer.InstallAsync(fixture.Instance);

        Assert.False(first.FilesReady);
        Assert.Empty(fixture.Downloader.Requests);
        Assert.Equal(0, runtime.Calls);
        Assert.Contains(first.Issues, issue => issue.Contains("https://www.curseforge.com/minecraft/mc-mods/test-100/files/1000"));
        var state = Assert.IsType<CurseForgeFileResolution>(CurseForgeFileStateStore.Read(fixture.Instance, fixture.Manifest));
        var file = Assert.Single(state.Files).Requirement;
        Assert.Empty(file.Downloads);
        Write(fixture.Instance, file.Path!, fixture.Payloads["https://cdn.example.test/1000"]);
        fixture.Api.FailIfCalled = true;

        var second = await installer.InstallAsync(fixture.Instance);

        Assert.True(second.FilesReady, string.Join("\n", second.Issues));
        Assert.Equal(1, runtime.Calls);
        Assert.Empty(fixture.Downloader.Requests);
    }

    /// <summary>API 未返回的条目保存在缓存中作为阻断项，下次补全仍会重新请求而非永久卡住。</summary>
    [Fact]
    public async Task MissingMetadataIsRetriedAndSuccessfulFilesRemainAvailable()
    {
        var fixture = await FixtureAsync(2);
        var missing = fixture.Api.Files[1001];
        fixture.Api.Files.Remove(1001);
        var runtime = new FakeRuntime();
        var installer = Installer(fixture, runtime);
        var first = await installer.InstallAsync(fixture.Instance);
        Assert.False(first.FilesReady);
        Assert.Single(fixture.Downloader.Requests);
        Assert.Contains(first.Issues, issue => issue.Contains("未返回"));
        fixture.Api.Files[1001] = missing;
        var second = await installer.InstallAsync(fixture.Instance);
        Assert.True(second.FilesReady, string.Join("\n", second.Issues));
        Assert.Equal(2, fixture.Downloader.Requests.Count);
        Assert.Equal(2, fixture.Api.FileRequests.Count);
        Assert.Equal(1, runtime.Calls);
    }

    /// <summary>MD5 或未知算法不会被误当 SHA-1，无法校验的文件阻止运行准备。</summary>
    [Fact]
    public async Task MissingSha1DoesNotDownloadOrRun()
    {
        var fixture = await FixtureAsync(1);
        fixture.Api.Files[1000] = fixture.Api.Files[1000] with
        {
            Hashes = [new CurseForgeFileHash { Algo = 2, Value = new string('a', 32) }]
        };
        var runtime = new FakeRuntime();
        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);
        Assert.False(result.FilesReady);
        Assert.Contains(result.Issues, issue => issue.Contains("SHA-1"));
        Assert.Empty(fixture.Downloader.Requests);
        Assert.Equal(0, runtime.Calls);
    }

    /// <summary>未知类别和世界存档不凭扩展名当模组安装，不执行解包或选择不确定目录。</summary>
    [Theory]
    [InlineData(17)]
    [InlineData(4471)]
    [InlineData(null)]
    public async Task UnsupportedProjectClassesRemainExplicitlyBlocked(int? classId)
    {
        var fixture = await FixtureAsync(1);
        fixture.Api.Mods[100] = fixture.Api.Mods[100] with { ClassId = classId };
        var result = await Installer(fixture, new FakeRuntime()).InstallAsync(fixture.Instance);
        Assert.False(result.FilesReady);
        Assert.Contains(result.Issues, issue => issue.Contains("类别"));
        Assert.Empty(fixture.Downloader.Requests);
    }

    /// <summary>仅在清单声明已知 Paxi 和包内加载顺序配置时，数据包按官方约定放入全局数据包目录。</summary>
    [Theory]
    [InlineData(418881, ".minecraft")]
    [InlineData(515708, ".minecraft")]
    [InlineData(418881, ".")]
    public async Task PaxiDataPacksUseDeclaredGlobalDirectory(long paxiProjectId, string gameDirectory)
    {
        var fixture = await PaxiFixtureAsync(paxiProjectId, gameDirectory);
        var runtime = new FakeRuntime();

        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        var prefix = gameDirectory == "." ? "" : gameDirectory + "/";
        Assert.True(File.Exists(Path.Combine(fixture.Instance, prefix + "config/paxi/datapacks/datapack.zip")));
        Assert.False(File.Exists(Path.Combine(fixture.Instance, prefix + "mods/datapack.zip")));
        Assert.False(File.Exists(Path.Combine(fixture.Instance, prefix + "resourcepacks/datapack.zip")));
        Assert.Equal(1, runtime.Calls);
        var state = CurseForgeFileStateStore.Read(fixture.Instance, fixture.Manifest)!;
        Assert.Equal(prefix + "config/paxi/datapacks/datapack.zip", Assert.Single(state.Files, file => file.ClassId == 6945).Requirement.Path);
    }

    /// <summary>数据包目录依据缺少任意一项、模组可选或标记位置错误时，不猜测存档或全局安装目录。</summary>
    [Theory]
    [InlineData("missing-marker")]
    [InlineData("missing-paxi")]
    [InlineData("optional-paxi")]
    [InlineData("wrong-marker-path")]
    [InlineData("optional-marker")]
    public async Task DataPacksWithoutExplicitPaxiEvidenceRemainUnresolved(string scenario)
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        var files = fixture.Manifest.Files.Select(file => scenario switch
        {
            "optional-paxi" when file.ProjectId == 418881 => file with { Required = false },
            "wrong-marker-path" when file.Source == InstanceFileSource.Embedded => file with { Path = ".minecraft/other/config/paxi/datapack_load_order.json" },
            "optional-marker" when file.Source == InstanceFileSource.Embedded => file with { Required = false },
            _ => file
        }).Where(file => scenario != "missing-marker" || file.Source != InstanceFileSource.Embedded)
            .Where(file => scenario != "missing-paxi" || file.ProjectId != 418881).ToArray();
        var manifest = fixture.Manifest with { Files = files };
        await InstanceManifestStore.WriteAsync(fixture.ManifestPath, manifest, CancellationToken.None);
        var runtime = new FakeRuntime();

        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Equal(0, runtime.Calls);
        Assert.Contains(result.Issues, issue => issue.Contains("数据包加载目录"));
        Assert.DoesNotContain(fixture.Downloader.Requests, request => request.DestinationPath.EndsWith("datapack.zip"));
        var state = CurseForgeFileStateStore.Read(fixture.Instance, manifest)!;
        var dataPack = Assert.Single(state.Files, file => file.Requirement.ProjectId == 101);
        Assert.Null(dataPack.Requirement.Path);
        Assert.NotNull(dataPack.BlockReason);
    }

    /// <summary>全局数据包仍遵守作者分发限制，用户放入精确 ZIP 后可继续补全。</summary>
    [Fact]
    public async Task PaxiDataPackDistributionRestrictionSupportsManualVerifiedFile()
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        fixture.Api.Mods[101] = fixture.Api.Mods[101] with { AllowModDistribution = false };
        var runtime = new FakeRuntime();
        var installer = Installer(fixture, runtime);

        var first = await installer.InstallAsync(fixture.Instance);

        Assert.False(first.FilesReady);
        Assert.Contains(first.Issues, issue => issue.Contains("config/paxi/datapacks/datapack.zip"));
        Assert.DoesNotContain(fixture.Downloader.Requests, request => request.DestinationPath.EndsWith("datapack.zip"));
        Write(fixture.Instance, ".minecraft/config/paxi/datapacks/datapack.zip", fixture.Payloads["https://cdn.example.test/1001"]);
        fixture.Api.FailIfCalled = true;
        var second = await installer.InstallAsync(fixture.Instance);
        Assert.True(second.FilesReady, string.Join("\n", second.Issues));
        Assert.Equal(1, runtime.Calls);
    }

    /// <summary>下载的数据包即使放在 config 下也不是可变包内配置，已有错误内容必须阻断且保留。</summary>
    [Fact]
    public async Task ChangedDownloadedDataPackCannotUseConfigurationWhitelist()
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        var path = ".minecraft/config/paxi/datapacks/datapack.zip";
        Write(fixture.Instance, path, Encoding.UTF8.GetBytes("player changed this data pack"));
        var runtime = new FakeRuntime();

        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Equal(0, runtime.Calls);
        Assert.Equal("player changed this data pack", File.ReadAllText(Path.Combine(fixture.Instance, path)));
        Assert.DoesNotContain(result.Notes, note => note.Contains("已保留导入后修改的配置") && note.Contains("datapack.zip"));
        Assert.Equal(InstanceDownloadState.Failed, Assert.Single(result.Files, file => file.Requirement.Path == path).State);
    }

    /// <summary>API 数据包与包内同名同大小但哈希不同的声明不覆盖；修改缓存目录也不能绕过 Paxi 目录约束。</summary>
    [Fact]
    public async Task PaxiDataPackCannotOverwriteEmbeddedFileOrUseTamperedCachePath()
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        var installed = await Installer(fixture, new FakeRuntime()).InstallAsync(fixture.Instance);
        Assert.True(installed.FilesReady);
        var cache = Path.Combine(fixture.Instance, CurseForgeFileStateStore.RelativePath);
        File.WriteAllText(cache, File.ReadAllText(cache).Replace(".minecraft/config/paxi/datapacks/datapack.zip", ".minecraft/resourcepacks/datapack.zip"));
        Assert.Throws<InvalidDataException>(() => CurseForgeFileStateStore.Read(fixture.Instance, fixture.Manifest));

        var embedded = Embedded(".minecraft/config/paxi/datapacks/datapack.zip", "pack-owned-data");
        var changed = fixture.Manifest with { Files = fixture.Manifest.Files.Append(embedded).ToArray() };
        await InstanceManifestStore.WriteAsync(fixture.ManifestPath, changed, CancellationToken.None);
        var downloader = new FakeDownloader(fixture.Payloads);
        var result = await new CurseForgeInstanceInstaller(_root, downloader, fixture.Api, new FakeRuntime()).InstallAsync(fixture.Instance);
        Assert.False(result.FilesReady);
        Assert.Contains(result.Issues, issue => issue.Contains("哈希声明冲突"));
        Assert.Empty(downloader.Requests);
    }

    /// <summary>同路径包内与 API 数据包的实际内容相同时复用一份文件，同时保留 SHA-1 和导入 SHA-256。</summary>
    [Fact]
    public async Task IdenticalEmbeddedDataPackIsReusedWithBothHashesAndSingleCheckEntry()
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        fixture = await AddEmbeddedDataPackAsync(fixture, writeFile: true);
        var runtime = new FakeRuntime();

        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Single(fixture.Downloader.Requests);
        Assert.DoesNotContain(fixture.Downloader.Requests, request => request.DestinationPath.EndsWith("datapack.zip"));
        var state = CurseForgeFileStateStore.Read(fixture.Instance, fixture.Manifest)!;
        var effective = CurseForgeFileStateStore.EffectiveRequirements(fixture.Manifest, state);
        var dataPack = Assert.Single(effective, file => file.Path == ".minecraft/config/paxi/datapacks/datapack.zip");
        Assert.Equal(InstanceFileSource.CurseForge, dataPack.Source);
        Assert.Equal(new[] { "sha1", "sha256" }, dataPack.Hashes.Keys.OrderBy(value => value));
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Instance);
        var checkedFile = Assert.Single(report.Files, file => file.Requirement.Path == dataPack.Path);
        Assert.Equal(InstanceFileState.Verified, checkedFile.State);
        Assert.False(checkedFile.IsAllowedConfigurationChange);
    }

    /// <summary>同路径包内数据包缺失时可由官方文件恢复，下载请求同时校验两个独立来源的哈希。</summary>
    [Fact]
    public async Task MissingEmbeddedDataPackDownloadsOnceWithCombinedHashRequirements()
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        fixture = await AddEmbeddedDataPackAsync(fixture, writeFile: false);
        var runtime = new FakeRuntime();

        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Equal(2, fixture.Downloader.Requests.Count);
        var request = Assert.Single(fixture.Downloader.Requests, request => request.DestinationPath.EndsWith("datapack.zip"));
        Assert.Equal(new[] { "sha1", "sha256" }, request.Hashes.Keys.OrderBy(value => value));
        Assert.Equal(fixture.Payloads["https://cdn.example.test/1001"].Length, request.ExpectedSize);
        Assert.DoesNotContain(result.Files, file => file.State == InstanceDownloadState.Unavailable);
        Assert.Equal(1, runtime.Calls);
    }

    /// <summary>同路径文件实际不匹配任一来源时保留原内容并阻断，不能借 config 目录按可变配置放行。</summary>
    [Fact]
    public async Task MismatchedEmbeddedDataPackIsPreservedAndNotTreatedAsMutableConfiguration()
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        fixture = await AddEmbeddedDataPackAsync(fixture, writeFile: true);
        var path = ".minecraft/config/paxi/datapacks/datapack.zip";
        var changed = Enumerable.Repeat((byte)'x', fixture.Payloads["https://cdn.example.test/1001"].Length).ToArray();
        Write(fixture.Instance, path, changed);
        var runtime = new FakeRuntime();

        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Equal(0, runtime.Calls);
        Assert.Equal(changed, File.ReadAllBytes(Path.Combine(fixture.Instance, path)));
        Assert.DoesNotContain(fixture.Downloader.Requests, request => request.DestinationPath.EndsWith("datapack.zip"));
        Assert.DoesNotContain(result.Notes, note => note.Contains("已保留导入后修改的配置") && note.Contains("datapack.zip"));
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Instance);
        var checkedFile = Assert.Single(report.Files, file => file.Requirement.Path == path);
        Assert.Equal(InstanceFileState.HashMismatch, checkedFile.State);
        Assert.False(checkedFile.IsAllowedConfigurationChange);
    }

    /// <summary>缓存删除导入基线后不能把重叠目标伪装成合法 API 文件。</summary>
    [Fact]
    public async Task CachedDataPackOverlapCannotDropEmbeddedHashBaseline()
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        fixture = await AddEmbeddedDataPackAsync(fixture, writeFile: true);
        Assert.True((await Installer(fixture, new FakeRuntime()).InstallAsync(fixture.Instance)).FilesReady);
        var state = CurseForgeFileStateStore.Read(fixture.Instance, fixture.Manifest)!;
        var altered = state with
        {
            Files = state.Files.Select(entry => entry.ClassId == 6945
                ? entry with { Requirement = entry.Requirement with { Hashes = new Dictionary<string, string> { ["sha1"] = entry.Requirement.Hashes["sha1"] } } }
                : entry).ToArray()
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => CurseForgeFileStateStore.WriteAsync(fixture.Instance,
            fixture.Manifest, altered, CancellationToken.None));
    }

    /// <summary>不安全文件名、非 Minecraft 数据和错误项目归属都不能产生磁盘下载请求。</summary>
    [Theory]
    [InlineData("path")]
    [InlineData("file-game")]
    [InlineData("mod-game")]
    [InlineData("owner")]
    [InlineData("http-url")]
    public async Task UnsafeOrMismatchedApiDataIsRejected(string scenario)
    {
        var fixture = await FixtureAsync(1);
        if (scenario == "path") fixture.Api.Files[1000] = fixture.Api.Files[1000] with { FileName = "../outside.jar" };
        if (scenario == "file-game") fixture.Api.Files[1000] = fixture.Api.Files[1000] with { GameId = 999 };
        if (scenario == "mod-game") fixture.Api.Mods[100] = fixture.Api.Mods[100] with { GameId = 999 };
        if (scenario == "owner") fixture.Api.Files[1000] = fixture.Api.Files[1000] with { ModId = 999 };
        if (scenario == "http-url") fixture.Api.Files[1000] = fixture.Api.Files[1000] with { DownloadUrl = "http://cdn.example.test/mod.jar" };
        var runtime = new FakeRuntime();
        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);
        Assert.False(result.FilesReady);
        Assert.NotEmpty(result.Issues);
        Assert.Empty(fixture.Downloader.Requests);
        Assert.Equal(0, runtime.Calls);
    }

    /// <summary>API 文件与 overrides 同名时明确报冲突并保留原文件，而不是覆盖或擅自选择一个版本。</summary>
    [Fact]
    public async Task EmbeddedPathCollisionIsRejectedBeforeDownloading()
    {
        var fixture = await FixtureAsync(1);
        var embedded = Embedded(".minecraft/mods/file-0.jar", "pack override");
        var manifest = fixture.Manifest with { Files = fixture.Manifest.Files.Append(embedded).ToArray() };
        await InstanceManifestStore.WriteAsync(fixture.ManifestPath, manifest, CancellationToken.None);
        Write(fixture.Instance, embedded.Path!, Encoding.UTF8.GetBytes("pack override"));
        var result = await Installer(fixture, new FakeRuntime()).InstallAsync(fixture.Instance);
        Assert.False(result.FilesReady);
        Assert.Contains(result.Issues, issue => issue.Contains("重复"));
        Assert.Empty(fixture.Downloader.Requests);
        Assert.Equal("pack override", File.ReadAllText(Path.Combine(fixture.Instance, embedded.Path!)));
    }

    /// <summary>已有同名不同内容的文件保留原样，哈希冲突不能自动覆盖或绕过。</summary>
    [Fact]
    public async Task ExistingWrongContentIsPreservedAndBlocksRuntime()
    {
        var fixture = await FixtureAsync(1);
        Write(fixture.Instance, ".minecraft/mods/file-0.jar", Encoding.UTF8.GetBytes("wrong content"));
        var runtime = new FakeRuntime();
        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);
        Assert.False(result.FilesReady);
        Assert.Empty(fixture.Downloader.Requests);
        Assert.Equal(0, runtime.Calls);
        Assert.Equal("wrong content", File.ReadAllText(Path.Combine(fixture.Instance, ".minecraft/mods/file-0.jar")));
    }

    /// <summary>默认四路下载有明确并发上限，不为每个文件无界启动请求。</summary>
    [Fact]
    public async Task DownloadsHaveBoundedConcurrency()
    {
        var fixture = await FixtureAsync(8);
        fixture.Downloader.BarrierTarget = 4;
        var result = await Installer(fixture, new FakeRuntime()).InstallAsync(fixture.Instance);
        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Equal(4, fixture.Downloader.Peak);
    }

    /// <summary>某个模组下载失败不丢弃其他完成文件，但缺失必需内容时不准备运行配置。</summary>
    [Fact]
    public async Task PartialDownloadFailureRetainsSuccessfulFiles()
    {
        var fixture = await FixtureAsync(3);
        fixture.Downloader.Before = request =>
        {
            if (request.Urls[0].EndsWith("1001")) throw new HttpRequestException("test failure");
        };
        var runtime = new FakeRuntime();
        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);
        Assert.False(result.FilesReady);
        Assert.Equal(2, result.Files.Count(file => file.State == InstanceDownloadState.Downloaded));
        Assert.Equal(1, result.Files.Count(file => file.State == InstanceDownloadState.Failed));
        Assert.Equal(0, runtime.Calls);
    }

    /// <summary>下载提交后的取消仍返回成功文件记录，缓存和已下载内容可供下次继续使用。</summary>
    [Fact]
    public async Task CancellationRetainsCommittedFileAndReusableCache()
    {
        var fixture = await FixtureAsync(3);
        using var cancellation = new CancellationTokenSource();
        fixture.Downloader.After = _ => cancellation.Cancel();
        var runtime = new FakeRuntime();
        var result = await new CurseForgeInstanceInstaller(_root, fixture.Downloader, fixture.Api, runtime, 1)
            .InstallAsync(fixture.Instance, cancellationToken: cancellation.Token);
        Assert.True(result.IsCanceled);
        Assert.Equal(InstanceDownloadState.Downloaded, Assert.Single(result.Files).State);
        Assert.NotNull(CurseForgeFileStateStore.Read(fixture.Instance, fixture.Manifest));
        Assert.Equal(0, runtime.Calls);
    }

    /// <summary>可选文件不查询和下载；包内已修改配置保留玩家内容并注明原因。</summary>
    [Fact]
    public async Task OptionalEntriesAreSkippedAndMutableConfigurationIsPreserved()
    {
        var fixture = await FixtureAsync(2);
        var config = Embedded(".minecraft/config/player.cfg", "pack settings");
        var manifest = fixture.Manifest with
        {
            Files = [fixture.Manifest.Files[0], fixture.Manifest.Files[1] with { Required = false }, config]
        };
        await InstanceManifestStore.WriteAsync(fixture.ManifestPath, manifest, CancellationToken.None);
        Write(fixture.Instance, config.Path!, Encoding.UTF8.GetBytes("changed settings"));
        var result = await Installer(fixture, new FakeRuntime()).InstallAsync(fixture.Instance);
        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Single(fixture.Downloader.Requests);
        Assert.Equal(new long[] { 1000 }, Assert.Single(fixture.Api.FileRequests));
        Assert.Contains(result.Notes, note => note.Contains("1 个可选文件"));
        Assert.Contains(result.Notes, note => note.Contains("已保留导入后修改的配置"));
    }

    /// <summary>更改导入声明使旧缓存失效，下一次补全重新解析；下载期间改声明也不能发布就绪结果。</summary>
    [Fact]
    public async Task ChangedManifestInvalidatesCacheAndPreventsRuntime()
    {
        var fixture = await FixtureAsync(1);
        var changed = fixture.Manifest with { Name = "Changed" };
        fixture.Downloader.After = _ => InstanceManifestStore.WriteAsync(fixture.ManifestPath, changed,
            CancellationToken.None).GetAwaiter().GetResult();
        var runtime = new FakeRuntime();
        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);
        Assert.False(result.FilesReady);
        Assert.Single(result.Files);
        Assert.Equal(0, runtime.Calls);
        Assert.Contains(result.Issues, issue => issue.Contains("清单发生变化"));
        Assert.Null(CurseForgeFileStateStore.Read(fixture.Instance, changed));
    }

    /// <summary>缓存即使来自本地也需重新验证目标目录，不能通过编辑缓存写入任意位置。</summary>
    [Fact]
    public async Task TamperedCachedPathIsRejected()
    {
        var fixture = await FixtureAsync(1);
        Assert.True((await Installer(fixture, new FakeRuntime()).InstallAsync(fixture.Instance)).FilesReady);
        var path = Path.Combine(fixture.Instance, CurseForgeFileStateStore.RelativePath);
        File.WriteAllText(path, File.ReadAllText(path).Replace(".minecraft/mods/file-0.jar", "outside/file-0.jar"));
        Assert.Throws<InvalidDataException>(() => CurseForgeFileStateStore.Read(fixture.Instance, fixture.Manifest));
    }

    /// <summary>构造默认服务时拒绝无效并发范围，避免运行阶段才出现难以处理的参数问题。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void InvalidConcurrencyIsRejected(int concurrency) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        new CurseForgeInstanceInstaller(_root, new FakeDownloader(new Dictionary<string, byte[]>()),
            new FakeApi(), new FakeRuntime(), concurrency));

    /// <summary>在随机临时目录建立带固定 API 元数据的实例，不接触桌面用户文件。</summary>
    private async Task<Fixture> FixtureAsync(int count)
    {
        var instance = Path.Combine(_root, "instances", "CF Test");
        Directory.CreateDirectory(instance);
        var api = new FakeApi();
        var payloads = new Dictionary<string, byte[]>();
        var references = new List<InstanceFileRequirement>();
        for (var index = 0; index < count; index++)
        {
            var modId = 100L + index;
            var fileId = 1000L + index;
            var url = "https://cdn.example.test/" + fileId;
            var bytes = Encoding.UTF8.GetBytes("fixed content " + index);
            payloads[url] = bytes;
            api.Files[fileId] = new CurseForgeFile
            {
                Id = fileId, ModId = modId, GameId = 432, IsAvailable = true, FileName = $"file-{index}.jar",
                FileLength = bytes.Length, DownloadUrl = url,
                Hashes = [new CurseForgeFileHash { Algo = 1, Value = Convert.ToHexStringLower(SHA1.HashData(bytes)) }],
                Dependencies = [new CurseForgeFileDependency { ModId = 999999, RelationType = 3 }]
            };
            api.Mods[modId] = new CurseForgeMod
            {
                Id = modId, GameId = 432, ClassId = 6, AllowModDistribution = true, IsAvailable = true,
                Links = new CurseForgeModLinks { WebsiteUrl = "https://www.curseforge.com/minecraft/mc-mods/test-" + modId }
            };
            references.Add(new InstanceFileRequirement { Source = InstanceFileSource.CurseForge, ProjectId = modId, FileId = fileId });
        }
        var manifest = new InstanceImportManifest
        {
            SourceFormat = "CurseForge", Name = "CF Test", MinecraftVersion = "1.20.1", Files = references,
            Components = [new("minecraft", "1.20.1"), new("fabric", "0.19.3")]
        };
        var manifestPath = Path.Combine(instance, InstanceImportManifest.FileName);
        await InstanceManifestStore.WriteAsync(manifestPath, manifest, CancellationToken.None);
        return new(instance, manifestPath, manifest, api, payloads, new FakeDownloader(payloads));
    }

    /// <summary>为数据包测试声明固定 Paxi 项目及实际包内加载顺序标记，不依赖模组文件名推断。</summary>
    private async Task<Fixture> PaxiFixtureAsync(long paxiProjectId, string gameDirectory)
    {
        var fixture = await FixtureAsync(2);
        fixture.Api.Mods[paxiProjectId] = fixture.Api.Mods[100] with { Id = paxiProjectId };
        fixture.Api.Mods.Remove(100);
        fixture.Api.Files[1000] = fixture.Api.Files[1000] with { ModId = paxiProjectId, FileName = "paxi.jar" };
        fixture.Api.Mods[101] = fixture.Api.Mods[101] with { ClassId = 6945 };
        fixture.Api.Files[1001] = fixture.Api.Files[1001] with { FileName = "datapack.zip" };
        var prefix = gameDirectory == "." ? "" : gameDirectory + "/";
        var marker = Embedded(prefix + "config/paxi/datapack_load_order.json", "[]");
        var manifest = fixture.Manifest with
        {
            GameDirectory = gameDirectory,
            Files = [fixture.Manifest.Files[0] with { ProjectId = paxiProjectId }, fixture.Manifest.Files[1], marker]
        };
        await InstanceManifestStore.WriteAsync(fixture.ManifestPath, manifest, CancellationToken.None);
        Write(fixture.Instance, marker.Path!, Encoding.UTF8.GetBytes("[]"));
        return fixture with { Manifest = manifest };
    }

    /// <summary>模拟原包已内嵌同一个 API 数据包，导入文件按真实导入器保存 SHA-256 而非 API 的 SHA-1。</summary>
    private static async Task<Fixture> AddEmbeddedDataPackAsync(Fixture fixture, bool writeFile)
    {
        var bytes = fixture.Payloads["https://cdn.example.test/1001"];
        var path = ".minecraft/config/paxi/datapacks/datapack.zip";
        var embedded = new InstanceFileRequirement
        {
            Path = path, Source = InstanceFileSource.Embedded, Size = bytes.Length,
            Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) }
        };
        var manifest = fixture.Manifest with { Files = fixture.Manifest.Files.Append(embedded).ToArray() };
        await InstanceManifestStore.WriteAsync(fixture.ManifestPath, manifest, CancellationToken.None);
        if (writeFile) Write(fixture.Instance, path, bytes);
        return fixture with { Manifest = manifest };
    }

    /// <summary>创建固定测试文件的包内声明。</summary>
    private static InstanceFileRequirement Embedded(string path, string content) => new()
    {
        Path = path, Source = InstanceFileSource.Embedded, Size = Encoding.UTF8.GetByteCount(content),
        Hashes = new Dictionary<string, string> { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(content))) }
    };

    /// <summary>仅向测试实例的指定相对路径写入内容。</summary>
    private static void Write(string instance, string relative, byte[] bytes)
    {
        var path = Path.Combine(instance, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>统一注入全部测试替身，确保单元测试从不访问真实 API。</summary>
    private CurseForgeInstanceInstaller Installer(Fixture fixture, FakeRuntime runtime) =>
        new(_root, fixture.Downloader, fixture.Api, runtime);

    /// <summary>删除本测试随机生成的目录，不操作其他实例。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>保存隔离测试实例及全部可控依赖。</summary>
    private sealed record Fixture(string Instance, string ManifestPath, InstanceImportManifest Manifest,
        FakeApi Api, Dictionary<string, byte[]> Payloads, FakeDownloader Downloader);

    /// <summary>按 ID 返回固定 API 结果，并记录每批请求及离线场景的误联网行为。</summary>
    private sealed class FakeApi : ICurseForgeApiClient
    {
        public Dictionary<long, CurseForgeFile> Files { get; } = [];
        public Dictionary<long, CurseForgeMod> Mods { get; } = [];
        public List<long[]> FileRequests { get; } = [];
        public List<long[]> ModRequests { get; } = [];
        public bool FailIfCalled { get; set; }
        public CurseForgeRateLimit? LastRateLimit => null;

        /// <summary>模拟批量文件查询，可省略不存在的 ID。</summary>
        public Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
        {
            Assert.False(FailIfCalled, "This operation must reuse offline metadata.");
            FileRequests.Add(ids.ToArray());
            return Task.FromResult<IReadOnlyList<CurseForgeFile>>(ids.Where(Files.ContainsKey).Select(id => Files[id]).ToArray());
        }

        /// <summary>模拟批量项目查询，保留项目类别与分发标记。</summary>
        public Task<IReadOnlyList<CurseForgeMod>> GetModsAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
        {
            Assert.False(FailIfCalled, "This operation must reuse offline metadata.");
            ModRequests.Add(ids.ToArray());
            return Task.FromResult<IReadOnlyList<CurseForgeMod>>(ids.Where(Mods.ContainsKey).Select(id => Mods[id]).ToArray());
        }

        /// <summary>当前流程不得退回到非批量单文件查询。</summary>
        public Task<CurseForgeFile> GetFileAsync(long projectId, long fileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>当前流程不得查询与批量列表无关的单个项目。</summary>
        public Task<CurseForgeMod> GetModAsync(long projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>实例补全不得调用搜索选择最新版替代清单内容。</summary>
        public Task<CurseForgeSearchResult> SearchAsync(CurseForgeSearchOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>记录运行安装调用次数并返回可控配置，避免真实运行依赖下载。</summary>
    private sealed class FakeRuntime : IPrismInstanceInstaller
    {
        public int Calls { get; private set; }

        /// <summary>返回固定测试启动配置。</summary>
        public Task<PrismInstallResult> InstallAsync(string instancePath, IProgress<InstanceDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PrismInstallResult { SourceFormat = "CurseForge", LaunchProfilePath = "test-launch.json" });
        }
    }

    /// <summary>无覆盖写入固定字节，支持并发屏障、单文件失败以及提交后的取消。</summary>
    private sealed class FakeDownloader(IReadOnlyDictionary<string, byte[]> payloads) : IFileDownloader
    {
        private readonly TaskCompletionSource _barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _peak;
        public int Peak => _peak;
        public int BarrierTarget { get; set; }
        public Action<FileDownloadRequest>? Before { get; set; }
        public Action<FileDownloadRequest>? After { get; set; }
        public ConcurrentQueue<FileDownloadRequest> Requests { get; } = new();

        /// <summary>记录请求并模拟原子提交后的成功结果。</summary>
        public async Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            var active = Interlocked.Increment(ref _active);
            int observed;
            do { observed = Volatile.Read(ref _peak); }
            while (active > observed && Interlocked.CompareExchange(ref _peak, active, observed) != observed);
            try
            {
                Before?.Invoke(request);
                if (BarrierTarget > 0)
                {
                    if (active >= BarrierTarget) _barrier.TrySetResult();
                    await _barrier.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                }
                var bytes = payloads[request.Urls[0]];
                Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
                await using (var output = new FileStream(request.DestinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await output.WriteAsync(bytes, cancellationToken);
                After?.Invoke(request);
                return new(request.DestinationPath, bytes.Length, true);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
