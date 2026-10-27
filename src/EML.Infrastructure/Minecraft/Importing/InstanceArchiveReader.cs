using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EML.Core.Minecraft;

namespace EML.Infrastructure.Minecraft.Importing;

/// <summary>
/// 将不同平台的压缩包转换为统一导入计划，只解析清单和文件布局，不下载文件。
/// </summary>
internal static class InstanceArchiveReader
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> GameFolders = new(PathComparer)
    {
        "versions", "mods", "config", "saves", "resourcepacks", "shaderpacks", "assets", "libraries"
    };
    private static readonly HashSet<string> GameFiles = new(PathComparer)
    {
        "options.txt", "servers.dat", "launcher_profiles.json"
    };
    private static readonly HashSet<string> PayloadFolders = new(GameFolders, PathComparer)
    {
        ".minecraft", "minecraft", "overrides", "client-overrides", "server-overrides", "patches"
    };

    /// <summary>验证 ZIP 条目并识别唯一包格式，调用对应解析器生成导入计划。</summary>
    internal static InstanceArchivePlan Read(ZipArchive archive, string archiveFileName, CancellationToken token)
    {
        var entries = SafeInstanceArchive.ReadEntries(archive, token);
        var markers = new List<PackageMarker>();
        var emlRoots = entries.Where(item => !item.Directory && !IsPayloadPath(item.Path) &&
                LastPart(item.Path).Equals(InstanceImportManifest.FileName, StringComparison.OrdinalIgnoreCase))
            .Select(item => Parent(item.Path)).ToHashSet(PathComparer);
        var customPayloadRoots = new List<string>();
        foreach (var entry in entries.Where(item => !item.Directory)
                     .OrderBy(item => item.Path.Count(character => character == '/')))
        {
            token.ThrowIfCancellationRequested();
            // 游戏目录和覆盖目录中的同名文件属于游戏载荷，不能当作第二个整合包清单解析。
            if (IsPayloadPath(entry.Path) || customPayloadRoots.Any(root => IsWithin(entry.Path, root)))
                continue;
            var name = LastPart(entry.Path);
            string? format = name.ToLowerInvariant() switch
            {
                "eml-instance.json" => "EML",
                "modrinth.index.json" => "Modrinth",
                "mmc-pack.json" => "Prism",
                _ => null
            };
            if (format is null && name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                // 自有备份以 eml-instance.json 为准；保留的平台清单即使过期也不重新覆盖声明。
                if (emlRoots.Contains(Parent(entry.Path)))
                    continue;
                var json = SafeInstanceArchive.ReadJson(entry);
                if (json.ValueKind == JsonValueKind.Object &&
                    (json.TryGetProperty("manifestType", out _) ||
                     (json.TryGetProperty("manifestVersion", out _) && json.TryGetProperty("minecraft", out _))))
                {
                    format = "CurseForge";
                    var overrides = InstanceImportPaths.Relative(OptionalString(json, "overrides") ?? "overrides");
                    customPayloadRoots.Add(Combine(Parent(entry.Path), overrides));
                }
            }
            if (format is not null)
                markers.Add(new PackageMarker(Parent(entry.Path), format, entry));
        }

        if (markers.Count == 0)
            return ReadGeneric(entries, archiveFileName, token);

        var roots = markers.Select(marker => marker.Root).Distinct(PathComparer).ToArray();
        if (roots.Length != 1)
            throw new InvalidDataException("压缩包包含多个实例清单或嵌套整合包，无法确定要导入的实例。请一次选择一个实例包。");
        var selected = markers.FirstOrDefault(marker => marker.Format == "EML");
        if (selected is null)
        {
            if (markers.Count != 1)
                throw new InvalidDataException("同一目录同时包含多个平台的实例清单，无法可靠判断包格式。");
            selected = markers[0];
        }

        // 包装目录外可以有 README；若还有另一个游戏目录，则不能只导入其中一个而静默丢弃另一个。
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (!IsWithin(entry.Path, selected.Root) &&
                !(entry.Directory && IsWithin(selected.Root, entry.Path)) && HasGameContent(entry))
                throw new InvalidDataException("平台整合包目录外还存在其他游戏内容。请把每个实例分别打包后导入。");
        }

        return selected.Format switch
        {
            "EML" => ReadEml(entries, selected, archiveFileName),
            "Modrinth" => ReadModrinth(entries, selected, archiveFileName, token),
            "CurseForge" => ReadCurseForge(entries, selected, archiveFileName, token),
            "Prism" => ReadPrism(entries, selected, archiveFileName, token),
            _ => throw new InvalidDataException("不支持的实例包格式。")
        };
    }

    /// <summary>解析 EML 备份，保留原始下载需求，并为包内文件生成重新提取计划。</summary>
    private static InstanceArchivePlan ReadEml(IReadOnlyList<SafeArchiveEntry> entries, PackageMarker marker,
        string archiveFileName)
    {
        var manifest = InstanceManifestStore.Parse(SafeInstanceArchive.ReadJson(marker.Entry));
        var copies = CopyTree(entries, marker.Root, "")
            .Where(copy => !copy.TargetPath.Equals(InstanceImportManifest.FileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new InstanceArchivePlan(manifest with
        {
            SourceArchive = Path.GetFileName(archiveFileName),
            IntegrityStatus = "NotChecked",
            Notes = manifest.Notes.Concat(["这是 EML 实例备份；已重新导入包内文件，远端依赖和完整性仍需后续检查。"]).ToArray()
        }, copies, PreserveRemoteRequirements: true);
    }

    /// <summary>解析 mrpack 的客户端下载需求，并按优先级合并公共及客户端覆盖文件。</summary>
    private static InstanceArchivePlan ReadModrinth(IReadOnlyList<SafeArchiveEntry> entries, PackageMarker marker,
        string archiveFileName, CancellationToken token)
    {
        var json = SafeInstanceArchive.ReadJson(marker.Entry);
        RequireObject(json, "Modrinth 清单");
        if (RequiredInteger(json, "formatVersion") != 1 || RequiredString(json, "game") != "minecraft")
            throw new InvalidDataException("仅支持 game=minecraft、formatVersion=1 的 Modrinth 整合包。");
        var name = RequiredString(json, "name");
        var dependencies = RequiredProperty(json, "dependencies", JsonValueKind.Object);
        var components = new List<InstanceComponentRequirement>();
        var componentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in dependencies.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(dependency.Name) || !componentIds.Add(dependency.Name) ||
                dependency.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(dependency.Value.GetString()))
                throw new InvalidDataException($"Modrinth 组件 {dependency.Name} 的版本无效。");
            components.Add(new InstanceComponentRequirement(dependency.Name, dependency.Value.GetString()!));
        }
        var minecraft = components.SingleOrDefault(component => component.Id == "minecraft")?.Version
            ?? throw new InvalidDataException("Modrinth 清单缺少 minecraft 版本。");
        var files = new List<InstanceFileRequirement>();
        var paths = new HashSet<string>(PathComparer);
        var skipped = 0;
        foreach (var file in RequiredProperty(json, "files", JsonValueKind.Array).EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            RequireObject(file, "Modrinth 文件项");
            var relative = InstanceImportPaths.Relative(RequiredString(file, "path"));
            if (!paths.Add(relative))
                throw new InvalidDataException($"Modrinth 清单存在重复文件路径：{relative}");
            var client = "required";
            if (file.TryGetProperty("env", out var env))
            {
                RequireObject(env, "Modrinth env");
                client = ReadEnvironment(env, "client");
                _ = ReadEnvironment(env, "server");
            }
            var hashesJson = RequiredProperty(file, "hashes", JsonValueKind.Object);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sha1"] = ReadHash(hashesJson, "sha1", 40),
                ["sha512"] = ReadHash(hashesJson, "sha512", 128)
            };
            var downloads = new List<string>();
            foreach (var download in RequiredProperty(file, "downloads", JsonValueKind.Array).EnumerateArray())
            {
                if (download.ValueKind != JsonValueKind.String ||
                    !Uri.TryCreate(download.GetString(), UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) ||
                    !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                    throw new InvalidDataException($"Modrinth 文件 {relative} 包含无效的 HTTPS 下载地址。");
                downloads.Add(uri.AbsoluteUri);
            }
            if (downloads.Count == 0)
                throw new InvalidDataException($"Modrinth 文件 {relative} 没有下载地址。");
            var size = RequiredInteger(file, "fileSize");
            if (size < 0)
                throw new InvalidDataException($"Modrinth 文件 {relative} 的大小不能为负数。");
            if (client == "unsupported")
            {
                skipped++;
                continue;
            }
            files.Add(new InstanceFileRequirement
            {
                Path = Combine(".minecraft", relative), Source = InstanceFileSource.DirectDownload,
                Required = client != "optional", Size = size, Hashes = hashes, Downloads = downloads
            });
        }

        var copies = new Dictionary<string, InstanceArchiveCopy>(PathComparer);
        ApplyOverrides(copies, entries, Combine(marker.Root, "overrides"));
        ApplyOverrides(copies, entries, Combine(marker.Root, "client-overrides"));
        copies.Add("modrinth.index.json", new InstanceArchiveCopy(marker.Entry.Entry, "modrinth.index.json", false));
        var notes = new List<string> { "已导入客户端覆盖文件；清单内的模组、Minecraft 和加载器尚需后续下载与检查。" };
        if (files.Any(file => !file.Required))
            notes.Add("客户端 optional 文件仅记录为可选需求，后续下载时应让用户选择是否安装。");
        if (skipped > 0)
            notes.Add($"已忽略 {skipped} 个标记为客户端 unsupported 的文件需求。");
        return new InstanceArchivePlan(NewManifest("Modrinth", name, archiveFileName, minecraft, components, files, notes), copies.Values.ToArray());
    }

    /// <summary>解析 CurseForge 组件和覆盖文件，保留项目/文件 ID 供后续下载服务解析。</summary>
    private static InstanceArchivePlan ReadCurseForge(IReadOnlyList<SafeArchiveEntry> entries, PackageMarker marker,
        string archiveFileName, CancellationToken token)
    {
        var json = SafeInstanceArchive.ReadJson(marker.Entry);
        RequireObject(json, "CurseForge 清单");
        if (RequiredString(json, "manifestType") != "minecraftModpack" || RequiredInteger(json, "manifestVersion") != 1)
            throw new InvalidDataException("仅支持 manifestType=minecraftModpack、manifestVersion=1 的 CurseForge 整合包。");
        var minecraftJson = RequiredProperty(json, "minecraft", JsonValueKind.Object);
        var minecraft = RequiredString(minecraftJson, "version");
        var components = new List<InstanceComponentRequirement> { new("minecraft", minecraft) };
        var notes = new List<string> { "已导入覆盖文件；CurseForge 文件保留 projectID/fileID，后续需通过平台解析下载地址，部分文件可能需要手动下载。" };
        if (minecraftJson.TryGetProperty("modLoaders", out var loaders))
        {
            if (loaders.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("CurseForge modLoaders 必须是数组。");
            foreach (var loader in loaders.EnumerateArray())
            {
                RequireObject(loader, "CurseForge 加载器");
                var id = RequiredString(loader, "id");
                var separator = id.IndexOf('-');
                var requirement = separator > 0 && separator < id.Length - 1
                    ? new InstanceComponentRequirement(id[..separator], id[(separator + 1)..])
                    : new InstanceComponentRequirement(id, "未知");
                var primary = OptionalBoolean(loader, "primary", false);
                components.Add(requirement with { Primary = primary });
                if (primary)
                    notes.Add($"CurseForge 主加载器：{id}；原始 primary 标记保留在 manifest.json。");
            }
        }
        var files = new List<InstanceFileRequirement>();
        var ids = new HashSet<(long ProjectId, long FileId)>();
        foreach (var file in RequiredProperty(json, "files", JsonValueKind.Array).EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            RequireObject(file, "CurseForge 文件项");
            var projectId = RequiredInteger(file, "projectID");
            var fileId = RequiredInteger(file, "fileID");
            if (projectId <= 0 || fileId <= 0 || !ids.Add((projectId, fileId)))
                throw new InvalidDataException("CurseForge 清单包含无效或重复的项目/文件 ID。");
            files.Add(new InstanceFileRequirement
            {
                Source = InstanceFileSource.CurseForge, Required = OptionalBoolean(file, "required", true),
                ProjectId = projectId, FileId = fileId
            });
        }
        if (files.Any(file => !file.Required))
            notes.Add("required=false 的 CurseForge 文件被保留为可选需求，导入阶段不自动选择下载。");
        var overrides = InstanceImportPaths.Relative(OptionalString(json, "overrides") ?? "overrides");
        var copies = new Dictionary<string, InstanceArchiveCopy>(PathComparer);
        ApplyOverrides(copies, entries, Combine(marker.Root, overrides));
        copies.Add("manifest.json", new InstanceArchiveCopy(marker.Entry.Entry, "manifest.json", false));
        return new InstanceArchivePlan(NewManifest("CurseForge", OptionalString(json, "name") ?? SuggestedName(marker.Root, archiveFileName),
            archiveFileName, minecraft, components, files, notes), copies.Values.ToArray());
    }

    /// <summary>解析 Prism 组件和名称，保留实例布局；实例外的共享依赖不加入提取计划。</summary>
    private static InstanceArchivePlan ReadPrism(IReadOnlyList<SafeArchiveEntry> entries, PackageMarker marker,
        string archiveFileName, CancellationToken token)
    {
        var json = SafeInstanceArchive.ReadJson(marker.Entry);
        RequireObject(json, "Prism 清单");
        if (json.TryGetProperty("formatVersion", out _) && RequiredInteger(json, "formatVersion") != 1)
            throw new InvalidDataException("不支持此 Prism 清单的 formatVersion。");
        var components = new List<InstanceComponentRequirement>();
        var notes = new List<string> { "Prism 实例内的文件已保留；实例外的共享库、资源和 Java 不在包内，仍需后续安装与检查。" };
        foreach (var component in RequiredProperty(json, "components", JsonValueKind.Array).EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            RequireObject(component, "Prism 组件");
            if (OptionalBoolean(component, "disabled", false))
                continue;
            var id = RequiredString(component, "uid");
            var version = OptionalString(component, "version") ?? OptionalString(component, "cachedVersion");
            if (version is null)
            {
                version = "未知";
                notes.Add($"Prism 组件 {id} 未指定版本，已保留组件 ID，后续需解析补丁或让用户确认。");
            }
            components.Add(new InstanceComponentRequirement(id, version));
        }
        var gameDirectory = SelectGameDirectory(entries, marker.Root, allowMissing: true);
        var minecraft = components.FirstOrDefault(component => component.Id == "net.minecraft")?.Version;
        if (minecraft is null || minecraft == "未知")
        {
            minecraft = null;
            notes.Add("Prism 清单没有可确定的 Minecraft 版本，导入后仍需补全版本信息。");
        }
        var cfg = entries.FirstOrDefault(entry => !entry.Directory && PathComparer.Equals(entry.Path, Combine(marker.Root, "instance.cfg")));
        var name = cfg is null ? null : ReadPrismName(cfg, token);
        var manifest = NewManifest("Prism", name ?? SuggestedName(marker.Root, archiveFileName),
            archiveFileName, minecraft, components, [], notes) with { GameDirectory = gameDirectory };
        return new InstanceArchivePlan(manifest, CopyTree(entries, marker.Root, ""));
    }

    /// <summary>从通用 ZIP 中定位唯一游戏目录，识别版本并生成保留布局的提取计划。</summary>
    private static InstanceArchivePlan ReadGeneric(IReadOnlyList<SafeArchiveEntry> entries, string archiveFileName, CancellationToken token)
    {
        var candidates = new HashSet<string>(PathComparer);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var segments = entry.Path.Split('/');
            for (var index = 0; index < segments.Length; index++)
            {
                var isDirectory = index < segments.Length - 1 || entry.Directory;
                if (isDirectory && (segments[index].Equals(".minecraft", StringComparison.OrdinalIgnoreCase) ||
                                    segments[index].Equals("minecraft", StringComparison.OrdinalIgnoreCase)))
                    candidates.Add(string.Join('/', segments.Take(index + 1)));
                if ((isDirectory && GameFolders.Contains(segments[index])) ||
                    (!isDirectory && GameFiles.Contains(segments[index])))
                    candidates.Add(string.Join('/', segments.Take(index)));
            }
        }
        // 从当前目录向上查 HashSet，避免 100000 个条目时两两比较候选目录。
        var roots = candidates.Where(candidate => !HasCandidateAncestor(candidate, candidates)).ToArray();
        if (roots.Length == 0)
            throw new InvalidDataException("ZIP 中未找到可识别的 Minecraft 游戏目录（例如 .minecraft/versions、mods 或 config）。");
        if (roots.Length != 1)
            throw new InvalidDataException("通用 ZIP 中包含多个游戏目录，请把每个实例分别打包后导入。");
        var gameRoot = roots[0];
        // 有些备份把共享 libraries 与 .minecraft 并排保存；此时保留外层布局，避免变成 .minecraft/.minecraft。
        if (new[] { ".minecraft", "minecraft" }.Any(name => entries.Any(entry => IsWithin(entry.Path, Combine(gameRoot, name)))))
            gameRoot = Combine(gameRoot, SelectGameDirectory(entries, gameRoot, allowMissing: false));
        var isNamedGameDirectory = LastPart(gameRoot).Equals(".minecraft", StringComparison.OrdinalIgnoreCase) ||
                                   LastPart(gameRoot).Equals("minecraft", StringComparison.OrdinalIgnoreCase);
        var instanceRoot = isNamedGameDirectory ? Parent(gameRoot) : gameRoot;
        var gameDirectory = isNamedGameDirectory ? LastPart(gameRoot) : ".minecraft";
        if (isNamedGameDirectory)
            _ = SelectGameDirectory(entries, instanceRoot, allowMissing: false);
        var copies = CopyTree(entries, instanceRoot, isNamedGameDirectory ? "" : ".minecraft");
        var notes = new List<string> { "通用 ZIP 只包含包内现有文件，不能仅根据压缩包推断全部应有依赖；后续需依据版本元数据检查缺失文件。" };
        var version = ReadGenericVersion(entries, gameRoot, notes, token);
        var components = version is null ? Array.Empty<InstanceComponentRequirement>() : [new InstanceComponentRequirement("minecraft", version)];
        var manifest = NewManifest("Zip", SuggestedName(instanceRoot, archiveFileName), archiveFileName,
            version, components, [], notes) with { GameDirectory = gameDirectory };
        return new InstanceArchivePlan(manifest, copies);
    }

    /// <summary>从版本 JSON 推断唯一 Minecraft 版本；无法确定时返回 null 并记录提示。</summary>
    private static string? ReadGenericVersion(IReadOnlyList<SafeArchiveEntry> entries, string gameRoot,
        List<string> notes, CancellationToken token)
    {
        var versions = new HashSet<string>(StringComparer.Ordinal);
        var prefix = Combine(gameRoot, "versions") + "/";
        foreach (var entry in entries.Where(entry => !entry.Directory && entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            var relative = entry.Path[prefix.Length..].Split('/');
            if (relative.Length != 2 || !relative[1].Equals(relative[0] + ".json", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var json = SafeInstanceArchive.ReadJson(entry);
                if (json.ValueKind != JsonValueKind.Object)
                    continue;
                var version = OptionalString(json, "inheritsFrom");
                if (version is null && json.TryGetProperty("patches", out var patches) && patches.ValueKind == JsonValueKind.Array)
                {
                    foreach (var patch in patches.EnumerateArray())
                        if (patch.ValueKind == JsonValueKind.Object && OptionalString(patch, "id") == "game")
                            version = OptionalString(patch, "version");
                }
                version ??= OptionalString(json, "id");
                if (version is not null && Regex.IsMatch(version, @"^(?:\d+\.\d+(?:\.\d+)?(?:-(?:pre|rc)\d+)?|\d{2}w\d{2}[a-z])$", RegexOptions.CultureInvariant))
                    versions.Add(version);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                notes.Add($"版本信息 {entry.Path} 无法解析，已保留文件，后续检查时需要修复。");
            }
        }
        if (versions.Count == 1)
            return versions.Single();
        notes.Add(versions.Count > 1 ? "包中包含多个 Minecraft 版本，后续启动时需要选择。" : "通用 ZIP 未提供可确定的 Minecraft 版本，需要在后续安装或检查时补全。");
        return null;
    }

    /// <summary>合并覆盖目录，后应用的同名文件优先，文件与目录类型冲突时拒绝导入。</summary>
    private static void ApplyOverrides(Dictionary<string, InstanceArchiveCopy> copies,
        IReadOnlyList<SafeArchiveEntry> entries, string sourceRoot)
    {
        foreach (var copy in CopyTree(entries, sourceRoot, ".minecraft"))
        {
            if (copies.TryGetValue(copy.TargetPath, out var previous) && previous.Directory != copy.Directory)
                throw new InvalidDataException($"覆盖目录中的文件与目录冲突：{copy.TargetPath}");
            copies[copy.TargetPath] = copy;
        }
    }

    /// <summary>将包内源目录映射到实例内目标目录，生成经过路径校验的提取条目。</summary>
    private static IReadOnlyList<InstanceArchiveCopy> CopyTree(IReadOnlyList<SafeArchiveEntry> entries,
        string sourceRoot, string destinationRoot)
    {
        var copies = new List<InstanceArchiveCopy>();
        foreach (var entry in entries)
        {
            if (!IsWithin(entry.Path, sourceRoot))
                continue;
            var relative = sourceRoot.Length == 0 ? entry.Path : entry.Path.Length == sourceRoot.Length ? "" : entry.Path[(sourceRoot.Length + 1)..];
            var target = Combine(destinationRoot, relative);
            if (target.Length == 0)
                continue;
            copies.Add(new InstanceArchiveCopy(entry.Entry, InstanceImportPaths.Relative(target), entry.Directory));
        }
        return copies;
    }

    /// <summary>选择 .minecraft 或 minecraft；允许缺失时默认用前者，两者同时存在则报错。</summary>
    private static string SelectGameDirectory(IReadOnlyList<SafeArchiveEntry> entries, string root, bool allowMissing)
    {
        var available = new[] { ".minecraft", "minecraft" }
            .Where(name => entries.Any(entry => IsWithin(entry.Path, Combine(root, name)))).ToArray();
        if (available.Length > 1)
            throw new InvalidDataException("实例同时包含 .minecraft 和 minecraft 游戏目录，无法确定应使用哪个目录。");
        if (available.Length == 1)
            return available[0];
        if (allowMissing)
            return ".minecraft";
        throw new InvalidDataException("实例中没有找到游戏目录。");
    }

    /// <summary>读取 Prism 的 General/name 作为建议名称；导入提交前会再同步用户选择的名称。</summary>
    private static string? ReadPrismName(SafeArchiveEntry entry, CancellationToken token)
    {
        if (entry.Entry.Length > 1024 * 1024)
            throw new InvalidDataException("Prism instance.cfg 超过允许的大小。");
        using var stream = entry.Entry.Open();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var inGeneral = true;
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            line = line.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
                inGeneral = line.Equals("[General]", StringComparison.OrdinalIgnoreCase);
            else if (inGeneral && !line.StartsWith('#') && !line.StartsWith(';'))
            {
                var equals = line.IndexOf('=');
                if (equals > 0 && line[..equals].Trim().Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    var name = line[(equals + 1)..].Trim().Trim('"');
                    if (name.Length > 0)
                        return name;
                }
            }
        }
        return null;
    }

    /// <summary>把平台信息整理成统一清单，文件完整性保持默认的未检查状态。</summary>
    private static InstanceImportManifest NewManifest(string format, string name, string archive, string? minecraft,
        IReadOnlyList<InstanceComponentRequirement> components, IReadOnlyList<InstanceFileRequirement> files, IReadOnlyList<string> notes) => new()
    {
        SourceFormat = format, Name = name, SourceArchive = Path.GetFileName(archive), MinecraftVersion = minecraft,
        Components = components, Files = files, Notes = notes
    };

    /// <summary>优先用包根目录名作为建议名，没有外层目录时使用压缩包文件名。</summary>
    private static string SuggestedName(string root, string archiveFileName) =>
        root.Length > 0 ? LastPart(root) : Path.GetFileNameWithoutExtension(archiveFileName);

    /// <summary>读取 Modrinth 客户端或服务端安装策略，缺省为必需，未知值报错。</summary>
    private static string ReadEnvironment(JsonElement env, string property)
    {
        var value = OptionalString(env, property) ?? "required";
        return value is "required" or "optional" or "unsupported" ? value
            : throw new InvalidDataException($"Modrinth env.{property} 的值无效。");
    }

    /// <summary>读取指定长度的十六进制哈希，验证后返回小写形式。</summary>
    private static string ReadHash(JsonElement hashes, string name, int length)
    {
        var value = RequiredString(hashes, name);
        if (value.Length != length || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"Modrinth {name} 哈希无效。");
        return value.ToLowerInvariant();
    }

    /// <summary>确认当前 JSON 值是对象，否则使用给定名称报告格式错误。</summary>
    private static void RequireObject(JsonElement json, string label)
    {
        if (json.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{label} 必须是 JSON 对象。");
    }

    /// <summary>读取指定类型的必填字段。</summary>
    private static JsonElement RequiredProperty(JsonElement json, string property, JsonValueKind kind)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(property, out var value) || value.ValueKind != kind)
            throw new InvalidDataException($"清单字段 {property} 缺失或类型不正确。");
        return value;
    }

    /// <summary>读取必填的非空字符串，字段缺失或为空时报告清单错误。</summary>
    private static string RequiredString(JsonElement json, string property) =>
        OptionalString(json, property) ?? throw new InvalidDataException($"清单字段 {property} 缺失或为空。");

    /// <summary>可选字符串允许缺失/null；错误类型直接拒绝，空白按未提供处理。</summary>
    private static string? OptionalString(JsonElement json, string property)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"清单字段 {property} 必须是字符串。");
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>读取 64 位整数；项目 ID、文件大小和格式版本不接受小数或字符串。</summary>
    private static long RequiredInteger(JsonElement json, string property)
    {
        var value = RequiredProperty(json, property, JsonValueKind.Number);
        return value.TryGetInt64(out var number) ? number
            : throw new InvalidDataException($"清单字段 {property} 必须是整数。");
    }

    /// <summary>读取布尔字段；缺失时返回调用方指定的默认值，类型错误时报错。</summary>
    private static bool OptionalBoolean(JsonElement json, string property, bool fallback)
    {
        if (!json.TryGetProperty(property, out var value))
            return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"清单字段 {property} 必须是布尔值。")
        };
    }

    /// <summary>按目录边界判断包内路径是否等于或位于给定根目录之下。</summary>
    private static bool IsWithin(string path, string root) => root.Length == 0 || PathComparer.Equals(path, root) ||
        path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>判断文件是否位于游戏或覆盖目录中，避免把游戏配置误认成整合包清单。</summary>
    private static bool IsPayloadPath(string path)
    {
        var segments = path.Split('/');
        return segments.Take(segments.Length - 1).Any(segment => PayloadFolders.Contains(segment));
    }

    /// <summary>根据目录及文件名识别游戏内容，用于排除压缩包中混入多个实例的情况。</summary>
    private static bool HasGameContent(SafeArchiveEntry entry)
    {
        var segments = entry.Path.Split('/');
        var directories = segments.Take(entry.Directory ? segments.Length : segments.Length - 1);
        return directories.Any(segment => GameFolders.Contains(segment) ||
                   segment.Equals(".minecraft", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("minecraft", StringComparison.OrdinalIgnoreCase)) ||
               (!entry.Directory && GameFiles.Contains(segments[^1]));
    }

    /// <summary>逐级检查父目录，判断当前候选是否已包含在另一个候选游戏目录中。</summary>
    private static bool HasCandidateAncestor(string path, HashSet<string> candidates)
    {
        while (path.Length > 0)
        {
            path = Parent(path);
            if (candidates.Contains(path))
                return true;
        }
        return false;
    }

    /// <summary>用斜杠连接两个包内路径片段，空片段不会额外产生分隔符。</summary>
    private static string Combine(string left, string right) => left.Length == 0 ? right : right.Length == 0 ? left : left + "/" + right;

    /// <summary>取得 ZIP 相对路径的父目录，根级文件返回空字符串。</summary>
    private static string Parent(string path) => path.LastIndexOf('/') is var index && index >= 0 ? path[..index] : "";

    /// <summary>取得 ZIP 路径最后一段，用于识别清单和默认名称。</summary>
    private static string LastPart(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>记录已发现的平台清单和它所在的逻辑实例根目录。</summary>
    /// <param name="Root">平台清单所在的包内目录，根级清单使用空字符串。</param>
    /// <param name="Format">已识别的平台格式名称。</param>
    /// <param name="Entry">用于读取清单的压缩条目。</param>
    private sealed record PackageMarker(string Root, string Format, SafeArchiveEntry Entry);
}
