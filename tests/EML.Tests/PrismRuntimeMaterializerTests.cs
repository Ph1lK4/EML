using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Prism;

namespace EML.Tests;

/// <summary>验证 Prism 运行文件的整理和安全边界，只使用独立临时目录。</summary>
public sealed class PrismRuntimeMaterializerTests
{
    /// <summary>资源索引按内容哈希去重，并生成明确的 Mojang 对象地址。</summary>
    [Fact]
    public void ExpandAssets_DeduplicatesHashes()
    {
        using var fixture = new Fixture();
        var content = Encoding.UTF8.GetBytes("sound");
        var hash = Sha1(content);
        var index = fixture.Index(new Dictionary<string, object>
        {
            ["sounds/a.ogg"] = new { hash, size = content.Length },
            ["sounds/b.ogg"] = new { hash, size = content.Length }
        });
        var result = new PrismRuntimeMaterializer().ExpandAssets(fixture.Plan with { AssetIndexId = "1.7.10", Files = [index] });
        var item = Assert.Single(result);
        Assert.Equal(PrismRuntimeFileRole.AssetObject, item.Role);
        Assert.Equal($".minecraft/assets/objects/{hash[..2]}/{hash}", item.Requirement.Path);
        Assert.Equal($"https://resources.download.minecraft.net/{hash[..2]}/{hash}", Assert.Single(item.Requirement.Downloads));
        Assert.Equal(content.Length, item.Requirement.Size);
    }

    /// <summary>索引内容被修改后不能继续根据旧哈希安排下载。</summary>
    [Fact]
    public void ExpandAssets_RejectsModifiedIndex()
    {
        using var fixture = new Fixture();
        var index = fixture.Index(new Dictionary<string, object>());
        File.AppendAllText(fixture.Absolute(index.Requirement.Path!), " ");
        Assert.Throws<InvalidDataException>(() => new PrismRuntimeMaterializer().ExpandAssets(fixture.Plan with
        {
            AssetIndexId = "1.7.10", Files = [index]
        }));
    }

    /// <summary>资源逻辑名称的越界路径必须在创建任何文件前被拒绝。</summary>
    [Theory]
    [InlineData("../outside.ogg")]
    [InlineData("/absolute.ogg")]
    [InlineData("sounds/a.ogg:stream")]
    public void ExpandAssets_RejectsUnsafeLogicalPath(string path)
    {
        using var fixture = new Fixture();
        var index = fixture.Index(new Dictionary<string, object> { [path] = new { hash = new string('a', 40), size = 1 } });
        Assert.Throws<InvalidDataException>(() => new PrismRuntimeMaterializer().ExpandAssets(fixture.Plan with
        {
            AssetIndexId = "1.7.10", Files = [index]
        }));
    }

    /// <summary>同一内容哈希的互相矛盾大小不能被去重逻辑掩盖。</summary>
    [Fact]
    public void ExpandAssets_RejectsConflictingSizes()
    {
        using var fixture = new Fixture();
        var hash = new string('a', 40);
        var index = fixture.Index(new Dictionary<string, object>
        {
            ["a"] = new { hash, size = 1 }, ["b"] = new { hash, size = 2 }
        });
        Assert.Throws<InvalidDataException>(() => new PrismRuntimeMaterializer().ExpandAssets(fixture.Plan with
        {
            AssetIndexId = "1.7.10", Files = [index]
        }));
    }

