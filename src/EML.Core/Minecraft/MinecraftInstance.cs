namespace EML.Core.Minecraft;

/// <summary>实例扫描结果；识别成功不代表文件完整或可以启动。</summary>
/// <param name="Name">标准包使用文件夹名，Prism 优先使用配置中的名称。</param>
/// <param name="InstancePath">实例根目录的绝对路径。</param>
/// <param name="GameDirectory">存放 mods、config、saves 等内容的游戏目录。</param>
/// <param name="VersionIds">版本配置 ID；Prism 没有标准配置 ID，此处使用本体版本。</param>
public sealed record MinecraftInstance(
    string Name,
    string InstancePath,
    string GameDirectory,
    IReadOnlyList<string> VersionIds)
{
    /// <summary>来源格式：标准目录、Prism 或未知。</summary>
    public string Format { get; init; } = "标准目录";
    /// <summary>每个配置的本体版本、加载器描述和元数据路径。</summary>
    public IReadOnlyList<MinecraftVersionInfo> Versions { get; init; } = [];
    /// <summary>缺失、损坏或无法确认的信息，供界面展示。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>去重并拼接本体版本；无结果时显示未知。</summary>
    public string MinecraftVersionDisplay => Versions.Count == 0 ? "未知" :
        string.Join("、", Versions.Select(v => v.MinecraftVersion).Distinct());
    /// <summary>拼接不同配置的加载器描述，用于列表列绑定。</summary>
    public string LoaderDisplay => Versions.Count == 0 ? "未知" :
        string.Join("、", Versions.Select(v => v.Loader).Distinct());
    /// <summary>将提示组合为多行文本。</summary>
    public string WarningDisplay => string.Join(Environment.NewLine, Warnings);
}

/// <summary>VersionId 是安装配置的 ID，MinecraftVersion 是游戏本体版本。</summary>
/// <param name="VersionId">安装配置 ID，例如 1.7.10-Forge1614。</param>
/// <param name="MinecraftVersion">游戏本体版本，例如 1.7.10，不是整合包发布版本。</param>
/// <param name="Loader">用于显示的组件描述，不是完整启动配置。</param>
/// <param name="MetadataPath">读取的版本清单绝对路径。</param>
public sealed record MinecraftVersionInfo(
    string VersionId,
    string MinecraftVersion,
    string Loader,
    string MetadataPath);
