using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EML.Core.Java;
using EML.Core.Minecraft.Accounts;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Launching;
using EML.Infrastructure.Java;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Prism;
using EML.Infrastructure.Minecraft.Runtime;
using EML.Infrastructure.Minecraft.Standard;

namespace EML.Infrastructure.Minecraft.Launching;

/// <summary>读取已补全实例的统一配置；只准备参数，不再次推导依赖或执行游戏。</summary>
public sealed partial class MinecraftLaunchPlanner
{
    private readonly string _launcherRoot;
    private readonly IPrismRuntimeResolver _resolver;
    private readonly IJavaLocator _javaLocator;
    private readonly Func<string, CancellationToken, Task<JavaInstallation>> _probe;
    private readonly SemaphoreSlim _javaLock = new(1, 1);
    private IReadOnlyList<JavaInstallation>? _knownJava;
    private readonly IMinecraftAccountService? _accounts;

    /// <summary>只处理直接子实例；保留 checker 参数兼容旧调用方，启动不再调用完整检查器。</summary>
    public MinecraftLaunchPlanner(string launcherRoot, IMinecraftInstanceChecker? checker = null, IJavaLocator? javaLocator = null,
        IMinecraftAccountService? accounts = null)
        : this(launcherRoot, new InstanceRuntimeResolver(), javaLocator ?? new WindowsJavaLocator(), ProbeJavaAsync, accounts) { }

    /// <summary>测试注入 Java 版本查询，不运行玩家的 Java 或游戏进程。</summary>
    internal MinecraftLaunchPlanner(string launcherRoot, IPrismRuntimeResolver resolver, IJavaLocator javaLocator,
        Func<string, CancellationToken, Task<JavaInstallation>> probe, IMinecraftAccountService? accounts = null)
    { _launcherRoot = launcherRoot; _resolver = resolver; _javaLocator = javaLocator; _probe = probe; _accounts = accounts; }

    /// <summary>只读验证已有配置、关键路径和 Java；不扫描整合包或计算内容文件哈希。</summary>
    public Task<MinecraftLaunchPlan> PrepareAsync(string instancePath, MinecraftLaunchSettings settings,
        IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => PrepareCoreAsync(instancePath, settings, progress, false, cancellationToken), cancellationToken);

    /// <summary>实际启动时允许为旧 ZIP 首次整理本地运行文件，完成后与其他格式共用快速准备。</summary>
    internal Task<MinecraftLaunchPlan> PrepareForRunAsync(string instancePath, MinecraftLaunchSettings settings,
        IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => PrepareCoreAsync(instancePath, settings, progress, true, cancellationToken), cancellationToken);

