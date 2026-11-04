using System.Text.Json.Serialization;

namespace EML.Core.Minecraft;

/// <summary>
/// 统一保存导入来源和安装需求，供后续下载、缺失检查使用；文件路径均相对实例根目录。
/// </summary>
public sealed record InstanceImportManifest
{
    /// <summary>实例根目录下统一清单的固定文件名。</summary>
    public const string FileName = "eml-instance.json";
    /// <summary>EML 清单结构版本，用于判断读取方式是否兼容。</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>原始来源格式：Prism、Modrinth、CurseForge 或 Zip。</summary>
    public string SourceFormat { get; init; } = "Zip";
    /// <summary>导入时的名称快照；界面以当前文件夹或 Prism 配置为准。</summary>
    public string Name { get; init; } = "";
    /// <summary>原压缩包文件名，仅用于溯源，不包含本机绝对路径。</summary>
    public string SourceArchive { get; init; } = "";
    /// <summary>相对实例根目录的游戏目录；点号表示实例根目录本身。</summary>
    public string GameDirectory { get; init; } = ".minecraft";
    /// <summary>清单声明的 Minecraft 版本；无法确认时为 null。</summary>
    public string? MinecraftVersion { get; init; }
    /// <summary>组件只是安装需求；Minecraft 本体、加载器、Java 等需由相应解析器展开依赖。</summary>
    public IReadOnlyList<InstanceComponentRequirement> Components { get; init; } = [];
    /// <summary>包内文件与待下载文件的统一需求列表。</summary>
    public IReadOnlyList<InstanceFileRequirement> Files { get; init; } = [];
    /// <summary>供扫描器和界面显示的导入说明。</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>导入不等于完整性检查；后续检查结果应独立保存，避免这份声明过期。</summary>
    public string IntegrityStatus { get; init; } = "NotChecked";
}

/// <summary>保存解析后的组件 ID 与版本，供后续安装器选择处理方式。</summary>
/// <param name="Id">组件标识，例如 minecraft、forge 或 net.minecraftforge。</param>
/// <param name="Version">组件版本；Prism 未指定版本时可能为“未知”。</param>
public sealed record InstanceComponentRequirement(string Id, string Version)
{
    /// <summary>CurseForge 主加载器标记；其他平台未声明时为 null。</summary>
    public bool? Primary { get; init; }
}

/// <summary>Embedded 对应包内文件，DirectDownload 有 URL，CurseForge 需要通过项目/文件 ID 解析。</summary>
public enum InstanceFileSource { Embedded, DirectDownload, CurseForge }

/// <summary>实际采用的内容来源；与旧 Source 的获取方式分开，旧清单未声明时继续按原规则读取。</summary>
public enum InstanceFileOrigin { EmbeddedOverride, CurseForgeDownload, ModrinthDownload, UserLocalImport }

/// <summary>文件用途独立于内容来源；可变配置和启动器状态不会改变原来源摘要的含义。</summary>
public enum InstanceFileRole { Content, UserConfiguration, LauncherMetadata, DerivedState }

/// <summary>原平台文件的来源信息，不参与另一个来源文件的强制校验。</summary>
public sealed record CurseForgeFileOrigin
{
    public long ProjectId { get; init; }
    public long FileId { get; init; }
    public long? Size { get; init; }
    public IReadOnlyDictionary<string, string> Hashes { get; init; } = new Dictionary<string, string>();
}

/// <summary>统一文件需求；CurseForge 未解析时路径和大小可为 null，哈希集合可为空。</summary>
public sealed record InstanceFileRequirement
{
    /// <summary>相对实例根目录的目标路径；平台尚未解析时可为 null。</summary>
    public string? Path { get; init; }
    /// <summary>内容来自包内条目、直接下载地址还是 CurseForge 文件引用。</summary>
    public InstanceFileSource Source { get; init; }
    /// <summary>当前内容及其 Hashes 所属的来源；null 保留旧清单的序列化指纹。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InstanceFileOrigin? Origin { get; init; }
    /// <summary>明确记录的文件用途；旧清单省略时由共享策略作保守分类。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InstanceFileRole? Role { get; init; }
    /// <summary>被包内覆盖或用户选择替代的官方固定文件信息，不作为当前内容的校验条件。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CurseForgeFileOrigin? CurseForgeOrigin { get; init; }
    /// <summary>是否为必需文件；false 的文件留待用户选择是否安装。</summary>
    public bool Required { get; init; } = true;
    /// <summary>未压缩的字节数；未知时为 null。</summary>
    public long? Size { get; init; }
    /// <summary>算法名称到十六进制哈希的映射，供后续校验使用。</summary>
    public IReadOnlyDictionary<string, string> Hashes { get; init; } = new Dictionary<string, string>();
    /// <summary>平台提供的 HTTPS 下载候选地址；导入时不会访问它们。</summary>
    public IReadOnlyList<string> Downloads { get; init; } = [];
    /// <summary>CurseForge 项目 ID；其他来源通常为 null。</summary>
    public long? ProjectId { get; init; }
    /// <summary>CurseForge 文件 ID；后续与项目 ID 一起解析文件信息。</summary>
    public long? FileId { get; init; }
    /// <summary>原 ZIP 内的条目路径；未来从原包恢复时仍要重新校验，不能信任它直接写磁盘。</summary>
    public string? ArchiveEntry { get; init; }
}
