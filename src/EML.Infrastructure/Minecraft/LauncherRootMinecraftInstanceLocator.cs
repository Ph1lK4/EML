using System.Security;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft;

/// <summary>只枚举指定根目录下 instances 的直接子目录，不修改游戏文件。</summary>
public sealed class LauncherRootMinecraftInstanceLocator : IMinecraftInstanceLocator
{
    /// <summary>只扫描此目录的直接子目录，每个子目录视作一个候选实例。</summary>
    public string InstancesRoot { get; }

    /// <summary>设置启动器旁的 instances 扫描目录；默认使用 EXE 所在目录。</summary>
    public LauncherRootMinecraftInstanceLocator(string? launcherRoot = null)
    {
        InstancesRoot = Path.Combine(Path.GetFullPath(launcherRoot ?? AppContext.BaseDirectory), "instances");
    }

    /// <summary>在后台扫描并返回实例列表，支持取消。</summary>
    public Task<IReadOnlyList<MinecraftInstance>> FindAllAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<MinecraftInstance>>(() => Scan(cancellationToken), cancellationToken);

    /// <summary>按目录格式选择解析器；单个实例读取失败只记录提示，不中断其他实例。</summary>
    private IReadOnlyList<MinecraftInstance> Scan(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(InstancesRoot)) return [];
        InstanceImportPaths.EnsureNoLinks(InstancesRoot);
        var instances = new List<MinecraftInstance>();
        // 根目录无法访问时交由界面报告；单个实例损坏不会中止其他实例。
        foreach (var directory in Directory.GetDirectories(InstancesRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (IsLink(directory)) continue;
                if (File.Exists(Path.Combine(directory, InstanceImportManifest.FileName)))
                {
                    instances.Add(ReadImported(directory, token));
                    continue;
                }
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

    /// <summary>读取导入实例，优先使用实际版本信息，缺少时回退到清单声明。</summary>
    private static MinecraftInstance ReadImported(string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var manifestPath = Path.Combine(directory, InstanceImportManifest.FileName);
        var manifest = InstanceManifestStore.Read(manifestPath);
        var game = manifest.GameDirectory == "." ? directory : InstanceImportPaths.Within(directory, manifest.GameDirectory);
        // 校验整个祖先链，避免自定义游戏目录通过中间目录联接指向实例外部。
        InstanceImportPaths.EnsureNoLinks(game);
        var warnings = new List<string>();
        var prism = File.Exists(Path.Combine(directory, "mmc-pack.json"));
        IReadOnlyList<MinecraftVersionInfo> versions = [];
        if (!Directory.Exists(game)) warnings.Add("导入清单指定的游戏目录不存在。");
        if (prism) versions = ReadPrism(directory, warnings, token);
        else if (Directory.Exists(Path.Combine(game, "versions"))) versions = ReadStandard(game, warnings, token);
        // Modrinth / CurseForge 正常只提供安装清单，缺少 versions 不能直接判定为导入损坏。
        var declaredVersions = ReadImportedVersions(manifest, manifestPath);
        versions = ApplyImportedVersionFallback(versions, declaredVersions, warnings);
        if (versions.Count == 0)
            warnings.Add(manifest.SourceFormat == "Zip"
                ? "通用 ZIP 未提供可识别的 Minecraft 版本信息，版本和加载器尚无法确认。"
                : "导入清单未提供可识别的 Minecraft 版本信息。");
        warnings.Add("导入完成，完整性尚未检查。");
        var directFiles = manifest.Files.Count(file => file.Source == InstanceFileSource.DirectDownload);
        var unresolvedFiles = manifest.Files.Count(file => file.Source == InstanceFileSource.CurseForge && file.Path is null);
        if (directFiles > 0) warnings.Add($"清单声明 {directFiles} 个可直接下载的文件；是否缺失尚未检查。");
        if (unresolvedFiles > 0) warnings.Add($"清单声明 {unresolvedFiles} 个 CurseForge 文件引用待解析；尚不能确定文件名及是否缺失。");
        warnings.AddRange(manifest.Notes.Where(note => !string.IsNullOrWhiteSpace(note)));
        var name = Path.GetFileName(directory);
        if (prism || manifest.SourceFormat == "Prism")
        {
            try { name = ReadPrismName(directory) ?? name; }
            catch (Exception ex) when (IsDataError(ex)) { warnings.Add("无法读取实例名称：" + ex.Message); }
        }
        var format = manifest.SourceFormat switch
        {
            "Modrinth" => "Modrinth",
            "CurseForge" => "CurseForge",
            "Prism" => "Prism",
            "Zip" => "通用 ZIP",
            _ => manifest.SourceFormat
        };
        // Name 是导入时的快照，之后重命名无需改写它；界面始终读取当前目录或 Prism 配置名称。
        return Create(directory, game, format, versions, warnings.Distinct().ToArray()) with { Name = name };
    }

    /// <summary>仅补充实际元数据中未知的本体版本，保留已有版本、配置 ID、加载器和元数据路径。</summary>
    private static IReadOnlyList<MinecraftVersionInfo> ApplyImportedVersionFallback(
        IReadOnlyList<MinecraftVersionInfo> versions, IReadOnlyList<MinecraftVersionInfo> declaredVersions,
        List<string> warnings)
    {
        if (versions.Count == 0) return declaredVersions;
        if (declaredVersions.Count == 0) return versions;
        var declaredVersion = declaredVersions[0].MinecraftVersion;
        return versions.Select(version =>
        {
            if (HasKnownMinecraftVersion(version.MinecraftVersion)) return version;
            warnings.Remove(version.VersionId + "：无法从元数据确认 Minecraft 版本。");
            warnings.Add($"{version.VersionId}：版本 JSON 未声明 Minecraft 版本，显示导入清单声明的 {declaredVersion}。");
            return version with { MinecraftVersion = declaredVersion };
        }).ToArray();
    }

    /// <summary>空值及显示占位符不能作为已知本体版本，避免覆盖可用的组件声明。</summary>
    private static bool HasKnownMinecraftVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version) && version != "未知";

    /// <summary>把组件声明转换为版本和加载器显示文本，不代表已经安装。</summary>
    private static IReadOnlyList<MinecraftVersionInfo> ReadImportedVersions(InstanceImportManifest manifest, string manifestPath)
    {
        var minecraft = HasKnownMinecraftVersion(manifest.MinecraftVersion) ? manifest.MinecraftVersion : manifest.Components
            .FirstOrDefault(component => (component.Id is "minecraft" or "net.minecraft") &&
                HasKnownMinecraftVersion(component.Version))?.Version;
        if (!HasKnownMinecraftVersion(minecraft)) return [];
        var loaders = new List<string>();
        foreach (var component in manifest.Components)
        {
            var loader = LoaderName(component.Id);
            var version = component.Version;
            // CurseForge 也允许保留 forge-47.3.0 等完整加载器 ID，展示时才拆分已知前缀。
            if (loader is null)
            {
                foreach (var prefix in new[] { "forge", "neoforge", "fabric", "quilt" })
                {
                    if (!component.Id.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)) continue;
                    loader = LoaderName(prefix);
                    version = component.Id[(prefix.Length + 1)..];
                    break;
                }
            }
            if (loader is not null) loaders.Add(loader + " " + version);
        }
        return [new(minecraft!, minecraft!, loaders.Count == 0 ? "未声明加载器" :
            string.Join(" + ", loaders.Distinct()), manifestPath)];
    }

