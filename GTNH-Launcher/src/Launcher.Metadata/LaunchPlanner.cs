using Launcher.Contracts;

namespace Launcher.Metadata;

/// <summary>
/// 启动核心：合并后的版本 → 一条能直接跑的命令行。
/// 这一层不碰网络、不碰磁盘（除了拼路径），纯函数，方便单测。
/// </summary>
public static class LaunchPlanner
{
    public const string LauncherBrand = "GTNH-Launcher";
    public const string LauncherVersion = "0.1.0";

    public static LaunchPlan Build(ResolvedVersion version, LaunchRequest request, RuleContext platform)
    {
        if (string.IsNullOrWhiteSpace(version.MainClass))
            throw new InvalidDataException($"版本 {version.Id} 缺少 mainClass");

        var gameDir = Path.GetFullPath(request.GameDirectory);
        var librariesDir = Path.Combine(gameDir, "libraries");
        var nativesDir = Path.Combine(gameDir, "natives");
        var classpath = new List<string>();
        var natives = new List<NativeJar>();

        foreach (var library in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, platform)) continue;

            var artifactPath = library.Downloads?.Artifact?.Path;
            if (!string.IsNullOrEmpty(artifactPath))
            {
                classpath.Add(ToLocal(librariesDir, artifactPath));
            }
            else if (library.Downloads is null && !string.IsNullOrEmpty(library.Name))
            {
                // 很老的库连 downloads 都没有，只能按坐标拼路径
                classpath.Add(ToLocal(librariesDir, ArgumentBuilder.MavenPath(library.Name)));
            }

            var native = ResolveNative(library, platform, librariesDir);
            if (native is not null) natives.Add(native);
        }

        var clientJar = Path.Combine(gameDir, "versions", version.ClientJarVersionId, version.ClientJarVersionId + ".jar");
        classpath.Add(clientJar);
        var classpathString = string.Join(Path.PathSeparator, classpath);

        var values = BuildPlaceholderValues(request, version, gameDir, librariesDir, nativesDir, classpathString);

        var jvmArgs = new List<string>
        {
            $"-Xms{request.MinMemoryMb}M",
            $"-Xmx{request.MaxMemoryMb}M",
        };

        var logging = version.Logging?.Client;
        var logConfigId = logging?.File?.Id;
        if (!string.IsNullOrEmpty(logging?.Argument) && !string.IsNullOrEmpty(logConfigId))
        {
            var logConfig = Path.Combine(gameDir, "assets", "log_configs", logConfigId);
            jvmArgs.Add(ArgumentBuilder.Expand(logging!.Argument!, new Dictionary<string, string> { ["path"] = logConfig }));
        }

        jvmArgs.Add($"-Djava.library.path={nativesDir}");
        jvmArgs.Add($"-Dminecraft.launcher.brand={LauncherBrand}");
        jvmArgs.Add($"-Dminecraft.launcher.version={LauncherVersion}");
        jvmArgs.Add("-cp");
        jvmArgs.Add(classpathString);
        jvmArgs.AddRange(request.ExtraJvmArgs);

        var gameArgs = new List<string>();
        if (!string.IsNullOrWhiteSpace(version.MinecraftArguments))
        {
            foreach (var token in ArgumentBuilder.SplitTemplate(version.MinecraftArguments))
                gameArgs.Add(ArgumentBuilder.Expand(token, values));
        }
        gameArgs.AddRange(request.ExtraGameArgs);

        return new LaunchPlan
        {
            JavaPath = request.Java.Path,
            MainClass = version.MainClass,
            JvmArgs = jvmArgs,
            GameArgs = gameArgs,
            Classpath = classpathString,
            WorkingDirectory = gameDir,
            Natives = natives,
        };
    }

    private static Dictionary<string, string> BuildPlaceholderValues(
        LaunchRequest request, ResolvedVersion version, string gameDir, string librariesDir, string nativesDir, string classpath)
    {
        var assetsDir = Path.Combine(gameDir, "assets");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = request.Auth.PlayerName,
            ["auth_uuid"] = request.Auth.Uuid.Replace("-", ""),
            ["auth_access_token"] = request.Auth.AccessToken,
            ["auth_session"] = $"token:{request.Auth.AccessToken}:{request.Auth.Uuid}",
            ["auth_xuid"] = "",
            ["clientid"] = "",
            ["user_type"] = request.Auth.UserType,
            ["user_properties"] = "{}",
            ["version_name"] = request.VersionName,
            ["version_type"] = "release",
            ["game_directory"] = gameDir,
            ["assets_root"] = assetsDir,
            ["game_assets"] = assetsDir,
            ["assets_index_name"] = version.AssetIndex?.Id ?? "",
            ["natives_directory"] = nativesDir,
            ["library_directory"] = librariesDir,
            ["classpath"] = classpath,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["launcher_name"] = LauncherBrand,
            ["launcher_version"] = LauncherVersion,
            ["resolution_width"] = "854",
            ["resolution_height"] = "480",
        };
    }

    private static NativeJar? ResolveNative(LibraryJson library, RuleContext platform, string librariesDir)
    {
        if (library.Natives is null || !library.Natives.TryGetValue(platform.OsName, out var template))
            return null;

        var classifier = ArgumentBuilder.Expand(template, new Dictionary<string, string> { ["arch"] = ArchBits(platform.Arch) });
        if (library.Downloads?.Classifiers is not { } classifiers ||
            !classifiers.TryGetValue(classifier, out var file) ||
            string.IsNullOrEmpty(file.Path))
        {
            return null;
        }

        var excludes = library.Extract?.Exclude ?? new List<string> { "META-INF/" };
        return new NativeJar(library.Name ?? "", ToLocal(librariesDir, file.Path), excludes);
    }

    private static string ArchBits(string arch) => arch switch
    {
        "x86_64" => "64",
        "x86" => "32",
        _ => arch,
    };

    private static string ToLocal(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
}
