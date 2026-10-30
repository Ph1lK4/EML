using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Prism;

/// <summary>已经生成的标准启动描述和辅助文件；所有路径均相对实例根目录。</summary>
public sealed record PrismPreparedProfile(string LaunchProfilePath, IReadOnlyList<InstanceFileRequirement> GeneratedFiles);

/// <summary>将已下载的 Prism 依赖整理为可移植的标准版本目录，不执行游戏或任何包内脚本。</summary>
public sealed class PrismRuntimeMaterializer
{
    private const int MaximumIndexBytes = 32 * 1024 * 1024;
    private const int MaximumAssetEntries = 200_000;
    private const long MaximumAssetSize = 1024L * 1024 * 1024;
    private const int MaximumNativeEntries = 20_000;
    private const long MaximumNativeEntryBytes = 256L * 1024 * 1024;
    private const long MaximumNativeBytes = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> KnownPlaceholders = new(StringComparer.Ordinal)
    {
        "auth_player_name", "version_name", "game_directory", "assets_root", "assets_index_name",
        "auth_uuid", "auth_access_token", "user_properties", "user_type", "version_type", "auth_session",
        "game_assets", "resolution_width", "resolution_height", "natives_directory", "launcher_name",
        "launcher_version", "classpath", "classpath_separator", "library_directory", "primary_jar",
        "auth_xuid", "clientid", "quickPlayPath", "quickPlaySingleplayer", "quickPlayMultiplayer", "quickPlayRealms", "logging_path"
    };

    /// <summary>从已校验的资源索引展开固定哈希对象，重复内容只安排一次下载。</summary>
    public IReadOnlyList<PrismRuntimeFile> ExpandAssets(PrismRuntimePlan plan)
    {
        ValidatePlanRoot(plan);
        var index = ReadAssets(plan);
        if (index is null) return [];
        var files = new Dictionary<string, PrismRuntimeFile>(StringComparer.Ordinal);
        foreach (var entry in index.Entries)
        {
            var relative = GamePath(plan, $"assets/objects/{entry.Hash[..2]}/{entry.Hash}");
            var requirement = new InstanceFileRequirement
            {
                Path = relative, Source = InstanceFileSource.DirectDownload, Size = entry.Size,
                Hashes = new Dictionary<string, string> { ["sha1"] = entry.Hash },
                Downloads = [$"https://resources.download.minecraft.net/{entry.Hash[..2]}/{entry.Hash}"]
            };
            if (files.TryGetValue(entry.Hash, out var existing) && existing.Requirement.Size != entry.Size)
                throw new InvalidDataException("同一资源哈希声明了不同的文件大小。");
            files.TryAdd(entry.Hash, new PrismRuntimeFile(requirement, PrismRuntimeFileRole.AssetObject));
        }
        return files.Values.ToArray();
    }

    /// <summary>验证所有输入文件，准备类库、资源映射和本机库，最后才发布启动描述。</summary>
    public async Task<PrismPreparedProfile> PrepareAsync(PrismRuntimePlan plan, CancellationToken cancellationToken = default)
    {
        ValidatePlanRoot(plan);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.BlockingIssues.Count > 0 || plan.MetadataRequests.Count > 0)
            throw new InvalidOperationException("Prism 组件仍有未解决的问题或未下载的元数据。");
        if (plan.Fingerprint.Length < 12 || !plan.Fingerprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("Prism 运行计划缺少有效指纹。");
        if (string.IsNullOrWhiteSpace(plan.MainClass) || plan.MainClass.Any(char.IsWhiteSpace))
            throw new InvalidDataException("Prism 运行计划缺少有效主类。");
        if (plan.Files.Count > MaximumAssetEntries + 10_000)
            throw new InvalidDataException("运行依赖数量超过允许范围。");
        var logging = ValidateLogging(plan);
        var mainJars = plan.Files.Where(f => f.Role == PrismRuntimeFileRole.MainJar).ToArray();
        if (mainJars.Length != 1) throw new InvalidDataException("运行计划必须且只能包含一个 Minecraft 主 JAR。");
        ValidateTraits(plan);
        var gameArguments = BuildGameArguments(plan);
        var jvmArguments = BuildJvmArguments(plan);
        if (plan.VersionIdPrefix is not ("EML-Prism" or "EML-Modrinth" or "EML-CurseForge"))
            throw new InvalidDataException("运行计划的版本目录前缀无效。");
        var id = plan.VersionIdPrefix + "-" + plan.Fingerprint[..12].ToLowerInvariant();
        var versionRoot = GamePath(plan, "versions/" + id);
        var nativesRoot = GamePath(plan, "natives/" + plan.Fingerprint[..12].ToLowerInvariant());
        var generated = new Dictionary<string, InstanceFileRequirement>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new Dictionary<string, PrismRuntimeFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = RequirePath(file.Requirement);
            if (!dependencies.TryAdd(relative, file)) throw new InvalidDataException("运行计划包含重复目标路径：" + relative);
            await VerifyFileAsync(Resolve(plan, relative), file.Requirement, cancellationToken).ConfigureAwait(false);
            if (file.Requirement.Hashes.Count == 0 && file.Role is PrismRuntimeFileRole.Library or PrismRuntimeFileRole.MainJar or PrismRuntimeFileRole.NativeArchive)
                await VerifyUnhashedArchiveAsync(Resolve(plan, relative), cancellationToken).ConfigureAwait(false);
        }

