using System.Text;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Prism;

namespace EML.Tests;

/// <summary>用最小 Prism 补丁验证运行计划，不联网、不下载也不修改真实实例。</summary>
public sealed class PrismRuntimeResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-prism-resolver-" + Guid.NewGuid().ToString("N"));
    private readonly InstanceImportManifest _manifest = new() { SourceFormat = "Prism", Name = "测试包" };

    /// <summary>创建带 Minecraft 主文件与资源索引声明的最小实例。</summary>
    public PrismRuntimeResolverTests()
    {
        Directory.CreateDirectory(_root);
        Pack(("net.minecraft", "1.7.10"));
        Patch("net.minecraft", "1.7.10", """
            "mainClass":"net.minecraft.client.main.Main",
            "minecraftArguments":"--username ${auth_player_name}",
            "mainJar":{"name":"com.mojang:minecraft:1.7.10:client","downloads":{"artifact":{"url":"https://example.invalid/client.jar","sha1":"0000000000000000000000000000000000000000","size":12}}},
            "assetIndex":{"id":"1.7.10","url":"https://example.invalid/assets.json","sha1":"1111111111111111111111111111111111111111","size":30}
            """);
    }

    /// <summary>写入测试专用文件，自动准备父目录。</summary>
    private void Write(string path, string content)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }

    /// <summary>保存明确顺序的组件列表；null 版本表示必须从本地补丁读取。</summary>
    private void Pack(params (string Id, string? Version)[] components) => Write("mmc-pack.json", JsonSerializer.Serialize(new
    {
        formatVersion = 1,
        components = components.Select(component => new { uid = component.Id, version = component.Version })
    }));

    /// <summary>生成只含所需字段的组件补丁。</summary>
    private void Patch(string id, string version, string extra) =>
        Write("patches/" + id + ".json", "{\"formatVersion\":1,\"uid\":" + JsonSerializer.Serialize(id) +
            ",\"version\":" + JsonSerializer.Serialize(version) + (extra.Length == 0 ? "" : "," + extra) + "}");

    /// <summary>按 Windows x64 平台解析，避免测试结果依赖执行机器。</summary>
    private PrismRuntimePlan Resolve() => new PrismRuntimeResolver("windows", "x86_64").Resolve(_root, _manifest);

    /// <summary>最小实例解析出 Maven 位置、客户端主文件和资源索引，不写任何安装产物。</summary>
    [Fact]
    public void ResolvesBaseClientAndAssetsWithoutWrites()
    {
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray();
        var plan = Resolve();
        Assert.Empty(plan.BlockingIssues);
        Assert.Empty(plan.MetadataRequests);
        Assert.Equal("1.7.10", plan.MinecraftVersion);
        Assert.Equal(".minecraft/libraries/com/mojang/minecraft/1.7.10/minecraft-1.7.10-client.jar",
            Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.MainJar).Requirement.Path);
        Assert.Equal(".minecraft/assets/indexes/1.7.10.json",
            Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.AssetIndex).Requirement.Path);
        Assert.Equal(before, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    /// <summary>代表 GTNH 的五个补丁按列表顺序合并，保留早期本地库及最终自定义启动主类。</summary>
    [Fact]
    public void MergesGtnhStyleFiveComponentsAndLocalBootstrap()
    {
        Pack(("org.lwjgl3", "3.4.2"), ("net.minecraft", "1.7.10"), ("custom.bootstrap", null),
            ("net.minecraftforge", "10.13.4.1614"), ("custom.launchargs", null));
        Patch("org.lwjgl3", "3.4.2", """
            "libraries":[{"name":"org.lwjgl:lwjgl:3.4.2","downloads":{"artifact":{"url":"https://example.invalid/lwjgl.jar","sha1":"2222222222222222222222222222222222222222"}}}]
            """);
        Patch("custom.bootstrap", "3.0.23", """
            "order":100,"+jvmArgs":["-Djava.system.class.loader=example.Loader","--add-opens","java.base/java.lang=ALL-UNNAMED"],
            "libraries":[{"name":"com.github.GTNewHorizons:lwjgl3ify:3.0.23:forgePatches","MMC-hint":"local"}]
            """);
        Write("libraries/lwjgl3ify-3.0.23-forgePatches.jar", "bootstrap");
        Patch("net.minecraftforge", "10.13.4.1614", """
            "mainClass":"example.Bootstrap","+tweakers":["cpw.mods.fml.common.launcher.FMLTweaker"],
            "libraries":[{"name":"net.minecraftforge:forge:1.7.10:universal","downloads":{"artifact":{"url":"https://example.invalid/forge.jar"}}}]
            """);
        Patch("custom.launchargs", "3.0.23", """
            "order":-100,"mainClass":"example.MainStartOnFirstThread","+traits":["FirstThreadOnMacOS"],"compatibleJavaMajors":[17,21,25]
            """);
        var plan = Resolve();
        Assert.Empty(plan.BlockingIssues);
        Assert.Equal("example.MainStartOnFirstThread", plan.MainClass);
        Assert.Contains("--add-opens", plan.JvmArguments);
        Assert.Contains("FirstThreadOnMacOS", plan.Traits);
        Assert.Equal(new[] { 17, 21, 25 }, plan.CompatibleJavaMajors);
        var classpath = plan.Files.Where(file => file.Role == PrismRuntimeFileRole.Library && file.OnClassPath).ToArray();
        Assert.Contains("lwjgl3ify", classpath[1].MavenName);
        Assert.Contains("forge:", classpath[2].MavenName);
        Assert.Equal(InstanceFileSource.Embedded, classpath[1].Requirement.Source);
        Assert.Empty(classpath[1].Requirement.Downloads);
    }

    /// <summary>重复 Maven 库保留数字版本较高者，维持首次出现的位置。</summary>
    [Fact]
    public void DeduplicatesByHighestVersionWithoutMovingClasspathPosition()
    {
        Pack(("net.minecraft", "1.7.10"), ("extra", "1"));
        Patch("extra", "1", """
            "libraries":[
              {"name":"com.google.guava:guava:15.0","downloads":{"artifact":{"url":"https://example.invalid/15.jar"}}},
              {"name":"example:other:1","downloads":{"artifact":{"url":"https://example.invalid/other.jar"}}},
              {"name":"com.google.guava:guava:17.0","downloads":{"artifact":{"url":"https://example.invalid/17.jar"}}},
              {"name":"com.google.guava:guava:2.9","downloads":{"artifact":{"url":"https://example.invalid/2.jar"}}}
            ]
            """);
        var libraries = Resolve().Files.Where(file => file.Role == PrismRuntimeFileRole.Library).ToArray();
        Assert.Equal(2, libraries.Length);
        Assert.Equal("com.google.guava:guava:17.0", libraries[0].MavenName);
        Assert.Equal("example:other:1", libraries[1].MavenName);
    }

    /// <summary>Forge 的空 SHA1 和 size=0 表示未提供校验依据，不能要求下载零字节。</summary>
    [Fact]
    public void NormalizesLegacyUnknownSizeAndHash()
    {
        Pack(("net.minecraft", "1.7.10"), ("extra", "1"));
        Patch("extra", "1", """
            "libraries":[{"name":"lzma:lzma:0.0.1","downloads":{"artifact":{"url":"https://example.invalid/lzma.jar","sha1":"","size":0}}}]
            """);
        var plan = Resolve();
        var file = Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.Library);
        Assert.Null(file.Requirement.Size);
        Assert.Empty(file.Requirement.Hashes);
        Assert.Empty(plan.BlockingIssues);
        Assert.Contains(plan.Notes, note => note.Contains("来源没有提供内容哈希"));
    }

    /// <summary>LWJGL3 的 native artifact 保留在 classpath，只有传统 natives 字段触发解压角色。</summary>
    [Fact]
    public void DistinguishesClasspathNativeArtifactsFromLegacyNativeArchives()
    {
        Pack(("net.minecraft", "1.7.10"), ("extra", "1"));
        Patch("extra", "1", """
            "libraries":[
              {"name":"org.lwjgl:lwjgl-natives-windows:3.4.2","rules":[{"action":"allow","os":{"name":"windows"}}],"downloads":{"artifact":{"url":"https://example.invalid/lwjgl-native.jar"}}},
              {"name":"org.lwjgl:lwjgl-natives-linux:3.4.2","rules":[{"action":"allow","os":{"name":"linux"}}],"downloads":{"artifact":{"url":"https://example.invalid/linux.jar"}}},
              {"name":"tv.twitch:twitch-platform:5.16","natives":{"windows":"natives-windows-${arch}"},"extract":{"exclude":["META-INF/"]},"downloads":{"classifiers":{"natives-windows-64":{"url":"https://example.invalid/twitch.jar"}}}}
            ]
            """);
        var plan = Resolve();
        Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.Library && file.OnClassPath);
        var native = Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.NativeArchive);
        Assert.False(native.OnClassPath);
        Assert.Contains("natives-windows-64", native.Requirement.Path);
        Assert.Contains("META-INF/", native.ExtractExcludes);
        Assert.DoesNotContain(plan.Files, file => file.Requirement.Downloads.Contains("https://example.invalid/linux.jar"));
    }

    /// <summary>ARM64 不应错误匹配只适用传统 x86 架构的 windows 简写规则。</summary>
    [Fact]
    public void UsesPrismExactArchitectureRulesOnArm()
    {
        Pack(("net.minecraft", "1.7.10"), ("extra", "1"));
        Patch("extra", "1", """
            "libraries":[
              {"name":"example:legacy:1","rules":[{"action":"allow","os":{"name":"windows"}}],"downloads":{"artifact":{"url":"https://example.invalid/legacy.jar"}}},
              {"name":"example:arm:1","rules":[{"action":"allow","os":{"name":"windows-arm64"}}],"downloads":{"artifact":{"url":"https://example.invalid/arm.jar"}}}
            ]
            """);
        var plan = new PrismRuntimeResolver("windows", "arm64").Resolve(_root, _manifest);
        Assert.Equal("example:arm:1", Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.Library).MavenName);
    }

    /// <summary>元数据缺失只请求明确版本，不能请求 latest 或使用缓存展示版本猜测。</summary>
    [Fact]
    public void RequestsExactMissingMetadata()
    {
        Pack(("net.minecraft", "1.7.10"), ("net.fabricmc.fabric-loader", "0.16.10"));
        var request = Assert.Single(Resolve().MetadataRequests);
        Assert.Equal("https://meta.prismlauncher.org/v1/net.fabricmc.fabric-loader/0.16.10.json", request.Url);
        Assert.Equal(".eml-runtime/metadata/net.fabricmc.fabric-loader/0.16.10.json", request.RelativePath);
    }

    /// <summary>没有明确版本的自定义组件缺补丁时阻断，不把 cachedVersion 当作可下载证明。</summary>
    [Fact]
    public void DoesNotUseCachedDisplayVersionForMissingCustomPatch()
    {
        Write("mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"custom.missing","cachedVersion":"9.9"}]}""");
        var plan = Resolve();
        Assert.Empty(plan.MetadataRequests);
        Assert.Contains(plan.BlockingIssues, issue => issue.Contains("cachedVersion"));
    }

    /// <summary>本地自定义补丁优先于同版本官方缓存，防止 GTNH 补丁被普通版本替换。</summary>
    [Fact]
    public void LocalPatchTakesPrecedenceOverOfficialCache()
    {
        Pack(("net.minecraft", "1.7.10"), ("custom", "1"));
        Patch("custom", "1", "\"mainClass\":\"local.Main\"");
        Write(".eml-runtime/metadata/custom/1.json", """{"uid":"custom","version":"1","mainClass":"remote.Main"}""");
        Assert.Equal("local.Main", Resolve().MainClass);
    }

    /// <summary>缺少本地补丁时可以使用 UID 与版本一致的官方元数据缓存。</summary>
    [Fact]
    public void UsesVerifiedIdentityOfCachedMetadata()
    {
        Pack(("net.minecraft", "1.7.10"), ("custom", "1"));
        Write(".eml-runtime/metadata/custom/1.json", """{"uid":"custom","version":"1","mainClass":"cached.Main"}""");
        var plan = Resolve();
        Assert.Equal("cached.Main", plan.MainClass);
        Assert.Empty(plan.MetadataRequests);
        Write(".eml-runtime/metadata/custom/1.json", """{"uid":"another","version":"1"}""");
        Assert.Throws<InvalidDataException>(() => Resolve());
    }

    /// <summary>组件版本约束冲突和启用组件冲突必须形成阻断说明。</summary>
    [Fact]
    public void ReportsRequiresAndConflicts()
    {
        Pack(("net.minecraft", "1.7.10"), ("extra", "1"));
        Patch("extra", "1", """
            "requires":[{"uid":"net.minecraft","equals":"1.12.2"}],"conflicts":[{"uid":"net.minecraft"}]
            """);
        var issues = Resolve().BlockingIssues;
        Assert.Contains(issues, issue => issue.Contains("1.12.2"));
        Assert.Contains(issues, issue => issue.Contains("冲突"));
    }

    /// <summary>缺少的建议依赖使用声明的固定建议版本，已有组件选择不被替换。</summary>
    [Fact]
    public void ResolvesMissingSuggestedDependencyWithoutLatestGuess()
    {
        Pack(("net.minecraft", "1.7.10"), ("extra", "1"));
        Patch("extra", "1", "\"requires\":[{\"uid\":\"dependency\",\"suggests\":\"2.3\"}]");
        var request = Assert.Single(Resolve().MetadataRequests);
        Assert.Equal("dependency", request.Id);
        Assert.Equal("2.3", request.Version);
    }

    /// <summary>缺少本地运行库必须说明从原包恢复，不能生成猜测下载地址。</summary>
    [Fact]
    public void MissingLocalLibraryRemainsLocalAndBlocking()
    {
        Pack(("net.minecraft", "1.7.10"), ("extra", "1"));
        Patch("extra", "1", "\"libraries\":[{\"name\":\"example:local:1\",\"MMC-hint\":\"local\"}]");
        var plan = Resolve();
        var file = Assert.Single(plan.Files, file => file.Role == PrismRuntimeFileRole.Library);
        Assert.Equal("libraries/local-1.jar", file.Requirement.Path);
        Assert.Empty(file.Requirement.Downloads);
        Assert.Contains(plan.BlockingIssues, issue => issue.Contains("本地运行库缺失"));
    }

    /// <summary>元数据、平台和游戏目录变化会改变指纹，未变化的重复解析保持稳定。</summary>
    [Fact]
    public void FingerprintTracksMetadataAndPlatform()
    {
        var first = Resolve().Fingerprint;
        Assert.Equal(first, Resolve().Fingerprint);
        Assert.NotEqual(first, new PrismRuntimeResolver("windows", "arm64").Resolve(_root, _manifest).Fingerprint);
        var patch = Path.Combine(_root, "patches/net.minecraft.json");
        File.AppendAllText(patch, " ");
        Assert.NotEqual(first, Resolve().Fingerprint);
    }

    /// <summary>不支持的安装处理器、补丁删除语义或日志参数不能被悄悄忽略。</summary>
    [Theory]
    [InlineData("\"processors\":[{}]")]
    [InlineData("\"-libraries\":[\"example:library\"]")]
    [InlineData("\"logging\":{\"client\":{}}")]
    public void UnsupportedInstructionsBlockReadiness(string unsupported)
    {
        Pack(("net.minecraft", "1.7.10"), ("extra", "1"));
        Patch("extra", "1", unsupported);
        Assert.NotEmpty(Resolve().BlockingIssues);
    }

    /// <summary>现代参数保留参数边界，标准 classpath 和 natives 占位交给生成器统一提供。</summary>
    [Fact]
    public void PreservesModernArgumentsAndNormalizesBaseJvmFields()
    {
        Pack(("net.minecraft", "1.7.10"), ("extra", "1"));
        Patch("extra", "1", """
            "arguments":{"game":["--demo",{"rules":[{"action":"allow","os":{"name":"windows"}}],"value":["--custom","value with spaces"]}],
            "jvm":["-Djava.library.path=${natives_directory}","-cp","${classpath}","-Dtest=true"]}
            """);
        var plan = Resolve();
        Assert.Equal(new[] { "--demo", "--custom", "value with spaces" }, plan.GameArguments);
        Assert.Equal(new[] { "-Dtest=true" }, plan.JvmArguments);
    }

    /// <summary>不安全 UID、Maven 路径或 URL 一律拒绝，不能进入下载计划。</summary>
    [Theory]
    [InlineData("../escape", "1")]
    [InlineData("valid", "../2")]
    public void RejectsUnsafeComponentIdentity(string id, string version)
    {
        Pack((id, version));
        Assert.Throws<InvalidDataException>(() => Resolve());
    }

    /// <summary>组件声明与本地版本不一致时明确拒绝，不自动升级或降级。</summary>
    [Fact]
    public void RejectsMismatchedPatchVersion()
    {
        Pack(("net.minecraft", "1.7.10"), ("extra", "1"));
        Patch("extra", "2", "");
        Assert.Throws<InvalidDataException>(() => Resolve());
    }

    /// <summary>无法安全定位的 Maven 名称和非 HTTPS 下载地址在解析阶段被拒绝。</summary>
    [Theory]
    [InlineData("../escape:lib:1", "https://example.invalid/file.jar")]
    [InlineData("example:lib:1", "http://example.invalid/file.jar")]
    [InlineData("example:lib:1", "https://user:pass@example.invalid/file.jar")]
    public void RejectsUnsafeArtifactSource(string name, string url)
    {
        Pack(("net.minecraft", "1.7.10"), ("extra", "1"));
        Patch("extra", "1", "\"libraries\":[{\"name\":" + JsonSerializer.Serialize(name) +
            ",\"downloads\":{\"artifact\":{\"url\":" + JsonSerializer.Serialize(url) + "}}}]");
        Assert.Throws<InvalidDataException>(() => Resolve());
    }

    /// <summary>requires 不能自动重新启用用户已禁用的组件。</summary>
    [Fact]
    public void RequiredDisabledComponentIsBlocking()
    {
        Write("mmc-pack.json", """
            {"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"},
            {"uid":"extra","version":"1"},{"uid":"dependency","version":"2","disabled":true}]}
            """);
        Patch("extra", "1", "\"requires\":[{\"uid\":\"dependency\",\"equals\":\"2\"}]");
        var plan = Resolve();
        Assert.Empty(plan.MetadataRequests);
        Assert.Contains(plan.BlockingIssues, issue => issue.Contains("已禁用"));
    }

    /// <summary>存在功能开关规则的参数不能在不知道启动状态时默默跳过。</summary>
    [Fact]
    public void UnknownFeatureRulesBlockReadiness()
    {
        Pack(("net.minecraft", "1.7.10"), ("extra", "1"));
        Patch("extra", "1", """
            "arguments":{"game":[{"rules":[{"action":"allow","features":{"is_demo_user":true}}],"value":"--demo"}]}
            """);
        Assert.Contains(Resolve().BlockingIssues, issue => issue.Contains("features"));
    }

    /// <summary>只清理当前测试独占的临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
