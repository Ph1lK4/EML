using Launcher.Contracts;
using Launcher.Metadata;

namespace Launcher.Prism;

/// <summary>
/// 把 Prism 实例拼成一条可以直接跑的命令行。
/// 和 <see cref="LaunchPlanner"/> 的区别：库/资源在 Prism 根目录（全局共享），
/// 客户端 jar 也以 Maven 坐标的形式放在 libraries 里，不是 versions/&lt;id&gt;/&lt;id&gt;.jar。
/// </summary>
public static class PrismPlanner
{
    public static LaunchPlan Build(PrismInstance instance, LaunchRequest request, RuleContext platform)
    {
        if (string.IsNullOrWhiteSpace(instance.MainClass))
            throw new InvalidDataException($"实例 {instance.Name} 的补丁里没有 mainClass");

        var classpath = new List<string>();
        foreach (var library in instance.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, platform)) continue;

            var artifactPath = library.Downloads?.Artifact?.Path;
            if (string.IsNullOrEmpty(artifactPath))
            {
                // 只声明了 natives、没有 artifact 的库（twitch/jinput 这类）：native 已被 Prism
                // 解压进实例的 natives/ 目录，本身不进 classpath。
                if (library.Natives is { Count: > 0 }) continue;
                if (string.IsNullOrWhiteSpace(library.Name)) continue;
                artifactPath = ArgumentBuilder.MavenPath(library.Name!);
            }

            classpath.Add(ResolveLibraryPath(instance, artifactPath!));
        }

        classpath.Add(ResolveLibraryPath(instance, ArgumentBuilder.MavenPath(instance.MainJarName)));
        var classpathString = string.Join(Path.PathSeparator, classpath);

        var jvmArgs = new List<string>
        {
            $"-Xms{request.MinMemoryMb}M",
            $"-Xmx{request.MaxMemoryMb}M",
        };

        var logConfig = Path.Combine(instance.AssetsDirectory, "log_configs", "client-1.7.xml");
        if (File.Exists(logConfig))
            jvmArgs.Add($"-Dlog4j.configurationFile={logConfig}");

        jvmArgs.AddRange(instance.ExtraJvmArgs);
        jvmArgs.Add($"-Djava.library.path={instance.NativesDirectory}");
        jvmArgs.Add($"-Dminecraft.launcher.brand={LaunchPlanner.LauncherBrand}");
        jvmArgs.Add($"-Dminecraft.launcher.version={LaunchPlanner.LauncherVersion}");
        jvmArgs.Add("-cp");
        jvmArgs.Add(classpathString);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = request.Auth.PlayerName,
            ["auth_uuid"] = request.Auth.Uuid,
            ["auth_access_token"] = request.Auth.AccessToken,
            ["auth_session"] = $"token:{request.Auth.AccessToken}:{request.Auth.Uuid}",
            ["auth_xuid"] = "",
            ["clientid"] = "",
            ["user_type"] = request.Auth.UserType,
            ["user_properties"] = "{}",
            ["version_name"] = request.VersionName,
            ["game_directory"] = instance.GameDirectory,
            ["assets_root"] = instance.AssetsDirectory,
            ["game_assets"] = instance.AssetsDirectory,
            ["assets_index_name"] = instance.AssetIndexId,
            ["natives_directory"] = instance.NativesDirectory,
            ["library_directory"] = instance.LibrariesDirectory,
            ["classpath"] = classpathString,
        };

        var gameArgs = new List<string>();
        foreach (var token in ArgumentBuilder.SplitTemplate(instance.MinecraftArguments))
            gameArgs.Add(ArgumentBuilder.Expand(token, values));

        // Forge 的 tweak 在补丁里是 +tweakers，Prism 会把它拼成 --tweakClass。
        foreach (var tweaker in instance.Tweakers)
        {
            gameArgs.Add("--tweakClass");
            gameArgs.Add(tweaker);
        }

        gameArgs.AddRange(request.ExtraGameArgs);

        return new LaunchPlan
        {
            JavaPath = request.Java.Path,
            MainClass = instance.MainClass!,
            JvmArgs = jvmArgs,
            GameArgs = gameArgs,
            Classpath = classpathString,
            WorkingDirectory = instance.GameDirectory,
        };
    }

    private static string ToLocal(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>先找实例自带的 libraries，再找 Prism 根 libraries；实例目录里也可能直接平铺放 jar。</summary>
    private static string ResolveLibraryPath(PrismInstance instance, string relative)
    {
        var fromInstance = ToLocal(instance.InstanceLibrariesDirectory, relative);
        if (File.Exists(fromInstance)) return fromInstance;

        var fromRoot = ToLocal(instance.LibrariesDirectory, relative);
        if (File.Exists(fromRoot)) return fromRoot;

        var flat = Path.Combine(instance.InstanceLibrariesDirectory, Path.GetFileName(relative));
        return File.Exists(flat) ? flat : fromRoot;
    }
}
