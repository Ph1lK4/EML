using System.Net;
using System.Text;
using System.Text.Json;
using EML.Core.CurseForge;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.CurseForge;
using EML.Infrastructure.Minecraft.CurseForge.Api;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Tests;

/// <summary>验证官方 API 模式的缓存迁移、CDN 约束和已有文件离线复查。</summary>
public sealed class CurseForgeFileResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-CurseForge-resolver-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>旧后端下载地址不能用于缺失文件，必须重新获取官方返回的固定版本 CDN 地址。</summary>
    [Theory]
    [InlineData("https://old-service.example.test/v1/curseforge/projects/10/files/20/download")]
    [InlineData("https://localhost:5015/v1/curseforge/projects/10/files/20/download")]
    public async Task MissingFileFromOldGatewayCacheIsResolvedAgain(string oldUrl)
    {
        var fixture = await FixtureAsync(oldUrl);
        using var handler = new MetadataHandler("https://edge.forgecdn.net/files/20/test.jar");
        using var client = new CurseForgeApiClient(handler, new FixtureKeyProvider());

        var result = await new CurseForgeFileResolver(client).ResolveAsync(fixture.Instance, fixture.Manifest);

        Assert.Equal(2, handler.Calls);
        Assert.Equal("https://edge.forgecdn.net/files/20/test.jar", Assert.Single(Assert.Single(result.Files).Requirement.Downloads));
        Assert.Equal(fixture.OriginalManifest, File.ReadAllBytes(Path.Combine(fixture.Instance, InstanceImportManifest.FileName)));
        Assert.DoesNotContain("test-only-key", File.ReadAllText(Path.Combine(fixture.Instance, CurseForgeFileStateStore.RelativePath)));
    }

    /// <summary>有效官方 CDN 缓存可用于恢复尚未完成的文件，不重新请求同一批不可变元数据。</summary>
    [Theory]
    [InlineData("https://edge.forgecdn.net/files/20/test.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/20/test.jar")]
    [InlineData("https://edge.forgecdn.net/files/20/test.jar?download=true")]
    public async Task AllowedCdnCacheCanBeReusedForMissingFile(string address)
    {
        var fixture = await FixtureAsync(address);
        using var handler = new MetadataHandler(address) { FailIfCalled = true };
        using var client = new CurseForgeApiClient(handler, new FixtureKeyProvider { FailIfRead = true });

        var result = await new CurseForgeFileResolver(client).ResolveAsync(fixture.Instance, fixture.Manifest);

        Assert.Equal(address, Assert.Single(Assert.Single(result.Files).Requirement.Downloads));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>已有文件可继续离线校验，即使旧缓存来自后端，也不读取密钥或访问 API。</summary>
    [Fact]
    public async Task ExistingFileFromOldGatewayCacheRemainsReadableOffline()
    {
        var fixture = await FixtureAsync("https://old-service.example.test/v1/curseforge/projects/10/files/20/download");
        var filePath = Path.Combine(fixture.Instance, ".minecraft/mods/test.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, [(byte)'x']);
        using var handler = new MetadataHandler("") { FailIfCalled = true };
        using var client = new CurseForgeApiClient(handler, new FixtureKeyProvider { FailIfRead = true });

        var result = await new CurseForgeFileResolver(client).ResolveAsync(fixture.Instance, fixture.Manifest);

        Assert.Single(result.Files);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>官方 API 返回其他域名、非标准端口或凭据查询参数时，不允许写入新的下载缓存。</summary>
    [Theory]
    [InlineData("https://other.example.test/mod.jar")]
    [InlineData("https://edge.forgecdn.net.attacker.test/mod.jar")]
    [InlineData("https://media.forgecdn.net/files/20/test.jar")]
    [InlineData("https://edge.forgecdn.net:444/files/20/test.jar")]
    [InlineData("http://edge.forgecdn.net/files/20/test.jar")]
    [InlineData("https://edge.forgecdn.net/files/20/test.jar?x-api-key=untrusted-value")]
    [InlineData("https://edge.forgecdn.net/files/20/test.jar?signature=untrusted-value")]
    public async Task OfficialApiCannotIntroduceUntrustedOrCredentialBearingDownloadUrls(string address)
    {
        var fixture = await FixtureAsync(null);
        using var handler = new MetadataHandler(address);
        using var client = new CurseForgeApiClient(handler, new FixtureKeyProvider());

        await Assert.ThrowsAsync<InvalidDataException>(() => new CurseForgeFileResolver(client).ResolveAsync(fixture.Instance, fixture.Manifest));

        Assert.False(File.Exists(Path.Combine(fixture.Instance, CurseForgeFileStateStore.RelativePath)));
    }

    /// <summary>受限制文件保留空下载来源和人工入口，不能改写成允许自动请求的 CDN 地址。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestrictedOrMissingUrlRemainsManual(bool distributionDenied)
    {
        var fixture = await FixtureAsync(null);
        using var handler = new MetadataHandler(distributionDenied ? "https://edge.forgecdn.net/files/20/test.jar" : null)
        {
            DistributionAllowed = !distributionDenied
        };
        using var client = new CurseForgeApiClient(handler, new FixtureKeyProvider());

        var result = await new CurseForgeFileResolver(client).ResolveAsync(fixture.Instance, fixture.Manifest);

        var file = Assert.Single(result.Files);
        Assert.Empty(file.Requirement.Downloads);
        Assert.Null(file.BlockReason);
        Assert.Equal("https://www.curseforge.com/projects/10/files/20", file.ManualDownloadUrl);
    }

    /// <summary>创建独立实例，可选择写入旧解析缓存；导入声明保持原始固定引用。</summary>
    private async Task<Fixture> FixtureAsync(string? cachedUrl)
    {
        var instance = Path.Combine(_root, "instances", "Test");
        Directory.CreateDirectory(instance);
        var original = new InstanceFileRequirement { Source = InstanceFileSource.CurseForge, ProjectId = 10, FileId = 20 };
        var manifest = new InstanceImportManifest { SourceFormat = "CurseForge", Name = "Test", Files = [original] };
        var manifestPath = Path.Combine(instance, InstanceImportManifest.FileName);
        await InstanceManifestStore.WriteAsync(manifestPath, manifest, CancellationToken.None);
        if (cachedUrl is not null)
            await CurseForgeFileStateStore.WriteAsync(instance, manifest, new()
            {
                Fingerprint = CurseForgeFileStateStore.Fingerprint(manifest),
                Files = [new()
                {
                    ClassId = 6, FileName = "test.jar", ManualDownloadUrl = "https://www.curseforge.com/projects/10/files/20",
                    Requirement = original with
                    {
                        Path = ".minecraft/mods/test.jar", Size = 1, Downloads = [cachedUrl],
                        Hashes = new Dictionary<string, string> { ["sha1"] = new string('a', 40) }
                    }
                }]
            }, CancellationToken.None);
        return new(instance, manifest, await File.ReadAllBytesAsync(manifestPath));
    }

    /// <summary>删除本测试随机创建的目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>保存测试清单及其原始字节，用于确认解析流程不改写导入依据。</summary>
    private sealed record Fixture(string Instance, InstanceImportManifest Manifest, byte[] OriginalManifest);

    /// <summary>仅提供测试字符串，允许验证离线操作不触及密钥读取。</summary>
    private sealed class FixtureKeyProvider : ICurseForgeApiKeyProvider
    {
        public bool FailIfRead { get; init; }

        /// <summary>返回测试凭据，离线场景触发读取时直接失败。</summary>
        public string? GetApiKey()
        {
            Assert.False(FailIfRead, "Offline metadata must not require reading an API key.");
            return "test-only-key";
        }
    }

    /// <summary>模拟官方 data 包装的固定文件和项目响应，不访问真实官方服务。</summary>
    private sealed class MetadataHandler(string? downloadUrl) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool FailIfCalled { get; init; }
        public bool DistributionAllowed { get; init; } = true;

        /// <summary>按官方端点返回固定数据，检查请求始终仅发送给官方 API 主机。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.False(FailIfCalled, "This operation must reuse cached metadata.");
            Assert.Equal("api.curseforge.com", request.RequestUri!.Host);
            Calls++;
            object body = request.RequestUri.AbsolutePath.EndsWith("/files")
                ? new { data = new[] { new CurseForgeFile
                    {
                        Id = 20, ModId = 10, GameId = 432, IsAvailable = true, FileName = "test.jar", FileLength = 1,
                        DownloadUrl = downloadUrl, Hashes = [new CurseForgeFileHash { Algo = 1, Value = new string('a', 40) }]
                    } } }
                : new { data = new[] { new CurseForgeMod { Id = 10, GameId = 432, ClassId = 6, AllowModDistribution = DistributionAllowed } } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
            });
        }
    }
}