    /// <summary>同时生成标准版本和辅助配置，本地 early 类库排在其他类库及主 JAR 前面。</summary>
    [Fact]
    public async Task Prepare_GeneratesPortableVersionWithOrderedClasspath()
    {
        using var fixture = new Fixture();
        var local = fixture.File("libraries/early.jar", "early", PrismRuntimeFileRole.Library) with { MavenName = "example:early:1", OnClassPath = true };
        var library = fixture.File(".minecraft/libraries/example/normal/1/normal-1.jar", "normal", PrismRuntimeFileRole.Library) with { MavenName = "example:normal:1", OnClassPath = true };
        var main = fixture.File(".minecraft/cache/client.jar", "client", PrismRuntimeFileRole.MainJar);
        var plan = fixture.Plan with
        {
            Files = [local, library, main], CompatibleJavaMajors = [17, 21],
            MinecraftArguments = "--username ${auth_player_name} --userProperties '{}' --label \"two words\"",
            Tweakers = ["example.ForgeTweaker"], JvmArguments = ["-Dfile.encoding=UTF-8"]
        };
        var result = await new PrismRuntimeMaterializer().PrepareAsync(plan);
        Assert.Equal(fixture.StandardPath, result.LaunchProfilePath);
        using var standard = JsonDocument.Parse(File.ReadAllText(fixture.Absolute(result.LaunchProfilePath)));
        Assert.Equal("example.Main", standard.RootElement.GetProperty("mainClass").GetString());
        Assert.Equal(17, standard.RootElement.GetProperty("javaVersion").GetProperty("majorVersion").GetInt32());
        var arguments = standard.RootElement.GetProperty("arguments").GetProperty("game").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "--username", "${auth_player_name}", "--userProperties", "{}", "--label", "two words", "--tweakClass", "example.ForgeTweaker" }, arguments);
        using var portable = JsonDocument.Parse(File.ReadAllText(fixture.Absolute(fixture.PortablePath)));
        var classpath = portable.RootElement.GetProperty("classPath").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(".minecraft/libraries/example/early/1/early-1.jar", classpath[0]);
        Assert.Equal(library.Requirement.Path, classpath[1]);
        Assert.Equal(fixture.VersionRoot + "/" + fixture.Id + ".jar", classpath[2]);
        Assert.DoesNotContain(fixture.Root, File.ReadAllText(fixture.Absolute(fixture.PortablePath)));
        foreach (var file in result.GeneratedFiles)
        {
            Assert.Equal(file.Size, new FileInfo(fixture.Absolute(file.Path!)).Length);
            Assert.Equal(file.Hashes["sha256"], Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.Absolute(file.Path!)))));
        }
    }

    /// <summary>重复准备复用相同内容，生成结果保持稳定且不残留临时文件。</summary>
    [Fact]
    public async Task Prepare_IsIdempotent()
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan with { Files = [fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar)] };
        var materializer = new PrismRuntimeMaterializer();
        var first = await materializer.PrepareAsync(plan);
        var second = await materializer.PrepareAsync(plan);
        Assert.Equal(first.LaunchProfilePath, second.LaunchProfilePath);
        Assert.Equal(first.GeneratedFiles.Select(f => f.Hashes["sha256"]), second.GeneratedFiles.Select(f => f.Hashes["sha256"]));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.part", SearchOption.AllDirectories));
    }

    /// <summary>游戏目录等于实例根目录时，生成路径不重复添加 .minecraft。</summary>
    [Fact]
    public async Task Prepare_SupportsInstanceRootAsGameDirectory()
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan with
        {
            GameDirectory = ".", Files = [fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar)]
        };
        var result = await new PrismRuntimeMaterializer().PrepareAsync(plan);
        Assert.Equal($"versions/{fixture.Id}/{fixture.Id}.json", result.LaunchProfilePath);
        Assert.True(File.Exists(fixture.Absolute(result.LaunchProfilePath)));
    }

    /// <summary>缺失运行依赖会阻止发布标准版本文件。</summary>
    [Fact]
    public async Task Prepare_DoesNotPublishProfileWhenDependencyMissing()
    {
        using var fixture = new Fixture();
        var main = fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar);
        File.Delete(fixture.Absolute("client.jar"));
        await Assert.ThrowsAnyAsync<IOException>(() => new PrismRuntimeMaterializer().PrepareAsync(fixture.Plan with { Files = [main] }));
        Assert.False(File.Exists(fixture.Absolute(fixture.StandardPath)));
    }

    /// <summary>同大小损坏必须由哈希检测发现，不能生成可启动标记。</summary>
    [Fact]
    public async Task Prepare_RejectsSameSizeChangedDependency()
    {
        using var fixture = new Fixture();
        var main = fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar);
        File.WriteAllText(fixture.Absolute("client.jar"), "CLIENT");
        await Assert.ThrowsAsync<InvalidDataException>(() => new PrismRuntimeMaterializer().PrepareAsync(fixture.Plan with { Files = [main] }));
        Assert.False(File.Exists(fixture.Absolute(fixture.StandardPath)));
    }

    /// <summary>已有标准目录中的不同用户文件被保留，并导致准备失败。</summary>
    [Fact]
    public async Task Prepare_PreservesExistingConflictingJar()
    {
        using var fixture = new Fixture();
        var main = fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar);
        var target = fixture.VersionRoot + "/" + fixture.Id + ".jar";
        fixture.File(target, "user-data", PrismRuntimeFileRole.Library);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PrismRuntimeMaterializer().PrepareAsync(fixture.Plan with { Files = [main] }));
        Assert.Equal("user-data", File.ReadAllText(fixture.Absolute(target)));
        Assert.False(File.Exists(fixture.Absolute(fixture.StandardPath)));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.part", SearchOption.AllDirectories));
    }

    /// <summary>旧式本机库排除 META-INF 和指定目录，只释放需要的 DLL。</summary>
    [Fact]
    public async Task Prepare_ExtractsNativesWithExcludes()
    {
        using var fixture = new Fixture();
        var native = fixture.Native(("lwjgl64.dll", "dll"), ("META-INF/MANIFEST.MF", "manifest"), ("skip/other.dll", "skip"))
            with { ExtractExcludes = ["skip/"] };
        var main = fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar);
        var result = await new PrismRuntimeMaterializer().PrepareAsync(fixture.Plan with { Files = [main, native] });
        Assert.Equal("dll", File.ReadAllText(fixture.Absolute(fixture.NativesRoot + "/lwjgl64.dll")));
        Assert.False(Directory.Exists(fixture.Absolute(fixture.NativesRoot + "/META-INF")));
        Assert.False(Directory.Exists(fixture.Absolute(fixture.NativesRoot + "/skip")));
        Assert.Contains(result.GeneratedFiles, f => f.Path == fixture.NativesRoot + "/lwjgl64.dll");
    }

    /// <summary>新版 LWJGL native JAR 作为普通类库保留，不进行旧式解压。</summary>
    [Fact]
    public async Task Prepare_KeepsModernNativeJarOnClasspath()
    {
        using var fixture = new Fixture();
        var native = fixture.Native(("windows/x64/lwjgl.dll", "dll")) with
        {
            Role = PrismRuntimeFileRole.Library, OnClassPath = true, MavenName = "org.lwjgl:lwjgl:3.3.1:natives-windows"
        };
        var main = fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar);
        await new PrismRuntimeMaterializer().PrepareAsync(fixture.Plan with { Files = [native, main] });
        Assert.Empty(Directory.GetFiles(fixture.Absolute(fixture.NativesRoot), "*", SearchOption.AllDirectories));
        using var profile = JsonDocument.Parse(File.ReadAllText(fixture.Absolute(fixture.PortablePath)));
        Assert.Contains("natives-windows.jar", profile.RootElement.GetProperty("classPath")[0].GetString());
    }

    /// <summary>上游未给哈希的旧版 JAR 可以准备，但配置必须保留尚未验证来源的状态。</summary>
    [Fact]
    public async Task Prepare_AllowsReadableUnhashedJarWithoutClaimingSourceVerified()
    {
        using var fixture = new Fixture();
        var archive = fixture.Native(("example/Class.class", "bytecode"));
        var library = archive with
        {
            Role = PrismRuntimeFileRole.Library, OnClassPath = true, MavenName = "lzma:lzma:0.0.1",
            Requirement = archive.Requirement with { Size = null, Hashes = new Dictionary<string, string>() }
        };
        var main = fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar);
        await new PrismRuntimeMaterializer().PrepareAsync(fixture.Plan with { Files = [library, main] });
        using var standard = JsonDocument.Parse(File.ReadAllText(fixture.Absolute(fixture.StandardPath)));
        var entry = standard.RootElement.GetProperty("libraries")[0];
        Assert.False(entry.GetProperty("emlSourceHashVerified").GetBoolean());
        Assert.False(entry.GetProperty("downloads").GetProperty("artifact").TryGetProperty("sha1", out _));
        using var portable = JsonDocument.Parse(File.ReadAllText(fixture.Absolute(fixture.PortablePath)));
        Assert.False(portable.RootElement.GetProperty("sourceFiles")[0].GetProperty("sourceHashVerified").GetBoolean());
        Assert.NotEmpty(portable.RootElement.GetProperty("notes").EnumerateArray());
    }

    /// <summary>无哈希时也不能把 HTML 错误页等非 JAR 内容作为类库安装。</summary>
    [Fact]
    public async Task Prepare_RejectsUnhashedNonJarContent()
    {
        using var fixture = new Fixture();
        var source = fixture.File("broken.jar", "<html>error</html>", PrismRuntimeFileRole.Library);
        var library = source with
        {
            MavenName = "example:broken:1", OnClassPath = true,
            Requirement = source.Requirement with { Hashes = new Dictionary<string, string>() }
        };
        var main = fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PrismRuntimeMaterializer().PrepareAsync(fixture.Plan with { Files = [library, main] }));
        Assert.False(File.Exists(fixture.Absolute(fixture.StandardPath)));
    }

    /// <summary>本机库压缩包的路径穿越在实际写入之前被拒绝。</summary>
    [Fact]
    public async Task Prepare_RejectsNativeTraversal()
    {
        using var fixture = new Fixture();
        var native = fixture.Native(("../escape.dll", "bad"));
        var main = fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PrismRuntimeMaterializer().PrepareAsync(fixture.Plan with { Files = [main, native] }));
        Assert.False(File.Exists(fixture.Absolute(fixture.StandardPath)));
        Assert.False(File.Exists(fixture.Absolute(".minecraft/natives/escape.dll")));
    }

    /// <summary>压缩包内的重复目标文件不能覆盖前一个条目。</summary>
    [Fact]
    public async Task Prepare_RejectsDuplicateNativeTargets()
    {
        using var fixture = new Fixture();
        var native = fixture.Native(("native.dll", "first"), ("NATIVE.dll", "second"));
        var main = fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PrismRuntimeMaterializer().PrepareAsync(fixture.Plan with { Files = [main, native] }));
        Assert.False(File.Exists(fixture.Absolute(fixture.NativesRoot + "/native.dll")));
    }

    /// <summary>Unix 符号链接条目不能借助 native 解压进入实例目录。</summary>
    [Fact]
    public async Task Prepare_RejectsNativeSymlink()
    {
        using var fixture = new Fixture();
        var native = fixture.Native(("native.dll", "target"));
        using (var archive = ZipFile.Open(fixture.Absolute(native.Requirement.Path!), ZipArchiveMode.Update))
            archive.Entries[0].ExternalAttributes = unchecked((int)0xA1FF0000);
        native = native with { Requirement = fixture.Requirement(native.Requirement.Path!) };
        var main = fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PrismRuntimeMaterializer().PrepareAsync(fixture.Plan with { Files = [main, native] }));
        Assert.False(File.Exists(fixture.Absolute(fixture.StandardPath)));
    }

    /// <summary>旧版资源映射生成独立副本，修改 resources 不会改变哈希对象。</summary>
    [Fact]
    public async Task Prepare_MapsVirtualAndLegacyResourcesByCopy()
    {
        using var fixture = new Fixture();
        var bytes = Encoding.UTF8.GetBytes("sound");
        var hash = Sha1(bytes);
        var index = fixture.Index(new Dictionary<string, object> { ["sounds/a.ogg"] = new { hash, size = bytes.Length } }, true, true);
        var main = fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar);
        var plan = fixture.Plan with { AssetIndexId = "1.7.10", Files = [main, index] };
        var materializer = new PrismRuntimeMaterializer();
        var asset = Assert.Single(materializer.ExpandAssets(plan));
        fixture.Write(asset.Requirement.Path!, bytes);
        var result = await materializer.PrepareAsync(plan with { Files = [main, index, asset] });
        var virtualPath = ".minecraft/assets/virtual/1.7.10/sounds/a.ogg";
        var resourcesPath = ".minecraft/resources/sounds/a.ogg";
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Absolute(virtualPath)));
        File.WriteAllText(fixture.Absolute(resourcesPath), "changed");
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Absolute(asset.Requirement.Path!)));
        Assert.Contains(result.GeneratedFiles, f => f.Path == virtualPath);
        Assert.Contains(result.GeneratedFiles, f => f.Path == resourcesPath);
    }

    /// <summary>资源对象即使已存在，也必须出现在完整展开的计划中才能完成安装。</summary>
    [Fact]
    public async Task Prepare_RequiresExpandedAssetPlan()
    {
        using var fixture = new Fixture();
        var hash = Sha1([1]);
        var index = fixture.Index(new Dictionary<string, object> { ["a"] = new { hash, size = 1 } });
        var main = fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PrismRuntimeMaterializer().PrepareAsync(fixture.Plan with
        {
            AssetIndexId = "1.7.10", Files = [main, index]
        }));
        Assert.False(File.Exists(fixture.Absolute(fixture.StandardPath)));
    }

    /// <summary>无法解释的参数占位符、入口覆盖和未知启动特性必须显式失败。</summary>
    [Theory]
    [InlineData("placeholder")]
    [InlineData("classpath")]
    [InlineData("trait")]
    [InlineData("quote")]
    public async Task Prepare_RejectsUnsupportedLaunchFeatures(string scenario)
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan with { Files = [fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar)] };
        plan = scenario switch
        {
            "placeholder" => plan with { GameArguments = ["${unhandled_custom_value}"] },
            "classpath" => plan with { JvmArguments = ["-cp", "evil.jar"] },
            "trait" => plan with { Traits = ["legacyLaunch"] },
            _ => plan with { MinecraftArguments = "--label \"unclosed" }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new PrismRuntimeMaterializer().PrepareAsync(plan));
        Assert.False(File.Exists(fixture.Absolute(fixture.StandardPath)));
    }

    /// <summary>预先取消不会生成版本或复制依赖。</summary>
    [Fact]
    public async Task Prepare_HonorsCancellation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var plan = fixture.Plan with { Files = [fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar)] };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PrismRuntimeMaterializer().PrepareAsync(plan, cancellation.Token));
        Assert.False(Directory.Exists(fixture.Absolute(fixture.VersionRoot)));
    }

    /// <summary>新版官方 native 子目录和 Fabric 含空格参数保持原值，日志路径可随实例迁移。</summary>
    [Fact]
    public async Task Prepare_ModrinthPreservesNativeSubdirectoryAndPortableLogging()
    {
        using var fixture = new Fixture();
        var main = fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar);
        var logging = fixture.File(".minecraft/assets/log_configs/client.xml", "<Configuration/>", PrismRuntimeFileRole.Logging);
        var plan = fixture.Plan with
        {
            SourceFormat = "Modrinth", VersionIdPrefix = "EML-Modrinth", Files = [main, logging],
            LoggingArgument = "-Dlog4j.configurationFile=${path}",
            JvmArguments = ["-Djava.library.path=${natives_directory}/java", "-DFabricMcEmu= net.minecraft.client.main.Main "]
        };
        var result = await new PrismRuntimeMaterializer().PrepareAsync(plan);
        using var standard = JsonDocument.Parse(File.ReadAllText(fixture.Absolute(result.LaunchProfilePath)));
        var portablePath = standard.RootElement.GetProperty("emlLaunchProfile").GetString()!;
        using var profile = JsonDocument.Parse(File.ReadAllText(fixture.Absolute(portablePath)));
        var args = profile.RootElement.GetProperty("jvmArguments").EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert.StartsWith(".minecraft/versions/EML-Modrinth-", result.LaunchProfilePath);
        Assert.Contains("-Djava.library.path=${natives_directory}/java", args);
        Assert.Contains("-DFabricMcEmu= net.minecraft.client.main.Main ", args);
        Assert.Contains("-Dlog4j.configurationFile=${logging_path}", args);
        Assert.Equal(logging.Requirement.Path, profile.RootElement.GetProperty("loggingPath").GetString());
        Assert.Equal("-Dlog4j.configurationFile=${path}", standard.RootElement.GetProperty("logging").GetProperty("client").GetProperty("argument").GetString());
        Assert.DoesNotContain(fixture.Root, profile.RootElement.GetRawText());
    }

    /// <summary>重复本机库参数及越界目录不能绕过实例的 native 路径约束。</summary>
    [Theory]
    [InlineData("-Djava.library.path=${natives_directory}/../outside")]
    [InlineData("-Djava.library.path=C:/outside")]
    [InlineData("duplicate")]
    public async Task Prepare_RejectsInvalidNativeProperty(string argument)
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan with
        {
            Files = [fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar)],
            JvmArguments = argument == "duplicate" ? ["-Djava.library.path=${natives_directory}/java", "-Djava.library.path=${natives_directory}"] : [argument]
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new PrismRuntimeMaterializer().PrepareAsync(plan));
    }

    /// <summary>单独声明日志文件或日志参数均不能误报配置已齐全。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prepare_RejectsIncompleteLogging(bool hasFile)
    {
        using var fixture = new Fixture();
        var main = fixture.File("client.jar", "client", PrismRuntimeFileRole.MainJar);
        var plan = fixture.Plan with
        {
            Files = hasFile ? [main, fixture.File("log.xml", "log", PrismRuntimeFileRole.Logging)] : [main],
            LoggingArgument = hasFile ? null : "-Dlog4j.configurationFile=${path}"
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new PrismRuntimeMaterializer().PrepareAsync(plan));
    }

    /// <summary>计算测试资源的 SHA-1 内容地址。</summary>
    private static string Sha1(byte[] content) => Convert.ToHexStringLower(SHA1.HashData(content));

    /// <summary>为测试建立最小 Prism 计划和安全隔离的运行文件。</summary>
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "eml-prism-prepare-" + Guid.NewGuid().ToString("N"));
        public string Id => "EML-Prism-0123456789ab";
        public string VersionRoot => ".minecraft/versions/" + Id;
        public string StandardPath => VersionRoot + "/" + Id + ".json";
        public string PortablePath => VersionRoot + "/" + Id + ".eml-launch.json";
        public string NativesRoot => ".minecraft/natives/0123456789ab";
        public PrismRuntimePlan Plan => new() { InstancePath = Root, MainClass = "example.Main", MinecraftVersion = "1.7.10", Fingerprint = "0123456789abcdef" };

        /// <summary>创建属于本测试的实例根目录。</summary>
        public Fixture() => Directory.CreateDirectory(Root);

        /// <summary>将测试用相对路径转换为该测试实例内的路径。</summary>
        public string Absolute(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>写入测试源文件，父目录随测试数据一并建立。</summary>
        public void Write(string relative, byte[] content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Absolute(relative))!);
            System.IO.File.WriteAllBytes(Absolute(relative), content);
        }

        /// <summary>为已经存在的测试文件生成可信大小和 SHA-256。</summary>
        public InstanceFileRequirement Requirement(string relative)
        {
            var bytes = System.IO.File.ReadAllBytes(Absolute(relative));
            return new InstanceFileRequirement
            {
                Path = relative, Source = InstanceFileSource.Embedded, Size = bytes.Length,
                Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) }
            };
        }

        /// <summary>建立简单测试文件并标记其运行用途。</summary>
        public PrismRuntimeFile File(string relative, string text, PrismRuntimeFileRole role)
        {
            Write(relative, Encoding.UTF8.GetBytes(text));
            return new PrismRuntimeFile(Requirement(relative), role);
        }

        /// <summary>创建具有有效校验值的测试资源索引。</summary>
        public PrismRuntimeFile Index(Dictionary<string, object> objects, bool virtualFiles = false, bool resources = false)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["objects"] = objects, ["virtual"] = virtualFiles, ["map_to_resources"] = resources
            });
            const string relative = ".minecraft/assets/indexes/1.7.10.json";
            Write(relative, bytes);
            return new PrismRuntimeFile(Requirement(relative), PrismRuntimeFileRole.AssetIndex);
        }

        /// <summary>生成小型 native ZIP；允许构造重复或不安全名称以验证拒绝行为。</summary>
        public PrismRuntimeFile Native(params (string Name, string Content)[] entries)
        {
            const string relative = "native.jar";
            using (var archive = ZipFile.Open(Absolute(relative), ZipArchiveMode.Create))
            {
                foreach (var (name, content) in entries)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(content);
                }
            }
            return new PrismRuntimeFile(Requirement(relative), PrismRuntimeFileRole.NativeArchive);
        }

        /// <summary>仅清理本测试创建的唯一目录。</summary>
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
