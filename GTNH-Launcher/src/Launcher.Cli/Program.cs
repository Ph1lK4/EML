using System.Text.Json;
using Launcher.Contracts;
using Launcher.Download;
using Launcher.Forge;
using Launcher.Metadata;
using Launcher.Prism;
using Launcher.Runtime;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
return command switch
{
    "plan" => Plan(args[1..]),
    "run" => await Run(args[1..]),
    "exec" => await Exec(args[1..]),
    "download" => await Download(args[1..]),
    "forge" => await Forge(args[1..]),
    "prism" => Prism(args[1..]),
    "diagnose" => Diagnose(args[1..]),
    "help" or "-h" or "--help" => PrintHelp(),
    _ => Plan(args),
};

static int Plan(string[] argv)
{
    string? versionPath = null;
    string? forgePath = null;
    string? javaPath = null;
    string? instanceRoot = null;
    var player = "Steve";
    var memoryMb = 6144;

    for (var i = 0; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "--forge": forgePath = argv[++i]; break;
            case "--java": javaPath = argv[++i]; break;
            case "--player": player = argv[++i]; break;
            case "--memory": memoryMb = int.Parse(argv[++i]); break;
            case "--instance": instanceRoot = argv[++i]; break;
            default: versionPath = argv[i]; break;
        }
    }

    if (forgePath is not null) versionPath = forgePath;
    versionPath ??= Path.Combine(AppContext.BaseDirectory, "data", "1.7.10.json");

    var child = MetadataJson.Load(versionPath);
    var parent = LoadParent(child, Path.GetDirectoryName(Path.GetFullPath(versionPath))!);
    if (child.InheritsFrom is not null && parent is null)
        Console.WriteLine($"[警告] 找不到父版本 {child.InheritsFrom}.json，库列表会不完整");

    var resolved = VersionMerger.Merge(child, parent);
    var gameDir = instanceRoot is null
        ? Path.Combine(Environment.CurrentDirectory, "instance", ".minecraft")
        : new InstanceLayout(instanceRoot).GameDirectory;

    var request = new LaunchRequest
    {
        Java = new JavaRuntime(javaPath ?? @"C:\Program Files\Java\jre1.8.0_51\bin\javaw.exe", "1.8.0_51", 8, Source: "demo"),
        GameDirectory = gameDir,
        VersionName = resolved.Id,
        Auth = new AuthInfo(player, OfflineUuid.ForName(player)),
        MaxMemoryMb = memoryMb,
    };

    var plan = LaunchPlanner.Build(resolved, request, PlatformContext());
    PrintPlan(resolved, plan);
    WarnLeftovers(plan);
    return 0;
}

