using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
        Assert.Equal(1, runtime.Calls);
        Assert.Contains(first.Issues, issue => issue.Contains("https://www.curseforge.com/minecraft/mc-mods/test-100/files/1000"));
        var state = Assert.IsType<CurseForgeFileResolution>(CurseForgeFileStateStore.Read(fixture.Instance, fixture.Manifest));
        var file = Assert.Single(state.Files).Requirement;
        Assert.Empty(file.Downloads);
        Write(fixture.Instance, file.Path!, fixture.Payloads["https://cdn.example.test/1000"]);
        fixture.Api.FailIfCalled = true;

        var second = await installer.InstallAsync(fixture.Instance);

        Assert.True(second.FilesReady, string.Join("\n", second.Issues));
        Assert.Equal(2, runtime.Calls);
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
        Assert.Equal(2, runtime.Calls);
    }

    /// <summary>MD5 或未知算法不会被误当 SHA-1，内容校验失败不阻止准备独立运行依赖。</summary>
    [Fact]
    public async Task MissingSha1DoesNotDownloadOrAllowLaunch()
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
        Assert.Equal(1, runtime.Calls);
        Assert.Null(result.LaunchProfilePath);
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
        Assert.Equal(1, runtime.Calls);
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
        Assert.Equal(2, runtime.Calls);
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
        Assert.Equal(1, runtime.Calls);
        Assert.Equal("player changed this data pack", File.ReadAllText(Path.Combine(fixture.Instance, path)));
        Assert.DoesNotContain(result.Notes, note => note.Contains("已保留导入后修改的配置") && note.Contains("datapack.zip"));
        Assert.Equal(InstanceDownloadState.Failed, Assert.Single(result.Files, file => file.Requirement.Path == path).State);
    }

    /// <summary>来源证据不完整的同路径声明仍被拒绝；修改缓存目录也不能绕过 Paxi 目录约束。</summary>
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
        var result = await new CurseForgeInstanceInstaller(_root, downloader, fixture.Api, new FakeRuntime(),
            modrinthApiClient: new EmptyModrinthApiClient()).InstallAsync(fixture.Instance);
        Assert.False(result.FilesReady);
        Assert.NotEmpty(result.Issues);
        Assert.Empty(downloader.Requests);
    }

    /// <summary>相同内容也按独立来源保存摘要，安装计划仅选用包内基线进行一次完整性检查。</summary>
    [Fact]
    public async Task IdenticalEmbeddedDataPackKeepsIndependentHashesAndSingleCheckEntry()
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
        Assert.Equal(InstanceFileSource.Embedded, dataPack.Source);
        Assert.Equal(InstanceFileOrigin.EmbeddedOverride, dataPack.Origin);
        Assert.Equal(new[] { "sha256" }, dataPack.Hashes.Keys);
        var resolved = Assert.Single(state.Files, file => file.ClassId == 6945);
        Assert.Equal(new[] { "sha1" }, resolved.Requirement.Hashes.Keys);
        Assert.NotNull(resolved.EmbeddedOverride);
        Assert.Equal(new[] { "sha256" }, resolved.EmbeddedOverride.Hashes.Keys);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Instance);
        var checkedFile = Assert.Single(report.Files, file => file.Requirement.Path == dataPack.Path);
        Assert.Equal(InstanceFileState.Verified, checkedFile.State);
        Assert.False(checkedFile.IsAllowedConfigurationChange);
    }

    /// <summary>原包修改过的数据包只需匹配导入 SHA-256；不同内容的官方 SHA-1 留作来源元数据。</summary>
    [Fact]
    public async Task DifferentOfficialSha1DoesNotRejectVerifiedEmbeddedOverride()
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        var embeddedBytes = DifferentContent(fixture.Payloads["https://cdn.example.test/1001"]);
        fixture = await AddEmbeddedDataPackAsync(fixture, writeFile: true, embeddedBytes);
        var runtime = new FakeRuntime();

        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(embeddedBytes, File.ReadAllBytes(Path.Combine(fixture.Instance, ".minecraft/config/paxi/datapacks/datapack.zip")));
        Assert.DoesNotContain(fixture.Downloader.Requests, request => request.DestinationPath.EndsWith("datapack.zip"));
        var state = CurseForgeFileStateStore.Read(fixture.Instance, fixture.Manifest)!;
        var resolved = Assert.Single(state.Files, file => file.ClassId == 6945);
        Assert.Equal(101, resolved.Requirement.ProjectId);
        Assert.Equal(1001, resolved.Requirement.FileId);
        Assert.Equal(fixture.Api.Files[1001].Hashes.Single().Value, resolved.Requirement.Hashes["sha1"]);
        var effective = Assert.Single(CurseForgeFileStateStore.EffectiveRequirements(fixture.Manifest, state),
            file => file.Path == ".minecraft/config/paxi/datapacks/datapack.zip");
        Assert.Equal(InstanceFileOrigin.EmbeddedOverride, effective.Origin);
        Assert.Equal(new[] { "sha256" }, effective.Hashes.Keys);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(embeddedBytes)), effective.Hashes["sha256"]);
        Assert.Empty(effective.Downloads);
        Assert.NotNull(effective.CurseForgeOrigin);
        Assert.Equal(101, effective.CurseForgeOrigin.ProjectId);
        Assert.Equal(1001, effective.CurseForgeOrigin.FileId);
        Assert.Equal(fixture.Api.Files[1001].FileLength, effective.CurseForgeOrigin.Size);
        Assert.Equal(fixture.Api.Files[1001].Hashes.Single().Value, effective.CurseForgeOrigin.Hashes["sha1"]);
    }

    /// <summary>缺失的包内数据包只能从原 ZIP 的记录条目恢复，恢复后仍按包内 SHA-256 校验。</summary>
    [Fact]
    public async Task MissingEmbeddedDataPackIsRestoredFromOriginalArchive()
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        var embeddedBytes = DifferentContent(fixture.Payloads["https://cdn.example.test/1001"]);
        fixture = await AddEmbeddedDataPackAsync(fixture, writeFile: false, embeddedBytes);
        WriteSourceArchive(fixture, embeddedBytes);
        var runtime = new FakeRuntime();

        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Single(fixture.Downloader.Requests);
        Assert.DoesNotContain(fixture.Downloader.Requests, request => request.DestinationPath.EndsWith("datapack.zip"));
        var restored = Assert.Single(result.Files, file => file.Requirement.Path == ".minecraft/config/paxi/datapacks/datapack.zip");
        Assert.Equal(InstanceDownloadState.RestoredEmbeddedFile, restored.State);
        Assert.Equal(new[] { "sha256" }, restored.Requirement.Hashes.Keys);
        Assert.Equal(embeddedBytes, File.ReadAllBytes(Path.Combine(fixture.Instance, restored.Requirement.Path!)));
        Assert.Equal(1, runtime.Calls);
    }

    /// <summary>原 ZIP 不存在时明确报告包内文件待修复，不以同名官方文件自动替换。</summary>
    [Fact]
    public async Task MissingEmbeddedDataPackWithoutArchiveRemainsMissingButPreparesRuntime()
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        fixture = await AddEmbeddedDataPackAsync(fixture, writeFile: false);
        var runtime = new FakeRuntime();

        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.Equal(1, runtime.Calls);
        Assert.Single(fixture.Downloader.Requests);
        Assert.DoesNotContain(fixture.Downloader.Requests, request => request.DestinationPath.EndsWith("datapack.zip"));
        var missing = Assert.Single(result.Files, file => file.Requirement.Path == ".minecraft/config/paxi/datapacks/datapack.zip");
        Assert.Equal(InstanceDownloadState.MissingEmbeddedFile, missing.State);
        Assert.NotEmpty(result.ContentIssues);
        Assert.Empty(result.RuntimeIssues);
        Assert.False(File.Exists(Path.Combine(fixture.Instance, missing.Requirement.Path!)));
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
        Assert.Equal(1, runtime.Calls);
        Assert.Null(result.LaunchProfilePath);
        Assert.NotEmpty(result.ContentIssues);
        Assert.Empty(result.RuntimeIssues);
        Assert.Equal(changed, File.ReadAllBytes(Path.Combine(fixture.Instance, path)));
        Assert.DoesNotContain(fixture.Downloader.Requests, request => request.DestinationPath.EndsWith("datapack.zip"));
        Assert.DoesNotContain(result.Notes, note => note.Contains("已保留导入后修改的配置") && note.Contains("datapack.zip"));
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Instance);
        var checkedFile = Assert.Single(report.Files, file => file.Requirement.Path == path);
        Assert.Equal(InstanceFileState.HashMismatch, checkedFile.State);
        Assert.False(checkedFile.IsAllowedConfigurationChange);
    }

    /// <summary>即使 SHA-256 描述真实内容，包内文件大小与导入记录不同仍须阻止最终启动。</summary>
    [Fact]
    public async Task EmbeddedOverrideWrongSizeFailsWithoutBlockingFabricPreparation()
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        fixture = await AddEmbeddedDataPackAsync(fixture, writeFile: true);
        var originalBytes = fixture.Payloads["https://cdn.example.test/1001"];
        var path = ".minecraft/config/paxi/datapacks/datapack.zip";
        var manifest = fixture.Manifest with
        {
            Files = fixture.Manifest.Files.Select(file => file.Path == path ? file with { Size = file.Size + 1 } : file).ToArray()
        };
        await InstanceManifestStore.WriteAsync(fixture.ManifestPath, manifest, CancellationToken.None);
        var runtime = new FakeRuntime();

        var result = await Installer(fixture with { Manifest = manifest }, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.Equal(1, runtime.Calls);
        Assert.NotEmpty(result.ContentIssues);
        Assert.Empty(result.RuntimeIssues);
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(fixture.Instance, path)));
        Assert.DoesNotContain(fixture.Downloader.Requests, request => request.DestinationPath.EndsWith("datapack.zip"));
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Instance);
        Assert.Equal(InstanceFileState.SizeMismatch, Assert.Single(report.Files, file => file.Requirement.Path == path).State);
    }

    /// <summary>同路径优先级来自导入证据，普通 mods 目录中的确认包内覆盖也遵守同样规则。</summary>
    [Fact]
    public async Task ConfirmedEmbeddedModOverrideHasSamePriorityAsDataPack()
    {
        var fixture = await FixtureAsync(1);
        var bytes = Encoding.UTF8.GetBytes("author-customized-mod");
        var embedded = new InstanceFileRequirement
        {
            Source = InstanceFileSource.Embedded, Path = ".minecraft/mods/file-0.jar",
            ArchiveEntry = "overrides/mods/file-0.jar", Size = bytes.Length,
            Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) }
        };
        var manifest = fixture.Manifest with { Files = fixture.Manifest.Files.Append(embedded).ToArray() };
        await InstanceManifestStore.WriteAsync(fixture.ManifestPath, manifest, CancellationToken.None);
        Write(fixture.Instance, embedded.Path!, bytes);
        var runtime = new FakeRuntime();

        var result = await Installer(fixture with { Manifest = manifest }, runtime).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Equal(1, runtime.Calls);
        Assert.Empty(fixture.Downloader.Requests);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Instance, embedded.Path!)));
        var state = CurseForgeFileStateStore.Read(fixture.Instance, manifest)!;
        var requirement = Assert.Single(CurseForgeFileStateStore.EffectiveRequirements(manifest, state));
        Assert.Equal(InstanceFileOrigin.EmbeddedOverride, requirement.Origin);
        Assert.Equal(new[] { "sha256" }, requirement.Hashes.Keys);
    }

    /// <summary>原包证据不足时不接受内容、不修改文件；独立运行依赖准备不授予最终启动权限。</summary>
    [Theory]
    [InlineData("missing-entry")]
    [InlineData("missing-sha256")]
    public async Task OverridePriorityRequiresImportedArchiveAndSha256Evidence(string missing)
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        fixture = await AddEmbeddedDataPackAsync(fixture, writeFile: true);
        var path = ".minecraft/config/paxi/datapacks/datapack.zip";
        var manifest = fixture.Manifest with
        {
            Files = fixture.Manifest.Files.Select(file => file.Path != path ? file : missing == "missing-entry"
                ? file with { ArchiveEntry = null }
                : file with { Hashes = new Dictionary<string, string> { ["sha1"] = fixture.Api.Files[1001].Hashes.Single().Value } }).ToArray()
        };
        await InstanceManifestStore.WriteAsync(fixture.ManifestPath, manifest, CancellationToken.None);
        var filesBefore = Directory.EnumerateFiles(fixture.Instance, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, File.ReadAllBytes);
        var runtime = new FakeRuntime();

        var result = await Installer(fixture with { Manifest = manifest }, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.NotEmpty(result.Issues);
        Assert.NotEmpty(result.ContentIssues);
        Assert.Empty(result.RuntimeIssues);
        Assert.False(result.ContentReady);
        Assert.True(result.RuntimeReady);
        Assert.False(result.LaunchReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.NotNull(result.PreparedLaunchProfilePath);
        Assert.Empty(result.Files);
        Assert.Empty(fixture.Downloader.Requests);
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(filesBefore.Keys.Order(), Directory.EnumerateFiles(fixture.Instance, "*", SearchOption.AllDirectories).Order());
        Assert.All(filesBefore, file => Assert.Equal(file.Value, File.ReadAllBytes(file.Key)));
    }

    /// <summary>旧缓存曾合并两种来源摘要时，从导入清单迁移独立基线并继续支持离线检查。</summary>
    [Fact]
    public async Task LegacyCombinedCacheMigratesWithoutRequiringBothContentHashes()
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        var embeddedBytes = DifferentContent(fixture.Payloads["https://cdn.example.test/1001"]);
        fixture = await AddEmbeddedDataPackAsync(fixture, writeFile: true, embeddedBytes);
        Assert.True((await Installer(fixture, new FakeRuntime()).InstallAsync(fixture.Instance)).FilesReady);
        var cachePath = Path.Combine(fixture.Instance, CurseForgeFileStateStore.RelativePath);
        var legacy = JsonNode.Parse(File.ReadAllText(cachePath))!.AsObject();
        legacy["schemaVersion"] = 1;
        foreach (var item in legacy["files"]!.AsArray())
        {
            var entry = item!.AsObject();
            entry.Remove("embeddedOverride");
            var requirement = entry["requirement"]!.AsObject();
            requirement.Remove("origin");
            requirement.Remove("curseForgeOrigin");
            if (entry["classId"]!.GetValue<int>() == 6945)
                requirement["hashes"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(embeddedBytes));
        }
        File.WriteAllText(cachePath, legacy.ToJsonString());
        fixture.Api.FailIfCalled = true;

        var migrated = CurseForgeFileStateStore.Read(fixture.Instance, fixture.Manifest)!;
        var selected = Assert.Single(CurseForgeFileStateStore.EffectiveRequirements(fixture.Manifest, migrated),
            file => file.Path == ".minecraft/config/paxi/datapacks/datapack.zip");
        var result = await Installer(fixture, new FakeRuntime()).InstallAsync(fixture.Instance);

        Assert.Equal(2, migrated.SchemaVersion);
        Assert.Equal(InstanceFileOrigin.EmbeddedOverride, selected.Origin);
        Assert.Equal(new[] { "sha256" }, selected.Hashes.Keys);
        Assert.Equal(new[] { "sha1" }, Assert.Single(migrated.Files, entry => entry.ClassId == 6945).Requirement.Hashes.Keys);
        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Empty(result.Files);
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
                ? entry with { EmbeddedOverride = null }
                : entry).ToArray()
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => CurseForgeFileStateStore.WriteAsync(fixture.Instance,
            fixture.Manifest, altered, CancellationToken.None));
    }

    /// <summary>不安全平台数据不产生内容下载或写入；仅尝试独立运行依赖，内容及最终启动仍阻断。</summary>
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
        Write(fixture.Instance, ".minecraft/mods/file-0.jar", Encoding.UTF8.GetBytes("existing local mod"));
        Write(fixture.Instance, ".minecraft/outside.jar", Encoding.UTF8.GetBytes("outside destination must stay unchanged"));
        var filesBefore = Directory.EnumerateFiles(fixture.Instance, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, File.ReadAllBytes);
        var runtime = new FakeRuntime();
        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);
        Assert.False(result.FilesReady);
        Assert.NotEmpty(result.Issues);
        Assert.NotEmpty(result.ContentIssues);
        Assert.Empty(result.RuntimeIssues);
        Assert.False(result.ContentReady);
        Assert.True(result.RuntimeReady);
        Assert.False(result.LaunchReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.NotNull(result.PreparedLaunchProfilePath);
        Assert.Empty(result.Files);
        Assert.Empty(fixture.Downloader.Requests);
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(filesBefore.Keys.Order(), Directory.EnumerateFiles(fixture.Instance, "*", SearchOption.AllDirectories).Order());
        Assert.All(filesBefore, file => Assert.Equal(file.Value, File.ReadAllBytes(file.Key)));
    }

    /// <summary>只有同名和 SHA-1 而无原包来源证据时明确报冲突，不能自动选择该本地文件覆盖官方要求。</summary>
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
        Assert.NotEmpty(result.Issues);
        Assert.Empty(fixture.Downloader.Requests);
        Assert.Equal("pack override", File.ReadAllText(Path.Combine(fixture.Instance, embedded.Path!)));
    }

    /// <summary>普通 CurseForge 文件长度正确但 SHA-1 错误仍失败，保留用户内容且禁止最终启动。</summary>
    [Fact]
    public async Task OrdinaryCurseForgeWrongSha1IsPreservedAndBlocksLaunch()
    {
        var fixture = await FixtureAsync(1);
        var changed = DifferentContent(fixture.Payloads["https://cdn.example.test/1000"]);
        Write(fixture.Instance, ".minecraft/mods/file-0.jar", changed);
        var runtime = new FakeRuntime();
        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);
        Assert.False(result.FilesReady);
        Assert.Empty(fixture.Downloader.Requests);
        Assert.Equal(1, runtime.Calls);
        Assert.Null(result.LaunchProfilePath);
        Assert.Equal(changed, File.ReadAllBytes(Path.Combine(fixture.Instance, ".minecraft/mods/file-0.jar")));
        Assert.NotEmpty(result.ContentIssues);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Instance);
        Assert.Equal(InstanceFileState.HashMismatch, Assert.Single(report.Files, file => file.Requirement.Path == ".minecraft/mods/file-0.jar").State);
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

    /// <summary>某个模组下载失败仍保留完成文件并准备运行依赖，但必需内容缺失时不发布可用启动配置。</summary>
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
        Assert.Equal(1, runtime.Calls);
        Assert.Null(result.LaunchProfilePath);
    }

    /// <summary>下载提交后的取消仍返回成功文件记录，缓存和已下载内容可供下次继续使用。</summary>
    [Fact]
    public async Task CancellationRetainsCommittedFileAndReusableCache()
    {
        var fixture = await FixtureAsync(3);
        using var cancellation = new CancellationTokenSource();
        fixture.Downloader.After = _ => cancellation.Cancel();
        var runtime = new FakeRuntime();
        var result = await new CurseForgeInstanceInstaller(_root, fixture.Downloader, fixture.Api, runtime, 1,
            new EmptyModrinthApiClient())
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

    /// <summary>非必需包内内容缺失只作跳过提示，不阻止运行依赖或发布可用启动配置。</summary>
    [Fact]
    public async Task MissingOptionalEmbeddedContentDoesNotBlockReadiness()
    {
        var fixture = await FixtureAsync(1);
        var optional = new InstanceFileRequirement
        {
            Source = InstanceFileSource.Embedded, Origin = InstanceFileOrigin.EmbeddedOverride,
            Path = ".minecraft/config/paxi/datapacks/optional.zip", Required = false,
            ArchiveEntry = "overrides/config/paxi/datapacks/optional.zip", Size = 0,
            Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())) }
        };
        var manifest = fixture.Manifest with { Files = fixture.Manifest.Files.Append(optional).ToArray() };
        await InstanceManifestStore.WriteAsync(fixture.ManifestPath, manifest, CancellationToken.None);
        var runtime = new FakeRuntime();

        var result = await Installer(fixture with { Manifest = manifest }, runtime).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.NotNull(result.LaunchProfilePath);
        Assert.Equal(1, runtime.Calls);
        Assert.Empty(result.ContentIssues);
        Assert.Empty(result.RuntimeIssues);
        Assert.Contains(result.Notes, note => note.Contains("1 个可选文件"));
    }

    /// <summary>模组内容全部通过仍不能掩盖必要 Fabric 运行库失败，最终启动配置必须保持不可用。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRequiredRuntimeDependencyBlocksFinalLaunch(bool recordOnly)
    {
        var fixture = await FixtureAsync(1);
        var runtime = new FakeRuntime { Fail = true, FailureRecordOnly = recordOnly };

        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.Equal(1, runtime.Calls);
        Assert.Empty(result.ContentIssues);
        Assert.Contains(result.RuntimeIssues, issue => issue.Contains("Fabric"));
        Assert.Contains(result.Issues, issue => issue.Contains("Fabric"));
        Assert.Equal(InstanceDownloadState.Downloaded, Assert.Single(result.Files, file => file.Requirement.Source == InstanceFileSource.CurseForge).State);
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
        Assert.Contains(result.Issues, issue => issue.Contains("清单") && issue.Contains("变化"));
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

    /// <summary>平台 API 整体失败只阻断内容准备，Minecraft/Fabric 依赖仍独立执行且不误报启动就绪。</summary>
    [Fact]
    public async Task PlatformResolutionFailureStillPreparesRuntimeWithoutTouchingContent()
    {
        var fixture = await FixtureAsync(1);
        fixture.Api.FileRequestFailure = new HttpRequestException("platform unavailable");
        var local = Encoding.UTF8.GetBytes("existing local content");
        Write(fixture.Instance, ".minecraft/mods/file-0.jar", local);
        var runtime = new FakeRuntime();

        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.Equal(1, runtime.Calls);
        Assert.Empty(fixture.Downloader.Requests);
        Assert.False(result.ContentReady);
        Assert.True(result.RuntimeReady);
        Assert.False(result.LaunchReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.NotNull(result.PreparedLaunchProfilePath);
        Assert.Contains(result.ContentIssues, issue => issue.Contains("platform unavailable"));
        Assert.Empty(result.RuntimeIssues);
        Assert.Equal(local, File.ReadAllBytes(Path.Combine(fixture.Instance, ".minecraft/mods/file-0.jar")));
    }

    /// <summary>平台来源缓存损坏时保持内容失败、不重新信任已有字节，同时运行阶段仍会尝试。</summary>
    [Fact]
    public async Task InvalidPlatformCacheStillAllowsIndependentRuntimeAttempt()
    {
        var fixture = await FixtureAsync(1);
        var path = Path.Combine(fixture.Instance, CurseForgeFileStateStore.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "invalid JSON");
        var runtime = new FakeRuntime();

        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.Equal(1, runtime.Calls);
        Assert.Empty(fixture.Downloader.Requests);
        Assert.False(result.ContentReady);
        Assert.True(result.RuntimeReady);
        Assert.False(result.LaunchReady);
        Assert.NotEmpty(result.ContentIssues);
        Assert.Equal("invalid JSON", File.ReadAllText(path));
    }

    /// <summary>运行层只返回分类问题时仍需传递，不能把内容问题丢掉或归入运行失败。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClassifiedRuntimeResultIssuesArePreserved(bool contentFailure)
    {
        var fixture = await FixtureAsync(1);
        var runtime = new FakeRuntime
        {
            Result = new PrismInstallResult
            {
                LaunchProfilePath = "prepared.json", PreparedLaunchProfilePath = "prepared.json",
                ContentIssues = contentFailure ? ["required content failure"] : [],
                RuntimeIssues = contentFailure ? [] : ["required runtime failure"]
            }
        };

        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.Equal("prepared.json", result.PreparedLaunchProfilePath);
        Assert.Equal(contentFailure, result.RuntimeReady);
        Assert.Equal(!contentFailure, result.ContentReady);
        Assert.Contains(result.Issues, issue => issue.Contains("failure"));
        if (contentFailure) Assert.Contains("required content failure", result.ContentIssues);
        else Assert.Contains("required runtime failure", result.RuntimeIssues);
    }

    /// <summary>显式接受的包内替代来源用于后续 CF 补全，不回退原包或官方 SHA-1。</summary>
    [Fact]
    public async Task AcceptedReplacementRemainsAuthoritativeDuringCompletion()
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        fixture = await AddEmbeddedDataPackAsync(fixture, writeFile: true);
        var file = Assert.Single(fixture.Manifest.Files, file => file.ArchiveEntry is not null);
        var changed = Encoding.UTF8.GetBytes("user explicitly accepted replacement");
        Write(fixture.Instance, file.Path!, changed);
        await new InstanceLocalReplacementService(_root).AcceptExistingAsync(fixture.Instance, file.Path!);

        var result = await Installer(fixture, new FakeRuntime()).InstallAsync(fixture.Instance);

        Assert.True(result.LaunchReady, string.Join("\n", result.Issues));
        Assert.Equal(changed, File.ReadAllBytes(Path.Combine(fixture.Instance, file.Path!)));
        Assert.DoesNotContain(fixture.Downloader.Requests, request => request.DestinationPath.EndsWith("datapack.zip"));
    }

    /// <summary>运行阶段撤销替换记录后拒绝发布基于旧用户选择的完成结果。</summary>
    [Fact]
    public async Task ReplacementWithdrawnDuringRuntimePreventsLaunch()
    {
        var fixture = await PaxiFixtureAsync(418881, ".minecraft");
        fixture = await AddEmbeddedDataPackAsync(fixture, writeFile: true);
        var file = Assert.Single(fixture.Manifest.Files, file => file.ArchiveEntry is not null);
        Write(fixture.Instance, file.Path!, Encoding.UTF8.GetBytes("accepted replacement"));
        await new InstanceLocalReplacementService(_root).AcceptExistingAsync(fixture.Instance, file.Path!);
        var runtime = new FakeRuntime
        {
            BeforeInstall = () => File.Delete(Path.Combine(fixture.Instance, InstanceLocalReplacementService.RelativePath))
        };

        var result = await Installer(fixture, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.LaunchReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.NotNull(result.PreparedLaunchProfilePath);
        Assert.Contains(result.ContentIssues, issue => issue.Contains("替换记录"));
    }

    /// <summary>构造默认服务时拒绝无效并发范围，避免运行阶段才出现难以处理的参数问题。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void InvalidConcurrencyIsRejected(int concurrency) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        new CurseForgeInstanceInstaller(_root, new FakeDownloader(new Dictionary<string, byte[]>()),
            new FakeApi(), new FakeRuntime(), concurrency, new EmptyModrinthApiClient()));

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

    /// <summary>模拟旧版真实导入记录：包含精确原包条目和 SHA-256，尚未显式保存新增 Origin 字段。</summary>
    private static async Task<Fixture> AddEmbeddedDataPackAsync(Fixture fixture, bool writeFile, byte[]? embeddedBytes = null)
    {
        var bytes = embeddedBytes ?? fixture.Payloads["https://cdn.example.test/1001"];
        var path = ".minecraft/config/paxi/datapacks/datapack.zip";
        var embedded = new InstanceFileRequirement
        {
            Path = path, Source = InstanceFileSource.Embedded, Size = bytes.Length,
            ArchiveEntry = "overrides/config/paxi/datapacks/datapack.zip",
            Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) }
        };
        var manifest = fixture.Manifest with { SourceArchive = "source-pack.zip", Files = fixture.Manifest.Files.Append(embedded).ToArray() };
        await InstanceManifestStore.WriteAsync(fixture.ManifestPath, manifest, CancellationToken.None);
        if (writeFile) Write(fixture.Instance, path, bytes);
        return fixture with { Manifest = manifest };
    }

    /// <summary>只改变内容而保留大小，用于证明失败原因是摘要而不是长度检查。</summary>
    private static byte[] DifferentContent(byte[] original)
    {
        var changed = original.ToArray();
        changed[0] ^= 1;
        return changed;
    }

    /// <summary>在测试启动器根目录生成原始整合包，条目路径来自导入记录而非目标路径推断。</summary>
    private void WriteSourceArchive(Fixture fixture, byte[] bytes)
    {
        var file = Assert.Single(fixture.Manifest.Files, file => file.ArchiveEntry is not null);
        using var archive = ZipFile.Open(Path.Combine(_root, fixture.Manifest.SourceArchive), ZipArchiveMode.Create);
        using var entry = archive.CreateEntry(file.ArchiveEntry!).Open();
        entry.Write(bytes);
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
        new(_root, fixture.Downloader, fixture.Api, runtime, modrinthApiClient: new EmptyModrinthApiClient());

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
        public Exception? FileRequestFailure { get; set; }
        public CurseForgeRateLimit? LastRateLimit => null;

        /// <summary>模拟批量文件查询，可省略不存在的 ID。</summary>
        public Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
        {
            Assert.False(FailIfCalled, "This operation must reuse offline metadata.");
            FileRequests.Add(ids.ToArray());
            if (FileRequestFailure is not null) throw FileRequestFailure;
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
        public bool Fail { get; init; }
        public bool FailureRecordOnly { get; init; }
        public PrismInstallResult? Result { get; init; }
        public Action? BeforeInstall { get; init; }

        /// <summary>返回固定测试启动配置。</summary>
        public Task<PrismInstallResult> InstallAsync(string instancePath, IProgress<InstanceDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            BeforeInstall?.Invoke();
            if (Result is not null) return Task.FromResult(Result);
            if (FailureRecordOnly)
                return Task.FromResult(new PrismInstallResult
                {
                    SourceFormat = "CurseForge", LaunchProfilePath = "test-launch.json",
                    Files = [new(new InstanceFileRequirement { Source = InstanceFileSource.DirectDownload, Path = ".minecraft/libraries/fabric.jar" },
                        InstanceDownloadState.Failed, "Fabric 运行库 SHA-1 校验失败。")]
                });
            return Task.FromResult(Fail
                ? new PrismInstallResult { SourceFormat = "CurseForge", Issues = ["Fabric 运行库 SHA-1 校验失败。"] }
                : new PrismInstallResult { SourceFormat = "CurseForge", LaunchProfilePath = "test-launch.json" });
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
