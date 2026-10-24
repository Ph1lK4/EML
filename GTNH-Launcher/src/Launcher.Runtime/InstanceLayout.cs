namespace Launcher.Runtime;

/// <summary>
/// 实例目录布局。一个 GTNH 实例长这样：
/// <code>
/// &lt;root&gt;/              ← 实例根（用户能改名、能搬走）
///   .minecraft/          ← 游戏目录（LaunchPlan.WorkingDirectory 指向这里）
///     versions/&lt;id&gt;/      ← &lt;id&gt;.json + &lt;id&gt;.jar
///     libraries/         ← Maven 布局
///     assets/            ← objects / indexes / log_configs
///     natives/           ← 解压出来的 dll
///     mods/ config/ saves/ logs/
/// </code>
/// 和 Prism 的实例布局一致，方便以后兼容。
/// </summary>
public sealed class InstanceLayout
{
    public InstanceLayout(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("实例目录不能为空", nameof(root));
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
    public string GameDirectory => Path.Combine(Root, ".minecraft");
    public string VersionsDirectory => Path.Combine(GameDirectory, "versions");
    public string LibrariesDirectory => Path.Combine(GameDirectory, "libraries");
    public string AssetsDirectory => Path.Combine(GameDirectory, "assets");
    public string NativesDirectory => Path.Combine(GameDirectory, "natives");
    public string LogsDirectory => Path.Combine(GameDirectory, "logs");

    public string VersionJsonPath(string versionId) =>
        Path.Combine(VersionsDirectory, versionId, versionId + ".json");

    public string ClientJarPath(string versionId) =>
        Path.Combine(VersionsDirectory, versionId, versionId + ".jar");

    public string AssetsIndexPath(string assetIndexId) =>
        Path.Combine(AssetsDirectory, "indexes", assetIndexId + ".json");

    public string LogConfigPath(string logConfigId) =>
        Path.Combine(AssetsDirectory, "log_configs", logConfigId);
}
