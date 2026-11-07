using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Downloads;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Prism;

namespace EML.Tests;

/// <summary>用独立 Prism 实例、真实文件整理器和内存下载响应验证安装流程，不执行游戏或包内脚本。</summary>
public sealed class PrismInstanceInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-Prism-install-tests-" + Guid.NewGuid().ToString("N"));
    private const string MainPath = ".minecraft/libraries/com/mojang/minecraft/1.20.1/minecraft-1.20.1-client.jar";
    private const string LibraryPath = ".minecraft/libraries/example/library/1.0/library-1.0.jar";
    private const string IndexPath = ".minecraft/assets/indexes/test-assets.json";

    /// <summary>从有效小型 JAR 和资源索引完成真实 HTTP 下载与文件整理，且不执行实例中的启动脚本。</summary>
    [Fact]
    public async Task DownloadsLibrariesAndAssetsBuildsProfileAndPreservesEmbeddedFilesWithoutRunningScripts()
    {
        var fixture = await CreateFixtureAsync(includeAssets: true);
        using var handler = new FixtureHandler(fixture.Payloads);
        using var downloader = new HttpFileDownloader(handler);
        var installer = new PrismInstanceInstaller(_root, downloader, new PlanResolver(_ => fixture.Plan));

        var result = await installer.InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Equal(4, handler.Urls.Count);
        Assert.All(result.Files, file => Assert.Equal(InstanceDownloadState.Downloaded, file.State));
        Assert.True(File.Exists(result.LaunchProfilePath));
        using var profile = JsonDocument.Parse(File.ReadAllText(result.LaunchProfilePath!));
        Assert.Equal("net.minecraft.client.main.Main", profile.RootElement.GetProperty("mainClass").GetString());
        Assert.Equal("test-assets", profile.RootElement.GetProperty("assets").GetString());
        var state = PrismRuntimeStateStore.Read(fixture.Plan);
        Assert.NotNull(state);
        Assert.Contains(state.GeneratedFiles, file => Path.Combine(fixture.Instance, file.Path!.Replace('/', Path.DirectorySeparatorChar)) == result.LaunchProfilePath);
        Assert.Equal(fixture.ManifestBytes, File.ReadAllBytes(Path.Combine(fixture.Instance, InstanceImportManifest.FileName)));
        Assert.Equal(fixture.PreservedMod, File.ReadAllBytes(Path.Combine(fixture.Instance, ".minecraft", "mods", "preserved.jar")));
        Assert.False(File.Exists(fixture.ScriptMarker));
        Assert.Contains("PreLaunchCommand=", File.ReadAllText(Path.Combine(fixture.Instance, "instance.cfg")));
        Assert.Empty(Directory.EnumerateFiles(fixture.Instance, "*.part", SearchOption.AllDirectories));
    }

    /// <summary>真实本地补丁从 size=0 无哈希类库声明开始，经过解析、HTTP 下载、整理和检查形成可复用状态。</summary>
    [Fact]
    public async Task RealLocalPatchResolvesUnknownArtifactSizeAndPreparesRuntimeEndToEnd()
    {
        var fixture = await CreateFixtureAsync(includeAssets: true);
        WriteFile(fixture.Instance, "mmc-pack.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = 1, components = new[] { new { uid = "net.minecraft", version = "1.20.1" } }
        }));
        var main = fixture.Plan.Files.Single(file => file.Role == PrismRuntimeFileRole.MainJar).Requirement;
        var index = fixture.Plan.Files.Single(file => file.Role == PrismRuntimeFileRole.AssetIndex).Requirement;
        WriteFile(fixture.Instance, "patches/net.minecraft.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = 1, uid = "net.minecraft", version = "1.20.1", mainClass = "net.minecraft.client.main.Main",
            minecraftArguments = "--username ${auth_player_name} --gameDir ${game_directory}", compatibleJavaMajors = new[] { 17 },
            libraries = new[]
            {
                new { name = "example:library:1.0", downloads = new { artifact = new { url = "https://example.test/library.jar", size = 0 } } }
            },
            mainJar = new
            {
                name = "com.mojang:minecraft:1.20.1:client",
                downloads = new { artifact = new { url = main.Downloads[0], size = main.Size, sha1 = main.Hashes["sha1"] } }
            },
            assetIndex = new { id = "test-assets", url = index.Downloads[0], size = index.Size, sha1 = index.Hashes["sha1"] }
        }));
        var resolver = new PrismRuntimeResolver();
        var plan = resolver.Resolve(fixture.Instance, InstanceManifestStore.Read(Path.Combine(fixture.Instance, InstanceImportManifest.FileName)));
        Assert.Empty(plan.BlockingIssues);
        Assert.Null(Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.Library).Requirement.Size);
        using var handler = new FixtureHandler(fixture.Payloads);
        using var downloader = new HttpFileDownloader(handler);

        var result = await new PrismInstanceInstaller(_root, downloader, resolver).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Equal(4, handler.Urls.Count);
        Assert.Equal(InstanceDownloadState.DownloadedUnverified,
            Assert.Single(result.Files, file => file.Requirement.Path == LibraryPath).State);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Instance);
        Assert.True(report.IsPrismRuntime);
        Assert.Empty(report.RuntimeIssues);
        Assert.Equal(result.LaunchProfilePath, report.LaunchProfilePath);
        Assert.Contains(report.Files, file => file.Requirement.Path == LibraryPath && file.State == InstanceFileState.PresentUnverified);
        Assert.DoesNotContain(report.Files, file => file.State is InstanceFileState.Missing or InstanceFileState.HashMismatch or InstanceFileState.SizeMismatch);
        Assert.Equal(fixture.ManifestBytes, File.ReadAllBytes(Path.Combine(fixture.Instance, InstanceImportManifest.FileName)));
        Assert.False(File.Exists(fixture.ScriptMarker));
    }

    /// <summary>固定版本组件元数据使用独立的 8 MiB 请求上限，并明确记录其来源未提供哈希。</summary>
    [Fact]
    public async Task ComponentMetadataRequestUsesSmallLimitAndIsRecordedWithoutSourceHash()
    {
        var fixture = await CreateFixtureAsync();
        const string metadataPath = ".eml-runtime/components/net.minecraft/1.20.1.json";
        const string metadataUrl = "https://meta.example.test/net.minecraft/1.20.1.json";
        fixture.Payloads.Add(metadataUrl, Encoding.UTF8.GetBytes("{\"uid\":\"net.minecraft\",\"version\":\"1.20.1\"}"));
        var pending = fixture.Plan with
        {
            MetadataRequests = [new("net.minecraft", "1.20.1", metadataPath, metadataUrl)]
        };
        var resolver = new PlanResolver(_ => File.Exists(Path.Combine(fixture.Instance, metadataPath.Replace('/', Path.DirectorySeparatorChar)))
            ? fixture.Plan : pending);
        var downloader = new FixtureDownloader(fixture.Payloads);

        var result = await new PrismInstanceInstaller(_root, downloader, resolver).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        var request = Assert.Single(downloader.Requests, request => request.Urls.Contains(metadataUrl));
        Assert.Equal(8 * 1024 * 1024, request.MaximumBytes);
        Assert.Null(request.ExpectedSize);
        Assert.Empty(request.Hashes);
        Assert.Equal(InstanceDownloadState.DownloadedUnverified,
            Assert.Single(result.Files, file => file.Requirement.Path == metadataPath).State);
        Assert.True(resolver.Calls >= 3);
    }

    /// <summary>缺少上游哈希和已知大小的类库可以按声明地址下载，但不得标成来源哈希校验通过。</summary>
    [Fact]
    public async Task UnknownSizeLibraryIsDownloadedWithoutClaimingSourceHashVerification()
    {
        var fixture = await CreateFixtureAsync();
        var plan = fixture.Plan with
        {
            Files = fixture.Plan.Files.Select(file => file.Role == PrismRuntimeFileRole.Library
                ? file with { Requirement = file.Requirement with { Size = null, Hashes = new Dictionary<string, string>() } } : file).ToArray()
        };
        var downloader = new FixtureDownloader(fixture.Payloads);

        var result = await new PrismInstanceInstaller(_root, downloader, new PlanResolver(_ => plan)).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        var request = Assert.Single(downloader.Requests, request => request.DestinationPath.EndsWith("library-1.0.jar"));
        Assert.Null(request.ExpectedSize);
        Assert.Empty(request.Hashes);
        var file = Assert.Single(result.Files, file => file.Requirement.Path == LibraryPath);
        Assert.Equal(InstanceDownloadState.DownloadedUnverified, file.State);
        Assert.Contains("未", file.Message);
        Assert.True(File.Exists(request.DestinationPath));
    }

    /// <summary>本地包内组件缺失时不根据 Maven 名称猜测地址，也不能发布准备完成状态。</summary>
    [Fact]
    public async Task MissingLocalLibraryIsReportedWithoutNetworkRequest()
    {
        var fixture = await CreateFixtureAsync();
        var local = fixture.Plan.Files[0] with
        {
            Requirement = fixture.Plan.Files[0].Requirement with { Source = InstanceFileSource.Embedded, Downloads = [] }
        };
        var plan = fixture.Plan with { Files = [local] };
        var downloader = new FixtureDownloader(fixture.Payloads);

        var result = await new PrismInstanceInstaller(_root, downloader, new PlanResolver(_ => plan)).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Empty(downloader.Requests);
        Assert.Contains(result.Issues, issue => issue.Contains("原压缩包"));
        Assert.Equal(InstanceDownloadState.Failed, Assert.Single(result.Files).State);
        Assert.Null(PrismRuntimeStateStore.Read(plan));
    }

    /// <summary>已有但与来源哈希不符的库保留原内容，其他缺失依赖仍能完成下载。</summary>
    [Fact]
    public async Task ExistingCorruptLibraryIsNotOverwrittenWhileOtherFilesDownload()
    {
        var fixture = await CreateFixtureAsync();
        var corrupt = Encoding.UTF8.GetBytes("user file must survive");
        WriteFile(fixture.Instance, LibraryPath, corrupt);
        var downloader = new FixtureDownloader(fixture.Payloads);

        var result = await new PrismInstanceInstaller(_root, downloader, new PlanResolver(_ => fixture.Plan)).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Equal(corrupt, File.ReadAllBytes(Path.Combine(fixture.Instance, LibraryPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.DoesNotContain(downloader.Requests, request => request.DestinationPath.EndsWith("library-1.0.jar"));
        Assert.Single(result.Files, file => file.State == InstanceDownloadState.Failed);
        Assert.Single(result.Files, file => file.State == InstanceDownloadState.Downloaded && file.Requirement.Path == MainPath);
        Assert.Null(PrismRuntimeStateStore.Read(fixture.Plan));
    }

    /// <summary>一个运行依赖网络失败不会跳过后续依赖，但整个实例不能误报为已准备完成。</summary>
    [Fact]
    public async Task FailedDownloadDoesNotStopOtherRuntimeFiles()
    {
        var fixture = await CreateFixtureAsync();
        var downloader = new FixtureDownloader(fixture.Payloads)
        {
            BeforeDownload = request =>
            {
                if (request.DestinationPath.EndsWith("library-1.0.jar")) throw new HttpRequestException("simulated HTTP failure");
            }
        };

        var result = await new PrismInstanceInstaller(_root, downloader, new PlanResolver(_ => fixture.Plan)).InstallAsync(fixture.Instance);

        Assert.Equal(2, downloader.Requests.Count);
        Assert.False(result.FilesReady);
        Assert.Single(result.Files, file => file.State == InstanceDownloadState.Failed);
        Assert.Single(result.Files, file => file.State == InstanceDownloadState.Downloaded);
        Assert.Contains(result.Issues, issue => issue.Contains("simulated HTTP failure"));
        Assert.Null(PrismRuntimeStateStore.Read(fixture.Plan));
    }

    /// <summary>首文件提交后取消保留已下载内容与记录，剩余依赖和运行状态不会被发布。</summary>
    [Fact]
    public async Task CancellationPreservesCommittedFileWithoutPublishingRuntimeState()
    {
        var fixture = await CreateFixtureAsync();
        using var cancellation = new CancellationTokenSource();
        var downloader = new FixtureDownloader(fixture.Payloads) { AfterDownload = _ => cancellation.Cancel() };

        var result = await new PrismInstanceInstaller(_root, downloader, new PlanResolver(_ => fixture.Plan))
            .InstallAsync(fixture.Instance, cancellationToken: cancellation.Token);

        Assert.True(result.IsCanceled);
        Assert.False(result.FilesReady);
        Assert.Equal(InstanceDownloadState.Downloaded, Assert.Single(result.Files).State);
        Assert.True(File.Exists(Assert.Single(downloader.Requests).DestinationPath));
        Assert.False(File.Exists(Path.Combine(fixture.Instance, MainPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Null(PrismRuntimeStateStore.Read(fixture.Plan));
    }

    /// <summary>再次准备没有变化的实例复用已校验依赖及资源，不重复访问下载服务。</summary>
    [Fact]
    public async Task UnchangedSecondRunReusesAllDownloadedDependenciesAndAssets()
    {
        var fixture = await CreateFixtureAsync(includeAssets: true);
        var downloader = new FixtureDownloader(fixture.Payloads);
        var installer = new PrismInstanceInstaller(_root, downloader, new PlanResolver(_ => fixture.Plan));
        var first = await installer.InstallAsync(fixture.Instance);
        Assert.True(first.FilesReady, string.Join("\n", first.Issues));
        var originalRequests = downloader.Requests.Count;

        var second = await installer.InstallAsync(fixture.Instance);

        Assert.True(second.FilesReady, string.Join("\n", second.Issues));
        Assert.Equal(4, originalRequests);
        Assert.Equal(originalRequests, downloader.Requests.Count);
        Assert.Empty(second.Files);
        Assert.Equal(first.LaunchProfilePath, second.LaunchProfilePath);
        Assert.Equal(fixture.ManifestBytes, File.ReadAllBytes(Path.Combine(fixture.Instance, InstanceImportManifest.FileName)));
    }

    /// <summary>准备过程中源组件指纹改变时不能保存旧计划的已完成状态。</summary>
    [Fact]
    public async Task ChangedSourceFingerprintPreventsPublishingReadyState()
    {
        var fixture = await CreateFixtureAsync();
        var resolver = new PlanResolver(call => call == 1 ? fixture.Plan : fixture.Plan with { Fingerprint = new string('b', 64) });
        var downloader = new FixtureDownloader(fixture.Payloads);

        var result = await new PrismInstanceInstaller(_root, downloader, resolver).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.Contains(result.Issues, issue => issue.Contains("发生变化"));
        Assert.Equal(2, result.Files.Count(file => file.State == InstanceDownloadState.Downloaded));
        Assert.Null(PrismRuntimeStateStore.Read(fixture.Plan));
    }

    /// <summary>资源索引也使用 8 MiB 下载上限，资源对象本身按各自已声明大小下载。</summary>
    [Fact]
    public async Task AssetIndexDownloadUsesMetadataCapWithoutApplyingItToAssetObjects()
    {
        var fixture = await CreateFixtureAsync(includeAssets: true);
        var downloader = new FixtureDownloader(fixture.Payloads);

        var result = await new PrismInstanceInstaller(_root, downloader, new PlanResolver(_ => fixture.Plan)).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Equal(8 * 1024 * 1024,
            Assert.Single(downloader.Requests, request => request.DestinationPath.EndsWith("test-assets.json")).MaximumBytes);
        Assert.Null(Assert.Single(downloader.Requests, request => request.Urls[0].StartsWith("https://resources.download.minecraft.net/")).MaximumBytes);
    }

    /// <summary>Prism 内容损坏不阻止运行依赖准备，但公开安装结果不能把运行成功当成整个实例成功。</summary>
    [Fact]
    public async Task CorruptContentKeepsPreparedRuntimeButBlocksFinalReadiness()
    {
        var fixture = await CreateFixtureAsync();
        var changed = fixture.PreservedMod.ToArray();
        changed[^1] ^= 1;
        WriteFile(fixture.Instance, ".minecraft/mods/preserved.jar", changed);
        var downloader = new FixtureDownloader(fixture.Payloads);

        var result = await new PrismInstanceInstaller(_root, downloader, new PlanResolver(_ => fixture.Plan)).InstallAsync(fixture.Instance);

        Assert.True(result.RuntimeReady);
        Assert.False(result.ContentReady);
        Assert.False(result.FilesReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.True(File.Exists(result.PreparedLaunchProfilePath));
        Assert.Contains(result.ContentIssues, issue => issue.Contains("preserved.jar"));
        Assert.Empty(result.RuntimeIssues);
        Assert.Equal(2, downloader.Requests.Count);
        Assert.Equal(changed, File.ReadAllBytes(Path.Combine(fixture.Instance, ".minecraft/mods/preserved.jar")));
    }

    /// <summary>程序修改 Prism 名称后，公开安装服务沿用原导入声明并认可启动器元数据变化。</summary>
    [Fact]
    public async Task ChangedPrismNameDoesNotBlockContentPreparation()
    {
        var fixture = await CreateFixtureAsync();
        var manifestPath = Path.Combine(fixture.Instance, InstanceImportManifest.FileName);
        var manifest = InstanceManifestStore.Read(manifestPath);
        var original = File.ReadAllBytes(Path.Combine(fixture.Instance, "instance.cfg"));
        var config = new InstanceFileRequirement
        {
            Path = "instance.cfg", Source = InstanceFileSource.Embedded, Size = original.Length,
            Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(original)) }
        };
        await InstanceManifestStore.WriteAsync(manifestPath, manifest with { Files = manifest.Files.Append(config).ToArray() }, CancellationToken.None);
        var before = File.ReadAllBytes(manifestPath);
        WriteFile(fixture.Instance, "instance.cfg", Encoding.UTF8.GetBytes("[General]\nname=Renamed instance\n"));

        var result = await new PrismInstanceInstaller(_root, new FixtureDownloader(fixture.Payloads),
            new PlanResolver(_ => fixture.Plan)).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Equal(before, File.ReadAllBytes(manifestPath));
    }

    /// <summary>用户主动接受本地类库替换后，运行安装器和内容复核都使用相同的新基线。</summary>
    [Fact]
    public async Task LocalRuntimeLibraryUsesAcceptedReplacementAcrossBothStages()
    {
        var fixture = await CreateFixtureAsync();
        var manifestPath = Path.Combine(fixture.Instance, InstanceImportManifest.FileName);
        var manifest = InstanceManifestStore.Read(manifestPath);
        var original = fixture.Payloads["https://example.test/library.jar"];
        var local = new InstanceFileRequirement
        {
            Path = LibraryPath, Source = InstanceFileSource.Embedded, Origin = InstanceFileOrigin.EmbeddedOverride,
            Size = original.Length, ArchiveEntry = "libraries/library.jar",
            Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(original)) }
        };
        await InstanceManifestStore.WriteAsync(manifestPath, manifest with { Files = manifest.Files.Append(local).ToArray() }, CancellationToken.None);
        var replacement = CreateJar("example/Library.class", "user explicitly chosen library");
        WriteFile(fixture.Instance, LibraryPath, replacement);
        await new InstanceLocalReplacementService(_root).AcceptExistingAsync(fixture.Instance, LibraryPath);
        var plan = fixture.Plan with
        {
            Files = fixture.Plan.Files.Select(file => file.Role == PrismRuntimeFileRole.Library ? file with { Requirement = local } : file).ToArray()
        };
        var downloader = new FixtureDownloader(fixture.Payloads);

        var result = await new PrismInstanceInstaller(_root, downloader, new PlanResolver(_ => plan)).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Single(downloader.Requests);
        Assert.DoesNotContain(downloader.Requests, request => request.Urls.Contains("https://example.test/library.jar"));
        Assert.Equal(replacement, File.ReadAllBytes(Path.Combine(fixture.Instance, LibraryPath)));
    }

    /// <summary>替换记录损坏时不能退回旧本地类库发布成功；无关的可信远端运行依赖仍能下载。</summary>
    [Fact]
    public async Task InvalidSourceSnapshotDoesNotFallBackToOldEmbeddedRuntimeBaseline()
    {
        var fixture = await CreateFixtureAsync();
        var original = fixture.Payloads["https://example.test/library.jar"];
        WriteFile(fixture.Instance, LibraryPath, original);
        WriteFile(fixture.Instance, InstanceLocalReplacementService.RelativePath, Encoding.UTF8.GetBytes("{broken"));
        var plan = fixture.Plan with
        {
            Files = fixture.Plan.Files.Select(file => file.Role == PrismRuntimeFileRole.Library
                ? file with { Requirement = file.Requirement with { Source = InstanceFileSource.Embedded, Downloads = [] } } : file).ToArray()
        };
        var downloader = new FixtureDownloader(fixture.Payloads);

        var result = await new PrismInstanceInstaller(_root, downloader, new PlanResolver(_ => plan)).InstallAsync(fixture.Instance);

        Assert.False(result.RuntimeReady);
        Assert.False(result.ContentReady);
        Assert.Null(result.PreparedLaunchProfilePath);
        Assert.Null(result.LaunchProfilePath);
        Assert.Contains(result.RuntimeIssues, issue => issue.Contains("本地运行文件"));
        Assert.Single(downloader.Requests);
        Assert.Equal("https://example.test/main.jar", Assert.Single(downloader.Requests).Urls.Single());
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.Instance, LibraryPath)));
        Assert.Null(PrismRuntimeStateStore.Read(plan));
    }

    /// <summary>Prism 真实运行安装完成后认可正常配置变化和已消耗标记，且保留原始清单及用户字节。</summary>
    [Fact]
    public async Task GameplayChangesAndConsumedMarkerDoNotBlockPreparedPrismRuntime()
    {
        var fixture = await CreateFixtureAsync();
        var manifestPath = Path.Combine(fixture.Instance, InstanceImportManifest.FileName);
        var manifest = InstanceManifestStore.Read(manifestPath);
        const string marker = ".minecraft/config/modpack-update";
        const string config = ".minecraft/serverutilities/serverutilities.cfg";
        /// <summary>生成有原条目和 SHA-256 的普通包内测试快照。</summary>
        InstanceFileRequirement Embedded(string path, string content) => new()
        {
            Path = path, ArchiveEntry = path, Source = InstanceFileSource.Embedded, Size = Encoding.UTF8.GetByteCount(content),
            Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))) }
        };
        manifest = manifest with { Files = [.. manifest.Files, Embedded(marker, ""), Embedded(config, "base")] };
        await InstanceManifestStore.WriteAsync(manifestPath, manifest, CancellationToken.None);
        var originalManifest = File.ReadAllBytes(manifestPath);
        WriteFile(fixture.Instance, config, Encoding.UTF8.GetBytes("player settings"));
        var downloader = new FixtureDownloader(fixture.Payloads);

        var result = await new PrismInstanceInstaller(_root, downloader, new PlanResolver(_ => fixture.Plan)).InstallAsync(fixture.Instance);

        Assert.True(result.LaunchReady, string.Join(Environment.NewLine, result.Issues));
        Assert.True(result.RuntimeReady);
        Assert.Empty(result.ContentIssues);
        Assert.NotEmpty(downloader.Requests);
        Assert.False(File.Exists(Path.Combine(fixture.Instance, marker)));
        Assert.Equal("player settings", File.ReadAllText(Path.Combine(fixture.Instance, config)));
        Assert.Equal(originalManifest, File.ReadAllBytes(manifestPath));
    }

    /// <summary>构造有效 JAR、可选资源索引及必须保留的用户内容，并保存真实 Prism 导入清单。</summary>
    private async Task<Fixture> CreateFixtureAsync(bool includeAssets = false)
    {
        var instance = Path.Combine(_root, "instances", "Prism integration");
        Directory.CreateDirectory(instance);
        var preservedMod = CreateJar("mod/Test.class", "embedded mod");
        WriteFile(instance, ".minecraft/mods/preserved.jar", preservedMod);
        var scriptMarker = Path.Combine(instance, "script-was-executed.txt");
        var script = Encoding.UTF8.GetBytes("@echo off\r\necho executed>\"" + scriptMarker + "\"\r\n");
        WriteFile(instance, "must-not-run.cmd", script);
        WriteFile(instance, "instance.cfg", Encoding.UTF8.GetBytes("[General]\nname=Prism integration\nPreLaunchCommand=cmd /c must-not-run.cmd\n"));
        var manifest = new InstanceImportManifest
        {
            SourceFormat = "Prism", Name = "Prism integration", MinecraftVersion = "1.20.1",
            Components = [new("net.minecraft", "1.20.1")],
            Files = [Requirement(".minecraft/mods/preserved.jar", preservedMod, "") with { Source = InstanceFileSource.Embedded, Downloads = [] }]
        };
        var manifestPath = Path.Combine(instance, InstanceImportManifest.FileName);
        await InstanceManifestStore.WriteAsync(manifestPath, manifest, CancellationToken.None);
        var library = CreateJar("example/Library.class", "library class bytes");
        var main = CreateJar("net/minecraft/client/main/Main.class", "main class bytes");
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["https://example.test/library.jar"] = library, ["https://example.test/main.jar"] = main
        };
        var files = new List<PrismRuntimeFile>
        {
            new(Requirement(LibraryPath, library, "https://example.test/library.jar"), PrismRuntimeFileRole.Library)
            { OnClassPath = true, MavenName = "example:library:1.0" },
            new(Requirement(MainPath, main, "https://example.test/main.jar"), PrismRuntimeFileRole.MainJar) { OnClassPath = true }
        };
        if (includeAssets)
        {
            var asset = Encoding.UTF8.GetBytes("fixed test sound payload");
            var hash = Convert.ToHexStringLower(SHA1.HashData(asset));
            var index = JsonSerializer.SerializeToUtf8Bytes(new
            {
                objects = new Dictionary<string, object> { ["minecraft/sounds/test.ogg"] = new { hash, size = asset.Length } }
            });
            payloads.Add("https://example.test/index.json", index);
            payloads.Add($"https://resources.download.minecraft.net/{hash[..2]}/{hash}", asset);
            files.Add(new(Requirement(IndexPath, index, "https://example.test/index.json"), PrismRuntimeFileRole.AssetIndex));
        }
        var plan = new PrismRuntimePlan
        {
            InstancePath = instance, MinecraftVersion = "1.20.1", MainClass = "net.minecraft.client.main.Main",
            Fingerprint = new string('a', 64), AssetIndexId = includeAssets ? "test-assets" : null, Files = files,
            GameArguments = ["--username", "${auth_player_name}", "--gameDir", "${game_directory}"], CompatibleJavaMajors = [17]
        };
        return new(instance, plan, payloads, File.ReadAllBytes(manifestPath), preservedMod, scriptMarker);
    }

    /// <summary>根据真实测试内容生成固定大小和 SHA-1，便于真实下载器与整理器交叉验证。</summary>
    private static InstanceFileRequirement Requirement(string path, byte[] bytes, string url) => new()
    {
        Path = path, Source = InstanceFileSource.DirectDownload, Size = bytes.Length, Downloads = [url],
        Hashes = new Dictionary<string, string> { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(bytes)) }
    };

    /// <summary>生成结构有效的微型 JAR，测试内容只作文件处理而不会作为 Java 字节码执行。</summary>
    private static byte[] CreateJar(string entryName, string content)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryName).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return buffer.ToArray();
    }

    /// <summary>在唯一测试实例下写入指定内容，所有路径都来自固定测试数据。</summary>
    private static void WriteFile(string instance, string relative, byte[] bytes)
    {
        var path = Path.Combine(instance, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>只清理本测试创建且带随机标识的目录，不操作真实整合包。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>保存同一个实例的组件计划、预期网络内容和原始用户文件快照。</summary>
    private sealed record Fixture(string Instance, PrismRuntimePlan Plan, Dictionary<string, byte[]> Payloads,
        byte[] ManifestBytes, byte[] PreservedMod, string ScriptMarker);

    /// <summary>用调用次数控制解析结果，模拟元数据获取和源组件变化，不绕过真实文件整理。</summary>
    private sealed class PlanResolver : IPrismRuntimeResolver
    {
        private readonly Func<int, PrismRuntimePlan> _resolve;
        public int Calls { get; private set; }

        /// <summary>保存当前测试的解析结果工厂。</summary>
        public PlanResolver(Func<int, PrismRuntimePlan> resolve) => _resolve = resolve;

        /// <summary>记录解析次数并返回该阶段的固定组件计划。</summary>
        public PrismRuntimePlan Resolve(string instancePath, InstanceImportManifest manifest) => _resolve(++Calls);
    }

    /// <summary>记录每次请求并写入已知内容，以确定顺序模拟失败、提交后取消和请求上限。</summary>
    private sealed class FixtureDownloader : IFileDownloader
    {
        private readonly IReadOnlyDictionary<string, byte[]> _payloads;
        public List<FileDownloadRequest> Requests { get; } = [];
        public Action<FileDownloadRequest>? BeforeDownload { get; init; }
        public Action<FileDownloadRequest>? AfterDownload { get; init; }

        /// <summary>保存 URL 到测试文件字节的映射，不连接网络。</summary>
        public FixtureDownloader(IReadOnlyDictionary<string, byte[]> payloads) => _payloads = payloads;

        /// <summary>无覆盖写入固定内容后返回是否有来源哈希，并在提交后触发可选回调。</summary>
        public async Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request,
            IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            BeforeDownload?.Invoke(request);
            var bytes = _payloads[request.Urls[0]];
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await using (var output = new FileStream(request.DestinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await output.WriteAsync(bytes, cancellationToken);
            AfterDownload?.Invoke(request);
            return new(request.DestinationPath, bytes.Length, request.Hashes.Count > 0);
        }
    }

    /// <summary>为真实 HTTP 下载器提供响应内容，避免测试依赖外网或真实组件仓库。</summary>
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, byte[]> _payloads;
        public List<string> Urls { get; } = [];

        /// <summary>保存每个固定地址的响应字节。</summary>
        public FixtureHandler(IReadOnlyDictionary<string, byte[]> payloads) => _payloads = payloads;

        /// <summary>记录请求地址并返回内存内容，不解析 DNS 或启动其他进程。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri!.AbsoluteUri;
            Urls.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_payloads[url]) });
        }
    }
}
