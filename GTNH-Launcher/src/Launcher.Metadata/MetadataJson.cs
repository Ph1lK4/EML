using System.Text.Json;

namespace Launcher.Metadata;

/// <summary>版本 JSON 的读写入口，统一选项，省得每处各写一遍。</summary>
public static class MetadataJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static VersionJson Parse(string json) =>
        JsonSerializer.Deserialize<VersionJson>(json, Options)
        ?? throw new InvalidDataException("版本 JSON 解析结果为空");

    public static VersionJson Load(string path) => Parse(File.ReadAllText(path));
}