static async Task<int> Run(string[] argv)
{
    string? instance = null;
    string? versionId = null;
    string? java = null;
    var player = "Steve";
    var memoryMb = 6144;
    var dryRun = false;

    for (var i = 0; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "--instance": instance = argv[++i]; break;
            case "--version": versionId = argv[++i]; break;
            case "--java": java = argv[++i]; break;
            case "--player": player = argv[++i]; break;
            case "--memory": memoryMb = int.Parse(argv[++i]); break;
            case "--dry-run": dryRun = true; break;
            default:
                Console.Error.WriteLine($"未知参数：{argv[i]}");
                return 1;
        }
    }

    if (instance is null || versionId is null)
    {
        Console.Error.WriteLine("用法：run --instance <实例目录> --version <版本 id> [--java <java.exe>] [--memory 6144] [--player 名字] [--dry-run]");
        return 1;
    }

    var layout = new InstanceLayout(instance);
    var jsonPath = layout.VersionJsonPath(versionId);
    if (!File.Exists(jsonPath))
    {
        Console.Error.WriteLine($"找不到版本 JSON：{jsonPath}");
        Console.Error.WriteLine("（Forge 版本要先装好：versions/<版本 id>/<版本 id>.json）");
        return 1;
    }

    var child = MetadataJson.Load(jsonPath);
    VersionJson? parent = null;
    if (child.InheritsFrom is { } parentId)
    {
        var parentPath = layout.VersionJsonPath(parentId);
        if (File.Exists(parentPath))
            parent = MetadataJson.Load(parentPath);
        else
            Console.WriteLine($"[警告] 找不到父版本 {parentPath}，库列表会不完整");
    }

    var resolved = VersionMerger.Merge(child, parent);
    var request = new LaunchRequest
    {
        Java = new JavaRuntime(java ?? "java", "unknown", 8, Source: "user"),
        GameDirectory = layout.GameDirectory,
        VersionName = versionId,
        Auth = new AuthInfo(player, OfflineUuid.ForName(player)),
        MaxMemoryMb = memoryMb,
    };

    var service = LaunchService.CreateDefault();
    var preparation = service.Prepare(layout, resolved, request, PlatformContext(), jsonPath);

    Console.WriteLine($"实例       : {layout.Root}");
    Console.WriteLine($"游戏目录   : {layout.GameDirectory}");
    Console.WriteLine($"版本       : {resolved.Id}（客户端 jar 取 {resolved.ClientJarVersionId}）");
    Console.WriteLine($"Java       : {preparation.Plan.JavaPath}");
    Console.WriteLine($"主类       : {preparation.Plan.MainClass}");
    Console.WriteLine($"classpath  : {preparation.Plan.Classpath.Split(Path.PathSeparator).Length} 项");
    Console.WriteLine($"natives    : {preparation.Plan.Natives.Count} 个");
    Console.WriteLine($"预检       : {(preparation.Ready ? "就绪" : $"缺 {preparation.MissingFiles.Count} 个文件")}");

    foreach (var file in preparation.MissingFiles.Take(10))
        Console.WriteLine($"  缺少  {file}");
    if (preparation.MissingFiles.Count > 10)
        Console.WriteLine($"  …… 还有 {preparation.MissingFiles.Count - 10} 个");

    Console.WriteLine();
    Console.WriteLine("--- 命令行 ---");
    Console.WriteLine(preparation.Plan.ToDisplayCommandLine());

    if (dryRun)
        return preparation.Ready ? 0 : 2;

    if (!preparation.Ready)
    {
        Console.WriteLine();
        Console.WriteLine("预检没通过：先用下载模块补齐文件；只想看命令就加 --dry-run。");
        return 2;
    }

    Console.WriteLine();
    Console.WriteLine("--- 开始启动 ---");
    var outcome = await service.RunAsync(preparation, line => Console.WriteLine($"[{line.Stream}] {line.Text}"));

    Console.WriteLine();
    Console.WriteLine($"--- 进程退出：{outcome.Process.ExitCode}（{outcome.Process.Duration.TotalSeconds:F1}s）{(outcome.Process.Cancelled ? "，已取消" : "")} ---");
    if (outcome.LogPath is not null) Console.WriteLine($"日志：{outcome.LogPath}");
    PrintDiagnosis(outcome.Diagnosis);
    return outcome.Process.ExitCode;
}

static async Task<int> Exec(string[] argv)
{
    string? exe = null;
    string? cwd = null;
    var rest = new List<string>();
    var afterSeparator = false;

    for (var i = 0; i < argv.Length; i++)
    {
        if (afterSeparator)
        {
            rest.Add(argv[i]);
            continue;
        }

        switch (argv[i])
        {
            case "--exe": exe = argv[++i]; break;
            case "--cwd": cwd = argv[++i]; break;
            case "--": afterSeparator = true; break;
            default: rest.Add(argv[i]); break;
        }
    }

    if (exe is null)
    {
        Console.Error.WriteLine("用法：exec --exe <程序> [--cwd <目录>] [--] [参数…]");
        return 1;
    }

    var runner = new JavaProcessRunner();
    var result = await runner.RunAsync(
        exe,
        rest,
        cwd ?? Environment.CurrentDirectory,
        line => Console.WriteLine($"[{line.Stream}] {line.Text}"));

    Console.WriteLine($"退出码 {result.ExitCode}，耗时 {result.Duration.TotalSeconds:F2}s{(result.Cancelled ? "，已取消" : "")}");
    if (result.Error is not null)
    {
        Console.Error.WriteLine(result.Error);
        return 1;
    }

    return result.ExitCode;
}

