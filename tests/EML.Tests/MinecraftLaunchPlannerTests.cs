using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Java;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Launching;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Launching;
using EML.Infrastructure.Minecraft.Prism;

namespace EML.Tests;

/// <summary>以真实清单和生成文件验证启动计划，不启动 Java 或游戏、不联网。</summary>
public sealed class MinecraftLaunchPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-launch-plan-" + Guid.NewGuid().ToString("N"));

    /// <summary>各来源复用同一配置，保留加载器参数并正确填入玩家、内存和中文路径。</summary>
    [Theory]
    [InlineData("Prism", ".minecraft", "25", "x64（64 位）")]
    [InlineData("Modrinth", "data/game", "21.0.6", "amd64")]
    [InlineData("CurseForge", ".", "1.8.0_501", "x86_64")]
    [InlineData("Zip", ".minecraft", "1.8.0_501", "x86_64")]
    public async Task SupportedPreparedFormatsShareLaunchPlan(string format, string game, string java, string architecture)
    {
        var fixture = await CreateAsync(format, game, java, architecture);
        var plan = await fixture.Planner.PrepareAsync(fixture.Instance, new()
        { PlayerName = "Alex", MinMemoryMb = 512, MaxMemoryMb = 4096, AdditionalJvmArguments = "-Dexample=\"with spaces\" --add-opens java.base/java.lang=ALL-UNNAMED" });

        Assert.Equal(Path.GetFullPath(game == "." ? fixture.Instance : Path.Combine(fixture.Instance, game)), plan.GameDirectory);
        Assert.Equal("-Xms512M", plan.Arguments[0]);
        Assert.Equal("-Xmx4096M", plan.Arguments[1]);
        Assert.Single(plan.Arguments, value => value.StartsWith("-Xmx"));
        Assert.Contains("-Dexample=with spaces", plan.Arguments);
        Assert.Contains("-Dloader.flag=true", plan.Arguments);
        Assert.Contains("java.base/java.lang=ALL-UNNAMED", plan.Arguments);
        Assert.Contains("--tweakClass", plan.Arguments);
        Assert.Contains("example.Tweaker", plan.Arguments);
        Assert.Contains("Alex", plan.Arguments);
        Assert.Contains(MinecraftLaunchPlanner.OfflineUuid("Alex"), plan.Arguments);
        Assert.DoesNotContain(plan.Arguments, value => value.Contains("${"));
        Assert.Contains(Path.Combine("libraries", "library.jar") + Path.PathSeparator + Path.Combine("versions", "fixture", "fixture.jar"), plan.Arguments);
        Assert.False(Directory.Exists(Path.Combine(fixture.Instance, ".eml-runtime", "launch")));
    }

    /// <summary>普通启动不打开整合包内容文件，不因玩家修改或缺失的非运行文件强制完整检查。</summary>
    [Fact]
    public async Task PreparationDoesNotScanImportedContent()
    {
        var fixture = await CreateAsync();
        var path = Path.Combine(fixture.Instance, ".minecraft/config/changed.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "player changed this");
        var manifestPath = Path.Combine(fixture.Instance, InstanceImportManifest.FileName);
        var manifest = InstanceManifestStore.Read(manifestPath);
        await InstanceManifestStore.WriteAsync(manifestPath, manifest with
        {
            Files = [new() { Path = ".minecraft/config/changed.cfg", Source = InstanceFileSource.Embedded,
                Size = 4, Hashes = new Dictionary<string, string> { ["sha256"] = new string('b', 64) } },
                new() { Path = ".minecraft/mods/missing.jar", Source = InstanceFileSource.Embedded,
                    Size = 4, Hashes = new Dictionary<string, string> { ["sha256"] = new string('b', 64) } }]
        }, CancellationToken.None);
        using var lockedContent = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var plan = await fixture.Planner.PrepareAsync(fixture.Instance, new());

        Assert.Equal("fake-java", plan.JavaPath);
        Assert.Equal(1, fixture.ProbeCalls());
    }

    /// <summary>运行配置仍须有本轮组件对应的完成记录，不能仅凭 JSON 文件存在就启动。</summary>
    [Fact]
    public async Task MissingPreparationStateIsRejected()
    {
        var fixture = await CreateAsync();
        File.Delete(Path.Combine(fixture.Instance, PrismRuntimeStateStore.RelativePath));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Planner.PrepareAsync(fixture.Instance, new()));

        Assert.Equal(0, fixture.ProbeCalls());
    }

    /// <summary>组件元数据改变后，原来的生成配置不会继续使用。</summary>
    [Fact]
    public async Task StalePreparationStateIsRejected()
    {
        var fixture = await CreateAsync();
        fixture.Resolver.Plan = fixture.Resolver.Plan with { Fingerprint = new string('b', 64) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Planner.PrepareAsync(fixture.Instance, new()));

        Assert.Equal(0, fixture.ProbeCalls());
    }

    /// <summary>Java 查询期间组件发生变化时，不把旧配置交给启动进程。</summary>
    [Fact]
    public async Task SourceChangeDuringJavaProbeIsRejected()
    {
        var fixture = await CreateAsync();
        fixture.BeforeProbe = () => fixture.Resolver.Plan = fixture.Resolver.Plan with { Fingerprint = new string('b', 64) };

        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Planner.PrepareAsync(fixture.Instance, new()));
    }

    /// <summary>快速准备仍检查启动类库、本机库、日志配置和资源索引是否存在。</summary>
    [Theory]
    [InlineData("libraries/library.jar")]
    [InlineData("versions/fixture/fixture.jar")]
    [InlineData("natives/fixture/lwjgl.dll")]
    [InlineData("assets/log_configs/client.xml")]
    [InlineData("assets/indexes/test.json")]
    public async Task MissingEssentialRuntimeFileIsRejected(string relative)
    {
        var fixture = await CreateAsync();
        File.Delete(Path.Combine(fixture.Instance, ".minecraft", relative));

        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Planner.PrepareAsync(fixture.Instance, new()));

        Assert.Equal(0, fixture.ProbeCalls());
    }

    /// <summary>关键文件大小明显变化时可立即提示补全，无需读取整个 JAR。</summary>
    [Fact]
    public async Task RuntimeSizeMismatchIsRejected()
    {
        var fixture = await CreateAsync();
        File.AppendAllText(Path.Combine(fixture.Instance, ".minecraft/libraries/library.jar"), "changed");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Planner.PrepareAsync(fixture.Instance, new()));
    }

    /// <summary>普通启动不重新计算运行库内容哈希；同大小替换留给手动完整检查识别。</summary>
    [Fact]
    public async Task RuntimeContentIsNotHashedOnEveryPreparation()
    {
        var fixture = await CreateAsync();
        File.WriteAllText(Path.Combine(fixture.Instance, ".minecraft/libraries/library.jar"), "changed");

        var plan = await fixture.Planner.PrepareAsync(fixture.Instance, new());

        Assert.Equal("fake-java", plan.JavaPath);
    }

    /// <summary>两份启动描述很小，仍逐次按生成记录校验，防止损坏的参数被执行。</summary>
    [Fact]
    public async Task CorruptPortableProfileIsRejectedBeforeProbingJava()
    {
        var fixture = await CreateAsync();
        File.WriteAllText(fixture.PortablePath, File.ReadAllText(fixture.PortablePath).Replace("example.Main", "changed.Main"));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Planner.PrepareAsync(fixture.Instance, new()));

        Assert.Equal(0, fixture.ProbeCalls());
    }

    /// <summary>再次启动复用 Java 发现列表，同时重新查询实际版本，避免重复搜索电脑。</summary>
    [Fact]
    public async Task RepeatedPreparationCachesDiscoveryButStillProbesJava()
    {
        var fixture = await CreateAsync();

        await fixture.Planner.PrepareAsync(fixture.Instance, new());
        await fixture.Planner.PrepareAsync(fixture.Instance, new());

        Assert.Equal(1, fixture.Locator.Calls);
        Assert.Equal(2, fixture.ProbeCalls());
    }

    /// <summary>Java 查询期间被替换的启动 JSON 不会复用之前的哈希结果。</summary>
    [Fact]
    public async Task ProfileChangeDuringJavaProbeIsRejected()
    {
        var fixture = await CreateAsync();
        fixture.BeforeProbe = () => File.AppendAllText(fixture.PortablePath, " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Planner.PrepareAsync(fixture.Instance, new()));
    }

    /// <summary>用户明确指定的错误 Java 不能悄悄回退到另一版本。</summary>
    [Fact]
    public async Task IncompatibleManualJavaIsRejected()
    {
        var fixture = await CreateAsync();
        fixture.Probed = new("manual", "17.0.2", "amd64");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Planner.PrepareAsync(fixture.Instance, new() { JavaPath = "manual" }));
    }

    /// <summary>过期的发现结果必须由实时 Java 查询复核。</summary>
    [Fact]
    public async Task AutoSelectionDoesNotTrustStaleDiscovery()
    {
        var fixture = await CreateAsync();
        fixture.Probed = new("stale", "8", "amd64");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Planner.PrepareAsync(fixture.Instance, new()));
    }

    /// <summary>检测列表与实时查询使用不同的合法 x86 别名时，仍识别为同一架构。</summary>
    [Theory]
    [InlineData("i486")]
    [InlineData("i586")]
    public async Task AutoSelectionAcceptsLegacyX86ArchitectureAliases(string probedArchitecture)
    {
        var fixture = await CreateAsync(version: "1.8.0_501", arch: "x86（32 位）", runtimeArchitecture: "x86");
        fixture.Probed = new("fake-java", "1.8.0_501", probedArchitecture);

        var plan = await fixture.Planner.PrepareAsync(fixture.Instance, new() { MinMemoryMb = 512, MaxMemoryMb = 1024 });

        Assert.Equal(8, plan.JavaMajorVersion);
        Assert.Equal("fake-java", plan.JavaPath);
        Assert.Equal(1, fixture.ProbeCalls());
    }

    /// <summary>游戏目录、未知参数与主类均从受验证的配置读取，并检查其结构。</summary>
    [Theory]
    [InlineData("gameDirectory", "../escape")]
    [InlineData("mainClass", "bad class")]
    [InlineData("runtimeArchitecture", "arm64")]
    [InlineData("assetsRoot", "../../escape")]
    public async Task InvalidProfileFieldsAreRejected(string field, string value)
    {
        var fixture = await CreateAsync();
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.PortablePath))!;
        json[field] = value;
        File.WriteAllText(fixture.PortablePath, json.ToJsonString());
        await fixture.RefreshStateAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Planner.PrepareAsync(fixture.Instance, new()));
    }

    /// <summary>没有实现的占位符需要明确报错，而不是直接传给 Java。</summary>
    [Fact]
    public async Task UnknownPlaceholderIsRejected()
    {
        var fixture = await CreateAsync();
        File.WriteAllText(fixture.PortablePath, File.ReadAllText(fixture.PortablePath).Replace("${auth_player_name}", "${unknown}"));
        await fixture.RefreshStateAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Planner.PrepareAsync(fixture.Instance, new()));
    }

    /// <summary>取消在准备阶段停止，不进行 Java 查询。</summary>
    [Fact]
    public async Task CanceledPreparationDoesNotProbeJava()
    {
        var fixture = await CreateAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Planner.PrepareAsync(fixture.Instance, new(), cancellationToken: new(true)));
        Assert.Equal(0, fixture.ProbeCalls());
    }

    /// <summary>实例设置保存往返不改变来源清单。</summary>
    [Fact]
    public async Task SettingsRoundTripIsSeparateFromManifest()
    {
        var fixture = await CreateAsync();
        var manifestPath = Path.Combine(fixture.Instance, InstanceImportManifest.FileName);
        var before = File.ReadAllBytes(manifestPath);
        var store = new MinecraftLaunchSettingsStore(_root);
        Assert.Equal(new MinecraftLaunchSettings(), await store.LoadAsync(fixture.Instance));
        var settings = new MinecraftLaunchSettings { PlayerName = "Alex_123", MaxMemoryMb = 4096, AdditionalJvmArguments = "-XX:+UseG1GC" };
        await store.SaveAsync(fixture.Instance, settings);
        Assert.Equal(settings, await store.LoadAsync(fixture.Instance));
        Assert.Equal(before, File.ReadAllBytes(manifestPath));
    }

    /// <summary>损坏的偏好记录不被静默默认值替代，也不会自动覆盖。</summary>
    [Fact]
    public async Task CorruptSettingsAreReportedAndPreserved()
    {
        var fixture = await CreateAsync();
        var path = Path.Combine(fixture.Instance, MinecraftLaunchSettingsStore.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => new MinecraftLaunchSettingsStore(_root).LoadAsync(fixture.Instance));
        Assert.Equal("broken", File.ReadAllText(path));
    }

    /// <summary>Unicode 转义后超过读取上限的设置不能替换已有可用设置或留下临时文件。</summary>
    [Fact]
    public async Task OversizedSerializedSettingsAreRejectedBeforeReplacingExistingFile()
    {
        var fixture = await CreateAsync();
        var store = new MinecraftLaunchSettingsStore(_root);
        var original = new MinecraftLaunchSettings { PlayerName = "SavedPlayer" };
        await store.SaveAsync(fixture.Instance, original);
        var path = Path.Combine(fixture.Instance, MinecraftLaunchSettingsStore.RelativePath);
        var before = File.ReadAllBytes(path);
        var oversized = original with { AdditionalJvmArguments = "-Dvalue=" + new string('测', 30000) };
        MinecraftLaunchValidation.Validate(oversized);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(fixture.Instance, oversized));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(original, await store.LoadAsync(fixture.Instance));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    /// <summary>上限以内的中文参数仍可正常保存读取，不因修复大小检查而丢失文本。</summary>
    [Fact]
    public async Task UnicodeSettingsWithinByteLimitRoundTrip()
    {
        var fixture = await CreateAsync();
        var settings = new MinecraftLaunchSettings { AdditionalJvmArguments = "-Dvalue=" + new string('测', 1000) };
        var store = new MinecraftLaunchSettingsStore(_root);

        await store.SaveAsync(fixture.Instance, settings);

        Assert.Equal(settings, await store.LoadAsync(fixture.Instance));
    }

    /// <summary>离线身份保持与标准 Steve UUID 一致。</summary>
    [Fact]
    public void OfflineIdentityUsesPlayerName() => Assert.Equal("5627dd98e6be3c21b8a8e92344183641", MinecraftLaunchPlanner.OfflineUuid("Steve"));

    /// <summary>非法设置和可能改变入口的附加参数在持久化前拒绝。</summary>
    [Theory]
    [InlineData("-Xmx2G")]
    [InlineData("-cp hacked.jar")]
    [InlineData("-jar hacked.jar")]
    [InlineData("@file.args")]
    [InlineData("-Djava.library.path=hacked")]
    [InlineData("--module=bad")]
    [InlineData("--add-opens")]
    [InlineData("-Dvalue=\"unterminated")]
    public void ReservedOrBrokenJvmOptionsAreRejected(string argument) =>
        Assert.Throws<ArgumentException>(() => MinecraftLaunchValidation.Validate(new() { AdditionalJvmArguments = argument }));

    /// <summary>输入框原文不会被 shell 展开，Windows 反斜杠和带空格属性保持独立。</summary>
    [Fact]
    public void TokenizerPreservesQuotedWindowsPaths()
    {
        Assert.Equal([@"-Dpath=C:\Program Files\Java", "-Dvalue=two words"],
            MinecraftLaunchValidation.TokenizeJvmArguments("-Dpath=\"C:\\Program Files\\Java\" -Dvalue='two words'"));
        MinecraftLaunchValidation.Validate(new() { AdditionalJvmArguments = "--add-opens=java.base/java.lang=ALL-UNNAMED -XX:+UseG1GC" });
    }

    /// <summary>引号前的偶数斜杠折半并正常闭合，使带空格且以斜杠结尾的路径可表达。</summary>
    [Theory]
    [InlineData('"', 2)]
    [InlineData('"', 4)]
    [InlineData('\'', 2)]
    [InlineData('\'', 4)]
    public void TokenizerClosesQuotedPathsAfterEvenBackslashes(char quote, int slashCount)
    {
        var text = "-Dpath=" + quote + "C:\\Program Files" + new string('\\', slashCount) + quote + " -Dnext=true";

        Assert.Equal(["-Dpath=C:\\Program Files" + new string('\\', slashCount / 2), "-Dnext=true"],
            MinecraftLaunchValidation.TokenizeJvmArguments(text));
        MinecraftLaunchValidation.Validate(new() { AdditionalJvmArguments = text });
    }

    /// <summary>引号前的奇数斜杠折半并保留引号字面量，后续闭合引号仍按原语义处理。</summary>
    [Theory]
    [InlineData('"', 1)]
    [InlineData('"', 3)]
    [InlineData('\'', 1)]
    [InlineData('\'', 3)]
    public void TokenizerPreservesEscapedQuotesAfterOddBackslashes(char quote, int slashCount)
    {
        var text = "-Dvalue=" + quote + "prefix" + new string('\\', slashCount) + quote + "suffix" + quote;

        Assert.Equal(["-Dvalue=prefix" + new string('\\', slashCount / 2) + quote + "suffix"],
            MinecraftLaunchValidation.TokenizeJvmArguments(text));
        MinecraftLaunchValidation.Validate(new() { AdditionalJvmArguments = text });
    }

    /// <summary>玩家名称必须覆盖整个字符串，任何末尾或内部换行都不能被正则边界放行。</summary>
    [Theory]
    [InlineData("Steve\n")]
    [InlineData("Steve\r\n")]
    [InlineData("Ste\nve")]
    [InlineData("\nSteve")]
    public void PlayerNamesContainingLineBreaksAreRejected(string name) =>
        Assert.Throws<ArgumentException>(() => MinecraftLaunchValidation.Validate(new() { PlayerName = name }));

    /// <summary>建立真实清单、运行状态与启动描述，只替代组件解析和 Java 进程查询。</summary>
    private async Task<Fixture> CreateAsync(string format = "Prism", string game = ".minecraft", string version = "25",
        string arch = "x64（64 位）", string runtimeArchitecture = "x86_64",
        EML.Core.Minecraft.Accounts.IMinecraftAccountService? accounts = null)
    {
        var instance = Path.Combine(_root, "instances", "中文 测试实例");
        Directory.CreateDirectory(instance);
        var gameRoot = game == "." ? instance : Path.Combine(instance, game);
        Directory.CreateDirectory(gameRoot);
        string Relative(string value) => game == "." ? value : game + "/" + value;
        foreach (var file in new[] { "libraries/library.jar", "versions/fixture/fixture.jar", "natives/fixture/lwjgl.dll",
                     "assets/indexes/test.json", "assets/log_configs/client.xml" })
        {
            var path = Path.Combine(gameRoot, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }
        var standardPath = Path.Combine(gameRoot, "versions/fixture/fixture.json");
        var portablePath = Path.Combine(gameRoot, "versions/fixture/fixture.eml-launch.json");
        var fingerprint = new string('a', 64);
        await InstanceManifestStore.WriteAsync(Path.Combine(instance, InstanceImportManifest.FileName), new()
        { SourceFormat = format, GameDirectory = game, Name = "test" }, CancellationToken.None);
        File.WriteAllText(standardPath, JsonSerializer.Serialize(new { id = "fixture", mainClass = "example.Main", emlRuntimeFingerprint = fingerprint,
            emlRuntimeOs = "windows", emlRuntimeArchitecture = runtimeArchitecture, emlLaunchProfile = Relative("versions/fixture/fixture.eml-launch.json") }));
        File.WriteAllText(portablePath, JsonSerializer.Serialize(new { schemaVersion = 1, id = "fixture", mainClass = "example.Main", fingerprint,
            runtimeOs = "windows", runtimeArchitecture, gameDirectory = game,
            classPath = new[] { Relative("libraries/library.jar"), Relative("versions/fixture/fixture.jar") },
            nativesDirectory = Relative("natives/fixture"), assetsRoot = Relative("assets"), gameAssets = Relative("assets"), assetIndexId = "test",
            loggingPath = Relative("assets/log_configs/client.xml"),
            compatibleJavaMajors = new[] { MinecraftLaunchPlanner.MajorVersion(version) },
            jvmArguments = new[] { "-Xmx1G", "-Dloader.flag=true", "-Djava.library.path=${natives_directory}", "-cp", "${classpath}" },
            gameArguments = new[] { "--username", "${auth_player_name}", "--uuid", "${auth_uuid}", "--gameDir", "${game_directory}", "--tweakClass", "example.Tweaker" } }));
        var installed = new JavaInstallation("fake-java", version, arch);
        var fixture = new Fixture(instance, portablePath, standardPath, new FakeJavaLocator(installed));
        fixture.Resolver.Plan = new PrismRuntimePlan
        {
            InstancePath = instance, SourceFormat = format, GameDirectory = game, Fingerprint = fingerprint,
            MainClass = "example.Main", RuntimeOs = "windows", RuntimeArchitecture = runtimeArchitecture,
            CompatibleJavaMajors = [MinecraftLaunchPlanner.MajorVersion(version)], AssetIndexId = "test",
            Files = [
                new(Requirement(instance, Path.Combine(gameRoot, "libraries/library.jar")), PrismRuntimeFileRole.Library) { OnClassPath = true },
                new(Requirement(instance, Path.Combine(gameRoot, "versions/fixture/fixture.jar")), PrismRuntimeFileRole.MainJar) { OnClassPath = true },
                new(Requirement(instance, Path.Combine(gameRoot, "assets/indexes/test.json")), PrismRuntimeFileRole.AssetIndex),
                new(Requirement(instance, Path.Combine(gameRoot, "assets/log_configs/client.xml")), PrismRuntimeFileRole.Logging)]
        };
        await fixture.RefreshStateAsync();
        fixture.Probed = installed;
        fixture.Planner = new MinecraftLaunchPlanner(_root, fixture.Resolver, fixture.Locator, (path, token) =>
        {
            fixture.Calls++;
            fixture.BeforeProbe?.Invoke();
            return Task.FromResult(fixture.Probed);
        }, accounts);
        return fixture;
    }

    /// <summary>正版身份在不同整合包格式中共用，不能仍使用离线名或占位令牌。</summary>
    [Theory]
    [InlineData("Prism")]
    [InlineData("Modrinth")]
    [InlineData("CurseForge")]
    [InlineData("Zip")]
    public async Task MicrosoftIdentityIsUsedAcrossPackFormats(string format)
    {
        var accounts = new OnlineAccountStub();
        var fixture = await CreateAsync(format: format, accounts: accounts);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.PortablePath))!;
        json["gameArguments"] = JsonSerializer.SerializeToNode(new[] { "--username", "${auth_player_name}", "--uuid", "${auth_uuid}",
            "--accessToken", "${auth_access_token}", "--session", "${auth_session}", "--userType", "${user_type}", "--xuid", "${auth_xuid}", "--clientId", "${clientid}" });
        File.WriteAllText(fixture.PortablePath, json.ToJsonString());
        await fixture.RefreshStateAsync();
        var plan = await fixture.Planner.PrepareAsync(fixture.Instance, new() { PlayerName = "OfflineSteve", UseMicrosoftAccount = true });
        Assert.Contains("OnlineAlex", plan.Arguments);
        Assert.Contains(MicrosoftMinecraftAuthTests.PlayerId, plan.Arguments);
        Assert.Contains("online-secret", plan.Arguments);
        Assert.Contains("token:online-secret:" + MicrosoftMinecraftAuthTests.PlayerId, plan.Arguments);
        Assert.Contains("msa", plan.Arguments);
        Assert.Contains("123", plan.Arguments);
        Assert.Contains(MicrosoftMinecraftAuthTests.ClientId, plan.Arguments);
        Assert.DoesNotContain("OfflineSteve", plan.Arguments);
        Assert.Equal(new[] { "online-secret" }, plan.SensitiveValues);
        Assert.DoesNotContain("online-secret", JsonSerializer.Serialize(plan));
        Assert.DoesNotContain("online-secret", plan.ToString());
        Assert.Equal(1, accounts.Calls);
    }

    /// <summary>正版刷新失败时不能继续生成离线启动计划。</summary>
    [Fact]
    public async Task MicrosoftFailureDoesNotFallBackToOffline()
    {
        var fixture = await CreateAsync(accounts: new OnlineAccountStub { Fail = true });
        await Assert.ThrowsAsync<EML.Core.Minecraft.Accounts.MinecraftAuthenticationException>(() =>
            fixture.Planner.PrepareAsync(fixture.Instance, new() { UseMicrosoftAccount = true }));
    }

    /// <summary>未勾选正版时不调用账号或网络服务，旧配置继续使用原离线 UUID。</summary>
    [Fact]
    public async Task OfflineModeDoesNotCallAccountService()
    {
        var accounts = new OnlineAccountStub { Fail = true };
        var fixture = await CreateAsync(accounts: accounts);
        var plan = await fixture.Planner.PrepareAsync(fixture.Instance, new());
        Assert.Equal(0, accounts.Calls);
        Assert.Empty(plan.SensitiveValues);
        Assert.Contains(MinecraftLaunchPlanner.OfflineUuid("Steve"), plan.Arguments);
    }

    /// <summary>正版启动不受已禁用的离线名称输入影响，使用账号验证后的名称。</summary>
    [Fact]
    public async Task MicrosoftModeIgnoresUnusedOfflineName()
    {
        var fixture = await CreateAsync(accounts: new OnlineAccountStub());
        var plan = await fixture.Planner.PrepareAsync(fixture.Instance, new() { UseMicrosoftAccount = true, PlayerName = "" });
        Assert.Contains("OnlineAlex", plan.Arguments);
    }

    /// <summary>保存账号选择只保存布尔偏好，不把认证信息写进实例。</summary>
    [Fact]
    public async Task MicrosoftPreferenceRoundTripsWithoutCredentials()
    {
        var fixture = await CreateAsync();
        var store = new MinecraftLaunchSettingsStore(_root);
        await store.SaveAsync(fixture.Instance, new() { UseMicrosoftAccount = true });
        Assert.True((await store.LoadAsync(fixture.Instance)).UseMicrosoftAccount);
        Assert.DoesNotContain("token", File.ReadAllText(Path.Combine(fixture.Instance, MinecraftLaunchSettingsStore.RelativePath)), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class OnlineAccountStub : EML.Core.Minecraft.Accounts.IMinecraftAccountService
    {
        internal bool Fail;
        internal int Calls;
        public bool IsConfigured => true;
        public Task<EML.Core.Minecraft.Accounts.MinecraftAccountSession> GetSessionAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail) throw new EML.Core.Minecraft.Accounts.MinecraftAuthenticationException("test refresh failure");
            return Task.FromResult(new EML.Core.Minecraft.Accounts.MinecraftAccountSession(
                new(MicrosoftMinecraftAuthTests.PlayerId, "OnlineAlex"), "online-secret", DateTimeOffset.UtcNow.AddHours(1), MicrosoftMinecraftAuthTests.ClientId, "123"));
        }
        public Task<EML.Core.Minecraft.Accounts.MinecraftAccount?> LoadAccountAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EML.Core.Minecraft.Accounts.MinecraftAccount> SignInAsync(IProgress<EML.Core.Minecraft.Accounts.MinecraftLoginPrompt>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>为测试文件保存已知的生成基线，不用检查器伪造完整检查结果。</summary>
    private static InstanceFileRequirement Requirement(string instance, string path) => new()
    {
        Path = Path.GetRelativePath(instance, path).Replace('\\', '/'), Source = InstanceFileSource.Embedded,
        Size = new FileInfo(path).Length,
        Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) }
    };

    private sealed class Fixture(string instance, string portablePath, string standardPath, FakeJavaLocator locator)
    {
        public string Instance => instance;
        public string PortablePath => portablePath;
        public MinecraftLaunchPlanner Planner { get; set; } = null!;
        public FakeResolver Resolver { get; } = new();
        public FakeJavaLocator Locator => locator;
        public JavaInstallation Probed { get; set; } = null!;
        public Action? BeforeProbe { get; set; }
        public int Calls;
        public int ProbeCalls() => Calls;
        /// <summary>模拟补全器发布状态，只在测试主动修改生成配置时更新其基线。</summary>
        public Task RefreshStateAsync()
        {
            var gameRoot = Resolver.Plan.GameDirectory == "." ? instance : Path.Combine(instance, Resolver.Plan.GameDirectory);
            return PrismRuntimeStateStore.WriteAsync(Resolver.Plan, new PrismRuntimeState
            {
                Fingerprint = Resolver.Plan.Fingerprint,
                LaunchProfilePath = Path.GetRelativePath(instance, standardPath).Replace('\\', '/'),
                GeneratedFiles = new[] { standardPath, portablePath, Path.Combine(gameRoot, "libraries/library.jar"),
                    Path.Combine(gameRoot, "versions/fixture/fixture.jar"), Path.Combine(gameRoot, "natives/fixture/lwjgl.dll") }
                    .Select(path => Requirement(instance, path)).ToArray()
            }, CancellationToken.None);
        }
    }
    private sealed class FakeResolver : IPrismRuntimeResolver
    {
        public PrismRuntimePlan Plan { get; set; } = null!;
        /// <summary>返回当前元数据快照，允许测试在探测期间模拟组件更改。</summary>
        public PrismRuntimePlan Resolve(string instancePath, InstanceImportManifest manifest) => Plan;
    }
    private sealed class FakeJavaLocator(JavaInstallation java) : IJavaLocator
    {
        public int Calls { get; private set; }
        /// <summary>统计发现调用；Java 版本查询另由注入探测器统计。</summary>
        public Task<IReadOnlyList<JavaInstallation>> FindAllAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<JavaInstallation>>([java]);
        }
    }
    /// <summary>仅清理当前测试唯一的临时目录。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
