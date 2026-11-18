using System.Text.Json;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.External;

/// <summary>受控复制根；排除名称只作用于该根的第一层，不丢弃游戏内部的同名配置。</summary>
internal sealed record ExternalCopyRoot(string SourcePath, string TargetPath, IReadOnlyList<string> ExcludedTopLevelNames);

/// <summary>重新读取源元数据所得的复制计划；生成内容仅含 EML 所需的公开运行组件。</summary>
internal sealed record ExternalInstanceDefinition(ExternalMinecraftInstance Instance, InstanceImportManifest Manifest,
    IReadOnlyList<ExternalCopyRoot> Roots, IReadOnlyDictionary<string, string> GeneratedFiles,
    InstanceBackupManifest.Snapshot? PreservedSource = null);

/// <summary>把其他启动器的已安装实例转换为独立实例，不导入账号、Java 路径或启动钩子。</summary>
internal static class ExternalInstanceReader
{
    private const int MaximumJsonBytes = 8 * 1024 * 1024;
    private static readonly string[] RuntimeNames = ["versions", "assets", "libraries", "runtime", "natives"];
    private static readonly string[] PrivateRootNames = ["accounts.json", "launcher_accounts.json", "launcher_profiles.json",
        "launcher_profiles_microsoft_store.json", "instance.cfg", "profile.json", "minecraftinstance.json", "instance.json", "config.json",
        "PCL", "HMCL", ".hmcl", "hmclversion.cfg", ".eml-runtime", "cache", "caches", "runtime", "logs", "crash-reports", "natives"];

