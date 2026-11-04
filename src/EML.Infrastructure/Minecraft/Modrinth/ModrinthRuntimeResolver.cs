using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Prism;

namespace EML.Infrastructure.Minecraft.Modrinth;

/// <summary>将固定组件版本展开为官方运行依赖；公共入口保持 Modrinth 行为，内部允许其他来源复用解析过程。</summary>
public sealed class ModrinthRuntimeResolver : IPrismRuntimeResolver
{
    private readonly string _sourceFormat;
    private readonly string _os;
    private readonly string _architecture;
    private readonly string _osVersion;
    private readonly IReadOnlyDictionary<string, bool> _features;

    /// <summary>默认按当前平台解析；条件启动功能默认关闭，可由未来启动界面显式提供。</summary>
    public ModrinthRuntimeResolver(string? runtimeOs = null, string? runtimeArchitecture = null,
        IReadOnlyDictionary<string, bool>? features = null, string? runtimeOsVersion = null)
        : this("Modrinth", runtimeOs, runtimeArchitecture, features, runtimeOsVersion)
    {
    }

    /// <summary>为 CurseForge 复用官方解析流程，隔离缓存与指纹，不改变已有 Modrinth 公共契约。</summary>
    internal ModrinthRuntimeResolver(string sourceFormat, string? runtimeOs, string? runtimeArchitecture,
        IReadOnlyDictionary<string, bool>? features, string? runtimeOsVersion)
    {
        if (sourceFormat is not ("Modrinth" or "CurseForge")) throw new ArgumentOutOfRangeException(nameof(sourceFormat));
        _sourceFormat = sourceFormat;
        _os = runtimeOs ?? (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "unsupported");
        _architecture = runtimeArchitecture ?? RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64", Architecture.X86 => "x86", Architecture.Arm64 => "arm64", Architecture.Arm => "arm32", _ => "unsupported"
        };
        _osVersion = runtimeOsVersion ?? Environment.OSVersion.Version.ToString();
        _features = features is null ? new Dictionary<string, bool>() : new Dictionary<string, bool>(features, StringComparer.Ordinal);
        PrismRuntimeJson.Segment(_os);
        PrismRuntimeJson.Segment(_architecture);
    }

    /// <summary>校验实例清单后读取本地元数据，缺失元数据只转成固定地址的下载请求。</summary>
    public PrismRuntimePlan Resolve(string instancePath, InstanceImportManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancePath);
        ArgumentNullException.ThrowIfNull(manifest);
        InstanceManifestStore.Validate(manifest);
        if (!string.Equals(manifest.SourceFormat, _sourceFormat, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{_sourceFormat} 解析器只能处理 {_sourceFormat} 导入清单。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instancePath));
        InstanceImportPaths.EnsureNoLinks(root);
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) throw new IOException($"{_sourceFormat} 实例路径不是文件夹。");
        return new Resolution(root, manifest, _os, _architecture, _osVersion, _features, _sourceFormat).Build();
    }

    /// <summary>隔离一次解析的临时状态，避免不同实例共享缓存或条件参数。</summary>
    private sealed class Resolution(string root, InstanceImportManifest manifest, string os, string architecture,
        string osVersion, IReadOnlyDictionary<string, bool> features, string sourceFormat)
    {
        private readonly string MetadataRoot = ".eml-runtime/" + sourceFormat.ToLowerInvariant() + "/metadata/";
        private readonly List<PrismMetadataRequest> _requests = [];
        private readonly List<PrismRuntimeFile> _files = [];
        private readonly Dictionary<string, int> _libraryPositions = new(StringComparer.Ordinal);
        private readonly List<string> _issues = [];
        private readonly List<string> _notes = [];
        private readonly List<(string Path, byte[] Bytes)> _sources = [];
        private readonly List<string> _gameArguments = [];
        private readonly List<string> _jvmArguments = [];
        private readonly List<int> _javaMajors = [];
        private string _minecraft = "";
        private string _mainClass = "";
        private string? _legacyArguments;
        private string? _assetId;
        private string? _loggingArgument;

        /// <summary>严格使用清单固定版本，完整合并原版与加载器；未知加载器明确阻断。</summary>
        internal PrismRuntimePlan Build()
        {
            if (os == "unsupported" || architecture == "unsupported") _issues.Add("当前操作系统或架构尚不支持运行依赖准备。");
            var components = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var component in SelectComponents())
            {
                var id = PrismRuntimeJson.Segment(component.Id);
                var version = PrismRuntimeJson.Segment(component.Version);
                if (!components.TryAdd(id, version)) throw new InvalidDataException($"重复的 {sourceFormat} 组件：" + id);
            }
            if (!components.TryGetValue("minecraft", out var minecraft))
                throw new InvalidDataException($"{sourceFormat} 清单缺少固定的 minecraft 版本。");
            _minecraft = minecraft;
            if (manifest.MinecraftVersion is { } declared && declared != minecraft)
                throw new InvalidDataException($"{sourceFormat} MinecraftVersion 与组件固定版本不一致。");
            var loaders = components.Where(pair => pair.Key != "minecraft").ToArray();
            if (loaders.Length > 1) _issues.Add("整合包同时声明多个加载器，无法安全确定唯一启动配置。");
            foreach (var loader in loaders)
                if (loader.Key is not ("fabric-loader" or "quilt-loader"))
                    _issues.Add(loader.Key is "forge" or "neoforge"
                        ? $"{loader.Key} {loader.Value} 需要执行安装器处理器；当前尚未接入，不能将其当作原版准备完成。"
                        : $"尚未支持的 {sourceFormat} 组件：{loader.Key} {loader.Value}。");

            LoadMinecraft();
            if (loaders.Length == 1 && loaders[0].Key is "fabric-loader" or "quilt-loader") LoadLoader(loaders[0].Key, loaders[0].Value);
            if (_mainClass.Length == 0) _issues.Add("尚未解析出 Minecraft 启动主类。");
            if (!_files.Any(file => file.Role == PrismRuntimeFileRole.MainJar)) _issues.Add("尚未解析出 Minecraft 客户端文件。");
            if (!_files.Any(file => file.Role == PrismRuntimeFileRole.AssetIndex)) _issues.Add("尚未解析出 Minecraft 资源索引。");
            InstanceImportPaths.ValidateLayout(_files.Select(file => (file.Requirement.Path!, false)));
            _notes.Add($"运行依赖按 {os}/{architecture} 解析；所选 Java 架构必须与此计划一致。");
            _notes.Add("条件启动功能默认为关闭；更改分辨率、快速加入等条件后需重新生成启动计划。");
            return new PrismRuntimePlan
            {
                InstancePath = root, GameDirectory = manifest.GameDirectory, SourceFormat = sourceFormat, VersionIdPrefix = "EML-" + sourceFormat,
                RuntimeOs = os, RuntimeArchitecture = architecture, MinecraftVersion = _minecraft, MainClass = _mainClass,
                MinecraftArguments = _legacyArguments, GameArguments = _gameArguments, JvmArguments = _jvmArguments,
                CompatibleJavaMajors = _javaMajors.Distinct().ToArray(), AssetIndexId = _assetId, LoggingArgument = _loggingArgument,
                Files = _files, MetadataRequests = _requests, BlockingIssues = _issues.Distinct(StringComparer.Ordinal).ToArray(),
                Notes = _notes.Distinct(StringComparer.Ordinal).ToArray(), Fingerprint = Fingerprint()
            };
        }

        /// <summary>CurseForge 优先选择唯一主加载器；备选加载器不参与运行安装，歧义必须阻断。</summary>
        private IReadOnlyList<InstanceComponentRequirement> SelectComponents()
        {
            if (sourceFormat != "CurseForge") return manifest.Components;
            foreach (var component in manifest.Components)
            {
                PrismRuntimeJson.Segment(component.Id);
                PrismRuntimeJson.Segment(component.Version);
            }
            var minecraft = manifest.Components.Where(component => component.Id == "minecraft").ToArray();
            var loaders = manifest.Components.Where(component => component.Id != "minecraft").ToArray();
            var primary = loaders.Where(component => component.Primary == true).ToArray();
            if (primary.Length > 1)
            {
                _issues.Add("CurseForge 清单声明了多个 primary 主加载器，无法确定唯一启动配置。");
                return minecraft;
            }
            if (primary.Length == 0 && loaders.Length > 1)
            {
                _issues.Add("CurseForge 清单存在多个加载器但未声明唯一 primary，需明确选择后才能准备运行环境。");
                return minecraft;
            }
            var selected = primary.SingleOrDefault() ?? loaders.SingleOrDefault();
            if (selected is null) return minecraft;
            if (loaders.Length > 1)
                _notes.Add($"CurseForge 使用主加载器 {selected.Id} {selected.Version}；其余 {loaders.Length - 1} 个非主加载器作为备选，不参与本次安装。");
            var normalized = selected with
            {
                Id = selected.Id switch { "fabric" => "fabric-loader", "quilt" => "quilt-loader", _ => selected.Id }
            };
            return [.. minecraft, normalized];
        }

        /// <summary>从官方索引定位精确版本，校验其 SHA1 后读取本体、资源、Java 和日志要求。</summary>
        private void LoadMinecraft()
        {
            var index = LoadMetadata(new("minecraft-manifest", "v2", MetadataRoot + "minecraft/manifest-v2.json",
                "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"));
            if (index is null) return;
            var versions = PrismRuntimeJson.Array(index.Value, "versions")
                .Where(value => PrismRuntimeJson.Text(value, "id") == _minecraft).ToArray();
            if (versions.Length != 1)
            {
                _issues.Add($"官方版本索引没有唯一的 Minecraft {_minecraft}；不会改用最新版本。若索引过旧，请刷新元数据缓存。");
                return;
            }
            var entry = versions[0];
            var versionHashes = ReadHashes(entry);
            if (!versionHashes.ContainsKey("sha1")) throw new InvalidDataException("Minecraft 官方版本索引缺少版本元数据 SHA1。");
            var metadata = LoadMetadata(new("minecraft", _minecraft, MetadataRoot + "minecraft/" + _minecraft + ".json",
                PrismRuntimeJson.Url(PrismRuntimeJson.Text(entry, "url") ?? throw new InvalidDataException("版本索引缺少 URL。")))
                { RequirementHashes = versionHashes });
            if (metadata is not { } json) return;
            if (PrismRuntimeJson.Text(json, "id") != _minecraft) throw new InvalidDataException("Minecraft 元数据版本与清单不一致。");
            if (json.TryGetProperty("inheritsFrom", out _)) throw new InvalidDataException("官方原版元数据不应声明未解析的 inheritsFrom。");
            MergeProfile(json, false);
            if (!json.TryGetProperty("downloads", out var downloads) || !downloads.TryGetProperty("client", out var client))
                throw new InvalidDataException("Minecraft 元数据缺少客户端文件。");
            var maven = PrismMavenName.Parse("com.mojang:minecraft:" + _minecraft + ":client");
            _files.Add(new(Download(client, GamePath("libraries/" + maven.RelativePath)), PrismRuntimeFileRole.MainJar)
                { MavenName = "com.mojang:minecraft:" + _minecraft + ":client", OnClassPath = true });
            if (!json.TryGetProperty("assetIndex", out var assets)) throw new InvalidDataException("Minecraft 元数据缺少 assetIndex。");
            _assetId = PrismRuntimeJson.Segment(PrismRuntimeJson.Text(assets, "id") ?? throw new InvalidDataException("资源索引缺少 id。"));
            _files.Add(new(Download(assets, GamePath("assets/indexes/" + _assetId + ".json")), PrismRuntimeFileRole.AssetIndex));
            if (json.TryGetProperty("javaVersion", out var java))
            {
                if (!java.TryGetProperty("majorVersion", out var major) || !major.TryGetInt32(out var number) || number < 1)
                    throw new InvalidDataException("Minecraft Java 主版本无效。");
                _javaMajors.Add(number);
            }
            else
            {
                _javaMajors.Add(8);
                _notes.Add("该旧版 Minecraft 未声明 Java 版本，使用 Java 8 作为兼容基线。");
            }
            if (json.TryGetProperty("logging", out var logging) && logging.TryGetProperty("client", out var log))
            {
                if (PrismRuntimeJson.Text(log, "type") is { } type && type != "log4j2-xml")
                    _issues.Add("尚未支持的 Minecraft 日志配置类型：" + type);
                if (!log.TryGetProperty("file", out var file)) throw new InvalidDataException("日志配置缺少 file。");
                var id = PrismRuntimeJson.Segment(PrismRuntimeJson.Text(file, "id") ?? throw new InvalidDataException("日志配置缺少 id。"));
                _loggingArgument = PrismRuntimeJson.Text(log, "argument") ?? throw new InvalidDataException("日志配置缺少启动参数。");
                if (!_loggingArgument.Contains("${path}", StringComparison.Ordinal)) throw new InvalidDataException("日志参数缺少文件路径占位符。");
                _files.Add(new(Download(file, GamePath("assets/log_configs/" + id)), PrismRuntimeFileRole.Logging));
            }
        }

        /// <summary>下载固定 Fabric/Quilt 配置，验证继承版本及加载器坐标，不能静默替换加载器。</summary>
        private void LoadLoader(string id, string version)
        {
            var fabric = id == "fabric-loader";
            var url = $"https://{(fabric ? "meta.fabricmc.net" : "meta.quiltmc.org")}/v{(fabric ? "2" : "3")}/versions/loader/{Uri.EscapeDataString(_minecraft)}/{Uri.EscapeDataString(version)}/profile/json";
            var metadata = LoadMetadata(new(id, version, MetadataRoot + id + "/" + _minecraft + "/" + version + ".json", url));
            if (metadata is not { } json) return;
            if (PrismRuntimeJson.Text(json, "inheritsFrom") != _minecraft) throw new InvalidDataException("加载器配置继承的 Minecraft 版本与整合包不一致。");
            var coordinate = (fabric ? "net.fabricmc:fabric-loader:" : "org.quiltmc:quilt-loader:") + version;
            if (!PrismRuntimeJson.Array(json, "libraries").Any(library => PrismRuntimeJson.Text(library, "name") == coordinate))
                throw new InvalidDataException("加载器配置未包含所要求的固定版本：" + coordinate);
            MergeProfile(json, true);
        }

        /// <summary>合并启动参数及类库；加载器覆盖主类，额外处理器等未知安装步骤不能忽略。</summary>
        private void MergeProfile(JsonElement json, bool loader)
        {
            foreach (var field in new[] { "processors", "jarMods", "downloads", "data" })
                if (loader && json.TryGetProperty(field, out var value) &&
                    !(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0))
                    _issues.Add("加载器包含尚未支持的安装字段：" + field);
            if (PrismRuntimeJson.Text(json, "mainClass") is { Length: > 0 } main) _mainClass = main;
            if (PrismRuntimeJson.Text(json, "minecraftArguments") is { } legacy) _legacyArguments = legacy;
            if (json.TryGetProperty("arguments", out var arguments))
            {
                AddArguments(arguments, "game", _gameArguments);
                AddArguments(arguments, "jvm", _jvmArguments);
                if (arguments.TryGetProperty("default-user-jvm", out _))
                    _notes.Add("官方 default-user-jvm 只是可选默认调优；内存等用户参数由启动界面决定，安装阶段不会覆盖。");
            }
            foreach (var library in PrismRuntimeJson.Array(json, "libraries")) AddLibrary(library);
        }

        /// <summary>普通 artifact 与 natives 同时存在时分别保留，现代 native JAR 则维持原有类路径形式。</summary>
        private void AddLibrary(JsonElement library)
        {
            if (!Active(library)) return;
            var name = PrismRuntimeJson.Text(library, "name") ?? throw new InvalidDataException("运行类库缺少 name。");
            var maven = PrismMavenName.Parse(name);
            var hasDownloads = library.TryGetProperty("downloads", out var downloads);
            var hasArtifact = hasDownloads && downloads.TryGetProperty("artifact", out _);
            if (hasDownloads && downloads.TryGetProperty("artifact", out var artifact))
                PutLibrary(new(Download(artifact, GamePath("libraries/" + maven.RelativePath)), PrismRuntimeFileRole.Library)
                    { MavenName = name, OnClassPath = true });
            else if (!hasDownloads)
            {
                var repository = PrismRuntimeJson.Url(PrismRuntimeJson.Text(library, "url") ?? "https://libraries.minecraft.net/");
                if (!repository.EndsWith('/') || new Uri(repository).Query.Length != 0 || new Uri(repository).Fragment.Length != 0)
                    throw new InvalidDataException("Maven 仓库地址必须为不含查询参数的目录 URL。");
                var hashes = ReadHashes(library);
                if (hashes.Count == 0) _notes.Add("官方加载器未提供类库哈希，下载文件会标为未验证来源哈希：" + name);
                PutLibrary(new(new InstanceFileRequirement
                {
                    Path = GamePath("libraries/" + maven.RelativePath), Source = InstanceFileSource.DirectDownload,
                    Downloads = [new Uri(new Uri(repository), maven.RelativePath).AbsoluteUri], Hashes = hashes, Size = ReadSize(library)
                }, PrismRuntimeFileRole.Library) { MavenName = name, OnClassPath = true });
            }
            if (!library.TryGetProperty("natives", out var natives))
            {
                if (hasDownloads && !hasArtifact) throw new InvalidDataException("运行库缺少 artifact 或 natives 下载声明：" + name);
                return;
            }
            var classifier = PrismRuntimeJson.Text(natives, os + "-" + architecture);
            if (classifier is null && architecture is "x86" or "x86_64") classifier = PrismRuntimeJson.Text(natives, os);
            if (classifier is null && PrismRuntimeJson.Text(natives, os) is not null)
            {
                _issues.Add("旧式本机库未声明适用于当前架构的文件：" + name);
                return;
            }
            if (classifier is null) return;
            if (architecture is not ("x86" or "x86_64") && classifier.Contains("${arch}", StringComparison.Ordinal))
            {
                _issues.Add("旧式本机库未声明适用于当前架构的文件：" + name);
                return;
            }
            classifier = PrismRuntimeJson.Segment(classifier.Replace("${arch}", architecture == "x86" ? "32" : "64", StringComparison.Ordinal));
            if (!hasDownloads || !downloads.TryGetProperty("classifiers", out var classifiers) || !classifiers.TryGetProperty(classifier, out var native))
                throw new InvalidDataException("本机库缺少固定 classifier 下载声明：" + name + ":" + classifier);
            var excludes = new List<string>();
            if (library.TryGetProperty("extract", out var extract))
                foreach (var exclusion in PrismRuntimeJson.Array(extract, "exclude"))
                {
                    if (exclusion.ValueKind != JsonValueKind.String) throw new InvalidDataException("native 排除项必须为字符串。");
                    var raw = exclusion.GetString()!;
                    var safe = InstanceImportPaths.Relative(raw.TrimEnd('/'));
                    excludes.Add(raw.EndsWith('/') ? safe + "/" : safe);
                }
            var nativeMaven = maven with { Classifier = classifier };
            PutLibrary(new(Download(native, GamePath("libraries/" + nativeMaven.RelativePath)), PrismRuntimeFileRole.NativeArchive)
                { MavenName = $"{maven.Group}:{maven.Artifact}:{maven.Version}:{classifier}", ExtractExcludes = excludes });
        }

        /// <summary>相同 Maven 身份保留后合并配置指定的版本，维持首次出现的 classpath 顺序。</summary>
        private void PutLibrary(PrismRuntimeFile file)
        {
            var identity = file.Role + ":" + PrismMavenName.Parse(file.MavenName!).Identity;
            if (_libraryPositions.TryGetValue(identity, out var existing))
            {
                var previous = _files[existing];
                if (previous.MavenName == file.MavenName)
                {
                    if (previous.Requirement.Hashes.Any(hash => file.Requirement.Hashes.TryGetValue(hash.Key, out var value) &&
                        !string.Equals(value, hash.Value, StringComparison.OrdinalIgnoreCase)) ||
                        previous.Requirement.Size is { } oldSize && file.Requirement.Size is { } newSize && oldSize != newSize)
                        throw new InvalidDataException("同名类库存在相互矛盾的哈希或大小：" + file.MavenName);
                    var mergedHashes = previous.Requirement.Hashes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    foreach (var hash in file.Requirement.Hashes) mergedHashes[hash.Key] = hash.Value;
                    file = file with { Requirement = file.Requirement with { Hashes = mergedHashes, Size = file.Requirement.Size ?? previous.Requirement.Size } };
                }
                _files[existing] = file;
            }
            else { _libraryPositions[identity] = _files.Count; _files.Add(file); }
        }

        /// <summary>按顺序应用操作系统及布尔功能规则，未知功能默认 false，正则有执行时限。</summary>
        private bool Active(JsonElement value)
        {
            if (!value.TryGetProperty("rules", out var rules)) return true;
            if (rules.ValueKind != JsonValueKind.Array) throw new InvalidDataException("rules 必须为数组。");
            var allowed = rules.GetArrayLength() == 0;
            foreach (var rule in rules.EnumerateArray())
            {
                var matches = true;
                if (rule.TryGetProperty("os", out var system))
                {
                    foreach (var property in system.EnumerateObject())
                        if (property.Name is not ("name" or "arch" or "version"))
                        {
                            _issues.Add("必需运行参数包含尚未支持的操作系统条件：" + property.Name);
                            return false;
                        }
                    if (PrismRuntimeJson.Text(system, "name") is { } name) matches &= name == os;
                    if (PrismRuntimeJson.Text(system, "arch") is { } arch)
                        matches &= arch switch { "x86_64" or "amd64" => architecture == "x86_64", "aarch64" or "arm64" => architecture == "arm64", _ => arch == architecture };
                    if (PrismRuntimeJson.Text(system, "version") is { } version)
                        matches &= Regex.IsMatch(osVersion, version, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                }
                if (rule.TryGetProperty("features", out var conditions))
                {
                    if (conditions.ValueKind != JsonValueKind.Object) throw new InvalidDataException("features 必须为对象。");
                    foreach (var condition in conditions.EnumerateObject())
                    {
                        if (condition.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("功能条件必须为布尔值。");
                        matches &= (features.TryGetValue(condition.Name, out var enabled) && enabled) == condition.Value.GetBoolean();
                    }
                }
                var action = PrismRuntimeJson.Text(rule, "action");
                if (action is not ("allow" or "disallow")) throw new InvalidDataException("不支持的规则 action。");
                if (matches) allowed = action == "allow";
            }
            return allowed;
        }

        /// <summary>保留每个参数的边界，并移除由统一启动配置补充的标准 classpath/native 参数。</summary>
        private void AddArguments(JsonElement source, string name, List<string> target)
        {
            foreach (var entry in PrismRuntimeJson.Array(source, name))
            {
                if (entry.ValueKind == JsonValueKind.String) { target.Add(entry.GetString()!); continue; }
                if (entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException("参数必须为字符串或条件对象。");
                if (!Active(entry)) continue;
                if (!entry.TryGetProperty("value", out var value)) throw new InvalidDataException("条件参数缺少 value。");
                if (value.ValueKind == JsonValueKind.String) target.Add(value.GetString()!);
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var part in value.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.String) throw new InvalidDataException("条件参数数组必须全为字符串。");
                        target.Add(part.GetString()!);
                    }
                else throw new InvalidDataException("条件参数 value 格式无效。");
            }
            if (name != "jvm") return;
            for (var index = target.Count - 1; index >= 0; index--)
            {
                if (target[index] == "-Djava.library.path=${natives_directory}") target.RemoveAt(index);
                else if (target[index] == "${classpath}" && index > 0 && target[index - 1] is "-cp" or "-classpath" or "--class-path")
                { target.RemoveRange(index - 1, 2); index--; }
            }
        }

        /// <summary>读取本地固定元数据并校验已知哈希；不存在时只登记下载请求。</summary>
        private JsonElement? LoadMetadata(PrismMetadataRequest request)
        {
            var full = InstanceImportPaths.Within(root, request.RelativePath);
            InstanceImportPaths.EnsureNoLinks(full);
            try
            {
                var (json, bytes) = PrismRuntimeJson.Read(full);
                if (request.ExpectedSize is { } size && bytes.LongLength != size) throw new InvalidDataException("元数据大小与官方声明不符：" + request.RelativePath);
                foreach (var (algorithm, expected) in request.RequirementHashes)
                {
                    var actual = algorithm switch
                    {
                        "sha1" => SHA1.HashData(bytes), "sha256" => SHA256.HashData(bytes), "sha512" => SHA512.HashData(bytes),
                        _ => throw new InvalidDataException("不支持的元数据哈希算法。")
                    };
                    if (!string.Equals(Convert.ToHexString(actual), expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("元数据哈希与官方声明不符：" + request.RelativePath);
                }
                _sources.Add((request.RelativePath, bytes));
                return json;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            _requests.Add(request);
            _issues.Add($"固定版本元数据尚未下载：{request.Id} {request.Version}。");
            return null;
        }

        /// <summary>使用来源声明的 URL、大小和哈希生成下载项，不相信来源提供的本地绝对路径。</summary>
        private InstanceFileRequirement Download(JsonElement artifact, string path)
        {
            var url = PrismRuntimeJson.Url(PrismRuntimeJson.Text(artifact, "url") ?? throw new InvalidDataException("下载项缺少 URL。"));
            var hashes = ReadHashes(artifact);
            if (hashes.Count == 0) _notes.Add("来源未提供内容哈希：" + path);
            return new() { Path = path, Source = InstanceFileSource.DirectDownload, Downloads = [url], Hashes = hashes, Size = ReadSize(artifact) };
        }

        /// <summary>接受已知 SHA 哈希算法，空字符串表示没有提供校验依据。</summary>
        private static IReadOnlyDictionary<string, string> ReadHashes(JsonElement value)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (algorithm, length) in new[] { ("sha1", 40), ("sha256", 64), ("sha512", 128) })
            {
                var hash = PrismRuntimeJson.Text(value, algorithm);
                if (string.IsNullOrEmpty(hash)) continue;
                if (hash.Length != length || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("下载项哈希无效：" + algorithm);
                result[algorithm] = hash.ToLowerInvariant();
            }
            return result;
        }

        /// <summary>读取已知大小；历史元数据的零值按未知处理，拒绝负数和小数。</summary>
        private static long? ReadSize(JsonElement value)
        {
            if (!value.TryGetProperty("size", out var size)) return null;
            if (!size.TryGetInt64(out var number) || number < 0) throw new InvalidDataException("下载项大小无效。");
            return number == 0 ? null : number;
        }

        /// <summary>将游戏目录内的文件转换为已验证的实例相对路径。</summary>
        private string GamePath(string relative) => InstanceImportPaths.Relative(manifest.GameDirectory == "." ? relative : manifest.GameDirectory + "/" + relative);

        /// <summary>指纹涵盖原文、平台、功能开关和导入基线，使旧启动产物在输入变化时失效。</summary>
        private string Fingerprint()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var value in new[] { "eml-" + sourceFormat.ToLowerInvariant() + "-resolver-v1", os, architecture, osVersion, JsonSerializer.Serialize(manifest),
                JsonSerializer.Serialize(features.OrderBy(pair => pair.Key, StringComparer.Ordinal)) })
            { hash.AppendData(Encoding.UTF8.GetBytes(value)); hash.AppendData([0]); }
            foreach (var source in _sources)
            { hash.AppendData(Encoding.UTF8.GetBytes(source.Path)); hash.AppendData([0]); hash.AppendData(source.Bytes); hash.AppendData([0]); }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }
}
