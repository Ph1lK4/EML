using System.Text.Json.Serialization;
using Launcher.Metadata;

namespace Launcher.Prism;

/// <summary>
/// MultiMC / Prism 的组件补丁（instances/&lt;实例&gt;/patches/*.json）。
/// 它不是完整版本 JSON，而是「在已有组件上追加/覆盖」的增量：
/// +tweakers、+jvmArgs、libraries、mainClass 等。
/// </summary>
public sealed class PrismPatch
{
    public string? Uid { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }

    /// <summary>合入顺序，越小越先应用。1.7.10 默认是 net.minecraft=-2、org.lwjgl3=-1、forge=5、lwjgl3ify launchargs=100。</summary>
    public int? Order { get; set; }

    public string? MainClass { get; set; }
    public string? MinecraftArguments { get; set; }
    public AssetIndexRef? AssetIndex { get; set; }
    public PrismMainJar? MainJar { get; set; }
    public List<LibraryJson>? Libraries { get; set; }

    [JsonPropertyName("+tweakers")]
    public List<string>? Tweakers { get; set; }

    [JsonPropertyName("+jvmArgs")]
    public List<string>? JvmArgs { get; set; }

    [JsonPropertyName("+gameArgs")]
    public List<string>? GameArgs { get; set; }

    [JsonPropertyName("+traits")]
    public List<string>? Traits { get; set; }
}

/// <summary>补丁里的 mainJar 是对象（不是字符串）。</summary>
public sealed class PrismMainJar
{
    public string? Name { get; set; }
    public LibraryDownloads? Downloads { get; set; }
}
