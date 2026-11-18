using System.Text.Json;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.External;
using EML.Infrastructure.Minecraft.Standard;

namespace EML.Tests;

/// <summary>使用真实格式的隔离目录，联测发现、适配、复制和标准运行解析，不访问真实账号或网络。</summary>
public sealed class ExternalInstanceDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-external-discovery-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "eml");

    /// <summary>创建独立源和目标，防止源目标重叠保护掩盖适配器问题。</summary>
    public ExternalInstanceDiscoveryTests() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Target); }

    /// <summary>真实 Prism 组件和名称被保留，现有模组不会改成下载清单，本机钩子不被带入。</summary>
    [Theory]
    [InlineData(".minecraft")]
    [InlineData("minecraft")]
    public async Task PrismCopiesRealContentAndSafeMetadata(string game)
    {
        Write("instances/GTNH/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"},{"uid":"net.minecraftforge","version":"10.13.4.1614"}]}""");
        Write("instances/GTNH/instance.cfg", "[General]\nname=GTNH 中文，本地\nJavaPath=C:\\private\\java.exe\nPreLaunchCommand=private-hook");
        Write("instances/GTNH/" + game + "/mods/local.jar", "existing mod");
        Write("instances/GTNH/" + game + "/saves/世界/level.dat", "player world");
        var credentials = Write("instances/GTNH/" + game + "/accounts.json", "secret sentinel");
        using var denied = new FileStream(credentials, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var discovered = await Discover();
        Assert.Empty(discovered.Warnings);
        var candidate = Assert.Single(discovered.Instances);
        Assert.Equal("GTNH 中文，本地", candidate.Name);
        Assert.Equal("1.7.10", candidate.MinecraftVersion);
        Assert.Contains("net.minecraftforge", candidate.Loader);
        Assert.Equal(Path.Combine(Source, "instances", "GTNH", game), candidate.GameDirectory);
        var imported = await new MinecraftFolderImporter(Target).ImportAsync(candidate, "GTNH 副本");
        Assert.Equal("existing mod", File.ReadAllText(Path.Combine(imported.GameDirectory, "mods", "local.jar")));
        Assert.Equal("player world", File.ReadAllText(Path.Combine(imported.GameDirectory, "saves", "世界", "level.dat")));
        Assert.False(File.Exists(Path.Combine(imported.GameDirectory, "accounts.json")));
        var cfg = File.ReadAllText(Path.Combine(imported.InstancePath, "instance.cfg"));
        Assert.Contains("name=GTNH 副本", cfg);
        Assert.DoesNotContain("private", cfg);
        Assert.All(imported.Manifest.Files, file => Assert.Equal(InstanceFileOrigin.LocalFolderImport, file.Origin));
        Assert.False(Assert.Single(imported.Manifest.Files, file => file.Path!.EndsWith("level.dat")).Required);
        var located = await new LauncherRootMinecraftInstanceLocator(Target).FindAllAsync();
        Assert.Contains(located, instance => instance.InstancePath == imported.InstancePath);
    }

    /// <summary>不同启动器只转换公开版本字段；已经下载的模组照原样复制。</summary>
    [Theory]
    [InlineData("profile.json", "{\"name\":\"中文包\",\"game_version\":\"1.20.1\",\"loader\":\"fabric\",\"loader_version\":{\"id\":\"0.16.10\"}}", "Modrinth App", "net.fabricmc.fabric-loader", "0.16.10")]
    [InlineData("profile.json", "{\"metadata\":{\"name\":\"中文包\",\"game_version\":\"1.20.1\",\"loader\":\"forge\",\"loader_version\":\"47.4.0\"}}", "Modrinth App", "net.minecraftforge", "47.4.0")]
    [InlineData("minecraftinstance.json", "{\"name\":\"中文包\",\"gameVersion\":\"1.20.1\",\"baseModLoader\":{\"name\":\"forge-47.4.0\",\"forgeVersion\":\"47.4.0\"}}", "CurseForge", "net.minecraftforge", "47.4.0")]
    [InlineData("minecraftinstance.json", "{\"name\":\"中文包\",\"gameVersion\":\"1.20.1\",\"baseModLoader\":{\"name\":\"fabric-0.16.10-1.20.1\"}}", "CurseForge", "net.fabricmc.fabric-loader", "0.16.10")]
    [InlineData("instance.json", "{\"id\":\"1.20.1\",\"launcher\":{\"name\":\"中文包\",\"loaderVersion\":{\"type\":\"Forge\",\"rawVersion\":\"47.4.0\",\"version\":\"1.20.1-47.4.0\"}}}", "ATLauncher", "net.minecraftforge", "47.4.0")]
    [InlineData("config.json", "{\"name\":\"中文包\",\"loader\":{\"mcVersion\":\"1.20.1\",\"loaderType\":\"fabric\",\"loaderVersion\":\"0.16.10\"}}", "GDLauncher", "net.fabricmc.fabric-loader", "0.16.10")]
    public async Task InstalledProfilesConvertExactComponents(string file, string json, string platform, string uid, string version)
    {
        Write("instances/test/" + file, json);
        Write("instances/test/mods/installed.jar", "local modified mod");
        Write("instances/test/options.txt", "lang:zh_cn");
        var discovered = await Discover();
        Assert.Empty(discovered.Warnings);
        var candidate = Assert.Single(discovered.Instances);
        Assert.Equal(platform, candidate.Launcher);
        Assert.Equal("中文包", candidate.Name);
        var imported = await new MinecraftFolderImporter(Target).ImportAsync(candidate, "copy");
        Assert.Equal("Prism", imported.Manifest.SourceFormat);
        Assert.Contains(new InstanceComponentRequirement(uid, version), imported.Manifest.Components);
        Assert.Equal("local modified mod", File.ReadAllText(Path.Combine(imported.GameDirectory, "mods", "installed.jar")));
        Assert.False(File.Exists(Path.Combine(imported.GameDirectory, file)));
        Assert.All(imported.Manifest.Files, item => Assert.Empty(item.Downloads));
    }

    /// <summary>明确原版不应伪造加载器；未知加载器不能降级成原版悄悄导入。</summary>
    [Theory]
    [InlineData("vanilla", 1)]
    [InlineData("unknown-loader", 0)]
    [InlineData("fabric", 0)]
    public async Task UnknownOrUnpinnedLoadersAreRejected(string loader, int count)
    {
        Write("profiles/pack/profile.json", JsonSerializer.Serialize(new { game_version = "1.20.1", loader }));
        var result = await Discover();
        Assert.Equal(count, result.Instances.Count);
        Assert.Equal(count == 0, result.Warnings.Count > 0);
    }

    /// <summary>中文版本名称合法；只带入所选继承链，运行解析仍选择对应的加载器主类。</summary>
    [Theory]
    [InlineData("isolated", "isolated data")]
    [InlineData("shared", "shared data")]
    public async Task StandardVersionKeepsOneInheritanceChainAndExplicitGameDirectory(string mode, string content)
    {
        Write(".minecraft/versions/1.20.1/1.20.1.json", """{"id":"1.20.1","mainClass":"net.minecraft.client.main.Main","assets":"test","libraries":[]}""");
        Write(".minecraft/versions/1.20.1/1.20.1.jar", "client");
        Write(".minecraft/versions/中文整合包（测试）/中文整合包（测试）.json", """{"id":"中文整合包（测试）","inheritsFrom":"1.20.1","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.10"}]}""");
        Write(".minecraft/versions/中文整合包（测试）/mods/example.jar", "isolated data");
        Write(".minecraft/mods/example.jar", "shared data");
        Write(".minecraft/assets/indexes/test.json", "{\"objects\":{}}");
        Write(".minecraft/libraries/net/fabricmc/fabric-loader/0.16.10/fabric-loader-0.16.10.jar", "loader bytes");
        Write(".minecraft/versions/1.19.2/1.19.2.json", """{"id":"1.19.2","mainClass":"net.minecraft.client.main.Main","libraries":[]}""");
        var found = await Discover();
        Assert.Empty(found.Warnings);
        var candidate = Assert.Single(found.Instances, item => item.SelectionId == "standard:中文整合包（测试）:" + mode);
        Assert.Equal("1.20.1", candidate.MinecraftVersion);
        Assert.Equal("Fabric", candidate.Loader);
        var copied = await new MinecraftFolderImporter(Target).ImportAsync(candidate, "selected");
        Assert.Equal(content, File.ReadAllText(Path.Combine(copied.GameDirectory, "mods", "example.jar")));
        Assert.False(Directory.Exists(Path.Combine(copied.GameDirectory, "versions", "1.19.2")));
        var runtime = new StandardMinecraftRuntimeResolver().Resolve(copied.InstancePath, copied.Manifest);
        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", runtime.MainClass);
        Assert.Equal("1.20.1", runtime.MinecraftVersion);
    }

    /// <summary>一个实例坏掉不影响其他实例，重复自动位置不产生重复候选。</summary>
    [Fact]
    public async Task DiscoveryDeduplicatesAndContinuesPastBrokenInstances()
    {
        Write("instances/broken/mmc-pack.json", "not json");
        Write("instances/good/profile.json", """{"name":"good","game_version":"1.21.1","loader":"vanilla"}""");
        var result = await new ExternalInstanceDiscovery([Source, Source]).DiscoverAsync();
        Assert.Single(result.Instances);
        Assert.Contains(result.Warnings, warning => warning.Contains("broken"));
    }

    /// <summary>Prism 中附带合法 EML 来源清单的实例仍可发现，名称与版本来自可验证的实例元数据。</summary>
    [Fact]
    public async Task DiscoversPrismWithPreservedEmlManifest()
    {
        Write("instances/GTNH/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}""");
        Write("instances/GTNH/instance.cfg", "[General]\nname=GTNH 中文，本地\n");
        Write("instances/GTNH/.minecraft/options.txt", "lang:zh_CN");
        Write("instances/GTNH/eml-instance.json", """{"schemaVersion":1,"sourceFormat":"Prism","name":"GTNH 原始名称","gameDirectory":".minecraft","minecraftVersion":"1.7.10","components":[{"id":"net.minecraft","version":"1.7.10"}],"files":[],"notes":[]}""");

        var result = await Discover();

        Assert.Empty(result.Warnings);
        var candidate = Assert.Single(result.Instances);
        Assert.Equal("GTNH 中文，本地", candidate.Name);
        Assert.Equal("1.7.10", candidate.MinecraftVersion);
        Assert.Equal(Path.Combine(Source, "instances", "GTNH"), candidate.SourcePath);
        Assert.Equal(Path.Combine(candidate.SourcePath, ".minecraft"), candidate.GameDirectory);
    }

    /// <summary>损坏的 EML 清单不能降级为普通 Prism 而丢失来源，其他正常实例仍继续发现。</summary>
    [Fact]
    public async Task InvalidEmlManifestReportsWarningWithoutHidingOtherInstances()
    {
        Write("instances/broken/eml-instance.json", "not json");
        Write("instances/broken/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}""");
        Write("instances/broken/.minecraft/options.txt", "lang:zh_CN");
        Write("instances/good/profile.json", """{"name":"good","game_version":"1.21.1","loader":"vanilla"}""");

        var result = await Discover();

        Assert.Equal("good", Assert.Single(result.Instances).Name);
        Assert.Contains(result.Warnings, warning => warning.Contains(Path.Combine(Source, "instances", "broken")));
    }

    /// <summary>启动器共享版本目录与独立 instances 可以共存，标准候选不能遮蔽 Prism 子实例。</summary>
    [Theory]
    [InlineData("versions")]
    [InlineData(".minecraft/versions")]
    public async Task SharedVersionDirectoryDoesNotHideInstanceContainer(string versions)
    {
        Write(versions + "/1.20.1/1.20.1.json", """{"id":"1.20.1","mainClass":"net.minecraft.client.main.Main","libraries":[]}""");
        Write("instances/GTNH/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}""");
        Write("instances/GTNH/.minecraft/options.txt", "lang:zh_CN");

        var result = await Discover();

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Instances.Count);
        Assert.Contains(result.Instances, instance => instance.SourcePath == Path.Combine(Source, "instances", "GTNH"));
        Assert.Contains(result.Instances, instance => instance.MinecraftVersion == "1.20.1");
    }

    /// <summary>根目录标记解析失败后也继续扫描明确的实例容器，但不会隐藏原始失败原因。</summary>
    [Theory]
    [InlineData("mmc-pack.json", "instances")]
    [InlineData("eml-instance.json", "profiles")]
    public async Task InvalidRootMarkerDoesNotHideInstanceContainer(string marker, string container)
    {
        Write(marker, "not json");
        Write(container + "/good/profile.json", """{"name":"good","game_version":"1.21.1","loader":"vanilla"}""");

        var result = await Discover();

        Assert.Equal("good", Assert.Single(result.Instances).Name);
        Assert.Contains(result.Warnings, warning => warning.StartsWith(Source + "：", StringComparison.Ordinal));
    }

    /// <summary>候选实例自己的版本与游戏内容不再递归识别，避免一次扫描显示出重复的幽灵实例。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoesNotDescendIntoConcreteInstanceGameDirectory(bool selectInstanceDirectly)
    {
        Write("instances/GTNH/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}""");
        Write("instances/GTNH/.minecraft/versions/1.20.1/1.20.1.json", """{"id":"1.20.1","mainClass":"net.minecraft.client.main.Main","libraries":[]}""");
        Write("instances/GTNH/.minecraft/profiles/not-an-instance/profile.json", """{"name":"ghost","game_version":"1.21.1","loader":"vanilla"}""");

        var result = selectInstanceDirectly
            ? await new ExternalInstanceDiscovery([]).DiscoverAsync(Path.Combine(Source, "instances", "GTNH"))
            : await Discover();

        Assert.Empty(result.Warnings);
        Assert.Equal("1.7.10", Assert.Single(result.Instances).MinecraftVersion);
    }

    /// <summary>同一容器由多个入口进入时只保留一条实例，最多三层的发现边界保持不变。</summary>
    [Fact]
    public async Task ExplicitContainersRemainBoundedAndDeduplicated()
    {
        Write("instances/good/profile.json", """{"name":"good","game_version":"1.21.1","loader":"vanilla"}""");
        Write("data/data/data/instances/too-deep/profile.json", """{"name":"too deep","game_version":"1.21.1","loader":"vanilla"}""");

        var result = await new ExternalInstanceDiscovery([Source, Path.Combine(Source, "instances")]).DiscoverAsync();

        Assert.Empty(result.Warnings);
        Assert.Equal("good", Assert.Single(result.Instances).Name);
    }

    /// <summary>只有模组载荷不够确认运行版本，手动选择也不能捏造原版候选。</summary>
    [Fact]
    public async Task ModsAloneAreNotAnInstance()
    {
        Write("mods/not-a-version.jar", "mod");
        var result = await new ExternalInstanceDiscovery([]).DiscoverAsync(Source);
        Assert.Empty(result.Instances);
        Assert.NotEmpty(result.Warnings);
    }

    /// <summary>预览候选不能篡改游戏位置或加载器，导入服务重新读取原描述。</summary>
    [Fact]
    public async Task ReReadsSourceInsteadOfTrustingCachedCandidate()
    {
        Write("profile.json", """{"name":"old","game_version":"1.20.1","loader":"vanilla"}""");
        var candidate = Assert.Single((await new ExternalInstanceDiscovery([]).DiscoverAsync(Source)).Instances);
        var preview = await new MinecraftFolderImporter(Target).InspectAsync(candidate with { GameDirectory = "C:\\", MinecraftVersion = "fake", Loader = "fake" });
        Assert.Equal(Source, preview.Instance.GameDirectory);
        Assert.Equal("1.20.1", preview.Import.MinecraftVersion);
    }

    /// <summary>默认位置由测试替换，避免扫描用户真实游戏。</summary>
    private Task<ExternalInstanceDiscoveryResult> Discover() => new ExternalInstanceDiscovery([Source]).DiscoverAsync();

    /// <summary>在测试源目录创建 UTF-8 文件。</summary>
    private string Write(string path, string content)
    {
        var full = Path.Combine(Source, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>仅清理本测试创建的随机目录。</summary>
    public void Dispose() => Directory.Delete(_root, true);
}
