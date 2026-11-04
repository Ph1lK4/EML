using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Modrinth;

namespace EML.Tests;

/// <summary>以合成官方元数据验证 Modrinth 运行依赖解析，不联网或执行游戏。</summary>
public sealed class ModrinthRuntimeResolverTests : IDisposable
{
    private const string Metadata = ".eml-runtime/modrinth/metadata/";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-modrinth-runtime-" + Guid.NewGuid().ToString("N"));
    private InstanceImportManifest _manifest = new()
    {
        SourceFormat = "Modrinth", Name = "测试包", MinecraftVersion = "26.3",
        Components = [new("minecraft", "26.3")]
    };

    /// <summary>创建独立测试实例和带官方哈希链的最小原版元数据。</summary>
    public ModrinthRuntimeResolverTests()
    {
        Directory.CreateDirectory(_root);
        Minecraft();
    }

    /// <summary>写入测试元数据，创建所需父目录。</summary>
    private void Write(string path, string text)
    {
        var full = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    /// <summary>构建原版配置并更新索引 SHA1，确保测试只在指定字段处发生变化。</summary>
    private void Minecraft(string extra = "", string id = "26.3")
    {
        var json = """
            {"id":"VERSION","mainClass":"net.minecraft.client.main.Main",
            "downloads":{"client":{"url":"https://example.invalid/client.jar","sha1":"1111111111111111111111111111111111111111","size":12}},
            "assetIndex":{"id":"30","url":"https://example.invalid/assets.json","sha1":"2222222222222222222222222222222222222222","size":30},
            "javaVersion":{"majorVersion":25}
            """.Replace("VERSION", id, StringComparison.Ordinal) + (extra.Length == 0 ? "" : "," + extra) + "}";
        Write(Metadata + "minecraft/26.3.json", json);
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(json)));
        Write(Metadata + "minecraft/manifest-v2.json", JsonSerializer.Serialize(new
        {
            latest = new { release = "99.9" },
            versions = new[] { new { id = "26.3", url = "https://piston-meta.mojang.com/v1/packages/test/26.3.json", sha1 } }
        }));
    }

    /// <summary>生成只含所需字段的固定 Fabric 或 Quilt 配置。</summary>
    private void Loader(string id = "fabric-loader", string version = "0.19.5", string inherited = "26.3", string extra = "")
    {
        _manifest = _manifest with { Components = [new("minecraft", "26.3"), new(id, version)] };
        var coordinate = (id == "fabric-loader" ? "net.fabricmc:fabric-loader:" : "org.quiltmc:quilt-loader:") + version;
        var json = JsonSerializer.Serialize(new
        {
            id = id + "-" + version + "-26.3", inheritsFrom = inherited, mainClass = "example.LoaderMain",
            libraries = new[] { new { name = coordinate, url = "https://maven.fabricmc.net/" } }
        });
        if (extra.Length != 0) json = json[..^1] + "," + extra + "}";
        Write(Metadata + id + "/26.3/" + version + ".json", json);
    }

    /// <summary>固定操作系统版本以保证功能和指纹断言可重复。</summary>
    private PrismRuntimePlan Resolve(IReadOnlyDictionary<string, bool>? features = null, string architecture = "x86_64") =>
        new ModrinthRuntimeResolver("windows", architecture, features, "10.0").Resolve(_root, _manifest);

    /// <summary>解析原版主文件、资源及 Java 25，并保持只读行为。</summary>
    [Fact]
    public void ResolvesPinnedMinecraftAndJavaWithoutWritingFiles()
    {
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray();
        var plan = Resolve();
        Assert.Empty(plan.BlockingIssues);
        Assert.Empty(plan.MetadataRequests);
        Assert.Equal("Modrinth", plan.SourceFormat);
        Assert.Equal("EML-Modrinth", plan.VersionIdPrefix);
        Assert.Equal("26.3", plan.MinecraftVersion);
        Assert.Equal(new[] { 25 }, plan.CompatibleJavaMajors);
        Assert.Equal(".minecraft/libraries/com/mojang/minecraft/26.3/minecraft-26.3-client.jar",
            Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.MainJar).Requirement.Path);
        Assert.Equal(before, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    /// <summary>元数据缺失时先请求官方索引，不采用索引中的 latest 版本替换固定版本。</summary>
    [Fact]
    public void MissingIndexRequestsOfficialManifestWithoutWrites()
    {
        File.Delete(Path.Combine(_root, Metadata + "minecraft/manifest-v2.json"));
        var plan = Resolve();
        var request = Assert.Single(plan.MetadataRequests);
        Assert.Equal("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", request.Url);
        Assert.Equal("26.3", plan.MinecraftVersion);
        Assert.NotEmpty(plan.BlockingIssues);
        Assert.False(File.Exists(Path.Combine(_root, request.RelativePath)));
    }

    /// <summary>版本元数据下载请求携带官方索引 SHA1，并保留精确版本。</summary>
    [Fact]
    public void VersionMetadataRequestCarriesHashAndExactPin()
    {
        File.Delete(Path.Combine(_root, Metadata + "minecraft/26.3.json"));
        var request = Assert.Single(Resolve().MetadataRequests);
        Assert.Equal("minecraft", request.Id);
        Assert.Equal("26.3", request.Version);
        Assert.Equal(40, request.RequirementHashes["sha1"].Length);
    }

    /// <summary>官方索引找不到指定版本时明确阻断，不下载另一版本。</summary>
    [Fact]
    public void MissingPinnedVersionDoesNotChooseLatest()
    {
        Write(Metadata + "minecraft/manifest-v2.json", "{\"latest\":{\"release\":\"99.9\"},\"versions\":[]}");
        var plan = Resolve();
        Assert.Empty(plan.MetadataRequests);
        Assert.Contains(plan.BlockingIssues, issue => issue.Contains("26.3") && issue.Contains("最新"));
    }

    /// <summary>缓存内容被改动时，官方哈希链会阻止解析被篡改的版本文件。</summary>
    [Fact]
    public void TamperedVersionMetadataIsRejected()
    {
        File.AppendAllText(Path.Combine(_root, Metadata + "minecraft/26.3.json"), " ");
        var exception = Assert.Throws<InvalidDataException>(() => Resolve());
        Assert.Contains("哈希", exception.Message);
    }

    /// <summary>即使哈希与索引一致，文件内的版本 ID 也必须匹配固定版本。</summary>
    [Fact]
    public void WrongMinecraftIdentityIsRejected()
    {
        Minecraft(id: "99.9");
        Assert.Throws<InvalidDataException>(() => Resolve());
    }

    /// <summary>Fabric 配置合并主类与库，不改变原版版本和 Java 约束。</summary>
    [Fact]
    public void ResolvesFabricPinnedLoaderAndRepositoryLibrary()
    {
        Loader();
        var plan = Resolve();
        Assert.Empty(plan.BlockingIssues);
        Assert.Equal("example.LoaderMain", plan.MainClass);
        var library = Assert.Single(plan.Files, file => file.MavenName == "net.fabricmc:fabric-loader:0.19.5");
        Assert.Equal("https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.19.5/fabric-loader-0.19.5.jar", Assert.Single(library.Requirement.Downloads));
        Assert.Equal(new[] { 25 }, plan.CompatibleJavaMajors);
    }

    /// <summary>未缓存的 Fabric 与 Quilt 请求都包含整合包固定的游戏和加载器版本。</summary>
    [Theory]
    [InlineData("fabric-loader", "https://meta.fabricmc.net/v2/versions/loader/26.3/0.19.5/profile/json")]
    [InlineData("quilt-loader", "https://meta.quiltmc.org/v3/versions/loader/26.3/0.19.5/profile/json")]
    public void LoaderRequestsUseExactPins(string id, string url)
    {
        _manifest = _manifest with { Components = [new("minecraft", "26.3"), new(id, "0.19.5")] };
        Assert.Equal(url, Assert.Single(Resolve().MetadataRequests).Url);
    }

    /// <summary>加载器继承其他游戏版本时不接受看似完整的启动配置。</summary>
    [Fact]
    public void WrongLoaderInheritanceIsRejected()
    {
        Loader(inherited: "1.21.1");
        Assert.Throws<InvalidDataException>(() => Resolve());
    }

    /// <summary>Forge/NeoForge 尚未安装时必须有阻断项，不能显示原版文件齐全即成功。</summary>
    [Theory]
    [InlineData("forge")]
    [InlineData("neoforge")]
    public void UnsupportedProcessorLoadersBlockReadiness(string loader)
    {
        _manifest = _manifest with { Components = [new("minecraft", "26.3"), new(loader, "1.0")] };
        Assert.Contains(Resolve().BlockingIssues, issue => issue.Contains(loader) && issue.Contains("处理器"));
    }

    /// <summary>现代可选功能默认关闭；显式开关控制参数，且参数数组边界不会被拼接丢失。</summary>
    [Fact]
    public void FeatureRulesDefaultFalseAndHonorExplicitValues()
    {
        Minecraft("""
            "arguments":{"game":["--base",{"rules":[{"action":"allow","features":{"has_custom_resolution":true}}],"value":["--width","1234"]},
            {"rules":[{"action":"allow","features":{"is_demo_user":false}}],"value":"--not-demo"}],
            "jvm":["-cp","${classpath}","-Djava.library.path=${natives_directory}","-Dkeep=value with spaces"]}
            """);
        var plan = Resolve();
        Assert.Equal(new[] { "--base", "--not-demo" }, plan.GameArguments);
        Assert.Equal(new[] { "-Dkeep=value with spaces" }, plan.JvmArguments);
        var explicitPlan = Resolve(new Dictionary<string, bool> { ["has_custom_resolution"] = true });
        Assert.Equal(new[] { "--base", "--width", "1234", "--not-demo" }, explicitPlan.GameArguments);
        Assert.NotEqual(plan.Fingerprint, explicitPlan.Fingerprint);
    }

    /// <summary>26.3 的分目录本机库参数和 Fabric 含空格的单个参数必须完整保留。</summary>
    [Fact]
    public void PreservesMinecraft263NativeFoldersAndFabricArgumentBoundaries()
    {
        Minecraft("""
            "arguments":{"jvm":["-Djava.library.path=${natives_directory}/java","-Djna.tmpdir=${natives_directory}/jna",
            "-Dorg.lwjgl.system.SharedLibraryExtractPath=${natives_directory}/lwjgl","-Dio.netty.native.workdir=${natives_directory}/netty"],
            "default-user-jvm":[{"value":["-Xms2G","-Xmx4G"]}]}
            """);
        Loader(extra: "\"arguments\":{\"jvm\":[\"-DFabricMcEmu= net.minecraft.client.main.Main \"]}");
        var plan = Resolve();
        Assert.Contains("-Djava.library.path=${natives_directory}/java", plan.JvmArguments);
        Assert.Contains("-Djna.tmpdir=${natives_directory}/jna", plan.JvmArguments);
        Assert.Contains("-DFabricMcEmu= net.minecraft.client.main.Main ", plan.JvmArguments);
        Assert.DoesNotContain("-Xmx4G", plan.JvmArguments);
        Assert.Contains(plan.Notes, note => note.Contains("default-user-jvm"));
        Assert.Empty(plan.BlockingIssues);
    }

    /// <summary>平台规则按操作系统、架构和版本匹配，保留最后一个命中动作。</summary>
    [Fact]
    public void AppliesOperatingSystemArchitectureAndVersionRules()
    {
        Minecraft("""
            "libraries":[
              {"name":"test:win:1","url":"https://example.invalid/","rules":[{"action":"allow","os":{"name":"windows","arch":"amd64","version":"^10\\."}}]},
              {"name":"test:linux:1","url":"https://example.invalid/","rules":[{"action":"allow","os":{"name":"linux"}}]},
              {"name":"test:blocked:1","url":"https://example.invalid/","rules":[{"action":"allow"},{"action":"disallow","os":{"name":"windows"}}]}
            ]
            """);
        var plan = Resolve();
        Assert.Equal("test:win:1", Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.Library).MavenName);
        Assert.DoesNotContain(Resolve(architecture: "arm64").Files, file => file.Role == PrismRuntimeFileRole.Library);
    }

    /// <summary>同一库同时包含 artifact 和旧式 native 时两个下载项都必须保留。</summary>
    [Fact]
    public void PreservesBothArtifactAndNativeClassifier()
    {
        Minecraft("""
            "libraries":[{"name":"test:lwjgl:2.9.1","downloads":{
              "artifact":{"url":"https://example.invalid/lwjgl.jar"},
              "classifiers":{"natives-windows":{"url":"https://example.invalid/lwjgl-native.jar"}}},
              "natives":{"windows":"natives-windows"},"extract":{"exclude":["META-INF/"]}}]
            """);
        var plan = Resolve();
        Assert.True(Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.Library).OnClassPath);
        var native = Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.NativeArchive);
        Assert.False(native.OnClassPath);
        Assert.Equal(new[] { "META-INF/" }, native.ExtractExcludes);
        Assert.Equal("test:lwjgl:2.9.1:natives-windows", native.MavenName);
    }

    /// <summary>旧式本机库不能把 Windows x86/x64 文件误用为 ARM64 兼容文件。</summary>
    [Fact]
    public void LegacyNativeWithoutArmMappingBlocksArmReadiness()
    {
        Minecraft("""
            "libraries":[{"name":"test:old:1","downloads":{"classifiers":{"natives-windows":{"url":"https://example.invalid/native.jar"}}},
            "natives":{"windows":"natives-windows"}}]
            """);
        var plan = Resolve(architecture: "arm64");
        Assert.Contains(plan.BlockingIssues, issue => issue.Contains("当前架构"));
        Assert.DoesNotContain(plan.Files, file => file.Role == PrismRuntimeFileRole.NativeArchive);
    }

    /// <summary>下载字段不完整的类库不能被静默跳过并错误标记准备完成。</summary>
    [Fact]
    public void LibraryWithoutArtifactOrNativesIsRejected()
    {
        Minecraft("\"libraries\":[{\"name\":\"test:missing:1\",\"downloads\":{}}]");
        Assert.Throws<InvalidDataException>(() => Resolve());
    }

    /// <summary>后续同版本库没有哈希时仍保留原版已知校验依据，不能降级为未校验文件。</summary>
    [Fact]
    public void RepeatedLibraryRetainsPreviouslyKnownHashAndSize()
    {
        Minecraft("""
            "libraries":[
            {"name":"test:same:1","downloads":{"artifact":{"url":"https://example.invalid/first.jar","sha1":"4444444444444444444444444444444444444444","size":42}}},
            {"name":"test:same:1","url":"https://example.invalid/"}]
            """);
        var library = Assert.Single(Resolve().Files, file => file.Role == PrismRuntimeFileRole.Library);
        Assert.Equal("4444444444444444444444444444444444444444", library.Requirement.Hashes["sha1"]);
        Assert.Equal(42, library.Requirement.Size);
    }

    /// <summary>现代 LWJGL native 的普通 artifact 保留在 classpath，不误当成旧式解压库。</summary>
    [Fact]
    public void ModernNativeArtifactStaysOnClasspath()
    {
        Minecraft("""
            "libraries":[{"name":"org.lwjgl:lwjgl:3.3.3:natives-windows","downloads":{"artifact":{"url":"https://example.invalid/native.jar"}}}]
            """);
        var library = Assert.Single(Resolve().Files, file => file.Role == PrismRuntimeFileRole.Library);
        Assert.True(library.OnClassPath);
        Assert.Contains("natives-windows", library.Requirement.Path);
    }

    /// <summary>日志 XML 与日志参数一起进入计划，避免静默丢失现代日志配置。</summary>
    [Fact]
    public void IncludesLogConfigurationAndArgumentTemplate()
    {
        Minecraft("""
            "logging":{"client":{"type":"log4j2-xml","argument":"-Dlog4j.configurationFile=${path}","file":{"id":"client.xml","url":"https://example.invalid/client.xml","sha1":"3333333333333333333333333333333333333333"}}}
            """);
        var plan = Resolve();
        Assert.Equal("-Dlog4j.configurationFile=${path}", plan.LoggingArgument);
        Assert.Equal(".minecraft/assets/log_configs/client.xml", Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.Logging).Requirement.Path);
        Assert.Empty(plan.BlockingIssues);
    }

    /// <summary>Maven 坐标或版本中的越界路径不能进入文件计划。</summary>
    [Fact]
    public void RejectsTraversalInLibraryCoordinates()
    {
        Minecraft("\"libraries\":[{\"name\":\"test:evil:../outside\",\"url\":\"https://example.invalid/\"}]");
        Assert.Throws<InvalidDataException>(() => Resolve());
    }

    /// <summary>指定实例根目录为游戏目录时，资源路径不会额外添加 .minecraft。</summary>
    [Fact]
    public void SupportsGameDirectoryAtInstanceRoot()
    {
        _manifest = _manifest with { GameDirectory = "." };
        Assert.Equal("assets/indexes/30.json", Assert.Single(Resolve().Files, file => file.Role == PrismRuntimeFileRole.AssetIndex).Requirement.Path);
    }

    /// <summary>只清理当前测试创建的临时实例。</summary>
    public void Dispose() => Directory.Delete(_root, true);
}