static async Task<int> Download(string[] argv)
{
    string? instance = null;
    string? versionId = null;
    var threads = 8;
    var dryRun = false;

    for (var i = 0; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "--instance": instance = argv[++i]; break;
            case "--version": versionId = argv[++i]; break;
            case "--threads": threads = int.Parse(argv[++i]); break;
            case "--dry-run": dryRun = true; break;
            default:
                Console.Error.WriteLine($"未知参数：{argv[i]}");
                return 1;
        }
    }

    if (instance is null || versionId is null)
    {
        Console.Error.WriteLine("用法：download --instance <实例目录> --version <版本 id> [--threads 8] [--dry-run]");
        return 1;
    }

    var layout = new InstanceLayout(instance);
    var jsonPath = layout.VersionJsonPath(versionId);
    if (!File.Exists(jsonPath))
    {
        Console.Error.WriteLine($"找不到版本 JSON：{jsonPath}");
        return 1;
    }

    var child = MetadataJson.Load(jsonPath);
    VersionJson? parent = null;
    if (child.InheritsFrom is { } parentId)
    {
        var parentPath = layout.VersionJsonPath(parentId);
        if (File.Exists(parentPath)) parent = MetadataJson.Load(parentPath);
        else Console.WriteLine($"[警告] 找不到父版本 {parentPath}，库列表会不完整");
    }

    var resolved = VersionMerger.Merge(child, parent);
    var platform = PlatformContext();
    var all = DownloadPlanner.Build(resolved, layout.GameDirectory, platform);
    var missing = DownloadPlanner.FilterExisting(all);

    Console.WriteLine($"实例       : {layout.Root}");
    Console.WriteLine($"版本       : {resolved.Id}（客户端 jar 取 {resolved.ClientJarVersionId}）");
    Console.WriteLine($"文件       : 共 {all.Count} 个，缺 {missing.Count} 个");
    foreach (var group in missing.GroupBy(t => t.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {group.Key,-12} {group.Count(),4}");

    if (!dryRun && missing.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"--- 开始下载（{threads} 并发）---");
        var progress = new Progress<DownloadProgress>(p => Console.WriteLine($"[{p.Completed,3}/{p.Total}] {p.Label}"));
        var downloader = new InstanceDownloader(new HttpFileDownloader(maxParallel: threads));
        try
        {
            var downloaded = await downloader.RunAsync(resolved, layout.GameDirectory, platform, progress);
            Console.WriteLine($"本次下载 {downloaded} 个文件。");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"下载失败：{ex.Message}");
            return 1;
        }
    }

    var left = DownloadPlanner.FilterExisting(DownloadPlanner.Build(resolved, layout.GameDirectory, platform));
    Console.WriteLine();
    if (left.Count == 0)
        Console.WriteLine("全部文件就位，可以 run 了。");
    else
        Console.WriteLine($"还缺 {left.Count} 个文件（资源文件要等索引下完才会入列）。");

    return left.Count == 0 ? 0 : 2;
}

static async Task<int> Forge(string[] argv)
{
    if (argv.Length == 0 || argv[0] is not ("install" or "info"))
    {
        Console.Error.WriteLine("用法：forge install --instance <实例目录> --installer <installer.jar> [--version id] [--base 1.7.10.json]");
        Console.Error.WriteLine("      forge info --installer <installer.jar>");
        return 1;
    }

    var sub = argv[0];
    string? instance = null;
    string? installer = null;
    string? version = null;
    string? baseJson = null;

    for (var i = 1; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "--instance": instance = argv[++i]; break;
            case "--installer": installer = argv[++i]; break;
            case "--version": version = argv[++i]; break;
            case "--base": baseJson = argv[++i]; break;
            default:
                Console.Error.WriteLine($"未知参数：{argv[i]}");
                return 1;
        }
    }

    if (installer is null || !File.Exists(installer))
    {
        Console.Error.WriteLine($"找不到安装包：{installer}");
        return 1;
    }

    if (sub == "info")
    {
        var profile = ForgeInstaller.ReadProfile(installer);
        Console.WriteLine($"Minecraft  : {profile.Minecraft}");
        Console.WriteLine($"Forge 版本 : {profile.Version}");
        Console.WriteLine($"库坐标     : {profile.ForgePath}");
        Console.WriteLine($"目标版本   : {profile.Target}");
        return 0;
    }

    if (instance is null)
    {
        Console.Error.WriteLine("forge install 需要 --instance <实例目录>");
        return 1;
    }

    try
    {
        var result = await new ForgeInstaller().InstallAsync(installer, instance, version, baseJson ?? DefaultBaseJson());
        Console.WriteLine($"版本 id    : {result.VersionId}");
        Console.WriteLine($"版本 JSON  : {result.VersionJsonPath}");
        Console.WriteLine($"universal  : {result.UniversalJarPath}");
        Console.WriteLine($"基础版本   : {(result.BaseVersionJsonCopied is null ? "实例里已有" : "从 " + result.BaseVersionJsonCopied + " 复制")}");
        Console.WriteLine();
        Console.WriteLine("下一步：");
        Console.WriteLine($"  download --instance {instance} --version {result.VersionId}");
        Console.WriteLine($"  run      --instance {instance} --version {result.VersionId}");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException)
    {
        Console.Error.WriteLine($"安装失败：{ex.Message}");
        return 1;
    }
}

