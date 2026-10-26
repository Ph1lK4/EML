using System.Security;
using System.Text.Json;
using EML.Core.Minecraft;

namespace EML.Infrastructure.Minecraft;

/// <summary>只枚举指定根目录下 instances 的直接子目录，不修改游戏文件。</summary>
public sealed class LauncherRootMinecraftInstanceLocator : IMinecraftInstanceLocator
{
    public string InstancesRoot { get; }

    /// <summary>构造扫描器：将启动器根目录转为绝对路径并拼接 instances；不传参数时使用 EXE 所在目录。</summary>
    public LauncherRootMinecraftInstanceLocator(string? launcherRoot = null)
    {
        InstancesRoot = Path.Combine(Path.GetFullPath(launcherRoot ?? AppContext.BaseDirectory), "instances");
    }

    /// <summary>对外扫描入口：把磁盘读取放到后台执行，返回实例列表；取消请求继续向调用方传播。</summary>
    public Task<IReadOnlyList<MinecraftInstance>> FindAllAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<MinecraftInstance>>(() => Scan(cancellationToken), cancellationToken);

    /// <summary>只检查 instances 的直接子目录，根据清单选择标准或 Prism 解析器；单个实例损坏会记录提示，不影响其他实例。</summary>
    private IReadOnlyList<MinecraftInstance> Scan(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(InstancesRoot)) return [];
        var instances = new List<MinecraftInstance>();
        // 根目录无法访问时交由界面报告；单个实例损坏不会中止其他实例。
        foreach (var directory in Directory.GetDirectories(InstancesRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (IsLink(directory)) continue;
                var prism = File.Exists(Path.Combine(directory, "mmc-pack.json"));
                var game = Path.Combine(directory, ".minecraft");
                if (!Directory.Exists(game) && prism) game = Path.Combine(directory, "minecraft");
                if (!Directory.Exists(game) && Directory.Exists(Path.Combine(directory, "versions"))) game = directory;
                if (!Directory.Exists(game))
                {
                    if (prism)
                    {
                        var missingWarnings = new List<string> { "未找到 .minecraft 或 minecraft 游戏目录。" };
                        var missingVersions = ReadPrism(directory, missingWarnings, token);
                        instances.Add(Create(directory, Path.Combine(directory, ".minecraft"),
                            "Prism", missingVersions, missingWarnings) with { Name = ReadPrismName(directory) ?? Path.GetFileName(directory) });
                    }
                    continue;
                }
                if (IsLink(game)) continue;
                var warnings = new List<string>();
                var versions = prism ? ReadPrism(directory, warnings, token) : ReadStandard(game, warnings, token);
                var name = Path.GetFileName(directory);
                if (prism)
                {
                    try { name = ReadPrismName(directory) ?? name; }
                    catch (Exception ex) when (IsDataError(ex)) { warnings.Add("无法读取实例名称：" + ex.Message); }
                }
                instances.Add(Create(directory, game, prism ? "Prism" : "标准目录", versions, warnings) with { Name = name });
            }
            catch (Exception ex) when (IsDataError(ex))
            {
                instances.Add(Create(directory, Path.Combine(directory, ".minecraft"), "未知", [],
                    ["实例读取失败：" + ex.Message]));
            }
        }
        return instances;
    }

    /// <summary>统一构造结果：规范化实例和游戏目录路径，并从详细版本信息生成兼容原接口的 VersionIds。</summary>
    private static MinecraftInstance Create(string directory, string game, string format,
        IReadOnlyList<MinecraftVersionInfo> versions, IReadOnlyList<string> warnings) =>
        new(Path.GetFileName(directory), Path.GetFullPath(directory), Path.GetFullPath(game),
            versions.Select(v => v.VersionId).ToArray())
        { Format = format, Versions = versions, Warnings = warnings };

    /// <summary>读取 mmc-pack.json 的 net.minecraft 和已知加载器组件；优先 version，其次 cachedVersion；损坏时返回空列表并记录提示。</summary>
    private static IReadOnlyList<MinecraftVersionInfo> ReadPrism(string directory, List<string> warnings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = Path.Combine(directory, "mmc-pack.json");
        try
        {
            var root = ReadJson(path);
            if (root.TryGetProperty("formatVersion", out var format) &&
                (format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out var number) || number != 1))
                throw new InvalidDataException("不支持的 Prism 清单格式版本。");
            var components = Array(root, "components").ToArray();
            var minecraft = components.FirstOrDefault(c => Text(c, "uid") == "net.minecraft");
            var version = Text(minecraft, "version") ?? Text(minecraft, "cachedVersion");
            if (version is null) throw new InvalidDataException("Prism 清单缺少 net.minecraft 版本。");
            var loaders = new List<string>();
            foreach (var component in components)
            {
                var loader = LoaderName(Text(component, "uid"));
                if (loader is not null)
                    loaders.Add(loader + " " + (Text(component, "version") ?? Text(component, "cachedVersion") ?? "未知"));
            }
            return [new(version, version, loaders.Count == 0 ? "未声明加载器" : string.Join(" + ", loaders), path)];
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            warnings.Add("mmc-pack.json：" + ex.Message);
            return [];
        }
    }

    /// <summary>先读取 versions 下各文件夹的同名 JSON，再解析版本和加载器；分两步是为了支持引用其他父版本。</summary>
    private static IReadOnlyList<MinecraftVersionInfo> ReadStandard(string game, List<string> warnings, CancellationToken token)
    {
        var root = Path.Combine(game, "versions");
        if (!Directory.Exists(root)) { warnings.Add("未找到 versions 目录，无法确认版本。"); return []; }
        if (IsLink(root)) { warnings.Add("跳过链接形式的 versions 目录。"); return []; }
        var metadata = new Dictionary<string, (JsonElement Json, string Path)>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (IsLink(directory)) continue;
                var id = Path.GetFileName(directory);
                var path = Path.Combine(directory, id + ".json");
                if (!File.Exists(path)) { warnings.Add(id + "：缺少同名版本 JSON。"); continue; }
                metadata[id] = (ReadJson(path), path);
            }
            catch (Exception ex) when (IsDataError(ex)) { warnings.Add(Path.GetFileName(directory) + "：" + ex.Message); }
        }
        var result = new List<MinecraftVersionInfo>();
        foreach (var (folder, entry) in metadata)
        {
            token.ThrowIfCancellationRequested();
            var id = Text(entry.Json, "id") ?? folder;
            var version = ResolveVersion(folder, metadata, new HashSet<string>(StringComparer.OrdinalIgnoreCase), warnings);
            var loaders = GetLoaders(entry.Json);
            result.Add(new(id, version ?? "未知", loaders.Count == 0 ?
                (Text(entry.Json, "mainClass") == "net.minecraft.client.main.Main" ? "原版" : "未知/自定义") :
                string.Join(" + ", loaders), entry.Path));
            if (version is null) warnings.Add(id + "：无法从元数据确认 Minecraft 版本。");
        }
        if (result.Count == 0 && warnings.Count == 0) warnings.Add("versions 中没有可识别的版本。");
        return result;
    }

    /// <summary>按 game 补丁、clientVersion、继承关系、旧 Forge 坐标、原版 ID 的顺序解析游戏版本；visited 防止循环，无法确认则返回 null。</summary>
    private static string? ResolveVersion(string id,
        Dictionary<string, (JsonElement Json, string Path)> metadata, HashSet<string> visited, List<string> warnings)
    {
        if (!visited.Add(id) || visited.Count > 32) { warnings.Add(id + "：版本继承循环或层级过深。"); return null; }
        if (!metadata.TryGetValue(id, out var entry)) return null;
        var json = entry.Json;
        // HMCL 等合并版本会保留 game 补丁，不从文件夹名称猜游戏版本。
        var gamePatch = Array(json, "patches").FirstOrDefault(p => Text(p, "id") is "game" or "net.minecraft");
        var explicitVersion = Text(gamePatch, "version") ?? Text(json, "clientVersion");
        if (explicitVersion is not null) return explicitVersion;
        var parent = Text(json, "inheritsFrom");
        if (parent is not null)
        {
            if (metadata.ContainsKey(parent)) return ResolveVersion(parent, metadata, visited, warnings);
            warnings.Add(id + "：父版本 " + parent + " 未安装；显示清单声明的父版本，尚未验证。");
            return parent;
        }
        foreach (var library in Array(json, "libraries"))
        {
            var coordinate = Text(library, "name")?.Split(':');
            if (coordinate is { Length: >= 3 } && coordinate[0] == "net.minecraftforge" && coordinate[1] == "forge")
            {
                var separator = coordinate[2].IndexOf('-');
                if (separator > 0) return coordinate[2][..separator];
            }
        }
        return Text(json, "mainClass") == "net.minecraft.client.main.Main" ? Text(json, "id") : null;
    }

    /// <summary>提取当前清单的加载器及组件：先读取 patches，再用 libraries 补充；同名组件优先保留补丁中的版本。</summary>
    private static List<string> GetLoaders(JsonElement json)
    {
        var loaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var patch in Array(json, "patches"))
        {
            var label = LoaderName(Text(patch, "id"));
            if (label is not null) loaders[label] = Text(patch, "version") ?? "未知";
        }
        foreach (var library in Array(json, "libraries"))
        {
            var c = Text(library, "name")?.Split(':');
            if (c is not { Length: >= 3 }) continue;
            var label = (c[0], c[1]) switch
            {
                ("net.minecraftforge", "forge") => "Forge",
                ("net.neoforged", "neoforge") => "NeoForge",
                ("net.fabricmc", "fabric-loader") => "Fabric",
                ("org.quiltmc", "quilt-loader") => "Quilt",
                ("com.mumfrey", "liteloader") => "LiteLoader",
                ("optifine", "OptiFine") => "OptiFine",
                _ => null
            };
            if (label is not null) loaders.TryAdd(label, c[2]);
        }
        return loaders.Select(pair => pair.Key + " " + pair.Value).ToList();
    }

    /// <summary>将已知的补丁 ID 或 Prism 组件 UID 映射为显示名称；未知组件返回 null。</summary>
    private static string? LoaderName(string? id) => id switch
    {
        "forge" or "net.minecraftforge" => "Forge",
        "neoforge" or "net.neoforged" => "NeoForge",
        "fabric" or "net.fabricmc.fabric-loader" => "Fabric",
        "quilt" or "org.quiltmc.quilt-loader" => "Quilt",
        "liteloader" or "com.mumfrey.liteloader" => "LiteLoader",
        "optifine" => "OptiFine",
        _ => null
    };

    /// <summary>读取 instance.cfg 中 [General] 节的 name；缺失时返回 null，由调用方退回文件夹名称；只按第一个等号切分。</summary>
    private static string? ReadPrismName(string directory)
    {
        var path = Path.Combine(directory, "instance.cfg");
        if (!File.Exists(path)) return null;
        if (IsLink(path)) throw new InvalidDataException("跳过链接形式的 instance.cfg。");
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("instance.cfg 过大。");
        var general = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { general = line.Equals("[General]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!general || line.StartsWith(';') || line.StartsWith('#')) continue;
            var equal = line.IndexOf('=');
            if (equal > 0 && line[..equal].Trim().Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                var name = line[(equal + 1)..].Trim().Trim('"');
                return name.Length == 0 ? null : name;
            }
        }
        return null;
    }

    /// <summary>限制清单大小和 JSON 深度，要求根节点为对象；Clone 返回独立数据，避免 using 释放文档后节点失效。</summary>
    private static JsonElement ReadJson(string path)
    {
        if (IsLink(path)) throw new InvalidDataException("跳过链接形式的元数据文件。");
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("版本清单超过 4 MiB。");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("清单必须是 JSON 对象。");
        return document.RootElement.Clone();
    }
    /// <summary>安全读取非空字符串属性；对象、字段或字段类型不符合要求时返回 null。</summary>
    private static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
    /// <summary>安全读取数组属性；缺少字段或字段不是数组时返回空集合，便于调用方直接遍历。</summary>
    private static IEnumerable<JsonElement> Array(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray() : [];
    /// <summary>检查路径是否属于符号链接或目录联接等重解析点，供扫描器决定是否跳过，避免沿链接扫描其他位置。</summary>
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    /// <summary>筛选可作为数据问题处理的异常；不包含取消异常，因此取消操作不会被误当成损坏清单。</summary>
    private static bool IsDataError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or SecurityException;
}
