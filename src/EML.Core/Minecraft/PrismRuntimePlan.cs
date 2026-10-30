namespace EML.Core.Minecraft;

/// <summary>运行文件的用途，用于安排资源索引、普通类库和旧式本机库的处理顺序。</summary>
public enum PrismRuntimeFileRole { Library, MainJar, AssetIndex, AssetObject, NativeArchive, Logging }

/// <summary>一个已解析的运行依赖；路径始终相对实例根目录。</summary>
public sealed record PrismRuntimeFile(InstanceFileRequirement Requirement, PrismRuntimeFileRole Role)
{
    public string? MavenName { get; init; }
    public bool OnClassPath { get; init; }
    public IReadOnlyList<string> ExtractExcludes { get; init; } = [];
}

/// <summary>本地没有补丁时需按固定组件版本获取的 Prism 元数据；不能选择最新版本替换。</summary>
public sealed record PrismMetadataRequest(string Id, string Version, string RelativePath, string Url)
{
    public IReadOnlyDictionary<string, string> RequirementHashes { get; init; } = new Dictionary<string, string>();
    public long? ExpectedSize { get; init; }
}

/// <summary>Prism 组件合并后的安装计划；存在阻断项时不能声称依赖齐全。</summary>
public sealed record PrismRuntimePlan
{
    public required string InstancePath { get; init; }
    // 保留原类型名称以兼容 Prism；不同包格式共用经过验证的运行文件管线。
    public string SourceFormat { get; init; } = "Prism";
    public string VersionIdPrefix { get; init; } = "EML-Prism";
    public string GameDirectory { get; init; } = ".minecraft";
    public string MinecraftVersion { get; init; } = "";
    public string RuntimeOs { get; init; } = "windows";
    public string RuntimeArchitecture { get; init; } = "x86_64";
    public string MainClass { get; init; } = "";
    public string? MinecraftArguments { get; init; }
    public IReadOnlyList<string> GameArguments { get; init; } = [];
    public IReadOnlyList<string> JvmArguments { get; init; } = [];
    public string? LoggingArgument { get; init; }
    public IReadOnlyList<string> Tweakers { get; init; } = [];
    public IReadOnlyList<string> Traits { get; init; } = [];
    public IReadOnlyList<int> CompatibleJavaMajors { get; init; } = [];
    public string? AssetIndexId { get; init; }
    public IReadOnlyList<PrismRuntimeFile> Files { get; init; } = [];
    public IReadOnlyList<PrismMetadataRequest> MetadataRequests { get; init; } = [];
    public IReadOnlyList<string> BlockingIssues { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>包内组件声明及当前系统的指纹，用于使旧的生成结果失效。</summary>
    public string Fingerprint { get; init; } = "";
}

/// <summary>只读解析 Prism 文件，供检查器和安装器共用；不在此层联网或改写原清单。</summary>
public interface IPrismRuntimeResolver
{
    /// <summary>合并已启用组件和本地补丁，输出适用于当前操作系统的文件及启动信息。</summary>
    PrismRuntimePlan Resolve(string instancePath, InstanceImportManifest manifest);
}

/// <summary>完成 Prism 运行依赖下载及启动配置准备；不启动游戏或执行包内脚本。</summary>
public interface IPrismInstanceInstaller
{
    /// <summary>下载固定版本依赖及资源，生成可交给启动模块的配置；取消保留已完成文件。</summary>
    Task<PrismInstallResult> InstallAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>运行依赖准备结果；与真实启动验证分开描述。</summary>
public sealed record PrismInstallResult
{
    public string SourceFormat { get; init; } = "Prism";
    public IReadOnlyList<InstanceDownloadFileResult> Files { get; init; } = [];
    public IReadOnlyList<string> Issues { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public string? LaunchProfilePath { get; init; }
    public bool IsCanceled { get; init; }
    public bool FilesReady => !IsCanceled && Issues.Count == 0 && LaunchProfilePath is not null;
}
