using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Infrastructure.Downloads;
using EML.Infrastructure.Minecraft;

namespace EML.Tests;

/// <summary>覆盖完整 Prism 备份导入后的补全过程，不要求包内模组有远端下载清单。</summary>
public sealed class PrismArchiveCompletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-Prism-archive-" + Guid.NewGuid().ToString("N"));

    /// <summary>真实包内模组和玩家文件原样保留；运行文件存在则复用，缺失才按组件声明下载。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackedModsAndPlayerDataSurviveCompletionWithoutModDownloads(bool includesRuntime)
    {
        var (archive, content, payloads) = CreateArchive(includesRuntime);
        var importer = new MinecraftInstanceImporter(_root);
        var preview = await importer.InspectAsync(archive);
        Assert.Equal("Prism", preview.Format);
        var imported = await importer.ImportAsync(archive, "Played Prism instance");
        Assert.All(imported.Manifest.Files, file =>
        {
            Assert.Equal(InstanceFileSource.Embedded, file.Source);
            Assert.Empty(file.Downloads);
        });
        using var handler = new RecordedTransport(payloads);
        using var transport = new HttpFileDownloader(handler);
        var completer = new MinecraftInstanceDownloader(_root, new MinecraftInstanceChecker(_root), transport);

        var completed = await completer.DownloadMissingAsync(imported.InstancePath);

        Assert.True(completed.LaunchReady, string.Join('\n', completed.Runtime?.Issues ?? []) + completed.CheckError);
        Assert.Equal(includesRuntime ? 0 : payloads.Count, handler.Urls.Count);
        Assert.All(handler.Urls, url => Assert.Contains(url, payloads.Keys));
        Assert.DoesNotContain(completed.Files, file => file.Requirement.Path!.Contains("/mods/"));
        AssertOriginalContent(imported.InstancePath, content);
        foreach (var mod in content.Keys.Where(path => path.Contains("/mods/")))
            Assert.Equal(InstanceFileState.Verified, Assert.Single(completed.CheckReport!.Files,
                file => file.Requirement.Path == mod).State);

        handler.Urls.Clear();
        var repeated = await completer.DownloadMissingAsync(imported.InstancePath);
        Assert.True(repeated.LaunchReady, string.Join('\n', repeated.Runtime?.Issues ?? []) + repeated.CheckError);
        Assert.Empty(handler.Urls);
        AssertOriginalContent(imported.InstancePath, content);
    }

    /// <summary>创建无模组下载清单的 Prism 导出包，包含自定义模组、禁用模组、存档、地图和中文设置。</summary>
    private (string Archive, Dictionary<string, byte[]> Content, Dictionary<string, byte[]> Payloads) CreateArchive(bool includesRuntime)
    {
        Directory.CreateDirectory(_root);
        var main = CreateJar("net/minecraft/client/main/Main.class", "test main");
        var library = CreateJar("example/Library.class", "test library");
        var asset = Encoding.UTF8.GetBytes("test sound");
        var assetHash = Sha1(asset);
        var index = JsonSerializer.SerializeToUtf8Bytes(new { objects = new Dictionary<string, object>
        { ["minecraft/sounds/test.ogg"] = new { hash = assetHash, size = asset.Length } } });
        var payloads = new Dictionary<string, byte[]>
        {
            ["https://example.test/main.jar"] = main,
            ["https://example.test/library.jar"] = library,
            ["https://example.test/index.json"] = index,
            [$"https://resources.download.minecraft.net/{assetHash[..2]}/{assetHash}"] = asset
        };
        var content = new Dictionary<string, byte[]>
        {
            ["mmc-pack.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            { formatVersion = 1, components = new[] { new { uid = "net.minecraft", version = "1.7.10" } } }),
            ["patches/net.minecraft.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                formatVersion = 1, uid = "net.minecraft", version = "1.7.10", mainClass = "net.minecraft.client.main.Main",
                minecraftArguments = "--username ${auth_player_name} --gameDir ${game_directory}", compatibleJavaMajors = new[] { 8 },
                libraries = new[] { new { name = "example:library:1.0", downloads = new
                { artifact = new { url = "https://example.test/library.jar", size = library.Length, sha1 = Sha1(library) } } } },
                mainJar = new { name = "com.mojang:minecraft:1.7.10:client", downloads = new
                { artifact = new { url = "https://example.test/main.jar", size = main.Length, sha1 = Sha1(main) } } },
                assetIndex = new { id = "test-assets", url = "https://example.test/index.json", size = index.Length, sha1 = Sha1(index) }
            }),
            [".minecraft/mods/personal-mod.jar"] = CreateJar("mod/Test.class", "local mod without API metadata"),
            [".minecraft/mods/disabled-mod.jar.disabled"] = CreateJar("mod/Test.class", "disabled mod"),
            [".minecraft/saves/My World/level.dat"] = Encoding.UTF8.GetBytes("player world data"),
            [".minecraft/journeymap/data/mp/server/day/region.png"] = Encoding.UTF8.GetBytes("player map data"),
            [".minecraft/options.txt"] = Encoding.UTF8.GetBytes("lang:zh_CN\n"),
            [".minecraft/config/custom.cfg"] = Encoding.UTF8.GetBytes("custom-setting=true")
        };
        if (includesRuntime)
        {
            content[".minecraft/libraries/com/mojang/minecraft/1.7.10/minecraft-1.7.10-client.jar"] = main;
            content[".minecraft/libraries/example/library/1.0/library-1.0.jar"] = library;
            content[".minecraft/assets/indexes/test-assets.json"] = index;
            content[$".minecraft/assets/objects/{assetHash[..2]}/{assetHash}"] = asset;
        }
        var path = Path.Combine(_root, "Prism played backup.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, bytes) in content)
            {
                using var output = zip.CreateEntry(name).Open();
                output.Write(bytes);
            }
        return (path, content, payloads);
    }

    /// <summary>按原 ZIP 字节比对每个文件，不能因补全而改写模组、存档或中文偏好。</summary>
    private static void AssertOriginalContent(string instance, Dictionary<string, byte[]> content)
    {
        foreach (var (path, bytes) in content)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(instance, path)));
    }

    /// <summary>生成有效但不会执行的微型 JAR，用于真实下载校验和启动配置生成。</summary>
    private static byte[] CreateJar(string entry, string content)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var output = new StreamWriter(zip.CreateEntry(entry).Open())) output.Write(content);
        return buffer.ToArray();
    }

    /// <summary>为测试运行依赖声明真实 SHA-1，由生产下载器验证。</summary>
    private static string Sha1(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));

    /// <summary>仅清理由本测试创建的临时实例及压缩包。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>只替代 HTTP 响应，保留生产下载器、解析器、安装器和检查器的完整调用链。</summary>
    private sealed class RecordedTransport(IReadOnlyDictionary<string, byte[]> payloads) : HttpMessageHandler
    {
        internal ConcurrentBag<string> Urls { get; } = [];

        /// <summary>记录每个请求并拒绝任何未声明的下载，包括根据模组文件名猜测的地址。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri!.AbsoluteUri;
            Urls.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payloads[url]) });
        }
    }
}