static string? DefaultBaseJson()
{
    var candidate = Path.Combine(AppContext.BaseDirectory, "data", "1.7.10.json");
    return File.Exists(candidate) ? candidate : null;
}

static int Prism(string[] argv)
{
    if (argv.Length == 0 || argv[0] is not ("plan" or "verify"))
    {
        Console.Error.WriteLine("用法：prism plan|verify --instance <Prism 实例目录> [--root <Prism 根>] [--java <java.exe>] [--memory 6144] [--player 名字]");
        return 1;
    }

    var action = argv[0];
    string? instanceArg = null, rootArg = null, javaArg = null;
    var memory = 0;
    var player = "Steve";

    for (var i = 1; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "--instance":
                instanceArg = NextValue(argv, ref i);
                break;
            case "--root":
                rootArg = NextValue(argv, ref i);
                break;
            case "--java":
                javaArg = NextValue(argv, ref i);
                break;
            case "--memory":
                if (int.TryParse(NextValue(argv, ref i), out var mb)) memory = mb;
                break;
            case "--player":
                player = NextValue(argv, ref i);
                break;
            default:
                Console.Error.WriteLine($"未知参数：{argv[i]}");
                return 1;
        }
    }

    if (instanceArg is null)
    {
        Console.Error.WriteLine("缺少 --instance <Prism 实例目录>");
        return 1;
    }

    PrismInstance instance;
    try
    {
        instance = PrismInstance.Open(instanceArg, rootArg);
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    var javaPath = javaArg ?? instance.ResolveConsoleJava();
    if (javaPath is null)
    {
        Console.Error.WriteLine("实例里没有配置 Java，也没有给 --java。");
        return 1;
    }

    var request = new LaunchRequest
    {
        Java = new JavaRuntime(javaPath, "unknown", 0),
        GameDirectory = instance.GameDirectory,
        VersionName = instance.Name,
        Auth = new AuthInfo(player, OfflineUuid.ForName(player)),
        MinMemoryMb = instance.MinMemoryMb,
        MaxMemoryMb = memory > 0 ? memory : instance.MaxMemoryMb,
    };

    var plan = PrismPlanner.Build(instance, request, PlatformContext());
    var entries = plan.Classpath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

    var missing = new List<string>();
    if (!File.Exists(javaPath)) missing.Add($"Java：{javaPath}");
    missing.AddRange(entries.Where(entry => !File.Exists(entry)));
    var assetIndex = Path.Combine(instance.AssetsDirectory, "indexes", instance.AssetIndexId + ".json");
    if (!File.Exists(assetIndex)) missing.Add(assetIndex);
    if (!Directory.Exists(instance.NativesDirectory)) missing.Add(instance.NativesDirectory);

    Console.WriteLine($"实例       : {instance.Name}");
    Console.WriteLine($"Prism 根   : {instance.PrismRoot}");
    Console.WriteLine($"游戏目录   : {instance.GameDirectory}");
    Console.WriteLine($"Java       : {javaPath}");
    Console.WriteLine($"主类       : {plan.MainClass}");
    Console.WriteLine($"classpath  : {entries.Length} 项");
    Console.WriteLine($"natives    : {instance.NativesDirectory}");
    Console.WriteLine($"内存       : {request.MinMemoryMb}-{request.MaxMemoryMb} MB");

    if (missing.Count == 0)
    {
        Console.WriteLine("体检       : 文件齐全，可以直接启动（本次没有联网、没有下载、没有写盘）");
    }
    else
    {
        Console.WriteLine($"体检       : 缺 {missing.Count} 个文件");
        foreach (var item in missing.Take(12))
            Console.WriteLine("   - " + item);
        if (missing.Count > 12) Console.WriteLine($"   ... 其余 {missing.Count - 12} 个略");
    }

    if (action == "plan")
    {
        Console.WriteLine();
        Console.WriteLine("--- 命令行（Prism 实例，不下载直接拼出来）---");
        Console.WriteLine(plan.ToDisplayCommandLine());
    }

    return missing.Count == 0 ? 0 : 2;
}

static string NextValue(string[] argv, ref int index)
{
    if (index + 1 >= argv.Length) throw new ArgumentException($"参数 {argv[index]} 后面缺值");
    return argv[++index];
}

static int Diagnose(string[] argv)
{
    if (argv.Length == 0)
    {
        Console.Error.WriteLine("用法：diagnose <日志文件> [退出码]");
        return 1;
    }

    var path = argv[0];
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"找不到日志：{path}");
        return 1;
    }

    var exitCode = argv.Length > 1 ? int.Parse(argv[1]) : 1;
    var lines = File.ReadLines(path).TakeLast(2000).ToList();
    var diagnosis = new CrashDiagnoser().Diagnose(lines, exitCode);

    if (diagnosis is null)
    {
        Console.WriteLine("退出码 0，没有崩。");
        return 0;
    }

    PrintDiagnosis(diagnosis);
    return 0;
}