        var assets = ReadAssets(plan);
        if (assets is not null)
        {
            foreach (var file in ExpandAssets(plan))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!dependencies.TryGetValue(file.Requirement.Path!, out var resolved) || resolved.Role != PrismRuntimeFileRole.AssetObject)
                    throw new InvalidDataException("资源索引尚未完整展开和下载：" + file.Requirement.Path);
                // 核对索引本身声明的哈希，不能只相信调用方重写后的计划项。
                await VerifyFileAsync(Resolve(plan, file.Requirement.Path!), file.Requirement, cancellationToken).ConfigureAwait(false);
            }
        }

        var libraryJson = new List<object>();
        var classPath = new List<string>();
        var classPathSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in plan.Files.Where(f => f.OnClassPath && f.Role != PrismRuntimeFileRole.MainJar))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Role != PrismRuntimeFileRole.Library)
                throw new InvalidDataException("只有普通类库可以加入 Java 类路径。");
            var mavenPath = MavenPath(file.MavenName);
            var target = GamePath(plan, "libraries/" + mavenPath);
            await CopyVerifiedAsync(plan, RequirePath(file.Requirement), target, generated, cancellationToken).ConfigureAwait(false);
            if (!classPathSeen.Add(target)) throw new InvalidDataException("多个类库对应相同 Maven 目标：" + target);
            classPath.Add(target);
            var artifact = new Dictionary<string, object?> { ["path"] = mavenPath, ["size"] = new FileInfo(Resolve(plan, target)).Length };
            if (file.Requirement.Hashes.Count > 0)
            {
                var hashes = await HashFileAsync(Resolve(plan, target), ["sha1"], cancellationToken).ConfigureAwait(false);
                artifact["sha1"] = hashes["sha1"];
            }
            if (file.Requirement.Downloads.FirstOrDefault() is { } libraryUrl) artifact["url"] = libraryUrl;
            libraryJson.Add(new { name = file.MavenName, downloads = new { artifact }, emlSourceHashVerified = file.Requirement.Hashes.Count > 0 });
        }
        var mainJar = mainJars[0];
        var jarPath = versionRoot + "/" + id + ".jar";
        await CopyVerifiedAsync(plan, RequirePath(mainJar.Requirement), jarPath, generated, cancellationToken).ConfigureAwait(false);
        classPath.Add(jarPath);

        await ExtractNativesAsync(plan, nativesRoot, generated, cancellationToken).ConfigureAwait(false);
        if (assets is not null)
            await MapAssetsAsync(plan, assets, generated, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var standardPath = versionRoot + "/" + id + ".json";
        var portablePath = versionRoot + "/" + id + ".eml-launch.json";
        var standard = new Dictionary<string, object?>
        {
            ["id"] = id, ["type"] = "release", ["mainClass"] = plan.MainClass,
            ["arguments"] = new { game = gameArguments, jvm = jvmArguments },
            ["libraries"] = libraryJson,
            ["emlMinecraftVersion"] = plan.MinecraftVersion,
            ["emlRuntimeOs"] = plan.RuntimeOs,
            ["emlRuntimeArchitecture"] = plan.RuntimeArchitecture,
            ["emlNativesDirectory"] = nativesRoot,
            ["emlRuntimeFingerprint"] = plan.Fingerprint,
            ["emlLaunchProfile"] = portablePath
        };
        if (plan.AssetIndexId is not null)
        {
            standard["assets"] = plan.AssetIndexId;
            var assetFile = plan.Files.Single(f => f.Role == PrismRuntimeFileRole.AssetIndex);
            var info = new Dictionary<string, object?> { ["id"] = plan.AssetIndexId, ["size"] = assetFile.Requirement.Size };
            if (assetFile.Requirement.Hashes.TryGetValue("sha1", out var sha1)) info["sha1"] = sha1;
            if (assetFile.Requirement.Downloads.FirstOrDefault() is { } url) info["url"] = url;
            standard["assetIndex"] = info;
        }
        if (plan.CompatibleJavaMajors.Count > 0)
        {
            standard["javaVersion"] = new { majorVersion = plan.CompatibleJavaMajors.Min() };
            standard["emlCompatibleJavaMajors"] = plan.CompatibleJavaMajors;
        }
        if (logging is not null)
        {
            var logInfo = new Dictionary<string, object?>
            {
                ["id"] = Path.GetFileName(logging.Requirement.Path), ["size"] = logging.Requirement.Size,
                ["url"] = logging.Requirement.Downloads.FirstOrDefault()
            };
            if (logging.Requirement.Hashes.TryGetValue("sha1", out var logHash)) logInfo["sha1"] = logHash;
            standard["logging"] = new { client = new { argument = plan.LoggingArgument, file = logInfo, type = "log4j2-xml" } };
            standard["emlLoggingPath"] = logging.Requirement.Path;
            // 标准启动器按 logging.client 自行添加参数；可移植配置则显式保存参数及路径。
            standard["arguments"] = new { game = gameArguments, jvm = jvmArguments.Where(a => a != plan.LoggingArgument!.Replace("${path}", "${logging_path}")).ToArray() };
        }
        var portable = new
        {
            schemaVersion = 1, id, minecraftVersion = plan.MinecraftVersion, fingerprint = plan.Fingerprint,
            runtimeOs = plan.RuntimeOs, runtimeArchitecture = plan.RuntimeArchitecture,
            gameDirectory = plan.GameDirectory, mainClass = plan.MainClass, classPath,
            jvmArguments, gameArguments, assetsRoot = GamePath(plan, "assets"), assetIndexId = plan.AssetIndexId,
            gameAssets = assets?.Virtual == true ? GamePath(plan, "assets/virtual/" + plan.AssetIndexId) : GamePath(plan, "assets"),
            nativesDirectory = nativesRoot, compatibleJavaMajors = plan.CompatibleJavaMajors,
            traits = plan.Traits,
            sourceFiles = plan.Files.Select(f => new { path = f.Requirement.Path, role = f.Role.ToString(), sourceHashVerified = f.Requirement.Hashes.Count > 0 }),
            notes = plan.Notes.Concat(plan.Files.Any(f => f.Requirement.Hashes.Count == 0)
                ? new[] { "部分上游文件没有校验值；已验证可读取和 JAR 结构，生成文件的本地哈希不代表上游真实性验证。" } : [])
        };
        var portableBytes = JsonSerializer.SerializeToUtf8Bytes(portable, JsonOptions);
        if (logging is not null)
        {
            var portableNode = System.Text.Json.Nodes.JsonNode.Parse(portableBytes)!;
            portableNode["loggingPath"] = logging.Requirement.Path;
            portableBytes = JsonSerializer.SerializeToUtf8Bytes(portableNode, JsonOptions);
        }
        await WriteBytesAsync(plan, portablePath, portableBytes, generated, cancellationToken)
            .ConfigureAwait(false);
        // 标准版本 JSON 是最后的完成标记；任何前置准备失败都不会创建一个看似可启动的新版本。
        await WriteBytesAsync(plan, standardPath, JsonSerializer.SerializeToUtf8Bytes(standard, JsonOptions), generated, cancellationToken)
            .ConfigureAwait(false);
        return new PrismPreparedProfile(standardPath, generated.Values.ToArray());
    }

    /// <summary>校验实例和游戏目录的边界，不允许通过目录链接写出实例。</summary>
    private static void ValidatePlanRoot(PrismRuntimePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Path.IsPathFullyQualified(plan.InstancePath)) throw new InvalidDataException("实例路径必须为绝对路径。");
        InstanceImportPaths.EnsureNoLinks(plan.InstancePath);
        if ((File.GetAttributes(plan.InstancePath) & FileAttributes.Directory) == 0)
            throw new InvalidDataException("实例根路径不是目录。");
        if (plan.GameDirectory != ".") _ = InstanceImportPaths.Relative(plan.GameDirectory);
        InstanceImportPaths.EnsureNoLinks(plan.GameDirectory == "." ? plan.InstancePath : Resolve(plan, plan.GameDirectory));
    }

    /// <summary>规范化游戏目录内的相对路径，同时兼容游戏目录就是实例根目录的布局。</summary>
    private static string GamePath(PrismRuntimePlan plan, string path) =>
        InstanceImportPaths.Relative(plan.GameDirectory == "." ? path : plan.GameDirectory + "/" + path);

    /// <summary>将实例相对路径转换为绝对路径并检查所有父目录是否含链接。</summary>
    private static string Resolve(PrismRuntimePlan plan, string relative)
    {
        var path = InstanceImportPaths.Within(plan.InstancePath, relative);
        InstanceImportPaths.EnsureNoLinks(path);
        return path;
    }

    /// <summary>文件需求必须已经具有确定的实例相对目标路径。</summary>
    private static string RequirePath(InstanceFileRequirement requirement) =>
        InstanceImportPaths.Relative(requirement.Path ?? throw new InvalidDataException("运行依赖尚未解析目标路径。"));

    /// <summary>有界读取并校验资源索引，只允许规范的 SHA-1 对象和安全的逻辑名称。</summary>
    private static AssetCatalog? ReadAssets(PrismRuntimePlan plan)
    {
        var indexes = plan.Files.Where(f => f.Role == PrismRuntimeFileRole.AssetIndex).ToArray();
        if (indexes.Length == 0)
        {
            if (!string.IsNullOrEmpty(plan.AssetIndexId)) throw new InvalidDataException("声明了资源索引 ID，但没有资源索引文件。");
            return null;
        }
        if (indexes.Length != 1 || string.IsNullOrWhiteSpace(plan.AssetIndexId) || plan.AssetIndexId.Contains('/') || plan.AssetIndexId.Contains('\\'))
            throw new InvalidDataException("运行计划必须提供唯一且合法的资源索引 ID。");
        _ = InstanceImportPaths.Relative(plan.AssetIndexId);
        var requirement = indexes[0].Requirement;
        if (!string.Equals(RequirePath(requirement), GamePath(plan, "assets/indexes/" + plan.AssetIndexId + ".json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("资源索引文件位置与索引 ID 不一致。");
        var path = Resolve(plan, RequirePath(requirement));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumIndexBytes || requirement.Hashes.Count == 0)
            throw new InvalidDataException("资源索引过大或没有可验证的哈希。");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        VerifyBytes(bytes, requirement);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (!json.RootElement.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("资源索引缺少 objects。");
        var entries = new List<AssetEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in objects.EnumerateObject())
        {
            if (entries.Count >= MaximumAssetEntries) throw new InvalidDataException("资源索引条目数量过多。");
            var name = InstanceImportPaths.Relative(item.Name);
            if (!names.Add(name)) throw new InvalidDataException("资源索引存在重复逻辑路径。");
            if (item.Value.ValueKind != JsonValueKind.Object || !item.Value.TryGetProperty("hash", out var hashNode) ||
                hashNode.ValueKind != JsonValueKind.String || !item.Value.TryGetProperty("size", out var sizeNode) ||
                sizeNode.ValueKind != JsonValueKind.Number || !sizeNode.TryGetInt64(out var size) || size is < 0 or > MaximumAssetSize)
                throw new InvalidDataException("资源对象缺少有效的哈希或大小。");
            var hash = hashNode.GetString()!;
            if (hash.Length != 40 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("资源对象 SHA-1 无效。");
            entries.Add(new AssetEntry(name, hash.ToLowerInvariant(), size));
        }
        InstanceImportPaths.ValidateLayout(entries.Select(e => (e.Name, false)));
        return new AssetCatalog(entries, ReadFlag(json.RootElement, "virtual"), ReadFlag(json.RootElement, "map_to_resources"));
    }

    /// <summary>可选布尔字段只接受布尔值，错误格式不能悄悄改变资源布局。</summary>
    private static bool ReadFlag(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var flag)) return false;
        if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("资源索引布尔字段格式无效：" + name);
        return flag.GetBoolean();
    }

    /// <summary>用有限且已实现的规则处理 Prism 特性，避免生成不完整的旧版启动配置。</summary>
    private static void ValidateTraits(PrismRuntimePlan plan)
    {
        foreach (var trait in plan.Traits)
            if (trait != "FirstThreadOnMacOS") throw new InvalidDataException("暂不支持 Prism 启动特性：" + trait);
        if (plan.CompatibleJavaMajors.Any(v => v is < 1 or > 100)) throw new InvalidDataException("Java 主版本声明无效。");
    }

    /// <summary>把旧式参数拆为独立参数，再追加固定 Tweaker，保留占位符供启动模块替换。</summary>
    private static IReadOnlyList<string> BuildGameArguments(PrismRuntimePlan plan)
    {
        var result = plan.GameArguments.Count > 0 ? plan.GameArguments.ToList() : SplitArguments(plan.MinecraftArguments ?? "");
        foreach (var tweaker in plan.Tweakers)
        {
            if (string.IsNullOrWhiteSpace(tweaker) || tweaker.Any(char.IsWhiteSpace)) throw new InvalidDataException("Tweaker 类名无效。");
            result.Add("--tweakClass");
            result.Add(tweaker);
        }
        ValidateArguments(result);
        return result;
    }

    /// <summary>统一提供类路径和本机库位置，保留声明的 JVM 参数但拒绝重复替换入口。</summary>
    private static IReadOnlyList<string> BuildJvmArguments(PrismRuntimePlan plan)
    {
        var nativeArguments = plan.JvmArguments.Where(a => a.StartsWith("-Djava.library.path=", StringComparison.Ordinal)).ToArray();
        if (nativeArguments.Length > 1 || nativeArguments.Any(a => !IsNativeDirectoryArgument(a)))
            throw new InvalidDataException("本机库参数必须唯一并位于生成的 natives 目录内。");
        var result = new List<string> { nativeArguments.FirstOrDefault() ?? "-Djava.library.path=${natives_directory}", "-cp", "${classpath}" };
        foreach (var argument in plan.JvmArguments)
        {
            if (nativeArguments.Contains(argument)) continue;
            if (argument is "-cp" or "-classpath" or "--class-path" or "-jar" or "-m" or "--module" or "-p" or "--module-path" or
                "-Djava.class.path" or "-Djava.library.path" || argument.StartsWith("--class-path=", StringComparison.Ordinal) ||
                argument.StartsWith("--module=", StringComparison.Ordinal) || argument.StartsWith("--module-path=", StringComparison.Ordinal) ||
                argument.StartsWith("-Djava.class.path=", StringComparison.Ordinal) ||
                argument.StartsWith("-Djava.library.path=", StringComparison.Ordinal))
                throw new InvalidDataException("组件 JVM 参数与生成的类路径或入口冲突。");
            result.Add(argument);
        }
        if (plan.LoggingArgument is not null) result.Add(plan.LoggingArgument.Replace("${path}", "${logging_path}"));
        if (plan.RuntimeOs == "osx" && plan.Traits.Contains("FirstThreadOnMacOS")) result.Add("-XstartOnFirstThread");
        ValidateArguments(result);
        return result;
    }

    /// <summary>允许官方在 natives 下选择子目录，同时拒绝绝对路径和越界路径。</summary>
    private static bool IsNativeDirectoryArgument(string argument)
    {
        const string prefix = "-Djava.library.path=${natives_directory}";
        if (argument == prefix) return true;
        if (!argument.StartsWith(prefix + "/", StringComparison.Ordinal)) return false;
        var suffix = argument[(prefix.Length + 1)..];
        try { return InstanceImportPaths.Relative(suffix) == suffix && !suffix.Contains('$'); }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>日志文件与参数必须成对且具有来源校验值，缺一时不能生成可启动配置。</summary>
    private static PrismRuntimeFile? ValidateLogging(PrismRuntimePlan plan)
    {
        var files = plan.Files.Where(f => f.Role == PrismRuntimeFileRole.Logging).ToArray();
        if (files.Length == 0 && plan.LoggingArgument is null) return null;
        if (files.Length != 1 || plan.LoggingArgument != "-Dlog4j.configurationFile=${path}" || files[0].Requirement.Hashes.Count == 0)
            throw new InvalidDataException("日志配置需要唯一已校验文件以及完整 Log4j 启动参数。");
        return files[0];
    }

    /// <summary>解析空白分隔和单双引号，不运行 shell，也不展开环境变量。</summary>
    private static List<string> SplitArguments(string text)
    {
        if (text.Length > 1024 * 1024) throw new InvalidDataException("旧式启动参数过长。");
        var result = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var started = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0' || c is '\r' or '\n') throw new InvalidDataException("启动参数含控制字符。");
            if (c == '\\' && i + 1 < text.Length && quote != '\0' && (text[i + 1] == quote || text[i + 1] == '\\'))
            {
                current.Append(text[++i]); started = true; continue;
            }
            if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else current.Append(c);
                continue;
            }
            if (c is '\'' or '"') { quote = c; started = true; continue; }
            if (char.IsWhiteSpace(c))
            {
                if (started) { result.Add(current.ToString()); current.Clear(); started = false; }
            }
            else { current.Append(c); started = true; }
        }
        if (quote != '\0') throw new InvalidDataException("旧式启动参数包含未闭合引号。");
        if (started) result.Add(current.ToString());
        return result;
    }

    /// <summary>限制参数规模并拒绝无法由标准启动模块解释的自定义占位符。</summary>
    private static void ValidateArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 8192) throw new InvalidDataException("启动参数数量过多。");
        foreach (var argument in arguments)
        {
            if (argument is null || argument.Length > 32768 || argument.Any(c => c < 32))
                throw new InvalidDataException("启动参数长度或字符无效。");
            foreach (Match match in Regex.Matches(argument, @"\$\{([^}]+)\}", RegexOptions.CultureInvariant))
                if (!KnownPlaceholders.Contains(match.Groups[1].Value)) throw new InvalidDataException("暂不支持启动参数占位符：" + match.Value);
        }
    }

    /// <summary>将明确的 Maven 坐标转换为标准类库相对路径，不猜测下载站点。</summary>
    private static string MavenPath(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("类库缺少 Maven 坐标。");
        var extensionParts = name.Split('@');
        if (extensionParts.Length > 2) throw new InvalidDataException("Maven 坐标扩展名格式无效。");
        var extension = extensionParts.Length == 2 ? extensionParts[1] : "jar";
        if (extension is not ("jar" or "zip")) throw new InvalidDataException("暂不支持类库扩展名：" + extension);
        var parts = extensionParts[0].Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(string.IsNullOrWhiteSpace) || parts.Any(p => p.Contains('/') || p.Contains('\\')))
            throw new InvalidDataException("Maven 坐标格式无效。");
        var filename = parts[1] + "-" + parts[2] + (parts.Length == 4 ? "-" + parts[3] : "") + "." + extension;
        return InstanceImportPaths.Relative(parts[0].Replace('.', '/') + "/" + parts[1] + "/" + parts[2] + "/" + filename);
    }

    /// <summary>将哈希对象复制到旧版虚拟资源及 resources 目录，保留同名的不同内容。</summary>
    private static async Task MapAssetsAsync(PrismRuntimePlan plan, AssetCatalog assets,
        Dictionary<string, InstanceFileRequirement> generated, CancellationToken token)
    {
        if (!assets.Virtual && !assets.MapToResources) return;
        foreach (var entry in assets.Entries)
        {
            token.ThrowIfCancellationRequested();
            var source = GamePath(plan, $"assets/objects/{entry.Hash[..2]}/{entry.Hash}");
            if (assets.Virtual)
                await CopyVerifiedAsync(plan, source, GamePath(plan, "assets/virtual/" + plan.AssetIndexId + "/" + entry.Name), generated, token)
                    .ConfigureAwait(false);
            if (assets.MapToResources)
                await CopyVerifiedAsync(plan, source, GamePath(plan, "resources/" + entry.Name), generated, token).ConfigureAwait(false);
        }
    }

    /// <summary>安全解压旧式本机库；普通类路径上的 LWJGL3 native JAR 不在此处解压。</summary>
    private static async Task ExtractNativesAsync(PrismRuntimePlan plan, string nativesRoot,
        Dictionary<string, InstanceFileRequirement> generated, CancellationToken token)
    {
        long total = 0;
        var count = 0;
        foreach (var file in plan.Files.Where(f => f.Role == PrismRuntimeFileRole.NativeArchive))
        {
            if (file.OnClassPath) throw new InvalidDataException("旧式本机库压缩包不能同时作为普通类路径文件。");
            var excludes = file.ExtractExcludes.Select(x => InstanceImportPaths.Relative(x.TrimEnd('/', '\\'))).ToArray();
            using var archive = ZipFile.OpenRead(Resolve(plan, RequirePath(file.Requirement)));
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<(ZipArchiveEntry Entry, string Relative)>();
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (++count > MaximumNativeEntries) throw new InvalidDataException("本机库压缩条目过多。");
                var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                var name = InstanceImportPaths.Relative(directory ? entry.FullName.TrimEnd('/', '\\') : entry.FullName);
                var mode = (entry.ExternalAttributes >> 16) & 0xF000;
                if (mode is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("本机库压缩包含链接或特殊文件条目。");
                if (!names.Add(name)) throw new InvalidDataException("本机库压缩包含重复目标路径。");
                if (directory || IsExcluded(name, "META-INF") || excludes.Any(x => IsExcluded(name, x))) continue;
                if (entry.Length > MaximumNativeEntryBytes || (total += entry.Length) > MaximumNativeBytes)
                    throw new InvalidDataException("本机库展开大小超过限制。");
                entries.Add((entry, name));
            }
            InstanceImportPaths.ValidateLayout(entries.Select(e => (e.Relative, false)));
            foreach (var (entry, relative) in entries)
            {
                token.ThrowIfCancellationRequested();
                await using var input = entry.Open();
                await WriteStreamAsync(plan, nativesRoot + "/" + relative, input, entry.Length, generated, token).ConfigureAwait(false);
            }
        }
        var directoryPath = Resolve(plan, nativesRoot);
        Directory.CreateDirectory(directoryPath);
        InstanceImportPaths.EnsureNoLinks(directoryPath);
    }

    /// <summary>按完整目录边界判断排除项，避免把类似前缀的合法文件误排除。</summary>
    private static bool IsExcluded(string path, string prefix) => string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>复制普通文件到新布局；已有相同内容可复用，不同内容不会覆盖。</summary>
    private static async Task CopyVerifiedAsync(PrismRuntimePlan plan, string source, string destination,
        Dictionary<string, InstanceFileRequirement> generated, CancellationToken token)
    {
        var sourcePath = Resolve(plan, source);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return;
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
        await WriteStreamAsync(plan, destination, input, input.Length, generated, token).ConfigureAwait(false);
    }

    /// <summary>将小型生成 JSON 交给统一的原子文件发布流程。</summary>
    private static async Task WriteBytesAsync(PrismRuntimePlan plan, string destination, byte[] bytes,
        Dictionary<string, InstanceFileRequirement> generated, CancellationToken token)
    {
        using var input = new MemoryStream(bytes, writable: false);
        await WriteStreamAsync(plan, destination, input, bytes.Length, generated, token).ConfigureAwait(false);
    }

    /// <summary>流式写入随机临时文件并计算 SHA-256，仅在无冲突或内容完全相同时发布目标。</summary>
    private static async Task WriteStreamAsync(PrismRuntimePlan plan, string destination, Stream input, long expectedSize,
        Dictionary<string, InstanceFileRequirement> generated, CancellationToken token)
    {
        var relative = InstanceImportPaths.Relative(destination);
        var target = Resolve(plan, relative);
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        InstanceImportPaths.EnsureNoLinks(parent);
        var temporary = Path.Combine(parent, ".eml-prepare-" + Guid.NewGuid().ToString("N") + ".part");
        var owned = false;
        Exception? originalFailure = null;
        try
        {
            string hash;
            using var calculator = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                owned = true;
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                long written = 0;
                try
                {
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                        if (read == 0) break;
                        written = checked(written + read);
                        if (written > expectedSize) throw new InvalidDataException("生成内容长度超过声明大小。");
                        calculator.AppendData(buffer.AsSpan(0, read));
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                    if (written != expectedSize) throw new InvalidDataException("生成内容长度与声明大小不一致。");
                    await output.FlushAsync(token).ConfigureAwait(false);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                hash = Convert.ToHexStringLower(calculator.GetHashAndReset());
            }
            var requirement = new InstanceFileRequirement
            {
                Path = relative, Source = InstanceFileSource.Embedded, Size = expectedSize,
                Hashes = new Dictionary<string, string> { ["sha256"] = hash }
            };
            token.ThrowIfCancellationRequested();
            InstanceImportPaths.EnsureNoLinks(target);
            try
            {
                File.Move(temporary, target, overwrite: false);
                owned = false;
            }
            catch (IOException)
            {
                // 同名文件可能由用户或另一次操作创建；只有内容完全相同才允许复用。
                await VerifyFileAsync(target, requirement, token).ConfigureAwait(false);
            }
            generated[relative] = requirement;
        }
        catch (Exception ex)
        {
            originalFailure = ex;
            throw;
        }
        finally
        {
            if (owned)
            {
                try { InstanceImportPaths.EnsureNoLinks(temporary); File.Delete(temporary); }
                catch (Exception ex) when (originalFailure is not null && ex is IOException or UnauthorizedAccessException)
                {
                    // 外部占用导致的清理失败不能掩盖用户取消或原始生成错误。
                    originalFailure.Data["TemporaryFileCleanupError"] = ex.Message;
                    originalFailure.Data["TemporaryFilePath"] = temporary;
                }
            }
        }
    }

    /// <summary>验证依赖大小及全部声明哈希，读取失败或内容变化均阻止生成启动配置。</summary>
    private static async Task VerifyFileAsync(string path, InstanceFileRequirement requirement, CancellationToken token)
    {
        InstanceImportPaths.EnsureNoLinks(path);
        var info = new FileInfo(path);
        var size = info.Length;
        var modified = info.LastWriteTimeUtc;
        if (requirement.Size.HasValue && size != requirement.Size.Value) throw new InvalidDataException("运行文件大小不符：" + path);
        var actual = await HashFileAsync(path, requirement.Hashes.Keys, token).ConfigureAwait(false);
        foreach (var (name, value) in requirement.Hashes)
            if (!string.Equals(value, actual[name], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("运行文件哈希不符：" + path);
        info.Refresh();
        if (size != info.Length || modified != info.LastWriteTimeUtc) throw new IOException("准备期间运行文件发生变化：" + path);
    }

    /// <summary>少数上游未声明哈希的旧版 JAR 只能确认结构可读，不能据此声称来源内容已验证。</summary>
    private static async Task VerifyUnhashedArchiveAsync(string path, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count is 0 or > MaximumNativeEntries) throw new InvalidDataException("无上游校验值的 JAR 结构无效。");
        long total = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Length > MaximumNativeEntryBytes || (total += entry.Length) > MaximumNativeBytes)
                    throw new InvalidDataException("无上游校验值的 JAR 展开大小超过验证限制。");
                await using var stream = entry.Open();
                long readTotal = 0;
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    if (read == 0) break;
                    readTotal += read;
                    if (readTotal > entry.Length) throw new InvalidDataException("JAR 条目长度与目录声明不符。");
                }
                if (readTotal != entry.Length) throw new InvalidDataException("JAR 条目内容不完整。");
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>校验已经有界读取的资源索引字节，避免对未经确认的索引安排下载。</summary>
    private static void VerifyBytes(byte[] bytes, InstanceFileRequirement requirement)
    {
        if (requirement.Size.HasValue && requirement.Size.Value != bytes.Length) throw new InvalidDataException("资源索引大小不符。");
        foreach (var (name, expected) in requirement.Hashes)
        {
            using var hash = IncrementalHash.CreateHash(Algorithm(name));
            hash.AppendData(bytes);
            if (!string.Equals(expected, Convert.ToHexString(hash.GetHashAndReset()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("资源索引哈希不符。");
        }
    }

    /// <summary>一次流式读取同时计算需要的哈希，内存用量不随文件大小增长。</summary>
    private static async Task<Dictionary<string, string>> HashFileAsync(string path, IEnumerable<string> names, CancellationToken token)
    {
        var hashes = names.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(x => x, x => IncrementalHash.CreateHash(Algorithm(x)), StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    if (read == 0) break;
                    foreach (var hash in hashes.Values) hash.AppendData(buffer.AsSpan(0, read));
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            return hashes.ToDictionary(x => x.Key, x => Convert.ToHexStringLower(x.Value.GetHashAndReset()), StringComparer.OrdinalIgnoreCase);
        }
        finally { foreach (var hash in hashes.Values) hash.Dispose(); }
    }

    /// <summary>限定允许的内容校验算法，未知算法必须显式报错。</summary>
    private static HashAlgorithmName Algorithm(string name) => name.ToLowerInvariant() switch
    {
        "sha1" => HashAlgorithmName.SHA1, "sha256" => HashAlgorithmName.SHA256, "sha512" => HashAlgorithmName.SHA512,
        _ => throw new InvalidDataException("不支持的运行文件哈希算法：" + name)
    };

    /// <summary>保存资源索引中的逻辑名称和经过验证的内容地址。</summary>
    private sealed record AssetEntry(string Name, string Hash, long Size);

    /// <summary>保存资源映射开关，避免把对象下载完成误当成旧版目录已准备完成。</summary>
    private sealed record AssetCatalog(IReadOnlyList<AssetEntry> Entries, bool Virtual, bool MapToResources);
}
