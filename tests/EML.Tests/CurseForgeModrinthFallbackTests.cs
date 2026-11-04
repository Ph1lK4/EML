using System.Net;
using System.Security.Cryptography;
using System.Text;
using EML.Core.CurseForge;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Core.Modrinth;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.CurseForge;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Modrinth.Api;

namespace EML.Tests;

/// <summary>用隔离实例验证跨平台精确内容补全、失败降级、缓存溯源和原始哈希校验。</summary>
public sealed class CurseForgeModrinthFallbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-CF-MR-fallback-" + Guid.NewGuid().ToString("N"));

    /// <summary>选择内容一致的附属文件而非默认文件，并保留 CurseForge 要求的名字和全部身份字段。</summary>
    [Fact]
    public async Task ExactSecondaryFileRetainsOriginalCurseForgeIdentityAndDestination()
    {
        var fixture = await FixtureAsync();
        var exact = Match(fixture);
        fixture.Modrinth.Versions[Hash(fixture.Bytes)] = Version(exact with { Primary = false }, exact with
        {
            Filename = "wrong-primary.jar", Url = Url("wrong-primary.jar"), Primary = true,
            Hashes = new Dictionary<string, string> { ["sha1"] = new string('a', 40) }
        });
        var originalManifest = File.ReadAllBytes(fixture.ManifestPath);

        var state = await Resolver(fixture).ResolveAsync(fixture.Instance, fixture.Manifest);

        var resolved = Assert.Single(state.Files);
        Assert.Null(resolved.BlockReason);
        Assert.Equal(InstanceFileSource.CurseForge, resolved.Requirement.Source);
        Assert.Equal(100, resolved.Requirement.ProjectId);
        Assert.Equal(1000, resolved.Requirement.FileId);
        Assert.Equal("cf-original.jar", resolved.FileName);
        Assert.Equal(".minecraft/mods/cf-original.jar", resolved.Requirement.Path);
        Assert.Equal(Hash(fixture.Bytes), resolved.Requirement.Hashes["sha1"]);
        Assert.Equal(Hash(fixture.Bytes, "sha512"), resolved.Requirement.Hashes["sha512"]);
        Assert.Equal(exact.Url, Assert.Single(resolved.Requirement.Downloads));
        Assert.NotNull(resolved.ModrinthFallback);
        Assert.Equal("project1", resolved.ModrinthFallback.ProjectId);
        Assert.Equal("version1", resolved.ModrinthFallback.VersionId);
        Assert.Equal("mr-renamed.jar", resolved.ModrinthFallback.FileName);
        Assert.Equal(exact.Url, resolved.ModrinthFallback.DownloadUrl);
        Assert.Equal(originalManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.Equal(new[] { Hash(fixture.Bytes) }, Assert.Single(fixture.Modrinth.Requests));
    }

    /// <summary>同名、同版本标签不能替代哈希和大小验证；含糊或不安全的文件元数据均回到人工入口。</summary>
    [Theory]
    [InlineData("sha1")]
    [InlineData("size")]
    [InlineData("missing-sha1")]
    [InlineData("invalid-sha512")]
    [InlineData("duplicate")]
    [InlineData("host")]
    [InlineData("http")]
    [InlineData("query")]
    [InlineData("fragment")]
    [InlineData("userinfo")]
    [InlineData("port")]
    [InlineData("project")]
    [InlineData("version")]
    [InlineData("filename")]
    public async Task UntrustedOrAmbiguousMetadataDoesNotBecomeADownload(string fault)
    {
        var fixture = await FixtureAsync();
        var file = Match(fixture);
        file = fault switch
        {
            "sha1" => file with { Hashes = new Dictionary<string, string> { ["sha1"] = new string('a', 40) } },
            "size" => file with { Size = file.Size + 1 },
            "missing-sha1" => file with { Hashes = new Dictionary<string, string>() },
            "invalid-sha512" => file with { Hashes = new Dictionary<string, string> { ["sha1"] = Hash(fixture.Bytes), ["sha512"] = "invalid" } },
            "host" => file with { Url = "https://cdn.modrinth.com.attacker.test/data/project1/versions/version1/mr-renamed.jar" },
            "http" => file with { Url = file.Url.Replace("https://", "http://") },
            "query" => file with { Url = file.Url + "?token=test" },
            "fragment" => file with { Url = file.Url + "#download" },
            "userinfo" => file with { Url = file.Url.Replace("https://", "https://name:pass@") },
            "port" => file with { Url = file.Url.Replace("cdn.modrinth.com/", "cdn.modrinth.com:444/") },
            "project" => file with { Url = file.Url.Replace("/project1/", "/project2/") },
            "version" => file with { Url = file.Url.Replace("/version1/", "/version2/") },
            "filename" => file with { Url = Url("different-file.jar") },
            _ => file
        };
        fixture.Modrinth.Versions[Hash(fixture.Bytes)] = fault == "duplicate" ? Version(file, file) : Version(file);

        var resolved = Assert.Single((await Resolver(fixture).ResolveAsync(fixture.Instance, fixture.Manifest)).Files);

        Assert.Empty(resolved.Requirement.Downloads);
        Assert.Null(resolved.ModrinthFallback);
        Assert.Equal(Hash(fixture.Bytes), resolved.Requirement.Hashes["sha1"]);
        Assert.Contains("/files/1000", resolved.ManualDownloadUrl);
    }

    /// <summary>无哈希命中时保留原官方文件页，不执行名称搜索或选择其他版本。</summary>
    [Fact]
    public async Task NoHashMatchPreservesManualFilePage()
    {
        var fixture = await FixtureAsync();
        var resolved = Assert.Single((await Resolver(fixture).ResolveAsync(fixture.Instance, fixture.Manifest)).Files);

        Assert.Empty(resolved.Requirement.Downloads);
        Assert.Null(resolved.ModrinthFallback);
        Assert.Equal("https://www.curseforge.com/minecraft/mc-mods/example/files/1000", resolved.ManualDownloadUrl);
        Assert.Single(fixture.Modrinth.Requests);
    }

    /// <summary>不同原始文件引用相同内容时只查询一次哈希，但保持各自指定的目标路径。</summary>
    [Fact]
    public async Task RepeatedContentUsesOneHashLookupWithoutMergingDestinations()
    {
        var fixture = await FixtureAsync(2);
        fixture.Modrinth.Versions[Hash(fixture.Bytes)] = Version(Match(fixture));

        var state = await Resolver(fixture).ResolveAsync(fixture.Instance, fixture.Manifest);

        Assert.Equal(new[] { Hash(fixture.Bytes) }, Assert.Single(fixture.Modrinth.Requests));
        Assert.Equal(2, state.Files.Count);
        Assert.Equal(2, state.Files.Select(file => file.Requirement.Path).Distinct().Count());
        Assert.All(state.Files, file => Assert.NotNull(file.ModrinthFallback));
    }

    /// <summary>备用平台只提供 SHA-1 时仍可精确补全，不凭空要求不存在的 SHA-512。</summary>
    [Fact]
    public async Task MatchingSha1AndSizeAreSufficientWithoutOptionalSha512()
    {
        var fixture = await FixtureAsync();
        fixture.Modrinth.Versions[Hash(fixture.Bytes)] = Version(Match(fixture) with
        {
            Hashes = new Dictionary<string, string> { ["sha1"] = Hash(fixture.Bytes).ToUpperInvariant() }
        });

        var resolved = Assert.Single((await Resolver(fixture).ResolveAsync(fixture.Instance, fixture.Manifest)).Files);

        Assert.NotNull(resolved.ModrinthFallback);
        Assert.Null(resolved.ModrinthFallback.Sha512);
        Assert.Single(resolved.Requirement.Hashes);
        Assert.Equal(Hash(fixture.Bytes), resolved.Requirement.Hashes["sha1"]);
        Assert.Single(resolved.Requirement.Downloads);
    }

    /// <summary>只相信当前请求哈希对应的响应条目，其他键中的相同名字和内容不能冒充命中。</summary>
    [Fact]
    public async Task UnrequestedResponseKeyDoesNotMatch()
    {
        var fixture = await FixtureAsync();
        fixture.Modrinth.Versions[new string('a', 40)] = Version(Match(fixture));
        var resolved = Assert.Single((await Resolver(fixture).ResolveAsync(fixture.Instance, fixture.Manifest)).Files);
        Assert.Empty(resolved.Requirement.Downloads);
        Assert.Null(resolved.ModrinthFallback);
    }

    /// <summary>备用平台超时、限流或传输错误不会取消仍可从 CurseForge 下载的其他文件。</summary>
    [Theory]
    [InlineData("timeout")]
    [InlineData("rate-limit")]
    [InlineData("network")]
    public async Task FallbackFailureDoesNotBlockOtherOfficialDownloads(string failure)
    {
        var fixture = await FixtureAsync(2);
        fixture.Api.Mods[101] = fixture.Api.Mods[101] with { AllowModDistribution = true };
        fixture.Api.Files[1001] = fixture.Api.Files[1001] with { DownloadUrl = "https://edge.forgecdn.net/files/1001/1/normal.jar" };
        fixture.Modrinth.Failure = failure switch
        {
            "timeout" => new TaskCanceledException("simulated timeout"),
            "rate-limit" => new ModrinthApiException(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(1), null),
            _ => new HttpRequestException("simulated connection failure")
        };

        var result = await Installer(fixture).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        var request = Assert.Single(fixture.Downloader.Requests);
        Assert.Equal(fixture.Api.Files[1001].DownloadUrl, Assert.Single(request.Urls));
        Assert.Contains(result.Issues, issue => issue.Contains("/files/1000"));
        Assert.Equal(1, fixture.Runtime.Calls);
    }

    /// <summary>用户主动取消传递给查询和上层安装器，不被当成普通无匹配结果吞掉。</summary>
    [Fact]
    public async Task UserCancellationPropagatesFromResolver()
    {
        var fixture = await FixtureAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Modrinth.BeforeQuery = () => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Resolver(fixture).ResolveAsync(fixture.Instance, fixture.Manifest, cancellation.Token));

        Assert.False(File.Exists(Path.Combine(fixture.Instance, CurseForgeFileStateStore.RelativePath)));
        Assert.Empty(fixture.Downloader.Requests);
    }

    /// <summary>正常 CurseForge 下载、已有本地文件、可选项和不可安全定位项都不会触发备用平台请求。</summary>
    [Theory]
    [InlineData("official")]
    [InlineData("existing")]
    [InlineData("optional")]
    [InlineData("unsupported-class")]
    [InlineData("missing-sha1")]
    public async Task UnnecessaryOrUnsafeFallbackIsNotQueried(string reason)
    {
        var fixture = await FixtureAsync();
        if (reason == "official")
        {
            fixture.Api.Mods[100] = fixture.Api.Mods[100] with { AllowModDistribution = true };
            fixture.Api.Files[1000] = fixture.Api.Files[1000] with { DownloadUrl = "https://edge.forgecdn.net/files/1000/1/normal.jar" };
        }
        if (reason == "existing") Write(fixture.Instance, ".minecraft/mods/cf-original.jar", fixture.Bytes);
        if (reason == "optional") fixture = await RewriteManifestAsync(fixture,
            fixture.Manifest with { Files = [fixture.Manifest.Files[0] with { Required = false }] });
        if (reason == "unsupported-class") fixture.Api.Mods[100] = fixture.Api.Mods[100] with { ClassId = 17 };
        if (reason == "missing-sha1") fixture.Api.Files[1000] = fixture.Api.Files[1000] with { Hashes = [] };

        await Resolver(fixture).ResolveAsync(fixture.Instance, fixture.Manifest);

        Assert.Empty(fixture.Modrinth.Requests);
    }

    /// <summary>缓存中的精确备用地址可用于续传，离线检查和已完成安装不再查询任一平台。</summary>
    [Fact]
    public async Task FallbackCacheAndOfflineCheckerReuseExactMetadata()
    {
        var fixture = await FixtureAsync();
        fixture.Modrinth.Versions[Hash(fixture.Bytes)] = Version(Match(fixture));
        await Resolver(fixture).ResolveAsync(fixture.Instance, fixture.Manifest);
        fixture.Api.FailIfCalled = true;
        fixture.Modrinth.FailIfCalled = true;

        var result = await Installer(fixture).InstallAsync(fixture.Instance);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Instance);
        var repeated = await Installer(fixture).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.True(repeated.FilesReady, string.Join("\n", repeated.Issues));
        Assert.Single(fixture.Downloader.Requests);
        Assert.Single(fixture.Modrinth.Requests);
        var checkedFile = Assert.Single(report.Files, file => file.Requirement.FileId == 1000);
        Assert.Equal(InstanceFileState.Verified, checkedFile.State);
        Assert.Equal(InstanceFileSource.CurseForge, checkedFile.Requirement.Source);
        Assert.Equal(Hash(fixture.Bytes), checkedFile.Requirement.Hashes["sha1"]);
    }

    /// <summary>缓存中的下载地址必须与备用来源证明对应，不能通过单独改动来源或链接绕过校验。</summary>
    [Theory]
    [InlineData("sha1")]
    [InlineData("sha512")]
    [InlineData("size")]
    [InlineData("project")]
    [InlineData("version")]
    [InlineData("filename")]
    [InlineData("download")]
    [InlineData("missing-provenance")]
    public async Task TamperedFallbackProvenanceIsRejected(string field)
    {
        var fixture = await FixtureAsync();
        fixture.Modrinth.Versions[Hash(fixture.Bytes)] = Version(Match(fixture));
        var state = await Resolver(fixture).ResolveAsync(fixture.Instance, fixture.Manifest);
        var entry = Assert.Single(state.Files);
        var provenance = entry.ModrinthFallback!;
        var changed = field switch
        {
            "sha1" => entry with { ModrinthFallback = provenance with { Sha1 = new string('a', 40) } },
            "sha512" => entry with { ModrinthFallback = provenance with { Sha512 = new string('a', 128) } },
            "size" => entry with { ModrinthFallback = provenance with { Size = provenance.Size + 1 } },
            "project" => entry with { ModrinthFallback = provenance with { ProjectId = "other123" } },
            "version" => entry with { ModrinthFallback = provenance with { VersionId = "other123" } },
            "filename" => entry with { ModrinthFallback = provenance with { FileName = "other.jar" } },
            "download" => entry with { Requirement = entry.Requirement with { Downloads = [Url("other.jar")] } },
            _ => entry with { ModrinthFallback = null }
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => CurseForgeFileStateStore.WriteAsync(fixture.Instance,
            fixture.Manifest, state with { Files = [changed] }, CancellationToken.None));
    }

    /// <summary>从备用平台下载后仍按原内容检查，等长修改 JAR 会失败且不会覆盖玩家现有文件。</summary>
    [Fact]
    public async Task ModifiedDownloadedModFailsOriginalContentCheckAndIsPreserved()
    {
        var fixture = await FixtureAsync();
        fixture.Modrinth.Versions[Hash(fixture.Bytes)] = Version(Match(fixture));
        Assert.True((await Installer(fixture).InstallAsync(fixture.Instance)).FilesReady);
        var changed = fixture.Bytes.ToArray();
        changed[0] ^= 1;
        Write(fixture.Instance, ".minecraft/mods/cf-original.jar", changed);
        fixture.Api.FailIfCalled = true;
        fixture.Modrinth.FailIfCalled = true;

        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Instance);
        var result = await Installer(fixture).InstallAsync(fixture.Instance);

        Assert.Equal(InstanceFileState.HashMismatch, Assert.Single(report.Files, file => file.Requirement.FileId == 1000).State);
        Assert.False(result.FilesReady);
        Assert.Single(fixture.Downloader.Requests);
        Assert.Equal(changed, File.ReadAllBytes(Path.Combine(fixture.Instance, ".minecraft/mods/cf-original.jar")));
    }

    /// <summary>包内覆盖缺失时即使 Modrinth 能匹配官方文件，也不能用平台内容替代不同的原包文件。</summary>
    [Fact]
    public async Task MissingEmbeddedOverrideNeverFallsBackToMatchingOfficialModrinthFile()
    {
        var fixture = await FixtureAsync(2);
        fixture.Api.Mods[418881] = fixture.Api.Mods[101] with { Id = 418881, AllowModDistribution = true };
        fixture.Api.Mods.Remove(101);
        fixture.Api.Files[1001] = fixture.Api.Files[1001] with
        {
            ModId = 418881, FileName = "paxi.jar", DownloadUrl = "https://edge.forgecdn.net/files/1001/1/paxi.jar"
        };
        fixture.Api.Mods[100] = fixture.Api.Mods[100] with { ClassId = 6945 };
        fixture.Api.Files[1000] = fixture.Api.Files[1000] with { FileName = "datapack.zip" };
        var markerBytes = Encoding.UTF8.GetBytes("{\"loadOrder\":[]}");
        var embeddedBytes = fixture.Bytes.ToArray();
        embeddedBytes[0] ^= 1;
        var markerPath = ".minecraft/config/paxi/datapack_load_order.json";
        var packPath = ".minecraft/config/paxi/datapacks/datapack.zip";
        fixture = await RewriteManifestAsync(fixture, fixture.Manifest with
        {
            Files = [fixture.Manifest.Files[0], fixture.Manifest.Files[1] with { ProjectId = 418881 },
                Embedded(markerPath, markerBytes), Embedded(packPath, embeddedBytes) with
                {
                    ArchiveEntry = "overrides/config/paxi/datapacks/datapack.zip"
                }]
        });
        Write(fixture.Instance, markerPath, markerBytes);
        var mr = Match(fixture) with { Filename = "renamed-datapack.zip", Url = Url("renamed-datapack.zip") };
        fixture.Modrinth.Versions[Hash(fixture.Bytes)] = Version(mr);

        var result = await Installer(fixture).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.Equal(1, fixture.Runtime.Calls);
        Assert.Empty(fixture.Modrinth.Requests);
        Assert.Single(fixture.Downloader.Requests);
        Assert.DoesNotContain(fixture.Downloader.Requests, file => file.DestinationPath.EndsWith("datapack.zip"));
        Assert.False(File.Exists(Path.Combine(fixture.Instance, packPath)));
        var missing = Assert.Single(result.Files, file => file.Requirement.Path == packPath);
        Assert.Equal(InstanceDownloadState.MissingEmbeddedFile, missing.State);
        Assert.Equal(InstanceFileOrigin.EmbeddedOverride, missing.Requirement.Origin);
        Assert.Equal(new[] { "sha256" }, missing.Requirement.Hashes.Keys);
        Assert.Equal(Hash(embeddedBytes, "sha256"), missing.Requirement.Hashes["sha256"]);
        var state = CurseForgeFileStateStore.Read(fixture.Instance, fixture.Manifest)!;
        var resolved = Assert.Single(state.Files, file => file.Requirement.FileId == 1000);
        Assert.Null(resolved.ModrinthFallback);
        Assert.Empty(resolved.Requirement.Downloads);
        Assert.Equal(Hash(fixture.Bytes), resolved.Requirement.Hashes["sha1"]);
        Assert.NotNull(resolved.EmbeddedOverride);
        Assert.Equal(Hash(embeddedBytes, "sha256"), resolved.EmbeddedOverride.Hashes["sha256"]);
        Assert.Equal(Hash(fixture.Bytes), resolved.EmbeddedOverride.CurseForgeOrigin!.Hashes["sha1"]);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(fixture.Instance);
        Assert.Equal(InstanceFileState.MissingEmbeddedFile, Assert.Single(report.Files, file => file.Requirement.Path == packPath).State);
    }

    /// <summary>创建隔离实例和完全离线的可控平台响应。</summary>
    private async Task<Fixture> FixtureAsync(int count = 1)
    {
        var instance = Path.Combine(_root, "instances", "CF fallback test");
        Directory.CreateDirectory(instance);
        var bytes = Encoding.UTF8.GetBytes("exact original CurseForge content");
        var api = new FakeCurseForgeApi();
        var files = new List<InstanceFileRequirement>();
        for (var index = 0; index < count; index++)
        {
            api.Files[1000 + index] = new CurseForgeFile
            {
                Id = 1000 + index, ModId = 100 + index, GameId = 432, IsAvailable = true,
                FileName = index == 0 ? "cf-original.jar" : "cf-second.jar", FileLength = bytes.Length,
                Hashes = [new CurseForgeFileHash { Algo = 1, Value = Hash(bytes) }]
            };
            api.Mods[100 + index] = new CurseForgeMod
            {
                Id = 100 + index, GameId = 432, ClassId = 6, IsAvailable = true, AllowModDistribution = false,
                Links = new CurseForgeModLinks { WebsiteUrl = "https://www.curseforge.com/minecraft/mc-mods/example" }
            };
            files.Add(new InstanceFileRequirement { Source = InstanceFileSource.CurseForge, ProjectId = 100 + index, FileId = 1000 + index });
        }
        var manifest = new InstanceImportManifest
        {
            SourceFormat = "CurseForge", Name = "CF fallback test", MinecraftVersion = "1.20.1", Files = files,
            Components = [new("minecraft", "1.20.1"), new("fabric", "0.19.3")]
        };
        var path = Path.Combine(instance, InstanceImportManifest.FileName);
        await InstanceManifestStore.WriteAsync(path, manifest, CancellationToken.None);
        return new(instance, path, manifest, bytes, api, new FakeModrinthApi(), new VerifyingDownloader(bytes), new FakeRuntime());
    }

    /// <summary>更新测试清单并返回对应快照，使缓存指纹与磁盘一致。</summary>
    private static async Task<Fixture> RewriteManifestAsync(Fixture fixture, InstanceImportManifest manifest)
    {
        await InstanceManifestStore.WriteAsync(fixture.ManifestPath, manifest, CancellationToken.None);
        return fixture with { Manifest = manifest };
    }

    /// <summary>构造带官方固定项目和版本路径的候选文件。</summary>
    private static ModrinthVersionFile Match(Fixture fixture) => new()
    {
        Filename = "mr-renamed.jar", Url = Url("mr-renamed.jar"), Size = fixture.Bytes.Length, Primary = true,
        Hashes = new Dictionary<string, string> { ["sha1"] = Hash(fixture.Bytes), ["sha512"] = Hash(fixture.Bytes, "sha512") }
    };

    /// <summary>把精确文件加入一个固定版本，不依赖模糊版本名称匹配。</summary>
    private static ModrinthVersion Version(params ModrinthVersionFile[] files) => new()
    {
        Id = "version1", ProjectId = "project1", Files = files, Name = "1.0", VersionNumber = "1.0",
        GameVersions = ["1.20.1"], Loaders = ["fabric"]
    };

    /// <summary>构造与测试固定项目和版本一致的官方 CDN 地址。</summary>
    private static string Url(string fileName) => "https://cdn.modrinth.com/data/project1/versions/version1/" + fileName;

    /// <summary>按测试指定算法生成真实哈希，以验证内容而非只检查字段是否存在。</summary>
    private static string Hash(byte[] bytes, string algorithm = "sha1") => Convert.ToHexStringLower(algorithm switch
    {
        "sha256" => SHA256.HashData(bytes), "sha512" => SHA512.HashData(bytes), _ => SHA1.HashData(bytes)
    });

    /// <summary>构造导入器保存的包内 SHA-256 基线。</summary>
    private static InstanceFileRequirement Embedded(string path, byte[] bytes) => new()
    {
        Path = path, Source = InstanceFileSource.Embedded, Size = bytes.Length,
        Hashes = new Dictionary<string, string> { ["sha256"] = Hash(bytes, "sha256") }
    };

    /// <summary>仅写本测试创建的随机临时实例。</summary>
    private static void Write(string instance, string relative, byte[] bytes)
    {
        var path = Path.Combine(instance, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>注入两个固定平台客户端测试元数据解析。</summary>
    private static CurseForgeFileResolver Resolver(Fixture fixture) => new(fixture.Api, fixture.Modrinth);

    /// <summary>注入所有网络和运行依赖，使安装测试从不联系真实服务。</summary>
    private CurseForgeInstanceInstaller Installer(Fixture fixture) => new(_root, fixture.Downloader, fixture.Api,
        fixture.Runtime, modrinthApiClient: fixture.Modrinth);

    /// <summary>清理当前测试的随机临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>封装隔离实例、固定内容及全部替身。</summary>
    private sealed record Fixture(string Instance, string ManifestPath, InstanceImportManifest Manifest, byte[] Bytes,
        FakeCurseForgeApi Api, FakeModrinthApi Modrinth, VerifyingDownloader Downloader, FakeRuntime Runtime);

    /// <summary>保留 CurseForge 原始身份信息，并可检测任何意外重复联网。</summary>
    private sealed class FakeCurseForgeApi : ICurseForgeApiClient
    {
        public Dictionary<long, CurseForgeFile> Files { get; } = [];
        public Dictionary<long, CurseForgeMod> Mods { get; } = [];
        public bool FailIfCalled { get; set; }
        public CurseForgeRateLimit? LastRateLimit => null;

        /// <summary>只返回请求的固定文件 ID。</summary>
        public Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
        {
            Assert.False(FailIfCalled, "CurseForge metadata must be reused offline.");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<CurseForgeFile>>(ids.Where(Files.ContainsKey).Select(id => Files[id]).ToArray());
        }

        /// <summary>只返回请求的固定项目 ID。</summary>
        public Task<IReadOnlyList<CurseForgeMod>> GetModsAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
        {
            Assert.False(FailIfCalled, "CurseForge metadata must be reused offline.");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<CurseForgeMod>>(ids.Where(Mods.ContainsKey).Select(id => Mods[id]).ToArray());
        }

        /// <summary>禁止把固定版本补全变成项目搜索。</summary>
        public Task<CurseForgeSearchResult> SearchAsync(CurseForgeSearchOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>当前流程应使用批量查询。</summary>
        public Task<CurseForgeFile> GetFileAsync(long projectId, long fileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>当前流程应使用批量查询。</summary>
        public Task<CurseForgeMod> GetModAsync(long projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>提供可控的精确哈希结果、网络失败与取消时机。</summary>
    private sealed class FakeModrinthApi : EmptyModrinthApiClient
    {
        public Dictionary<string, ModrinthVersion> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string[]> Requests { get; } = [];
        public Exception? Failure { get; set; }
        public Action? BeforeQuery { get; set; }
        public bool FailIfCalled { get; set; }

        /// <summary>记录哈希查询并模拟成功、失败和取消，不实现名称搜索兜底。</summary>
        public override Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(IReadOnlyList<string> hashes,
            string algorithm = "sha1", CancellationToken cancellationToken = default)
        {
            Assert.False(FailIfCalled, "Modrinth metadata must be reused offline.");
            Assert.Equal("sha1", algorithm);
            Requests.Add(hashes.ToArray());
            BeforeQuery?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) return Task.FromException<IReadOnlyDictionary<string, ModrinthVersion>>(Failure);
            return Task.FromResult<IReadOnlyDictionary<string, ModrinthVersion>>(Versions);
        }
    }

    /// <summary>返回固定字节并验证请求携带的每种哈希，避免替身掩盖丢失原始校验依据的问题。</summary>
    private sealed class VerifyingDownloader(byte[] bytes) : IFileDownloader
    {
        public List<FileDownloadRequest> Requests { get; } = [];

        /// <summary>逐项校验内容后以无覆盖方式写入测试目标。</summary>
        public Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Requests) Requests.Add(request);
            Assert.Equal(bytes.Length, request.ExpectedSize);
            foreach (var pair in request.Hashes) Assert.Equal(Hash(bytes, pair.Key), pair.Value, ignoreCase: true);
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            using var output = new FileStream(request.DestinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(bytes);
            return Task.FromResult(new FileDownloadResult(request.DestinationPath, bytes.Length, true));
        }
    }

    /// <summary>记录运行准备阶段是否被正确阻断。</summary>
    private sealed class FakeRuntime : IPrismInstanceInstaller
    {
        public int Calls { get; private set; }

        /// <summary>仅返回测试启动配置，不下载运行组件。</summary>
        public Task<PrismInstallResult> InstallAsync(string instancePath, IProgress<InstanceDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PrismInstallResult { SourceFormat = "CurseForge", LaunchProfilePath = "fake-launch.json" });
        }
    }
}

/// <summary>让原有 CurseForge 测试明确注入空备用平台，避免新增默认依赖造成真实联网。</summary>
internal class EmptyModrinthApiClient : IModrinthApiClient
{
    public ModrinthRateLimit? LastRateLimit => null;

    /// <summary>默认无精确哈希匹配，维持原人工补齐场景。</summary>
    public virtual Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(IReadOnlyList<string> hashes,
        string algorithm = "sha1", CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, ModrinthVersion>>(new Dictionary<string, ModrinthVersion>());

    /// <summary>补全不能通过名称搜索自动替换版本。</summary>
    public Task<ModrinthSearchResult> SearchAsync(ModrinthSearchOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>补全不需要项目详情请求。</summary>
    public Task<ModrinthProject> GetProjectAsync(string projectIdOrSlug, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>哈希批量查询已返回版本详情，不应重复查询。</summary>
    public Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>禁止按版本列表猜测匹配文件。</summary>
    public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectIdOrSlug, ModrinthVersionFilter? filter = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
