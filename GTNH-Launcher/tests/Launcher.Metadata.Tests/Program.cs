using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Launcher.Contracts;
using Launcher.Download;
using Launcher.Forge;
using Launcher.Metadata;
using Launcher.Prism;
using Launcher.Runtime;

// 没有 xunit 依赖的小测试台：dotnet run 就能跑，适合网络/还原受限的机器。
var passed = 0;
var failed = 0;

void Test(string name, Action body)
{
    try
    {
        body();
        passed++;
        Console.WriteLine($"  ok   {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"  FAIL {name}");
        Console.WriteLine($"       {ex.Message}");
    }
}

async Task TestAsync(string name, Func<Task> body)
{
    try
    {
        await body();
        passed++;
        Console.WriteLine($"  ok   {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"  FAIL {name}");
        Console.WriteLine($"       {ex.Message}");
    }
}

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

Test("占位符：替换已知、保留未知", () =>
{
    var values = new Dictionary<string, string> { ["a"] = "1", ["b"] = "two" };
    Check(ArgumentBuilder.Expand("x${a}y${b}z", values) == "x1ytwoz", "替换结果不对");
    Check(ArgumentBuilder.Expand("x${unknown}", values) == "x${unknown}", "未知占位符应原样保留");
});

Test("Maven 坐标 → 路径", () =>
{
    Check(ArgumentBuilder.MavenPath("net.minecraft:launchwrapper:1.12")
          == "net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar", "普通坐标解析错误");
    Check(ArgumentBuilder.MavenPath("org.lwjgl.lwjgl:lwjgl-platform:2.9.1:natives-windows")
          == "org/lwjgl/lwjgl/lwjgl-platform/2.9.1/lwjgl-platform-2.9.1-natives-windows.jar", "classifier 解析错误");
    Check(ArgumentBuilder.MavenPath("a.b:c:1@zip") == "a/b/c/1/c-1.zip", "扩展名解析错误");
});

Test("rules：twitch 的 disallow linux 在 Windows 上仍允许", () =>
{
    var twitch = new LibraryJson
    {
        Name = "tv.twitch:twitch-platform:5.16",
        Rules = new List<RuleJson>
        {
            new() { Action = "allow" },
            new() { Action = "disallow", Os = new OsRule { Name = "linux" } },
        },
    };
    Check(RuleEvaluator.IsAllowed(twitch.Rules, RuleContext.WindowsX64()), "Windows 应允许");
    Check(!RuleEvaluator.IsAllowed(twitch.Rules, RuleContext.LinuxX64()), "Linux 应禁止");
    Check(RuleEvaluator.IsAllowed(null, RuleContext.LinuxX64()), "没有 rules 应默认允许");
});

Test("离线 UUID 与 Java 版一致", () =>
{
    Check(OfflineUuid.ForName("Steve") == "5627dd98-e6be-3c21-b8a8-e92344183641", "Steve 的离线 UUID 不对");
    Check(OfflineUuid.ForName("Notch") == "b50ad385-829d-3141-a216-7e7d7539ba7f", "Notch 的离线 UUID 不对");
    Check(OfflineUuid.ForNameNoDashes("Steve").Length == 32, "无连字符版本长度应为 32");
});

Test("1.7.10 真实版本 JSON：主类 / classpath / natives", () =>
{
    var version = MetadataJson.Load(DataFile("1.7.10.json"));
    var resolved = VersionMerger.Merge(version, null);
    var plan = LaunchPlanner.Build(resolved, MakeRequest(), RuleContext.WindowsX64());

    Check(plan.MainClass == "net.minecraft.client.main.Main", $"主类不对：{plan.MainClass}");

    var entries = plan.Classpath.Split(Path.PathSeparator);
    Check(entries.Length == 30, $"classpath 应有 30 项（29 个库 + 客户端 jar），实际 {entries.Length}");
    Check(entries[^1].EndsWith(Path.Combine("versions", "1.7.10", "1.7.10.jar")), "classpath 末尾应是客户端 jar");

    Check(plan.Natives.Count == 4, $"natives 应有 4 个 jar，实际 {plan.Natives.Count}");
    Check(plan.Natives.All(n => FileName(n.JarPath) is { } f && (f.Contains("natives-windows"))),
        "Windows 平台应选中 natives-windows 分类器");

    Check(plan.JvmArgs.Any(a => a.StartsWith("-Dlog4j.configurationFile=", StringComparison.Ordinal)
                                 && a.EndsWith("client-1.7.xml", StringComparison.Ordinal)), "缺少 log4j 参数");
    Check(plan.JvmArgs.Contains("-Djava.library.path=" + Path.Combine(Path.GetFullPath(@"E:\test\instance"), "natives")),
        "java.library.path 不对");

    Check(plan.GameArgs.Contains("--username") && plan.GameArgs.Contains("Steve"), "缺少用户名参数");
    Check(plan.GameArgs.Contains("--userProperties") && plan.GameArgs.Contains("{}"), "userProperties 应为 {}");
    Check(plan.GameArgs.Contains("--assetIndex") && plan.GameArgs.Contains("1.7.10"), "assetIndex 不对");
    Check(!plan.EnumerateArgs().Any(a => a.Contains("${", StringComparison.Ordinal)),
        "还有未替换的占位符：" + string.Join(" | ", plan.EnumerateArgs().Where(a => a.Contains("${"))));
});

Test("Forge 合并：主类 / tweakClass / 同坐标去重", () =>
{
    var parentJson = MetadataJson.Load(DataFile("1.7.10.json"));
    var forgeJson = MetadataJson.Load(DataFile("forge-1.7.10.sample.json"));
    var resolved = VersionMerger.Merge(forgeJson, parentJson);

    Check(resolved.ClientJarVersionId == "1.7.10", "客户端 jar 应继承父版本");
    var plan = LaunchPlanner.Build(resolved, MakeRequest(versionName: forgeJson.Id!), RuleContext.WindowsX64());

    Check(plan.MainClass == "net.minecraft.launchwrapper.Launch", $"主类不对：{plan.MainClass}");
    Check(plan.Classpath.Contains("forge-1.7.10-10.13.4.1614-universal.jar"), "classpath 缺少 Forge universal");
    Check(plan.Classpath.Contains("launchwrapper-1.12.jar"), "classpath 缺少 launchwrapper");
    Check(plan.Classpath.Contains("jopt-simple-5.0.1.jar"), "子版本的 jopt-simple 应生效");
    Check(!plan.Classpath.Contains("jopt-simple-4.5.jar"), "父版本的同坐标库应被去掉");

    var tweakIndex = plan.GameArgs.ToList().IndexOf("--tweakClass");
    Check(tweakIndex >= 0 && plan.GameArgs[tweakIndex + 1] == "cpw.mods.fml.common.launcher.FMLTweaker",
        "--tweakClass 没拼进去");
});

Test("natives 解压：跳过 META-INF", () =>
{
    var dir = TempDir();
    try
    {
        var jar = Path.Combine(dir, "natives.jar");
        using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry("lwjgl.dll").Open()) s.WriteByte(1);
            using (var s = zip.CreateEntry("META-INF/MANIFEST.MF").Open()) s.WriteByte(1);
        }

        var outDir = Path.Combine(dir, "out");
        var count = NativeExtractor.Extract(new[] { new NativeJar("test", jar, new[] { "META-INF/" }) }, outDir);

        Check(count == 1, $"应只解压 1 个文件，实际 {count}");
        Check(File.Exists(Path.Combine(outDir, "lwjgl.dll")), "lwjgl.dll 没解出来");
        Check(!Directory.Exists(Path.Combine(outDir, "META-INF")), "META-INF 不该被解压");
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
});

// ---------------------------------------------------------------------------
// 下面是本轮新增的：实例布局 / 预检 / 进程层 / 崩溃诊断
// ---------------------------------------------------------------------------

Test("实例布局：所有路径都挂在 .minecraft 下", () =>
{
    var layout = new InstanceLayout(@"E:\inst");
    var game = Path.Combine(@"E:\inst", ".minecraft");

    Check(layout.GameDirectory == game, $"游戏目录不对：{layout.GameDirectory}");
    Check(layout.NativesDirectory == Path.Combine(game, "natives"), "natives 目录不对");
    Check(layout.VersionJsonPath("1.7.10") == Path.Combine(game, "versions", "1.7.10", "1.7.10.json"),
        $"版本 JSON 路径不对：{layout.VersionJsonPath("1.7.10")}");
    Check(layout.ClientJarPath("1.7.10").EndsWith(Path.Combine("versions", "1.7.10", "1.7.10.jar")), "客户端 jar 路径不对");
    Check(layout.AssetsIndexPath("1.7.10").EndsWith(Path.Combine("assets", "indexes", "1.7.10.json")), "assets index 路径不对");
    Check(layout.LogConfigPath("client-1.7.xml").EndsWith(Path.Combine("assets", "log_configs", "client-1.7.xml")), "log config 路径不对");
});

Test("预检：空实例应报缺文件且不 Ready", () =>
{
    var dir = TempDir();
    try
    {
        var layout = new InstanceLayout(dir);
        var version = VersionMerger.Merge(MetadataJson.Load(DataFile("1.7.10.json")), null);
        var prep = LaunchService.CreateDefault()
            .Prepare(layout, version, MakeRequest(gameDir: layout.GameDirectory), RuleContext.WindowsX64());

        Check(!prep.Ready, "空目录不该就绪");
        Check(prep.MissingFiles.Count >= 30, $"缺失文件应 >= 30，实际 {prep.MissingFiles.Count}");
        Check(prep.MissingFiles.Any(f => f.EndsWith("1.7.10.jar", StringComparison.OrdinalIgnoreCase)), "应缺少客户端 jar");
        Check(prep.MissingFiles.Any(f => f.EndsWith("1.7.10.json", StringComparison.OrdinalIgnoreCase)), "应缺少版本 JSON");
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
});

Test("预检：文件齐全时 Ready", () =>
{
    var dir = TempDir();
    try
    {
        var layout = new InstanceLayout(dir);
        Directory.CreateDirectory(layout.VersionsDirectory);
        Directory.CreateDirectory(layout.AssetsDirectory);

        var version = VersionMerger.Merge(MetadataJson.Load(DataFile("1.7.10.json")), null);
        var jsonPath = layout.VersionJsonPath("1.7.10");
        CopyFile(DataFile("1.7.10.json"), jsonPath);

        var javaPath = Path.Combine(dir, "fake-java.exe");
        File.WriteAllText(javaPath, "");

        var request = MakeRequest(gameDir: layout.GameDirectory, javaPath: javaPath);
        var plan = LaunchPlanner.Build(version, request, RuleContext.WindowsX64());
        foreach (var entry in plan.Classpath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            CreateFile(entry);
        CreateFile(layout.AssetsIndexPath("1.7.10"));
        CreateFile(layout.LogConfigPath("client-1.7.xml"));
        foreach (var native in plan.Natives)
            CreateFile(native.JarPath);

        var prep = LaunchService.CreateDefault()
            .Prepare(layout, version, request, RuleContext.WindowsX64(), jsonPath);

        Check(prep.Ready, "应就绪，缺：" + string.Join(", ", prep.MissingFiles));
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
});

await TestAsync("启动服务：预检不通过时不碰进程，直接给诊断", async () =>
{
    var dir = TempDir();
    try
    {
        var layout = new InstanceLayout(dir);
        var version = VersionMerger.Merge(MetadataJson.Load(DataFile("1.7.10.json")), null);
        var prep = LaunchService.CreateDefault()
            .Prepare(layout, version, MakeRequest(gameDir: layout.GameDirectory), RuleContext.WindowsX64());

        var outcome = await LaunchService.CreateDefault().RunAsync(prep);
        Check(outcome.Diagnosis?.Code == "MISSING_FILES", $"诊断码应为 MISSING_FILES，实际 {outcome.Diagnosis?.Code}");
        Check(outcome.Process.ExitCode == -1, "不该有真实进程退出码");
        Check(outcome.LogPath is null, "没启动就不该有日志文件");
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
});

Test("诊断：OOM 优先于 mod 冲突，正常退出不报", () =>
{
    var diagnoser = new CrashDiagnoser();

    Check(diagnoser.Diagnose(Array.Empty<string>(), 0) is null, "退出码 0 不应报崩溃");
    Check(diagnoser.Diagnose(Array.Empty<string>(), null) is null, "没有退出码也不应报");

    var oom = diagnoser.Diagnose(new[] { "[Client thread] java.lang.OutOfMemoryError: Java heap space" }, 1);
    Check(oom?.Code == "OOM", $"应识别为 OOM，实际 {oom?.Code}");

    var priority = diagnoser.Diagnose(
        new[] { "java.lang.OutOfMemoryError: Java heap space", "java.lang.NoSuchMethodError: x" }, 1);
    Check(priority?.Code == "OOM", "规则优先级：OOM 应先于 MOD_CONFLICT");

    var access = diagnoser.Diagnose(Array.Empty<string>(), unchecked((int)0xC0000005));
    Check(access?.Code == "ACCESS_VIOLATION", $"应识别为 ACCESS_VIOLATION，实际 {access?.Code}");

    var mod = diagnoser.Diagnose(new[] { "java.lang.NoSuchMethodError: cpw.mods.fml..." }, 1);
    Check(mod?.Code == "MOD_CONFLICT", $"应识别为 MOD_CONFLICT，实际 {mod?.Code}");

    var unknown = diagnoser.Diagnose(new[] { "something odd" }, 1);
    Check(unknown?.Code == "UNKNOWN", $"未知退出应给 UNKNOWN，实际 {unknown?.Code}");
});

await TestAsync("进程层：stdout / 退出码 / stderr", async () =>
{
    if (!OperatingSystem.IsWindows()) return;

    var runner = new JavaProcessRunner();

    var lines = new List<ProcessLine>();
    var result = await runner.RunAsync("cmd.exe", new[] { "/d", "/c", "echo hello-runner" }, AppContext.BaseDirectory, lines.Add);
    Check(result.ExitCode == 0, $"退出码应为 0，实际 {result.ExitCode}（{result.Error}）");
    Check(result.Error is null, "不该有启动错误");
    Check(lines.Any(l => l.Stream == "stdout" && l.Text.Contains("hello-runner")), "没抓到 stdout");

    var failedRun = await runner.RunAsync("cmd.exe", new[] { "/d", "/c", "exit 3" }, AppContext.BaseDirectory);
    Check(failedRun.ExitCode == 3, $"退出码应为 3，实际 {failedRun.ExitCode}");

    var errLines = new List<ProcessLine>();
    var withErr = await runner.RunAsync("cmd.exe", new[] { "/d", "/c", "echo oops 1>&2" }, AppContext.BaseDirectory, errLines.Add);
    Check(withErr.ExitCode == 0, "stderr 用例退出码应为 0");
    Check(errLines.Any(l => l.Stream == "stderr" && l.Text.Contains("oops")), "没抓到 stderr");
});

await TestAsync("进程层：启动失败返回 Error 而不是抛异常", async () =>
{
    var runner = new JavaProcessRunner();
    var result = await runner.RunAsync(
        "no-such-executable-" + Guid.NewGuid().ToString("N") + ".exe",
        Array.Empty<string>(),
        AppContext.BaseDirectory);

    Check(result.Error is not null, "启动失败应有 Error");
    Check(result.ExitCode == -1, "启动失败退出码应为 -1");
});

await TestAsync("进程层：取消会杀掉进程", async () =>
{
    if (!OperatingSystem.IsWindows()) return;

    var runner = new JavaProcessRunner();
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    var started = DateTime.UtcNow;
    var result = await runner.RunAsync(
        "cmd.exe",
        new[] { "/d", "/c", "ping -n 10 127.0.0.1" },
        AppContext.BaseDirectory,
        ct: cts.Token);

    Check(result.Cancelled, "应标记为已取消");
    Check(DateTime.UtcNow - started < TimeSpan.FromSeconds(8), "取消后应尽快返回");
});

// ---------------------------------------------------------------------------
// 下载引擎（M4）：镜像 / 任务清单 / 资产索引 / 下载器
// ---------------------------------------------------------------------------

Test("镜像：库与 assets 走 BMCLAPI，其它原样", () =>
{
    Check(Mirror.ToMirror("https://libraries.minecraft.net/com/mojang/x.jar")
          == "https://bmclapi2.bangbang93.com/libraries/com/mojang/x.jar", "库地址映射错误");
    Check(Mirror.ToMirror("https://resources.download.minecraft.net/ab/abcd")
          == "https://bmclapi2.bangbang93.com/assets/ab/abcd", "assets 地址映射错误");
    Check(Mirror.ToMirror("https://piston-meta.mojang.com/v1/packages/x.json") is null,
        "没验证过的地址不该硬映射");
});

Test("下载计划：1.7.10 共 36 个任务且各类数量正确", () =>
{
    var version = VersionMerger.Merge(MetadataJson.Load(DataFile("1.7.10.json")), null);
    var tasks = DownloadPlanner.Build(version, @"E:\test\instance\.minecraft", RuleContext.WindowsX64());

    Check(tasks.Count == 36, $"任务数应为 36，实际 {tasks.Count}");
    Check(tasks.Count(t => t.Kind == "client") == 1, "客户端 jar 应为 1 个");
    Check(tasks.Count(t => t.Kind == "library") == 29, $"库任务应为 29 个，实际 {tasks.Count(t => t.Kind == "library")}");
    Check(tasks.Count(t => t.Kind == "natives") == 4, $"natives 应为 4 个，实际 {tasks.Count(t => t.Kind == "natives")}");
    Check(tasks.Count(t => t.Kind == "asset-index") == 1, "资源索引应为 1 个");
    Check(tasks.Count(t => t.Kind == "log-config") == 1, "日志配置应为 1 个");
    Check(tasks.All(t => t.Sha1 is not null), "1.7.10 的每个任务都应有 sha1");
    Check(tasks.Select(t => t.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() == tasks.Count,
        "同一路径不该排两次");
});

Test("资产索引：对象文件按 hash 前两位分目录", () =>
{
    var json = """{"objects":{"icons/icon_16x16.png":{"hash":"a1b2c3d4e5","size":123}}}""";
    var index = AssetIndexJson.Parse(json);
    var tasks = DownloadPlanner.BuildAssets(index, @"E:\inst");

    Check(tasks.Count == 1, $"应排出 1 个资源任务，实际 {tasks.Count}");
    Check(tasks[0].Destination.EndsWith(Path.Combine("assets", "objects", "a1", "a1b2c3d4e5")),
        $"落盘路径不对：{tasks[0].Destination}");
    Check(tasks[0].Url == "https://resources.download.minecraft.net/a1/a1b2c3d4e5", $"下载地址不对：{tasks[0].Url}");
});

await TestAsync("下载器：写入文件、sha1 校验、已存在则跳过", async () =>
{
    var dir = TempDir();
    try
    {
        var payload = Encoding.UTF8.GetBytes("hello-download");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var downloader = new HttpFileDownloader(new HttpClient(handler), maxParallel: 2);

        var destination = Path.Combine(dir, "a.jar");
        var task = new DownloadTask("https://libraries.minecraft.net/x/a.jar", destination, sha1, payload.Length, "a.jar");

        await downloader.DownloadAsync(new[] { task });
        Check(File.Exists(destination), "文件没写出来");
        Check(new FileInfo(destination).Length == payload.Length, "大小不对");

        var calls = handler.Calls;
        await downloader.DownloadAsync(new[] { task });
        Check(handler.Calls == calls, "已存在且大小一致的文件不该再发请求");
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
});

await TestAsync("下载器：sha1 不匹配要报错且不留半成品", async () =>
{
    var dir = TempDir();
    try
    {
        var payload = Encoding.UTF8.GetBytes("bad");
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var downloader = new HttpFileDownloader(new HttpClient(handler));

        var destination = Path.Combine(dir, "b.jar");
        var task = new DownloadTask(
            "https://libraries.minecraft.net/x/b.jar",
            destination,
            "0000000000000000000000000000000000000000",
            payload.Length,
            "b.jar");

        var threw = false;
        try
        {
            await downloader.DownloadAsync(new[] { task });
        }
        catch (IOException)
        {
            threw = true;
        }

        Check(threw, "sha1 不匹配应抛 IOException");
        Check(!File.Exists(destination), "失败后不该留下目标文件");
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
});

// ---------------------------------------------------------------------------
// Forge 安装（M5）：解包 installer → 版本 JSON + universal jar
// ---------------------------------------------------------------------------

Test("Forge 安装：解包 installer、写出可直接启动的版本 JSON", () =>
{
    var dir = TempDir();
    try
    {
        var installer = Path.Combine(dir, "forge-installer.jar");
        var profile = """
        {
          "install": {
            "profileName": "Forge",
            "target": "1.7.10-Forge10.13.4.1614-1.7.10",
            "path": "net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10",
            "version": "Forge 10.13.4.1614-1.7.10",
            "filePath": "forge-1.7.10-10.13.4.1614-1.7.10-universal.jar",
            "minecraft": "1.7.10"
          },
          "versionInfo": {
            "id": "whatever",
            "mainClass": "net.minecraft.launchwrapper.Launch",
            "minecraftArguments": "--username ${auth_player_name} --tweakClass cpw.mods.fml.common.launcher.FMLTweaker",
            "inheritsFrom": "1.7.10",
            "libraries": [
              { "name": "net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10", "url": "https://maven.minecraftforge.net/" }
            ]
          }
        }
        """;

        using (var zip = ZipFile.Open(installer, ZipArchiveMode.Create))
        {
            using (var stream = zip.CreateEntry("install_profile.json").Open())
                stream.Write(Encoding.UTF8.GetBytes(profile));
            using (var stream = zip.CreateEntry("forge-1.7.10-10.13.4.1614-1.7.10-universal.jar").Open())
                stream.Write(Encoding.UTF8.GetBytes("fake-universal"));
        }

        var instance = Path.Combine(dir, "inst");
        var result = new ForgeInstaller().InstallAsync(
            installer, instance, baseVersionJsonPath: DataFile("1.7.10.json")).GetAwaiter().GetResult();

        Check(result.VersionId == "1.7.10-Forge10.13.4.1614", $"版本 id 推断错误：{result.VersionId}");
        Check(File.Exists(result.VersionJsonPath), "版本 JSON 没写出来");
        Check(File.Exists(result.UniversalJarPath), "universal jar 没解出来");
        Check(result.UniversalJarPath.EndsWith(Path.Combine(
                  "net", "minecraftforge", "forge", "1.7.10-10.13.4.1614-1.7.10",
                  "forge-1.7.10-10.13.4.1614-1.7.10-universal.jar")),
            $"universal jar 路径不对：{result.UniversalJarPath}");

        var layout = new InstanceLayout(instance);
        Check(File.Exists(layout.VersionJsonPath("1.7.10")), "基础版本 JSON 应被复制进实例");

        var child = MetadataJson.Load(result.VersionJsonPath);
        Check(child.InheritsFrom == "1.7.10", "inheritsFrom 应指向 1.7.10");

        var resolved = VersionMerger.Merge(child, MetadataJson.Load(DataFile("1.7.10.json")));
        var plan = LaunchPlanner.Build(
            resolved, MakeRequest(gameDir: layout.GameDirectory, versionName: resolved.Id), RuleContext.WindowsX64());

        Check(resolved.MainClass == "net.minecraft.launchwrapper.Launch", "主类应为 launchwrapper");
        Check(plan.Classpath.Contains("forge-1.7.10-10.13.4.1614-1.7.10-universal.jar"), "classpath 缺少 universal jar");
        Check(plan.GameArgs.Contains("cpw.mods.fml.common.launcher.FMLTweaker"), "--tweakClass 没保留");
        Check(resolved.Downloads?.Client is not null, "客户端 jar 下载信息应从基础版本继承");
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
});

// ---------------------------------------------------------------------------
// Java 探测（M6）：读 release、识别大版本
// ---------------------------------------------------------------------------

Test("Java 探测：解析 release 与版本号", () =>
{
    var dir = TempDir();
    try
    {
        var java8Home = Path.Combine(dir, "jdk8");
        Directory.CreateDirectory(Path.Combine(java8Home, "bin"));
        File.WriteAllText(Path.Combine(java8Home, "bin", "javaw.exe"), "");
        File.WriteAllText(Path.Combine(java8Home, "release"), "JAVA_VERSION=\"1.8.0_51\"\n");

        var candidate = JavaLocator.ParseInstall(java8Home);
        Check(candidate is not null, "应识别出 Java");
        Check(candidate!.Major == 8, $"1.8.0_51 应识别为 8，实际 {candidate.Major}");
        Check(JavaLocator.ParseMajor("17.0.9") == 17, "17.0.9 应识别为 17");
        Check(JavaLocator.ParseMajor("1.8.0_51") == 8, "1.8.0_51 应识别为 8");
        Check(JavaLocator.ParseMajor("21") == 21, "21 应识别为 21");

        var found = JavaLocator.Scan(dir).ToList();
        Check(found.Any(java => java.Major == 8), "Scan 应扫描到子目录里的 Java");
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
});

Test("Prism：patch 合并（主类取最高 order、库倒序去重、tweakers 累加）", () =>
{
    var (root, instanceDirectory) = MakePrismInstance();
    try
    {
        var instance = PrismInstance.Open(instanceDirectory);

        Check(instance.MainClass == "net.minecraft.launchwrapper.Launch", $"主类应取最高 order，实际 {instance.MainClass}");
        Check(instance.MinMemoryMb == 2048 && instance.MaxMemoryMb == 6144, "OverrideMemory=true 时应用实例自己的内存");
        Check(instance.Tweakers.Count == 1 && instance.Tweakers[0].EndsWith("FMLTweaker"), "tweakers 应累加");
        Check(instance.ExtraJvmArgs.Count == 3, $"jvmArgs 应原样保留 3 条，实际 {instance.ExtraJvmArgs.Count}");
        Check(instance.Libraries.Count == 5, $"合并去重后应有 5 个库，实际 {instance.Libraries.Count}");
        Check(instance.Libraries[0].Name!.StartsWith("net.minecraftforge:forge", StringComparison.Ordinal),
            "高 order 补丁的库应排在前面");
        Check(instance.Libraries.Count(l => l.Name!.StartsWith("org.lwjgl:lwjgl:", StringComparison.Ordinal)) == 1,
            "同一个 group:artifact 只能保留一份");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
});

Test("Prism：拼命令行（rules 过滤、缺 path 兜底、客户端 jar 收尾）", () =>
{
    var (root, instanceDirectory) = MakePrismInstance();
    try
    {
        var instance = PrismInstance.Open(instanceDirectory);
        var request = MakeRequest(player: "Alex", versionName: instance.Name, gameDir: instance.GameDirectory)
            with { MinMemoryMb = instance.MinMemoryMb, MaxMemoryMb = instance.MaxMemoryMb };
        var plan = PrismPlanner.Build(instance, request, RuleContext.WindowsX64());

        var entries = plan.Classpath.Split(Path.PathSeparator);
        Check(entries.Length == 5, $"classpath 应有 5 项，实际 {entries.Length}");
        Check(entries[0].EndsWith("forge-1.7.10-10.13.4.1614-1.7.10-universal.jar", StringComparison.Ordinal),
            "第一个应是 Forge universal");
        Check(entries[^1].EndsWith("minecraft-1.7.10-client.jar", StringComparison.Ordinal), "最后应是客户端 jar");
        Check(entries.Any(e => e.EndsWith("com/example/noartifact/1.0/noartifact-1.0.jar".Replace('/', Path.DirectorySeparatorChar),
            StringComparison.Ordinal)), "没有 downloads 的库要按 Maven 坐标兜底");
        Check(!entries.Any(e => e.Contains("only-linux", StringComparison.Ordinal)), "linux-only 的库应被 rules 过滤");

        Check(plan.JvmArgs.Contains("-Xms2048M") && plan.JvmArgs.Contains("-Xmx6144M"), "内存参数不对");
        Check(plan.JvmArgs.Contains("-Djava.library.path=" + instance.NativesDirectory), "缺少 java.library.path");
        Check(plan.JvmArgs.Contains("-Dfile.encoding=UTF-8"), "补丁 jvmArgs 应保留");
        Check(plan.GameArgs.Contains("Alex") && plan.GameArgs.Contains(instance.GameDirectory), "游戏参数没展开");
        Check(plan.GameArgs.Contains("--tweakClass") && plan.GameArgs.Contains("cpw.mods.fml.common.launcher.FMLTweaker"),
            "tweaker 应展开成 --tweakClass");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
});

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static string DataFile(string name) => Path.Combine(AppContext.BaseDirectory, "data", name);

static string FileName(string path) => Path.GetFileName(path);

static string TempDir()
{
    var dir = Path.Combine(AppContext.BaseDirectory, "tmp-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return dir;
}

/// <summary>造一个最小可用的 Prism 实例：3 个补丁（order 不同）、重复库、linux-only 库、缺失 path 的库。</summary>
static (string Root, string InstanceDirectory) MakePrismInstance()
{
    var root = TempDir();
    var instanceDirectory = Path.Combine(root, "instances", "GTNH");
    var patches = Path.Combine(instanceDirectory, "patches");
    Directory.CreateDirectory(patches);
    Directory.CreateDirectory(Path.Combine(instanceDirectory, ".minecraft"));
    Directory.CreateDirectory(Path.Combine(instanceDirectory, "natives"));
    Directory.CreateDirectory(Path.Combine(instanceDirectory, "libraries"));

    File.WriteAllText(Path.Combine(instanceDirectory, "instance.cfg"),
        "InstanceType=OneSix\nname=GTNH-Test\nJavaPath=C:/Java/bin/javaw.exe\nOverrideMemory=true\nMinMemAlloc=2048\nMaxMemAlloc=6144\n");
    File.WriteAllText(Path.Combine(root, "prismlauncher.cfg"), "MaxMemAlloc=4096\nMinMemAlloc=512\n");

    File.WriteAllText(Path.Combine(patches, "01-minecraft.json"), """
        { "order": -2, "mainClass": "net.minecraft.client.main.Main",
          "minecraftArguments": "--username ${auth_player_name} --gameDir ${game_directory}",
          "assetIndex": { "id": "1.7.10" },
          "mainJar": { "name": "com.mojang:minecraft:1.7.10:client" },
          "libraries": [
            { "name": "net.minecraft:launchwrapper:1.12",
              "downloads": { "artifact": { "path": "net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar" } } },
            { "name": "org.lwjgl:lwjgl:3.3.1",
              "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar" } } }
          ] }
        """);

    File.WriteAllText(Path.Combine(patches, "02-forge.json"), """
        { "order": 5, "mainClass": "net.minecraft.launchwrapper.Launch",
          "+tweakers": [ "cpw.mods.fml.common.launcher.FMLTweaker" ],
          "+jvmArgs": [ "-Dfile.encoding=UTF-8", "--add-opens", "java.base/java.lang=ALL-UNNAMED" ],
          "libraries": [
            { "name": "net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10:universal",
              "downloads": { "artifact": { "path": "net/minecraftforge/forge/1.7.10-10.13.4.1614-1.7.10/forge-1.7.10-10.13.4.1614-1.7.10-universal.jar" } } },
            { "name": "org.lwjgl:lwjgl:3.3.1",
              "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar" } } }
          ] }
        """);

    File.WriteAllText(Path.Combine(patches, "03-extra.json"), """
        { "order": 4, "libraries": [
            { "name": "com.example:noartifact:1.0" },
            { "name": "com.example:only-linux:1.0",
              "rules": [ { "action": "allow", "os": { "name": "linux" } } ],
              "downloads": { "artifact": { "path": "com/example/only-linux/1.0/only-linux-1.0.jar" } } }
          ] }
        """);

    return (root, instanceDirectory);
}

static void CreateFile(string path)
{
    var dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    File.WriteAllText(path, "");
}

static void CopyFile(string source, string destination)
{
    var dir = Path.GetDirectoryName(destination);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    File.Copy(source, destination, overwrite: true);
}

static LaunchRequest MakeRequest(
    string player = "Steve",
    string versionName = "1.7.10",
    string? gameDir = null,
    string? javaPath = null) => new()
{
    Java = new JavaRuntime(javaPath ?? @"C:\Java\bin\javaw.exe", "1.8.0_51", 8),
    GameDirectory = gameDir ?? @"E:\test\instance",
    VersionName = versionName,
    Auth = new AuthInfo(player, OfflineUuid.ForName(player)),
};

/// <summary>不联网的 HttpMessageHandler：让下载器测试可重复、无外网依赖。</summary>
sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    private int _calls;

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    public int Calls => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(_responder(request));
    }
}
