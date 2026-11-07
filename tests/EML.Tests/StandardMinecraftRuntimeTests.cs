using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Prism;
using EML.Infrastructure.Minecraft.Standard;

namespace EML.Tests;

/// <summary>用标准版本目录覆盖 ZIP 的旧版参数、继承、官方摘要和首次本地准备。</summary>
public sealed class StandardMinecraftRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-standard-tests-" + Guid.NewGuid().ToString("N"));
    private string Instance => Path.Combine(_root, "instances", "vanilla");
    private const string MainPath = ".minecraft/versions/1.7.10/1.7.10.jar";
    private const string VersionPath = ".minecraft/versions/1.7.10/1.7.10.json";
    private const string LibraryPath = ".minecraft/libraries/example/library/1.0/library-1.0.jar";
    private const string NativePath = ".minecraft/libraries/example/library/1.0/library-1.0-natives-windows-64.jar";
    private const string IndexPath = ".minecraft/assets/indexes/1.7.10.json";
    private InstanceImportManifest _manifest = new() { SourceFormat = "Zip", MinecraftVersion = "1.7.10", Components = [new("minecraft", "1.7.10")] };
    private readonly Dictionary<string, byte[]> _payloads = new(StringComparer.Ordinal);

    /// <summary>创建含旧式 native、资源索引和日志声明的最小完整 1.7.10 实例。</summary>
    public StandardMinecraftRuntimeTests()
    {
        var main = Encoding.UTF8.GetBytes("client fixture");
        var library = Encoding.UTF8.GetBytes("library fixture");
        var index = Encoding.UTF8.GetBytes("{\"objects\":{}}");
        var logging = Encoding.UTF8.GetBytes("<configuration />");
        using var nativeStream = new MemoryStream();
        using (var archive = new ZipArchive(nativeStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var output = archive.CreateEntry("lwjgl64.dll").Open()) output.Write([1, 2, 3]);
            using (var output = archive.CreateEntry("META-INF/ignored.txt").Open()) output.Write([4]);
        }
        var natives = nativeStream.ToArray();
        Write(MainPath, main);
        Write(LibraryPath, library);
        Write(IndexPath, index);
        Write(NativePath, natives);
        Write(".minecraft/assets/log_configs/client-1.7.xml", logging);
        Write(".minecraft/config/player.cfg", Encoding.UTF8.GetBytes("old config"));
        var version = new
        {
            id = "1.7.10", mainClass = "net.minecraft.client.main.Main",
            minecraftArguments = "--username ${auth_player_name} --gameDir ${game_directory} --userProperties {}",
            javaVersion = new { majorVersion = 8 }, downloads = new { client = Artifact("client.jar", main) },
            assetIndex = WithId(Artifact("index.json", index), "1.7.10"),
            logging = new { client = new { type = "log4j2-xml", argument = "-Dlog4j.configurationFile=${path}", file = WithId(Artifact("logging.xml", logging), "client-1.7.xml") } },
            libraries = new[] { new {
                name = "example:library:1.0", downloads = new { artifact = Artifact("library.jar", library),
                    classifiers = new Dictionary<string, object> { ["natives-windows-64"] = Artifact("natives.jar", natives) } },
                natives = new { windows = "natives-windows-${arch}" }, extract = new { exclude = new[] { "META-INF/" } }
            } }
        };
        Write(VersionPath, JsonSerializer.SerializeToUtf8Bytes(version));
        RefreshManifest();
    }

    /// <summary>解析标准主类、Java 8 和本机库，且不写入生成文件。</summary>
    [Fact]
    public void ResolvesVanilla1710WithLegacyArgumentsAndNativeClassifier()
    {
        var before = Directory.GetFiles(Instance, "*", SearchOption.AllDirectories).Order().ToArray();
        var plan = Resolve();
        Assert.Empty(plan.BlockingIssues);
        Assert.Empty(plan.MetadataRequests);
        Assert.Equal("Zip", plan.SourceFormat);
        Assert.Equal("EML-Standard", plan.VersionIdPrefix);
        Assert.Equal(new[] { 8 }, plan.CompatibleJavaMajors);
        Assert.Equal("net.minecraft.client.main.Main", plan.MainClass);
        Assert.Equal(new[] { "--username", "${auth_player_name}", "--gameDir", "${game_directory}", "--userProperties", "{}" }, plan.GameArguments);
        Assert.Equal(MainPath, Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.MainJar).Requirement.Path);
        var native = Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.NativeArchive);
        Assert.Equal(NativePath, native.Requirement.Path);
        Assert.False(native.OnClassPath);
        Assert.Contains("META-INF/", native.ExtractExcludes);
        Assert.Equal(before, Directory.GetFiles(Instance, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    /// <summary>首次准备只验证运行文件，用户配置修改不会触发整包内容检查。</summary>
    [Fact]
    public async Task FirstLocalPreparationBuildsPortableProfileWithoutCheckingPlayerContent()
    {
        Write(".minecraft/config/player.cfg", Encoding.UTF8.GetBytes("changed by game"));
        var result = await ZipInstanceInstaller.PrepareLocalAsync(_root, Instance, resolver: Resolver());
        Assert.True(result.RuntimeReady, string.Join('\n', result.Issues));
        Assert.False(result.LaunchReady);
        Assert.True(File.Exists(result.PreparedLaunchProfilePath));
        var plan = Resolve();
        var state = PrismRuntimeStateStore.Read(plan);
        Assert.NotNull(state);
        using var standard = JsonDocument.Parse(File.ReadAllBytes(result.PreparedLaunchProfilePath!));
        var portable = standard.RootElement.GetProperty("emlLaunchProfile").GetString()!;
        using var profile = JsonDocument.Parse(File.ReadAllBytes(Full(portable)));
        Assert.Equal("net.minecraft.client.main.Main", profile.RootElement.GetProperty("mainClass").GetString());
        Assert.Contains("-Dlog4j.configurationFile=${logging_path}", profile.RootElement.GetProperty("jvmArguments").EnumerateArray().Select(value => value.GetString()));
        var nativeRoot = profile.RootElement.GetProperty("nativesDirectory").GetString()!;
        Assert.True(File.Exists(Full(nativeRoot + "/lwjgl64.dll")));
        Assert.False(File.Exists(Full(nativeRoot + "/META-INF/ignored.txt")));
        Assert.Equal("changed by game", File.ReadAllText(Full(".minecraft/config/player.cfg")));
    }

    /// <summary>生成的版本不参与下次源选择，避免首次启动后变成多版本歧义。</summary>
    [Fact]
    public async Task GeneratedVersionDoesNotChangeSourceFingerprint()
    {
        var before = Resolve().Fingerprint;
        var result = await ZipInstanceInstaller.PrepareLocalAsync(_root, Instance, resolver: Resolver());
        Assert.True(result.RuntimeReady, string.Join('\n', result.Issues));
        Assert.Equal(before, Resolve().Fingerprint);
    }

    /// <summary>用户命名的 EML 版本和不完整自定义标记仍是源版本，不能跳过加载器而启动父原版。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("custom user metadata")]
    public void UserVersionWithEmlPrefixIsNotTreatedAsGenerated(string? customMarker)
    {
        AddProfile("EML-My-Forge", JsonSerializer.Serialize(new
        {
            id = "EML-My-Forge", inheritsFrom = "1.7.10", mainClass = "net.minecraft.launchwrapper.Launch",
            emlRuntimeFingerprint = customMarker
        }));
        var plan = Resolve();
        Assert.Equal("net.minecraft.launchwrapper.Launch", plan.MainClass);
        Assert.Empty(plan.BlockingIssues);
        Assert.Contains(plan.Notes, note => note.Contains("EML-My-Forge"));
    }

    /// <summary>缺少运行文件时只提示补全，不自动联网或发布准备状态。</summary>
    [Fact]
    public async Task MissingLocalDependencyDoesNotPublishState()
    {
        File.Delete(Full(LibraryPath));
        var result = await ZipInstanceInstaller.PrepareLocalAsync(_root, Instance, resolver: Resolver());
        Assert.False(result.RuntimeReady);
        Assert.Contains(result.RuntimeIssues, issue => issue.Contains("补全实例依赖"));
        Assert.False(File.Exists(Full(LibraryPath)));
        Assert.Null(PrismRuntimeStateStore.Read(Resolve()));
    }

    /// <summary>已有但错误的官方依赖不能被 ZIP 基线掩盖，也不能被静默覆盖。</summary>
    [Fact]
    public async Task CorruptOfficialDependencyFailsWithoutOverwrite()
    {
        var corrupt = Encoding.UTF8.GetBytes("library fixturE");
        Write(LibraryPath, corrupt);
        RefreshManifest();
        var result = await ZipInstanceInstaller.PrepareLocalAsync(_root, Instance, resolver: Resolver());
        Assert.False(result.RuntimeReady);
        Assert.Contains(result.RuntimeIssues, issue => issue.Contains("哈希"));
        Assert.Equal(corrupt, File.ReadAllBytes(Full(LibraryPath)));
    }

    /// <summary>显式补全会通过共享下载器补回缺失的固定文件并生成可启动配置。</summary>
    [Fact]
    public async Task InstallerDownloadsMissingFixedDependency()
    {
        File.Delete(Full(LibraryPath));
        var downloader = new FixtureDownloader(_payloads);
        var result = await new ZipInstanceInstaller(_root, downloader, Resolver()).InstallAsync(Instance);
        Assert.True(result.LaunchReady, string.Join('\n', result.Issues));
        Assert.Equal(LibraryPath, Path.GetRelativePath(Instance, Assert.Single(downloader.Requests).DestinationPath).Replace('\\', '/'));
        Assert.True(File.Exists(result.LaunchProfilePath));
    }

    /// <summary>独立的两个叶版本必须明确报错，不能忽略加载器或随机启动其中一个。</summary>
    [Fact]
    public void MultipleIndependentVersionsAreRejected()
    {
        AddProfile("other", "{\"id\":\"other\",\"mainClass\":\"example.Main\"}");
        Assert.Contains("多个独立版本", Assert.Throws<InvalidDataException>(() => Resolve()).Message);
    }

    /// <summary>加载器叶节点继承本体和旧参数，并保留加载器主类及现代追加参数。</summary>
    [Fact]
    public void InheritedLoaderRetainsBaseArgumentsAndOverridesLibraryVersion()
    {
        AddProfile("forge", """
            {"id":"forge","inheritsFrom":"1.7.10","mainClass":"net.minecraft.launchwrapper.Launch",
            "arguments":{"game":["--tweakClass","example.Tweaker"]},
            "libraries":[{"name":"example:library:2.0","url":"https://example.invalid/maven/"}]}
            """);
        var plan = Resolve();
        Assert.Equal("net.minecraft.launchwrapper.Launch", plan.MainClass);
        Assert.Contains("${auth_player_name}", plan.GameArguments);
        Assert.Equal(new[] { "--tweakClass", "example.Tweaker" }, plan.GameArguments.TakeLast(2));
        Assert.Contains(plan.Files, file => file.MavenName == "example:library:2.0");
        Assert.DoesNotContain(plan.Files, file => file.MavenName == "example:library:1.0");
        Assert.Equal(MainPath, Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.MainJar).Requirement.Path);
    }

    /// <summary>继承缺失、循环及越界路径均不能生成不完整的原版替代计划。</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("../outside")]
    [InlineData("self")]
    public void InvalidInheritanceIsRejected(string parent)
    {
        var version = ReadVersion();
        version["inheritsFrom"] = parent == "self" ? "1.7.10" : parent;
        SaveVersion(version);
        Assert.Throws<InvalidDataException>(() => Resolve());
    }

    /// <summary>尚需运行安装处理器的版本明确阻断，避免把原版依赖齐全误判为加载器可运行。</summary>
    [Fact]
    public void PendingProcessorsBlockPreparation()
    {
        var version = ReadVersion();
        version["processors"] = JsonNode.Parse("[{\"jar\":\"installer:processor:1\"}]");
        SaveVersion(version);
        Assert.Contains(Resolve().BlockingIssues, issue => issue.Contains("processors"));
    }

    /// <summary>标准库的官方 SHA-1 与原 ZIP SHA-256 分别保留，不合并为双重强制校验。</summary>
    [Fact]
    public void OfficialLibraryHashesDoNotMergeEmbeddedBaseline()
    {
        var requirement = Assert.Single(Resolve().Files, file => file.Role == PrismRuntimeFileRole.Library).Requirement;
        Assert.Equal(InstanceFileSource.DirectDownload, requirement.Source);
        Assert.True(requirement.Hashes.ContainsKey("sha1"));
        Assert.False(requirement.Hashes.ContainsKey("sha256"));
    }

    /// <summary>已安装的自定义无哈希类库使用原导入记录，不为当前字节重新生成摘要。</summary>
    [Fact]
    public void UnhashedLocalLibraryUsesTrustedImportBaseline()
    {
        var version = ReadVersion();
        version["libraries"] = JsonNode.Parse("[{\"name\":\"example:library:1.0\"}]");
        SaveVersion(version);
        var requirement = Assert.Single(Resolve().Files, file => file.Role == PrismRuntimeFileRole.Library).Requirement;
        var imported = Assert.Single(_manifest.Files, file => file.Path == LibraryPath);
        Assert.Equal(InstanceFileSource.Embedded, requirement.Source);
        Assert.Equal(imported.Hashes["sha256"], requirement.Hashes["sha256"]);
        Write(LibraryPath, Encoding.UTF8.GetBytes("different"));
        Assert.Equal(imported.Hashes["sha256"], Assert.Single(Resolve().Files, file => file.Role == PrismRuntimeFileRole.Library).Requirement.Hashes["sha256"]);
    }

    /// <summary>现代条件参数默认关闭未知功能，平台条件与 JVM 参数边界保持一致。</summary>
    [Fact]
    public void ModernRulesPreserveArgumentBoundariesAndRemoveDuplicateClasspath()
    {
        var version = ReadVersion();
        version.Remove("minecraftArguments");
        version["arguments"] = JsonNode.Parse("""
            {"game":["--username","${auth_player_name}",{"rules":[{"action":"allow","features":{"is_demo_user":true}}],"value":"--demo"}],
            "jvm":["-cp","${classpath}","-Djava.library.path=${natives_directory}",
            {"rules":[{"action":"allow","os":{"name":"windows","arch":"amd64","version":"^10"}}],"value":"-Dkeep=value with spaces"}]}
            """);
        SaveVersion(version);
        var plan = Resolve();
        Assert.DoesNotContain("--demo", plan.GameArguments);
        Assert.Equal(new[] { "-Dkeep=value with spaces" }, plan.JvmArguments);
    }

    /// <summary>为测试固定平台，避免当前电脑架构影响 native classifier。</summary>
    private static StandardMinecraftRuntimeResolver Resolver() => new("windows", "x86_64", runtimeOsVersion: "10.0");
    /// <summary>使用当前测试清单只读解析标准运行计划。</summary>
    private PrismRuntimePlan Resolve() => Resolver().Resolve(Instance, _manifest);
    /// <summary>从固定测试路径读取原版描述用于单字段变更。</summary>
    private JsonObject ReadVersion() => JsonNode.Parse(File.ReadAllBytes(Full(VersionPath)))!.AsObject();
    /// <summary>保存测试版本描述，但不重建导入基线。</summary>
    private void SaveVersion(JsonObject version) => Write(VersionPath, JsonSerializer.SerializeToUtf8Bytes(version));
    /// <summary>添加第二个版本描述，验证继承与歧义处理。</summary>
    private void AddProfile(string id, string json) => Write($".minecraft/versions/{id}/{id}.json", Encoding.UTF8.GetBytes(json));
    /// <summary>生成带固定大小及 SHA-1 的官方下载项，并登记模拟响应。</summary>
    private object Artifact(string file, byte[] bytes)
    {
        var url = "https://example.invalid/" + file;
        _payloads[url] = bytes;
        return new { url, size = bytes.Length, sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes)) };
    }
    /// <summary>为资源和日志下载项附加标准 id。</summary>
    private static JsonObject WithId(object artifact, string id)
    {
        var result = JsonSerializer.SerializeToNode(artifact)!.AsObject();
        result["id"] = id;
        return result;
    }
    /// <summary>建立模拟 ZIP 导入基线，仅用于测试夹具构造。</summary>
    private void RefreshManifest()
    {
        _manifest = _manifest with { Files = Directory.GetFiles(Instance, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != InstanceImportManifest.FileName)
            .Select(path => new InstanceFileRequirement
            {
                Path = Path.GetRelativePath(Instance, path).Replace('\\', '/'), Source = InstanceFileSource.Embedded,
                Size = new FileInfo(path).Length, Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) }
            }).ToArray() };
        Write(InstanceImportManifest.FileName, InstanceManifestStore.Serialize(_manifest));
    }
    /// <summary>将实例相对路径转换为测试目录内的绝对路径。</summary>
    private string Full(string path) => Path.Combine(Instance, path.Replace('/', Path.DirectorySeparatorChar));
    /// <summary>写入独立临时实例的测试文件。</summary>
    private void Write(string path, byte[] bytes)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }
    /// <summary>清理此测试创建的临时目录。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    /// <summary>只返回登记的固定测试内容，禁止测试访问真实网络。</summary>
    private sealed class FixtureDownloader(IReadOnlyDictionary<string, byte[]> payloads) : IFileDownloader
    {
        internal List<FileDownloadRequest> Requests { get; } = [];
        /// <summary>保存固定响应并确认请求携带正确官方摘要。</summary>
        public async Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request,
            IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var bytes = payloads[request.Urls[0]];
            Assert.Equal(request.Hashes["sha1"], Convert.ToHexStringLower(SHA1.HashData(bytes)));
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(request.DestinationPath, bytes, cancellationToken);
            return new(request.DestinationPath, bytes.Length, true);
        }
    }
}
