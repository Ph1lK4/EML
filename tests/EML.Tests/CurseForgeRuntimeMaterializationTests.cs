using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Infrastructure.Downloads;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.CurseForge;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Tests;

/// <summary>用真实解析、下载、生成及检查流程验证 CurseForge Fabric 运行环境，全程不连接网络或启动游戏。</summary>
public sealed class CurseForgeRuntimeMaterializationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-cf-materialization-" + Guid.NewGuid().ToString("N"));

    /// <summary>从缺失官方元数据开始补全原版、Fabric、资源和日志，然后检查独立状态及重复安装复用。</summary>
    [Fact]
    public async Task FixedFabricMetadataDownloadsAndMaterializesIntoIndependentCurseForgeState()
    {
        var instance = Path.Combine(_root, "instances", "Fabric test");
        Directory.CreateDirectory(instance);
        var manifest = new InstanceImportManifest
        {
            SourceFormat = "CurseForge", Name = "Fabric test", MinecraftVersion = "1.20.1",
            Components = [new("minecraft", "1.20.1"), new("fabric", "0.15.11") { Primary = true }]
        };
        var manifestPath = Path.Combine(instance, InstanceImportManifest.FileName);
        await InstanceManifestStore.WriteAsync(manifestPath, manifest, CancellationToken.None);
        var baseline = File.ReadAllBytes(manifestPath);
        var payloads = Payloads();
        using var handler = new FixtureHandler(payloads);
        using var downloader = new HttpFileDownloader(handler);
        var installer = new InstanceRuntimeInstaller(_root, downloader, new CurseForgeRuntimeResolver(), "CurseForge");

        var result = await installer.InstallAsync(instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Equal("CurseForge", result.SourceFormat);
        Assert.True(File.Exists(result.LaunchProfilePath));
        Assert.Contains("EML-CurseForge-", result.LaunchProfilePath!, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(instance, ".eml-runtime", "curseforge", "state.json")));
        Assert.False(File.Exists(Path.Combine(instance, ".eml-runtime", "state.json")));
        Assert.False(Directory.Exists(Path.Combine(instance, ".eml-runtime", "modrinth")));
        Assert.Equal(8, handler.Urls.Count);
        using var profile = JsonDocument.Parse(File.ReadAllText(result.LaunchProfilePath!));
        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", profile.RootElement.GetProperty("mainClass").GetString());
        Assert.Equal("fixture-assets", profile.RootElement.GetProperty("assets").GetString());
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(instance);
        Assert.Equal("CurseForge", report.RuntimeSourceFormat);
        Assert.False(report.IsPrismRuntime);
        Assert.Empty(report.RuntimeIssues);
        Assert.Equal(result.LaunchProfilePath, report.LaunchProfilePath);
        Assert.DoesNotContain(report.Files, file => file.State is InstanceFileState.Missing or InstanceFileState.HashMismatch or InstanceFileState.SizeMismatch);
        Assert.Equal(baseline, File.ReadAllBytes(manifestPath));

        var reused = await installer.InstallAsync(instance);
        Assert.True(reused.FilesReady, string.Join("\n", reused.Issues));
        Assert.Equal(8, handler.Urls.Count);
        Assert.Equal(result.LaunchProfilePath, reused.LaunchProfilePath);
        Assert.Empty(Directory.GetFiles(instance, "*.part", SearchOption.AllDirectories));
    }

    /// <summary>为真实官方 URL 的测试响应构造精确哈希链，确保测试覆盖元数据与运行文件两阶段。</summary>
    private static Dictionary<string, byte[]> Payloads()
    {
        var main = Jar("net/minecraft/client/main/Main.class");
        var loader = Jar("net/fabricmc/loader/impl/launch/knot/KnotClient.class");
        var asset = Encoding.UTF8.GetBytes("fixture resource");
        var assetHash = Hash(asset);
        var index = JsonSerializer.SerializeToUtf8Bytes(new
        {
            objects = new Dictionary<string, object> { ["minecraft/sounds/test.ogg"] = new { hash = assetHash, size = asset.Length } }
        });
        var logging = Encoding.UTF8.GetBytes("<Configuration status=\"WARN\" />");
        const string mainUrl = "https://example.test/client.jar";
        const string indexUrl = "https://example.test/assets.json";
        const string logUrl = "https://example.test/logging.xml";
        const string versionUrl = "https://piston-meta.mojang.com/v1/packages/fixture/1.20.1.json";
        var version = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "1.20.1", mainClass = "net.minecraft.client.main.Main", javaVersion = new { majorVersion = 17 },
            downloads = new { client = new { url = mainUrl, sha1 = Hash(main), size = main.Length } },
            assetIndex = new { id = "fixture-assets", url = indexUrl, sha1 = Hash(index), size = index.Length },
            arguments = new
            {
                game = new[] { "--username", "${auth_player_name}", "--gameDir", "${game_directory}", "--assetsDir", "${assets_root}", "--assetIndex", "${assets_index_name}" },
                jvm = new[] { "-Djava.library.path=${natives_directory}", "-cp", "${classpath}" }
            },
            logging = new { client = new { argument = "-Dlog4j.configurationFile=${path}", type = "log4j2-xml", file = new { id = "fixture.xml", url = logUrl, sha1 = Hash(logging), size = logging.Length } } }
        });
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            versions = new[] { new { id = "1.20.1", url = versionUrl, sha1 = Hash(version) } }
        });
        var fabric = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "fabric-loader-0.15.11-1.20.1", inheritsFrom = "1.20.1", mainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
            libraries = new[] { new { name = "net.fabricmc:fabric-loader:0.15.11", url = "https://maven.fabricmc.net/", sha1 = Hash(loader), size = loader.Length } }
        });
        return new(StringComparer.Ordinal)
        {
            ["https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"] = manifest,
            [versionUrl] = version,
            ["https://meta.fabricmc.net/v2/versions/loader/1.20.1/0.15.11/profile/json"] = fabric,
            [mainUrl] = main, [indexUrl] = index, [logUrl] = logging,
            ["https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.15.11/fabric-loader-0.15.11.jar"] = loader,
            [$"https://resources.download.minecraft.net/{assetHash[..2]}/{assetHash}"] = asset
        };
    }

    /// <summary>计算上游声明使用的 SHA1，便于真实下载器校验测试字节。</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));

    /// <summary>构造可读取的微型 JAR，条目内容从不作为真实 Java 字节码执行。</summary>
    private static byte[] Jar(string entryName)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry(entryName).Open();
            entry.Write(Encoding.UTF8.GetBytes("test bytes"));
        }
        return stream.ToArray();
    }

    /// <summary>仅移除当前测试创建的独立临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    /// <summary>为真实 HTTP 下载器提供内存响应，未声明请求直接失败，不访问外部网络。</summary>
    private sealed class FixtureHandler(IReadOnlyDictionary<string, byte[]> payloads) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Urls { get; } = new();

        /// <summary>记录请求并返回测试内容，取消仍遵守统一下载流程。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri!.AbsoluteUri;
            Urls.Enqueue(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payloads[url]) });
        }
    }
}