static void PrintPlan(ResolvedVersion resolved, LaunchPlan plan)
{
    var requirement = resolved.Java;
    Console.WriteLine($"版本       : {resolved.Id}（客户端 jar 取 {resolved.ClientJarVersionId}）");
    Console.WriteLine($"Java 需求  : {(requirement is null ? "未声明" : $"{requirement.Component} / Java {requirement.MajorVersion}")}");
    Console.WriteLine($"主类       : {plan.MainClass}");
    Console.WriteLine($"classpath  : {plan.Classpath.Split(Path.PathSeparator).Length} 项");
    Console.WriteLine($"natives    : {plan.Natives.Count} 个 jar");
    foreach (var native in plan.Natives)
        Console.WriteLine($"             - {Path.GetFileName(native.JarPath)}");
    Console.WriteLine($"工作目录    : {plan.WorkingDirectory}");
    Console.WriteLine();
    Console.WriteLine("--- 命令行（启动核心的最终产物）---");
    Console.WriteLine(plan.ToDisplayCommandLine());
}

static void WarnLeftovers(LaunchPlan plan)
{
    var leftovers = plan.EnumerateArgs().Where(arg => arg.Contains("${", StringComparison.Ordinal)).ToList();
    if (leftovers.Count == 0) return;

    Console.WriteLine();
    Console.WriteLine($"[警告] 还有 {leftovers.Count} 个未替换的占位符：{string.Join(", ", leftovers)}");
}

static void PrintDiagnosis(CrashDiagnosis? diagnosis)
{
    if (diagnosis is null) return;

    Console.WriteLine();
    Console.WriteLine($"诊断：{diagnosis.Title} [{diagnosis.Code}]");
    Console.WriteLine(diagnosis.Detail);
    if (diagnosis.MatchedLine is not null)
        Console.WriteLine($"命中：{diagnosis.MatchedLine}");
    Console.WriteLine("建议：");
    foreach (var suggestion in diagnosis.Suggestions)
        Console.WriteLine($"  - {suggestion}");
}

static RuleContext PlatformContext() =>
    OperatingSystem.IsWindows() ? RuleContext.WindowsX64() : RuleContext.LinuxX64();

static VersionJson? LoadParent(VersionJson child, string directory)
{
    if (child.InheritsFrom is not { } id) return null;
    var candidates = new[]
    {
        Path.Combine(directory, id + ".json"),
        Path.Combine(AppContext.BaseDirectory, "data", id + ".json"),
    };
    foreach (var candidate in candidates)
    {
        if (File.Exists(candidate)) return MetadataJson.Load(candidate);
    }
    return null;
}

static int PrintHelp()
{
    Console.WriteLine(
        """
        用法: dotnet run --project src/Launcher.Cli -- <命令> [参数]

        命令:
          plan [版本 JSON] [--forge JSON] [--java path] [--player 名字] [--memory MB] [--instance 目录]
               拼出命令行并打印（默认读 data/1.7.10.json）
          run --instance <目录> --version <版本 id> [--java <java.exe>] [--memory MB] [--player 名字] [--dry-run]
               预检 + 真启动（实例目录下要有 .minecraft）
          exec --exe <程序> [--cwd <目录>] [--] [参数…]
               用同一个进程层跑任意命令（调试进程管理/看流式输出）
          download --instance <目录> --version <版本 id> [--threads 8] [--dry-run]
               按版本 JSON 把缺的文件补齐（镜像优先、sha1 校验）
          forge install --instance <目录> --installer <installer.jar> [--version id] [--base 1.7.10.json]
               解包 Forge 安装器（不跑 java），写出版本 JSON + universal jar
          forge info --installer <installer.jar>
               只看安装包自述的版本信息
          prism verify --instance <Prism 实例目录> [--root <Prism 根>] [--java <java.exe>] [--memory MB] [--player 名字]
               只读体检一个 Prism/MultiMC 实例（库、资源、Java、natives 是否齐全），不下载不写盘
          prism plan --instance <目录> [同上参数]
               在体检之后把合并出来的完整命令行打印出来（离线 UUID，可直接拿去真启动）
          diagnose <日志文件> [退出码]
               对一份日志跑崩溃诊断
        """);
    return 0;
}