    /// <summary>统一构造扫描结果，规范化路径并汇总版本 ID。</summary>
    private static MinecraftInstance Create(string directory, string game, string format,
        IReadOnlyList<MinecraftVersionInfo> versions, IReadOnlyList<string> warnings) =>
        new(Path.GetFileName(directory), Path.GetFullPath(directory), Path.GetFullPath(game),
            versions.Select(v => v.VersionId).ToArray())
        { Format = format, Versions = versions, Warnings = warnings };

    /// <summary>读取 mmc-pack.json 中启用的 net.minecraft 和已知加载器组件；优先 version，其次 cachedVersion；损坏时返回空列表并记录提示。</summary>
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
            // 与导入器一致：禁用组件保留在原清单中，但不作为当前实例的游戏版本或加载器显示。
            var components = Array(root, "components").Where(component => component.ValueKind == JsonValueKind.Object &&
                (!component.TryGetProperty("disabled", out var disabled) || disabled.ValueKind != JsonValueKind.True)).ToArray();
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

    /// <summary>读取各版本 JSON，再解析继承关系和加载器，支持引用同目录中的父版本。</summary>
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

    /// <summary>从补丁、继承关系或库坐标解析游戏本体版本；防止循环，无法确认时返回 null。</summary>
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
        // 统一运行安装器写入的标准配置保留独立本体版本，不能把 EML 配置 ID 当作游戏版本。
        var generatedVersion = Text(json, "emlMinecraftVersion");
        if (HasKnownMinecraftVersion(generatedVersion)) return generatedVersion;
        return Text(json, "mainClass") == "net.minecraft.client.main.Main" ? Text(json, "id") : null;
    }

    /// <summary>从 patches 和 libraries 提取加载器，同名项优先采用补丁版本。</summary>
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
        "fabric" or "fabric-loader" or "net.fabricmc.fabric-loader" => "Fabric",
        "quilt" or "quilt-loader" or "org.quiltmc.quilt-loader" => "Quilt",
        "liteloader" or "com.mumfrey.liteloader" => "LiteLoader",
        "optifine" => "OptiFine",
        _ => null
    };

    /// <summary>读取 Prism 配置的 General/name；缺失时返回 null，由调用方使用文件夹名。</summary>
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

    /// <summary>验证大小与结构后读取 JSON，并返回不依赖原文档生命周期的副本。</summary>
    private static JsonElement ReadJson(string path)
    {
        if (IsLink(path)) throw new InvalidDataException("跳过链接形式的元数据文件。");
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("版本清单超过 4 MiB。");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("清单必须是 JSON 对象。");
        return document.RootElement.Clone();
    }
    /// <summary>读取非空字符串字段；缺失或类型不符时返回 null。</summary>
    private static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
    /// <summary>读取数组字段；缺失或类型不符时返回空集合。</summary>
    private static IEnumerable<JsonElement> Array(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray() : [];
    /// <summary>判断路径是否为符号链接或目录联接，供扫描器跳过。</summary>
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    /// <summary>筛选可记录为实例读取提示的异常；取消异常仍向外传播。</summary>
    private static bool IsDataError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or SecurityException;
}
