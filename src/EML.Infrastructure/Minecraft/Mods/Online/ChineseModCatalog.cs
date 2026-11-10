using System.Globalization;
using System.Text.Json;
using EML.Core.Minecraft.Mods.Online;

namespace EML.Infrastructure.Minecraft.Mods.Online;

/// <summary>随启动器提供的离线中文目录；仅按经过核对的平台编号匹配，不发起翻译请求。</summary>
public sealed class ChineseModCatalog : IOnlineModLocalization
{
    private const string ResourceName = "EML.OnlineMods.zh-CN.json";
    private static readonly Lazy<ChineseModCatalog> Shared = new(LoadEmbedded);
    private readonly Dictionary<OnlineProjectId, OnlineModTranslation> _projects = [];
    private readonly Dictionary<string, string?> _searchTerms = new(StringComparer.OrdinalIgnoreCase);

    public static ChineseModCatalog Default => Shared.Value;
    public int EntryCount { get; }
    public int ProjectCount => _projects.Count;

    /// <summary>读取本地目录并拒绝重复身份；相同中文别名对应不同项目时不进行搜索转换。</summary>
    internal ChineseModCatalog(Stream json)
    {
        var entries = JsonSerializer.Deserialize<Entry[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("中文模组目录为空。");
        if (entries.Length is 0 or > 10000) throw new InvalidDataException("中文模组目录条目数量无效。");
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ChineseName) || !ContainsChinese(entry.ChineseName) ||
                string.IsNullOrWhiteSpace(entry.EnglishName) || string.IsNullOrWhiteSpace(entry.Summary) ||
                entry.ModrinthId is null && entry.CurseForgeId is null)
                throw new InvalidDataException("中文模组目录缺少名称、简介或平台身份。");

            var translation = new OnlineModTranslation(entry.ChineseName, entry.Summary);
            if (entry.ModrinthId is { } modrinthId)
            {
                if (modrinthId.Length != 8 || !modrinthId.All(char.IsAsciiLetterOrDigit))
                    throw new InvalidDataException("中文模组目录中的 Modrinth 编号无效。");
                AddProject(new(ModProvider.Modrinth, modrinthId), translation);
            }
            if (entry.CurseForgeId is { } curseForgeId)
            {
                if (!long.TryParse(curseForgeId, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0 ||
                    number.ToString(CultureInfo.InvariantCulture) != curseForgeId)
                    throw new InvalidDataException("中文模组目录中的 CurseForge 编号无效。");
                AddProject(new(ModProvider.CurseForge, curseForgeId), translation);
            }

            AddSearchTerm(entry.ChineseName, entry.EnglishName);
            foreach (var alias in entry.Aliases ?? []) AddSearchTerm(alias, entry.EnglishName);
        }
        EntryCount = entries.Length;
    }

    /// <summary>平台与编号必须同时相符，未知项目继续显示平台原文。</summary>
    public OnlineModTranslation? Find(OnlineProjectId project) => _projects.GetValueOrDefault(project);

    /// <summary>只转换完整、明确的中文名或中文别名，不猜测英文名、部分词或相似项目。</summary>
    public string? ResolveSearchTerm(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !ContainsChinese(text)) return null;
        return _searchTerms.GetValueOrDefault(text.Trim());
    }

    /// <summary>固定身份只能登记一次，避免一个项目展示成另一个模组。</summary>
    private void AddProject(OnlineProjectId project, OnlineModTranslation translation)
    {
        if (!_projects.TryAdd(project, translation)) throw new InvalidDataException("中文模组目录存在重复的平台身份。");
    }

    /// <summary>含糊的中文别名永久标记为不转换，不依赖条目加载顺序。</summary>
    private void AddSearchTerm(string value, string englishName)
    {
        if (string.IsNullOrWhiteSpace(value) || !ContainsChinese(value)) return;
        var key = value.Trim();
        if (!_searchTerms.TryGetValue(key, out var previous)) _searchTerms.Add(key, englishName);
        else if (!string.Equals(previous, englishName, StringComparison.Ordinal)) _searchTerms[key] = null;
    }

    /// <summary>中文搜索词需要含汉字，纯英文和缩写保持用户原始输入。</summary>
    private static bool ContainsChinese(string value) => value.Any(character => character is >= '\u3400' and <= '\u9fff');

    /// <summary>使用程序集内资源确保首次打开页面无需联网或下载字典。</summary>
    private static ChineseModCatalog LoadEmbedded()
    {
        using var input = typeof(ChineseModCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("缺少内置中文模组目录。");
        return new ChineseModCatalog(input);
    }

    /// <summary>目录只存公开项目身份与人工整理的展示文案，不存下载地址或凭据。</summary>
    private sealed record Entry
    {
        public string ChineseName { get; init; } = "";
        public string EnglishName { get; init; } = "";
        public string Summary { get; init; } = "";
        public string? ModrinthId { get; init; }
        public string? CurseForgeId { get; init; }
        public string[]? Aliases { get; init; }
    }
}