    /// <summary>只信任用户选择的源路径与版本 ID，其余候选信息在导入前全部重新解析。</summary>
    internal static ExternalInstanceDefinition Read(ExternalMinecraftInstance source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.SourcePath));
        InstanceImportPaths.EnsureNoLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("源实例目录不存在：" + root);
        if (File.Exists(Path.Combine(root, InstanceImportManifest.FileName)))
            return ReadPreserved(root, token);
        if (File.Exists(Path.Combine(root, "mmc-pack.json"))) return ReadPrism(root, token);
        if (TryModrinthDatabase(root, token) is { } modrinth) return modrinth;
        if (File.Exists(Path.Combine(root, "profile.json"))) return ReadModrinth(root, token);
        if (File.Exists(Path.Combine(root, "minecraftinstance.json"))) return ReadCurseForge(root, token);
        if (File.Exists(Path.Combine(root, "instance.json"))) return ReadAtLauncher(root, token);
        if (File.Exists(Path.Combine(root, "config.json")) && !Directory.Exists(Path.Combine(root, "versions")))
            return ReadGdLauncher(root, token);
        var game = Directory.Exists(Path.Combine(root, "versions")) ? root : Path.Combine(root, ".minecraft");
        if (Directory.Exists(Path.Combine(game, "versions"))) return ReadStandard(root, game, source.SelectionId, token);
        throw new InvalidDataException("未找到受支持的实例元数据；不能从 mods 文件名猜测版本。请选择实例根目录，或从原启动器导出整合包。路径：" + root);
    }

    /// <summary>识别带有 EML 清单的外部实例，折叠可移植来源记录，不把当前磁盘内容重新认证。</summary>
    private static ExternalInstanceDefinition ReadPreserved(string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var original = InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName));
        if (original.SourceFormat is not ("Prism" or "Modrinth" or "CurseForge" or "Zip"))
            throw new InvalidDataException("不支持的 EML 来源格式：" + original.SourceFormat);
        var game = original.GameDirectory == "." ? root : InstanceImportPaths.Within(root, original.GameDirectory);
        InstanceImportPaths.EnsureNoLinks(game);
        if (!Directory.Exists(game)) throw new DirectoryNotFoundException("EML 清单中的游戏目录不存在：" + game);
        var snapshot = InstanceBackupManifest.Create(root, original, rebaseArchiveEntries: false);
        var name = string.IsNullOrWhiteSpace(original.Name) ? Path.GetFileName(root) : original.Name;
        var loader = string.Join(" + ", original.Components.Where(c => c.Id is "forge" or "neoforge" or "fabric-loader" or
            "quilt-loader" or "net.minecraftforge" or "net.neoforged" or "net.fabricmc.fabric-loader" or "org.quiltmc.quilt-loader")
            .Select(c => c.Id + " " + c.Version));
        var generated = new Dictionary<string, string>();
        var exclusions = new List<string> { InstanceImportManifest.FileName };
        var launcher = "EML 来源 / " + original.SourceFormat;
        if (original.SourceFormat == "Prism")
        {
            var prism = ReadPrism(root, token);
            var declared = original.Components.Select(c => (c.Id, c.Version)).OrderBy(c => c.Id).ThenBy(c => c.Version);
            var actual = prism.Manifest.Components.Select(c => (c.Id, c.Version)).OrderBy(c => c.Id).ThenBy(c => c.Version);
            if (!string.Equals(game, prism.Instance.GameDirectory, StringComparison.OrdinalIgnoreCase) ||
                original.MinecraftVersion is { } version && version != prism.Instance.MinecraftVersion || !declared.SequenceEqual(actual))
                throw new InvalidDataException("Prism 组件或游戏目录与已有 EML 来源清单不一致；请先核对实例版本，不能静默丢弃原来源。");
            var configuration = snapshot.Manifest.Files.FirstOrDefault(file =>
                string.Equals(file.Path, "instance.cfg", StringComparison.OrdinalIgnoreCase));
            if (configuration is not null && !InstanceFileChangePolicy.IsMutableConfiguration(configuration, original.GameDirectory, "Prism"))
                throw new InvalidDataException("instance.cfg 有独立的严格校验要求，不能在清除本机启动设置时改写；请先处理其来源记录。");
            name = prism.Instance.Name;
            launcher = "Prism / MultiMC（保留 EML 来源）";
            generated.Add("instance.cfg", prism.GeneratedFiles["instance.cfg"]);
            exclusions.Add("instance.cfg");
        }
        return new(new(name, launcher, root, game, original.MinecraftVersion, loader.Length == 0 ? "组件清单" : loader),
            snapshot.Manifest, [new(root, ".", exclusions)], generated, snapshot);
    }

    /// <summary>按版本枚举候选；隔离目录与共享目录同时有数据时展示两个明确选项。</summary>
    private static IReadOnlyList<string> StandardSelections(string game, IReadOnlyDictionary<string, JsonElement> profiles)
    {
        return profiles.Keys.Order(StringComparer.OrdinalIgnoreCase).SelectMany(id =>
            HasPlayerContent(Path.Combine(game, "versions", id))
                ? HasPlayerContent(game) ? new[] { "standard:" + id + ":isolated", "standard:" + id + ":shared" } : ["standard:" + id + ":isolated"]
                : ["standard:" + id + ":shared"]).ToArray();
    }

    /// <summary>一轮发现只读取一次共享版本描述，逐项处理坏继承链，避免大量版本时重复扫描。</summary>
    internal static IReadOnlyList<ExternalInstanceDefinition> StandardCandidates(string root, string game,
        List<string> warnings, CancellationToken token)
    {
        var profiles = ReadProfiles(game, token);
        var result = new List<ExternalInstanceDefinition>();
        foreach (var choice in StandardSelections(game, profiles))
        {
            token.ThrowIfCancellationRequested();
            try { result.Add(ReadStandard(root, game, choice, token, profiles)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            { warnings.Add(root + " [" + choice + "]：" + ex.Message); }
        }
        return result;
    }

    /// <summary>Prism/MultiMC 按实际组件和游戏目录复制，instance.cfg 仅重建名称。</summary>
    private static ExternalInstanceDefinition ReadPrism(string root, CancellationToken token)
    {
        var json = ReadJson(Path.Combine(root, "mmc-pack.json"), token);
        if (!json.TryGetProperty("formatVersion", out var format) || !format.TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException("不支持的 Prism 组件清单格式。");
        var components = Array(json, "components").Where(c => !False(c, "enabled") && !True(c, "disabled")).Select(c =>
            new InstanceComponentRequirement(FixedVersion(Required(c, "uid")), FixedVersion(Text(c, "version") ?? Required(c, "cachedVersion")))).ToArray();
        if (components.Length > 1024) throw new InvalidDataException("Prism 组件数量过多。");
        var minecraft = components.SingleOrDefault(c => c.Id == "net.minecraft")?.Version ??
            throw new InvalidDataException("Prism 清单缺少 Minecraft 版本。");
        var game = Directory.Exists(Path.Combine(root, ".minecraft")) ? Path.Combine(root, ".minecraft") : Path.Combine(root, "minecraft");
        if (!Directory.Exists(game)) throw new DirectoryNotFoundException("Prism 游戏目录不存在：" + game);
        var name = Path.GetFileName(root);
        var cfg = Path.Combine(root, "instance.cfg");
        if (File.Exists(cfg))
        {
            InstanceImportPaths.EnsureNoLinks(cfg);
            if (new FileInfo(cfg).Length > 1024 * 1024) throw new InvalidDataException("Prism 配置过大。");
            var general = true;
            using var input = new FileStream(cfg, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(input);
            while (reader.ReadLine() is { } line)
            {
                token.ThrowIfCancellationRequested();
                var clean = line.Trim();
                if (clean.StartsWith('[')) general = clean.Equals("[General]", StringComparison.OrdinalIgnoreCase);
                else if (general && clean.StartsWith("name=", StringComparison.OrdinalIgnoreCase) && clean.Length > 5) name = clean[5..];
            }
        }
        var roots = new List<ExternalCopyRoot> { new(game, ".minecraft", [.. PrivateRootNames, "versions"]), new(Path.Combine(root, "mmc-pack.json"), "mmc-pack.json", []) };
        foreach (var directory in new[] { "patches", "libraries", "jarmods" })
            if (Directory.Exists(Path.Combine(root, directory))) roots.Add(new(Path.Combine(root, directory), directory, []));
        var loader = string.Join(" + ", components.Where(c => c.Id is "net.minecraftforge" or "net.neoforged" or "net.fabricmc.fabric-loader" or "org.quiltmc.quilt-loader")
            .Select(c => c.Id + " " + c.Version));
        return Definition(root, game, name, "Prism / MultiMC", minecraft, loader.Length == 0 ? "组件清单" : loader,
            null, components, roots, new Dictionary<string, string> { ["instance.cfg"] = "[General]\nname=" + InstanceImportPaths.SuggestName(name) + "\n" });
    }

    /// <summary>读取 Modrinth 旧版 profile.json 的公开字段；未知加载器明确拒绝。</summary>
    private static ExternalInstanceDefinition ReadModrinth(string root, CancellationToken token)
    {
        var json = ReadJson(Path.Combine(root, "profile.json"), token);
        if (json.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object) json = metadata;
        var name = Text(json, "name") ?? Path.GetFileName(root);
        var minecraft = Text(json, "game_version") ?? Text(json, "gameVersion") ?? throw new InvalidDataException("Modrinth 实例缺少 game_version。");
        var loader = Text(json, "loader") ?? Text(json, "mod_loader") ?? throw new InvalidDataException("Modrinth 实例缺少加载器声明，不能自动当作原版。");
        string? loaderVersion = Text(json, "loader_version") ?? Text(json, "mod_loader_version");
        if (loaderVersion is null && json.TryGetProperty("loader_version", out var version) && version.ValueKind == JsonValueKind.Object)
            loaderVersion = Text(version, "id");
        return ComponentInstance(root, root, name, "Modrinth App", minecraft, loader, loaderVersion, ["profile.json", "modrinth.index.json"]);
    }

    /// <summary>CurseForge App 的已安装实例以 baseModLoader 为准，不把过期 manifest 当作当前模组清单。</summary>
    private static ExternalInstanceDefinition ReadCurseForge(string root, CancellationToken token)
    {
        var json = ReadJson(Path.Combine(root, "minecraftinstance.json"), token);
        var minecraft = Text(json, "gameVersion") ?? Text(json, "minecraftVersion") ?? throw new InvalidDataException("CurseForge 实例缺少游戏版本。");
        var name = Text(json, "name") ?? Path.GetFileName(root);
        if (!json.TryGetProperty("baseModLoader", out var loader))
            throw new InvalidDataException("CurseForge 实例未声明加载器，请从原启动器导出整合包。");
        if (loader.ValueKind == JsonValueKind.Null)
            return ComponentInstance(root, root, name, "CurseForge", minecraft, "vanilla", null, ["minecraftinstance.json", "manifest.json"]);
        var id = Required(loader, "name");
        var separator = id.IndexOf('-');
        if (separator < 1) throw new InvalidDataException("无法识别 CurseForge 加载器：" + id);
        var loaderVersion = Text(loader, "forgeVersion") ?? id[(separator + 1)..];
        if (loaderVersion.EndsWith("-" + minecraft, StringComparison.OrdinalIgnoreCase)) loaderVersion = loaderVersion[..^(minecraft.Length + 1)];
        return ComponentInstance(root, root, name, "CurseForge", minecraft, id[..separator], loaderVersion,
            ["minecraftinstance.json", "manifest.json"]);
    }

    /// <summary>ATLauncher 只转换明确记录的 Minecraft 和加载器版本；异常结构保留为诊断信息。</summary>
    private static ExternalInstanceDefinition ReadAtLauncher(string root, CancellationToken token)
    {
        var json = ReadJson(Path.Combine(root, "instance.json"), token);
        if (!json.TryGetProperty("launcher", out var launcher) || launcher.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("不支持此 instance.json 格式，请从原启动器导出整合包。");
        var minecraft = Text(json, "id") ?? Text(launcher, "minecraftVersion") ?? throw new InvalidDataException("ATLauncher 实例缺少 Minecraft 版本。");
        var name = Text(launcher, "name") ?? Path.GetFileName(root);
        if (!launcher.TryGetProperty("loaderVersion", out var loader) || loader.ValueKind == JsonValueKind.Null)
            return ComponentInstance(root, root, name, "ATLauncher", minecraft, "vanilla", null, ["instance.json"]);
        return ComponentInstance(root, root, name, "ATLauncher", minecraft, Required(loader, "type"), Text(loader, "rawVersion") ?? Required(loader, "version"), ["instance.json"]);
    }

    /// <summary>GDLauncher 旧版目录按 config.json 中明确的 loader 信息转换。</summary>
    private static ExternalInstanceDefinition ReadGdLauncher(string root, CancellationToken token)
    {
        var json = ReadJson(Path.Combine(root, "config.json"), token);
        if (!json.TryGetProperty("loader", out var loader) || loader.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("无法识别 GDLauncher 实例配置，请从原启动器导出整合包。");
        return ComponentInstance(root, root, Text(json, "name") ?? Path.GetFileName(root), "GDLauncher",
            Required(loader, "mcVersion"), Required(loader, "loaderType"), Text(loader, "loaderVersion"), ["config.json"]);
    }

    /// <summary>把明确的公开组件写成最小 Prism 清单，重建运行依赖而不执行原启动器钩子。</summary>
    internal static ExternalInstanceDefinition ComponentInstance(string root, string game, string name, string launcher,
        string minecraft, string loader, string? loaderVersion, IReadOnlyList<string> excluded)
    {
        FixedVersion(minecraft);
        var components = new List<InstanceComponentRequirement> { new("net.minecraft", minecraft) };
        var uid = loader.ToLowerInvariant() switch
        {
            "vanilla" or "minecraft" => null,
            "forge" => "net.minecraftforge",
            "neoforge" => "net.neoforged",
            "fabric" => "net.fabricmc.fabric-loader",
            "quilt" => "org.quiltmc.quilt-loader",
            _ => throw new InvalidDataException("不支持或无法确认加载器：" + loader)
        };
        if (uid is not null)
        {
            if (string.IsNullOrWhiteSpace(loaderVersion)) throw new InvalidDataException("实例缺少 " + loader + " 固定版本。");
            if (loader.Equals("forge", StringComparison.OrdinalIgnoreCase) && loaderVersion.StartsWith(minecraft + "-", StringComparison.Ordinal))
                loaderVersion = loaderVersion[(minecraft.Length + 1)..];
            FixedVersion(loaderVersion);
            components.Add(new(uid, loaderVersion));
        }
        var generated = new Dictionary<string, string>
        {
            ["mmc-pack.json"] = JsonSerializer.Serialize(new { formatVersion = 1, components = components.Select(c => new { uid = c.Id, version = c.Version }) }),
            ["instance.cfg"] = "[General]\nname=" + InstanceImportPaths.SuggestName(name) + "\n"
        };
        return Definition(root, game, name, launcher, minecraft, loader + (loaderVersion is null ? "" : " " + loaderVersion), null,
            components, [new(game, ".minecraft", [.. RuntimeNames, .. PrivateRootNames, .. excluded, "mmc-pack.json", "instance.cfg", "patches"])], generated);
    }

    /// <summary>标准目录保留选中版本与继承链，游戏数据按用户确认的隔离／共享选择复制。</summary>
    private static ExternalInstanceDefinition ReadStandard(string root, string game, string? selection, CancellationToken token,
        IReadOnlyDictionary<string, JsonElement>? existing = null)
    {
        var profiles = existing ?? ReadProfiles(game, token);
        var choices = StandardSelections(game, profiles);
        selection ??= choices.Count == 1 ? choices[0] : throw new InvalidDataException("此目录包含多个版本，请先选择要导入的版本。");
        if (!choices.Contains(selection, StringComparer.Ordinal)) throw new InvalidDataException("原实例版本或游戏目录选择已变化，请重新发现：" + selection);
        var separator = selection.LastIndexOf(':');
        var selectedId = selection["standard:".Length..separator];
        var isolated = selection[(separator + 1)..] == "isolated";
        var chain = new List<string>();
        var id = selectedId;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            ValidateVersion(id);
            if (chain.Contains(id, StringComparer.OrdinalIgnoreCase) || chain.Count >= 64) throw new InvalidDataException("版本存在循环或过深继承。");
            if (!profiles.TryGetValue(id, out var json)) throw new InvalidDataException("缺少父版本描述：" + id);
            chain.Add(id);
            if (Text(json, "inheritsFrom") is not { } parent) break;
            id = parent;
        }
        var selected = profiles[selectedId];
        var minecraft = chain.Select(member => ExplicitMinecraftVersion(profiles[member])).FirstOrDefault(value => value is not null)
            ?? (Text(profiles[chain[^1]], "mainClass") == "net.minecraft.client.main.Main" ? FixedVersion(chain[^1]) : null)
            ?? throw new InvalidDataException("无法从版本描述确认 Minecraft 版本：" + selectedId);
        var versionDirectory = Path.Combine(game, "versions", selectedId);
        var sharedData = HasPlayerContent(game);
        var actualGame = isolated ? versionDirectory : game;
        var roots = new List<ExternalCopyRoot> { new(actualGame, ".minecraft", [.. RuntimeNames, .. PrivateRootNames, selectedId + ".json", selectedId + ".jar"]) };
        foreach (var shared in new[] { "assets", "libraries" })
            if (Directory.Exists(Path.Combine(game, shared))) roots.Add(new(Path.Combine(game, shared), ".minecraft/" + shared, []));
        foreach (var version in chain)
        {
            var directory = Path.Combine(game, "versions", version);
            roots.Add(new(Path.Combine(directory, version + ".json"), ".minecraft/versions/" + version + "/" + version + ".json", []));
            if (File.Exists(Path.Combine(directory, version + ".jar"))) roots.Add(new(Path.Combine(directory, version + ".jar"), ".minecraft/versions/" + version + "/" + version + ".jar", []));
        }
        // jar 重定向只复制字节，不能把另一独立启动配置作为当前实例的候选。
        foreach (var jar in chain.Select(member => Text(profiles[member], "jar")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ValidateVersion(jar);
            var path = Path.Combine(game, "versions", jar, jar + ".jar");
            if (!chain.Contains(jar, StringComparer.OrdinalIgnoreCase) && File.Exists(path))
                roots.Add(new(path, $".minecraft/versions/{jar}/{jar}.jar", []));
        }
        var loader = DescribeLoader(chain.Select(member => profiles[member]));
        var name = selectedId + (HasPlayerContent(versionDirectory) && sharedData ? isolated ? "（独立目录）" : "（共享目录）" : "");
        var instance = new ExternalMinecraftInstance(name, "标准目录（官方 / PCL / HMCL / BakaXL）", root, actualGame, minecraft, loader, selection);
        return new(instance, new() { Name = name, SourceFormat = "Zip", GameDirectory = ".minecraft", MinecraftVersion = minecraft,
            Components = [new("minecraft", minecraft)], Notes = ["仅复制选中版本及其继承链；保留游戏数据，补全时仍检查官方运行依赖。",
                "不读取含账户信息的 launcher_profiles.json；自定义游戏目录请核对预览路径，避免导错存档。"] }, roots, new Dictionary<string, string>());
    }

    /// <summary>从合并配置或加载器元数据识别游戏版本，不从整合包名称猜测。</summary>
    private static string? ExplicitMinecraftVersion(JsonElement json)
    {
        if (Text(json, "clientVersion") is { } version) return FixedVersion(version);
        if (json.TryGetProperty("patches", out var patches) && patches.ValueKind == JsonValueKind.Array)
            foreach (var patch in patches.EnumerateArray())
                if (Text(patch, "id") is "game" or "net.minecraft" && Text(patch, "version") is { } value) return FixedVersion(value);
        if (json.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
            foreach (var library in libraries.EnumerateArray())
                if (Text(library, "name") is { } coordinate && coordinate.StartsWith("net.minecraftforge:forge:", StringComparison.Ordinal) &&
                    coordinate.Split(':')[2].Split('-') is { Length: > 1 } parts) return FixedVersion(parts[0]);
        return null;
    }

    /// <summary>只显示原描述中声明的加载器，未知主类保留自定义标签。</summary>
    private static string DescribeLoader(IEnumerable<JsonElement> profiles)
    {
        var names = new HashSet<string>();
        var vanilla = true;
        foreach (var json in profiles)
        {
            if (Text(json, "mainClass") is { } main && main != "net.minecraft.client.main.Main") vanilla = false;
            if (!json.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array) continue;
            foreach (var library in libraries.EnumerateArray())
                foreach (var (prefix, label) in new[] { ("net.minecraftforge:", "Forge"), ("net.neoforged:", "NeoForge"),
                    ("net.fabricmc:fabric-loader:", "Fabric"), ("org.quiltmc:quilt-loader:", "Quilt") })
                    if (Text(library, "name") is { } coordinate && coordinate.StartsWith(prefix, StringComparison.Ordinal)) names.Add(label);
        }
        return names.Count > 0 ? string.Join(" + ", names) : vanilla ? "原版" : "自定义加载器（保留原版本配置）";
    }

    /// <summary>仅检查常见游戏数据标记，数据位置存在歧义时交给用户选择。</summary>
    private static bool HasPlayerContent(string path) => new[] { "mods", "config", "saves", "resourcepacks", "screenshots" }
        .Any(folder => Directory.Exists(Path.Combine(path, folder))) || File.Exists(Path.Combine(path, "options.txt")) || File.Exists(Path.Combine(path, "servers.dat"));

    /// <summary>仅关联相邻 profiles/instances 容器的数据库，不查询账户或令牌表。</summary>
    private static ExternalInstanceDefinition? TryModrinthDatabase(string root, CancellationToken token)
    {
        var container = Path.GetDirectoryName(root);
        if (container is null || !new[] { "profiles", "instances" }.Contains(Path.GetFileName(container), StringComparer.OrdinalIgnoreCase)) return null;
        var database = Path.Combine(Path.GetDirectoryName(container)!, "app.db");
        if (!File.Exists(database)) return null;
        var relative = Path.GetFileName(root);
        var row = ModrinthProfileDatabase.Read(database, token).SingleOrDefault(profile =>
            profile.Path.Replace('\\', '/').TrimEnd('/').Equals(relative, StringComparison.OrdinalIgnoreCase) ||
            profile.Path.Replace('\\', '/').TrimEnd('/').Equals(root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (row is null) throw new InvalidDataException("Modrinth 数据库没有此实例的公开版本记录：" + root);
        return ComponentInstance(root, root, row.Name, "Modrinth App", row.MinecraftVersion, row.Loader, row.LoaderVersion,
            ["profile.json", "modrinth.index.json"]);
    }

    /// <summary>统一组件型目录的清单与预览说明，不把本地文件标记成官方下载。</summary>
    private static ExternalInstanceDefinition Definition(string root, string game, string name, string launcher,
        string minecraft, string loader, string? selection, IReadOnlyList<InstanceComponentRequirement> components,
        IReadOnlyList<ExternalCopyRoot> roots, IReadOnlyDictionary<string, string> generated) =>
        new(new(name, launcher, root, game, minecraft, loader, selection), new() { Name = name, SourceFormat = "Prism",
            GameDirectory = ".minecraft", MinecraftVersion = minecraft, Components = components,
            Notes = ["从 " + launcher + " 复制本地实例；不迁移账号、Java 路径和启动钩子。", "模组与玩家数据保持原样，运行依赖按已声明的固定组件重新准备。"] }, roots, generated);

    /// <summary>有界读取标准版本描述，拒绝无效名称与重复 ID。</summary>
    private static Dictionary<string, JsonElement> ReadProfiles(string game, CancellationToken token)
    {
        var root = Path.Combine(game, "versions");
        InstanceImportPaths.EnsureNoLinks(root);
        var profiles = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        long totalBytes = 0;
        foreach (var path in Directory.EnumerateDirectories(root))
        {
            token.ThrowIfCancellationRequested();
            if (++count > 1024) throw new InvalidDataException("版本目录过多，停止扫描。");
            InstanceImportPaths.EnsureNoLinks(path);
            var id = Path.GetFileName(path);
            ValidateVersion(id);
            var file = Path.Combine(path, id + ".json");
            if (!File.Exists(file)) continue;
            InstanceImportPaths.EnsureNoLinks(file);
            if ((totalBytes += new FileInfo(file).Length) > 32 * 1024 * 1024)
                throw new InvalidDataException("版本元数据总量超过 32 MiB，请选择较小的启动器目录。");
            var json = ReadJson(file, token);
            if (json.TryGetProperty("emlLaunchProfile", out _)) continue;
            if (Text(json, "id") is { } declared && !declared.Equals(id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("版本文件夹与配置 ID 不一致：" + path);
            profiles.Add(id, json);
        }
        if (profiles.Count == 0) throw new InvalidDataException("没有可读取的标准版本描述。");
        return profiles;
    }

    /// <summary>只打开有大小上限的元数据，禁止符号链接；不读取账号数据库。</summary>
    internal static JsonElement ReadJson(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        InstanceImportPaths.EnsureNoLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumJsonBytes) throw new InvalidDataException("实例元数据过大：" + path);
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("实例元数据不是对象：" + path);
        return document.RootElement.Clone();
    }

    /// <summary>仅接受单段版本标识，防止元数据构造实例外路径。</summary>
    private static void ValidateVersion(string value)
    {
        if (InstanceImportPaths.Relative(value).Contains('/')) throw new InvalidDataException("版本标识不能包含路径。");
    }

    /// <summary>远程组件版本必须是固定单段标识；标准目录名称另行允许中文与空格。</summary>
    private static string FixedVersion(string value)
    {
        if (value.Length is < 1 or > 128 || value is "." or ".." or "latest" or "recommended" ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_' or '+')))
            throw new InvalidDataException("无效或非固定的组件版本：" + value);
        return value;
    }

    /// <summary>读取非空字符串；不把 JSON 数字、数组转换为隐含版本。</summary>
    internal static string? Text(JsonElement json, string name) => json.ValueKind == JsonValueKind.Object &&
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
    /// <summary>读取必需的字符串字段。</summary>
    private static string Required(JsonElement json, string name) => Text(json, name) ?? throw new InvalidDataException("实例元数据缺少字段：" + name);
    /// <summary>只枚举真实数组；缺失时拒绝空组件清单。</summary>
    private static IEnumerable<JsonElement> Array(JsonElement json, string name) => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray() : throw new InvalidDataException("实例元数据缺少数组：" + name);
    /// <summary>识别明确禁用的组件，不从其他值推断。</summary>
    private static bool False(JsonElement json, string name) => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;
    /// <summary>识别明确标为 disabled 的组件。</summary>
    private static bool True(JsonElement json, string name) => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