    /// <summary>核对安装状态与源元数据，只对两份启动 JSON 计算摘要，最后复核准备期间的变化。</summary>
    private async Task<MinecraftLaunchPlan> PrepareCoreAsync(string instancePath, MinecraftLaunchSettings settings,
        IProgress<MinecraftLaunchProgress>? progress, bool allowLocalPreparation, CancellationToken token)
    {
        MinecraftLaunchValidation.Validate(settings);
        var root = EmbeddedFileBaseline.ValidateInstance(_launcherRoot, instancePath);
        var manifest = InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName));
        var sourceSnapshot = InstanceEffectiveRequirements.Build(root, manifest);
        progress?.Report(new(MinecraftLaunchStage.Preparing, "正在读取启动配置并确认关键运行文件……"));
        var runtime = _resolver.Resolve(root, manifest);
        ValidateRuntime(runtime);
        var state = PrismRuntimeStateStore.Read(runtime);
        if (state is null && allowLocalPreparation && manifest.SourceFormat.Equals("Zip", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new(MinecraftLaunchStage.Preparing, "首次为 ZIP 实例整理本地运行文件和启动配置……"));
            var prepared = await ZipInstanceInstaller.PrepareLocalAsync(_launcherRoot, root,
                new InlineProgress<InstanceDownloadProgress>(value => progress?.Report(new(MinecraftLaunchStage.Preparing,
                    value.Stage + "：" + value.FileName))), token, _resolver).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!prepared.RuntimeReady)
                throw new InvalidOperationException("无法从本地文件准备 ZIP 实例，请先执行“补全实例依赖”。\n" +
                    string.Join('\n', prepared.RuntimeIssues.Concat(prepared.Issues).Distinct().Take(10)));
            runtime = _resolver.Resolve(root, manifest);
            ValidateRuntime(runtime);
            state = PrismRuntimeStateStore.Read(runtime);
        }
        if (state is null) throw new InvalidOperationException("当前组件没有有效的启动配置，请先执行“补全实例依赖”。");
        var standardPath = state.LaunchProfilePath;
        using var standard = await ReadVerifiedJsonAsync(root, standardPath, state, token).ConfigureAwait(false);
        var standardJson = standard.RootElement;
        var portablePath = RequiredString(standardJson, "emlLaunchProfile");
        using var portable = await ReadVerifiedJsonAsync(root, portablePath, state, token).ConfigureAwait(false);
        var profile = portable.RootElement;
        var fingerprint = RequiredString(profile, "fingerprint");
        if (profile.GetProperty("schemaVersion").GetInt32() != 1 || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit) ||
            fingerprint != runtime.Fingerprint || fingerprint != RequiredString(standardJson, "emlRuntimeFingerprint") ||
            RequiredString(profile, "id") != RequiredString(standardJson, "id") ||
            RequiredString(profile, "mainClass") != runtime.MainClass ||
            RequiredString(profile, "mainClass") != RequiredString(standardJson, "mainClass"))
            throw new InvalidDataException("生成的启动配置与版本描述不一致，请重新补全实例。");
        var platform = RequiredString(profile, "runtimeOs");
        if (platform != "windows" || !OperatingSystem.IsWindows()) throw new InvalidDataException("此启动模块需要 Windows 运行配置。");
        var architecture = RequiredString(profile, "runtimeArchitecture");
        if (architecture != runtime.RuntimeArchitecture || platform != runtime.RuntimeOs ||
            architecture != RequiredString(standardJson, "emlRuntimeArchitecture") || platform != RequiredString(standardJson, "emlRuntimeOs"))
            throw new InvalidDataException("生成配置的平台声明不一致。");
        var gameRelative = RequiredString(profile, "gameDirectory");
        if (!gameRelative.Equals(manifest.GameDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("启动配置的游戏目录与实例声明不一致。");
        var game = Resolve(root, gameRelative, allowRoot: true);
        if (!Directory.Exists(game)) throw new DirectoryNotFoundException("游戏目录不存在。");
        var natives = Resolve(root, RequiredString(profile, "nativesDirectory"));
        if (File.Exists(natives)) throw new InvalidDataException("原生库目录被文件占用。");
        var classPaths = StringArray(profile, "classPath");
        if (classPaths.Length == 0 || classPaths.Length > 20000) throw new InvalidDataException("启动配置没有有效的类库列表。");
        CheckCriticalFiles(root, runtime, state, sourceSnapshot.Requirements, profile, classPaths, token);
        var allowedJava = profile.GetProperty("compatibleJavaMajors").EnumerateArray().Select(value => value.GetInt32()).Distinct().ToArray();
        if (allowedJava.Length == 0 || allowedJava.Any(major => major < 8 || major > 1000) ||
            !allowedJava.Order().SequenceEqual(runtime.CompatibleJavaMajors.Distinct().Order()))
            throw new InvalidDataException("实例没有有效的 Java 版本要求，请先补全或完善组件声明。");
        progress?.Report(new(MinecraftLaunchStage.Preparing, "正在选择并验证 Java " + string.Join('/', allowedJava) + "……"));
        var java = await SelectJavaAsync(settings.JavaPath, allowedJava, architecture, token).ConfigureAwait(false);
        var javaMajor = MajorVersion(java.Version);
        if (NormalizeArchitecture(java.Architecture) == "x86" && settings.MaxMemoryMb > 1536)
            throw new ArgumentException("32 位 Java 无法可靠使用此内存设置，请改用匹配实例的 64 位 Java。");

        MinecraftAccountSession? session = null;
        if (settings.UseMicrosoftAccount)
        {
            progress?.Report(new(MinecraftLaunchStage.Preparing, "正在验证微软账号会话……"));
            session = await (_accounts ?? throw new MinecraftAuthenticationException("未配置微软账号服务。"))
                .GetSessionAsync(token).ConfigureAwait(false);
            if (session.ExpiresAt <= DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(session.AccessToken))
                throw new MinecraftAuthenticationException("正版登录会话已过期，请重新登录。");
        }
        var playerName = session?.Account.Name ?? settings.PlayerName;
        var playerId = session?.Account.Id ?? OfflineUuid(settings.PlayerName);
        var accessToken = session?.AccessToken ?? "0";
        // 相对游戏目录的路径缩短 Windows 命令行，并避免中文实例目录反复出现在每项类库中。
        string GamePath(string path) => Path.GetRelativePath(game, Resolve(root, path));
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = playerName, ["auth_uuid"] = playerId,
            ["auth_access_token"] = accessToken, ["auth_session"] = session is null ? "0" : $"token:{accessToken}:{playerId}",
            ["auth_xuid"] = session?.Xuid ?? "", ["clientid"] = session?.ClientId ?? "",
            ["user_properties"] = "{}", ["user_type"] = session is null ? "legacy" : "msa", ["version_name"] = RequiredString(profile, "id"),
            ["version_type"] = "release", ["game_directory"] = ".", ["assets_root"] = GamePath(RequiredString(profile, "assetsRoot")),
            ["assets_index_name"] = profile.TryGetProperty("assetIndexId", out var index) && index.ValueKind == JsonValueKind.String ? index.GetString()! : "",
            ["game_assets"] = GamePath(RequiredString(profile, "gameAssets")), ["natives_directory"] = Path.GetRelativePath(game, natives),
            ["classpath"] = string.Join(Path.PathSeparator, classPaths.Select(GamePath)), ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = Path.Combine("libraries"), ["primary_jar"] = GamePath(classPaths[^1]),
            ["launcher_name"] = "EML", ["launcher_version"] = "1", ["resolution_width"] = "1280", ["resolution_height"] = "720"
        };
        if (profile.TryGetProperty("loggingPath", out var logging) && logging.ValueKind == JsonValueKind.String)
            values["logging_path"] = GamePath(logging.GetString()!);
        var arguments = new List<string> { $"-Xms{settings.MinMemoryMb}M", $"-Xmx{settings.MaxMemoryMb}M" };
        arguments.AddRange(StringArray(profile, "jvmArguments").Where(argument =>
            !argument.StartsWith("-Xms", StringComparison.Ordinal) && !argument.StartsWith("-Xmx", StringComparison.Ordinal))
            .Select(argument => Expand(argument, values)));
        arguments.AddRange(MinecraftLaunchValidation.TokenizeJvmArguments(settings.AdditionalJvmArguments));
        var mainClass = RequiredString(profile, "mainClass");
        if (!MainClassPattern().IsMatch(mainClass)) throw new InvalidDataException("无效的游戏主类。");
        arguments.Add(mainClass);
        arguments.AddRange(StringArray(profile, "gameArguments").Select(argument => Expand(argument, values)));
        token.ThrowIfCancellationRequested();
        InstanceEffectiveRequirements.EnsureUnchanged(root, manifest, sourceSnapshot);
        var currentRuntime = _resolver.Resolve(root, manifest);
        if (currentRuntime.Fingerprint != runtime.Fingerprint)
            throw new IOException("启动准备期间组件元数据发生变化，请重新补全实例。");
        // Java 探测期间配置可能改变，不能把旧的检查许可用于另一份启动配置。
        using var checkedStandard = await ReadVerifiedJsonAsync(root, standardPath, state, token).ConfigureAwait(false);
        using var checkedPortable = await ReadVerifiedJsonAsync(root, portablePath, state, token).ConfigureAwait(false);
        CheckCriticalFiles(root, runtime, state, sourceSnapshot.Requirements, profile, classPaths, token);
        progress?.Report(new(MinecraftLaunchStage.Preparing, $"启动准备完成；Java {javaMajor}，玩家 {playerName}（{(session is null ? "离线" : "正版")}），最大内存 {settings.MaxMemoryMb} MB。"));
        return new(root, game, java.ExecutablePath, javaMajor, arguments.ToArray())
        { NativesDirectory = natives, SensitiveValues = session is null ? [] : [accessToken] };
    }

    /// <summary>缺少组件元数据或加载器尚不支持时明确失败，不降级到其他启动入口。</summary>
    private static void ValidateRuntime(PrismRuntimePlan runtime)
    {
        if (runtime.BlockingIssues.Count > 0 || runtime.MetadataRequests.Count > 0)
            throw new InvalidOperationException("运行配置尚未准备完成，请执行“补全实例依赖”。\n" +
                string.Join('\n', runtime.BlockingIssues.Concat(runtime.MetadataRequests.Select(value =>
                    "缺少组件元数据：" + value.Id + " " + value.Version)).Take(10)));
    }

    /// <summary>仅确认类库、本机库、索引和日志文件的存在及大小，不遍历 mods、配置或资源对象。</summary>
    private static void CheckCriticalFiles(string root, PrismRuntimePlan runtime, PrismRuntimeState state,
        IReadOnlyList<InstanceFileRequirement> effective, JsonElement profile, string[] classPaths, CancellationToken token)
    {
        var selected = effective.Where(file => file.Path is not null).ToDictionary(file => file.Path!, StringComparer.OrdinalIgnoreCase);
        var requirements = runtime.Files.ToDictionary(file => file.Requirement.Path!, file =>
            file.Requirement.Source == InstanceFileSource.Embedded && selected.TryGetValue(file.Requirement.Path!, out var replacement)
                ? replacement : file.Requirement, StringComparer.OrdinalIgnoreCase);
        foreach (var generated in state.GeneratedFiles) requirements[generated.Path!] = generated;
        var paths = new HashSet<string>(classPaths, StringComparer.OrdinalIgnoreCase);
        var nativesPrefix = RequiredString(profile, "nativesDirectory").Replace('\\', '/') + "/";
        foreach (var file in state.GeneratedFiles.Where(file => file.Path!.StartsWith(nativesPrefix, StringComparison.OrdinalIgnoreCase)))
            paths.Add(file.Path!);
        foreach (var file in runtime.Files.Where(file => file.Role is PrismRuntimeFileRole.Logging or PrismRuntimeFileRole.AssetIndex))
            paths.Add(file.Requirement.Path!);
        if (profile.TryGetProperty("loggingPath", out var logging) && logging.ValueKind == JsonValueKind.String)
            paths.Add(logging.GetString()!);
        foreach (var relative in paths)
        {
            token.ThrowIfCancellationRequested();
            var path = Resolve(root, relative);
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("缺少关键运行文件，请执行“补全实例依赖”：" + relative, path);
            if (requirements.TryGetValue(relative, out var requirement) && requirement.Size is { } size && info.Length != size)
                throw new InvalidDataException("关键运行文件大小不符，请执行完整检查：" + relative);
        }
    }

    /// <summary>优先已发现的兼容 Java，但启动前必须重新运行版本查询，不能信任过期检测结果。</summary>
    private async Task<JavaInstallation> SelectJavaAsync(string? specified, int[] allowed, string architecture, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(specified))
        {
            var chosen = await _probe(specified, token).ConfigureAwait(false);
            if (!Compatible(chosen)) throw new InvalidOperationException($"指定 Java {chosen.Version} / {chosen.Architecture} 不符合实例要求：Java {string.Join('/', allowed)} / {architecture}。");
            return chosen;
        }
        await _javaLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // 窗口初始化已完成的 Java 检测可直接复用；缓存只减少发现扫描，不代替实时版本查询。
            var cached = (_javaLocator as WindowsJavaLocator)?.LastKnownInstallations ?? _knownJava;
            if (cached is not null && await ProbeCandidatesAsync(cached).ConfigureAwait(false) is { } known) return known;
            _knownJava = await _javaLocator.FindAllAsync(token).ConfigureAwait(false);
            if (await ProbeCandidatesAsync(_knownJava).ConfigureAwait(false) is { } found) return found;
        }
        finally { _javaLock.Release(); }
        throw new InvalidOperationException($"未找到可用的 Java {string.Join('/', allowed)}（{architecture}）。请在启动设置中指定兼容的 java.exe。");

        bool Compatible(JavaInstallation value) => allowed.Contains(MajorVersion(value.Version)) &&
            NormalizeArchitecture(value.Architecture) == NormalizeArchitecture(architecture);

        // 遗失或升级的 Java 会从实时结果剔除；只有旧缓存不可用时才重新发现。
        async Task<JavaInstallation?> ProbeCandidatesAsync(IReadOnlyList<JavaInstallation> candidates)
        {
            foreach (var candidate in candidates.Where(Compatible).OrderByDescending(item => MajorVersion(item.Version)))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var current = await _probe(candidate.ExecutablePath, token).ConfigureAwait(false);
                    if (Compatible(current)) return current;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
            }
            return null;
        }
    }

    /// <summary>查询指定 Java 的真实版本与架构；超时或取消仅终止本次探测进程。</summary>
    private static async Task<JavaInstallation> ProbeJavaAsync(string path, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !Path.GetFileName(path).Equals("java.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请指定现有 java.exe 的完整路径。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process { StartInfo = new(path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-XshowSettings:properties");
        process.StartInfo.ArgumentList.Add("-version");
        try
        {
            if (!process.Start()) throw new IOException("无法查询 Java。");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var details = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
            var version = JavaVersionPattern().Match(details);
            var arch = JavaArchitecturePattern().Match(details);
            if (process.ExitCode != 0 || !version.Success || !arch.Success) throw new IOException("无法识别指定 Java 的版本或架构。");
            return new(Path.GetFullPath(path), version.Groups[1].Value, arch.Groups[1].Value);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Java 版本查询超时。"); }
        finally
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); } }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>按安装时记录的 SHA-256 验证小型启动 JSON；不依赖整包检查报告。</summary>
    private static async Task<JsonDocument> ReadVerifiedJsonAsync(string root, string relative, PrismRuntimeState state, CancellationToken token)
    {
        var path = Resolve(root, relative);
        var match = state.GeneratedFiles.FirstOrDefault(file =>
            string.Equals(file.Path, relative.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) &&
            file.Hashes.ContainsKey("sha256")) ?? throw new InvalidDataException("启动配置不在运行准备状态记录中：" + relative);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        if (input.Length > 8 * 1024 * 1024 || input.Length != match.Size)
            throw new InvalidDataException("启动配置大小与安装记录不符，请重新补全实例。");
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, token).ConfigureAwait(false);
        var bytes = memory.ToArray();
        if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(match.Hashes["sha256"], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("启动配置与安装记录不符，请重新补全实例。");
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
    }

    /// <summary>所有配置路径必须位于实例内，且现有路径链不能包含目录联接或链接。</summary>
    private static string Resolve(string root, string relative, bool allowRoot = false)
    {
        var path = allowRoot && relative == "." ? root : InstanceImportPaths.Within(root, relative);
        InstanceImportPaths.EnsureNoLinks(path);
        return path;
    }

    /// <summary>只按键替换占位符，不执行变量表达式，也不递归解释用户输入。</summary>
    private static string Expand(string argument, IReadOnlyDictionary<string, string> values)
    {
        if (argument.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new InvalidDataException("启动参数含非法控制字符。");
        return PlaceholderPattern().Replace(argument, match => values.TryGetValue(match.Groups[1].Value, out var value)
            ? value : throw new InvalidDataException("未支持的启动占位符：" + match.Value));
    }

    /// <summary>离线身份遵循 OfflinePlayer 名称的 UUID v3 计算方式，保持同名玩家存档身份稳定。</summary>
    internal static string OfflineUuid(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>兼容 Java 8 的 1.8 与新版本的主版本格式。</summary>
    internal static int MajorVersion(string value)
    {
        var version = value.StartsWith("1.", StringComparison.Ordinal) ? value[2..] : value;
        var digits = new string(version.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : 0;
    }

    /// <summary>统一检测器显示文本和运行配置架构 ID；未知架构不默认为 x64。</summary>
    private static string NormalizeArchitecture(string value) => value.Trim().ToLowerInvariant() switch
    {
        "amd64" or "x86_64" or "x64" or "x64 (64-bit)" or "x64（64 位）" => "x64",
        "aarch64" or "arm64" or "arm64（64 位）" => "arm64",
        "x86" or "i386" or "i486" or "i586" or "i686" or "x86 (32-bit)" or "x86（32 位）" => "x86",
        _ => "unknown:" + value
    };

    /// <summary>要求配置字段为非空字符串，拒绝隐式类型转换。</summary>
    private static string RequiredString(JsonElement json, string property) =>
        json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidDataException("启动配置缺少字段：" + property);

    /// <summary>读取参数数组，元素保持独立，不按空格再次拆分已生成参数。</summary>
    private static string[] StringArray(JsonElement json, string property) => json.GetProperty(property).EnumerateArray().Select(value =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("启动配置参数必须是字符串。")).ToArray();

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    [GeneratedRegex("\\$\\{([^}]+)\\}", RegexOptions.CultureInvariant)] private static partial Regex PlaceholderPattern();
    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]*(\\.[A-Za-z_$][A-Za-z0-9_$]*)+$", RegexOptions.CultureInvariant)] private static partial Regex MainClassPattern();
    [GeneratedRegex(@"(?m)^\s*java.version\s*=\s*(\S+)", RegexOptions.CultureInvariant)] private static partial Regex JavaVersionPattern();
    [GeneratedRegex(@"(?m)^\s*os.arch\s*=\s*(\S+)", RegexOptions.CultureInvariant)] private static partial Regex JavaArchitecturePattern();
}
