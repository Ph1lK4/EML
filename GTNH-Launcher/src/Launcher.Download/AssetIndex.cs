using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Download;

/// <summary>assets/indexes/&lt;版本&gt;.json 里的一条：hash 决定落盘路径，size 用来判断要不要重下。</summary>
public sealed class AssetObjectJson
{
    public string? Hash { get; set; }
    public long Size { get; set; }
}

/// <summary>
/// 资源索引。1.7.10 只有 objects，没有 virtual / map_to_resources；
/// 这两个字段先留着，等支持更老版本时再用。
/// </summary>
public sealed class AssetIndexJson
{
    public Dictionary<string, AssetObjectJson>? Objects { get; set; }

    public bool Virtual { get; set; }

    [JsonPropertyName("map_to_resources")]
    public string? MapToResources { get; set; }

    public static AssetIndexJson Parse(string json) =>
        JsonSerializer.Deserialize<AssetIndexJson>(json, Options)
        ?? throw new InvalidDataException("资产索引 JSON 解析结果是 null");

    public static AssetIndexJson Load(string path) => Parse(File.ReadAllText(path));

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}
