using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Prism;

/// <summary>只读合并 Prism 的固定版本组件、本地补丁及当前平台依赖。</summary>
public sealed class PrismRuntimeResolver : IPrismRuntimeResolver
{
    private readonly string _os;
    private readonly string _architecture;

    /// <summary>默认采用当前进程平台；测试或启动模块可以明确指定与所选 Java 一致的平台。</summary>
    public PrismRuntimeResolver(string? runtimeOs = null, string? runtimeArchitecture = null)
    {
        _os = runtimeOs ?? (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "unsupported");
        _architecture = runtimeArchitecture ?? RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64", Architecture.X86 => "x86", Architecture.Arm64 => "arm64", Architecture.Arm => "arm32", _ => "unsupported"
        };
        PrismRuntimeJson.Segment(_os);
        PrismRuntimeJson.Segment(_architecture);
    }

    /// <summary>验证实例与导入清单后解析组件，不联网、不创建目录、不改写清单。</summary>
    public PrismRuntimePlan Resolve(string instancePath, InstanceImportManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancePath);
        ArgumentNullException.ThrowIfNull(manifest);
        InstanceManifestStore.Validate(manifest);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instancePath));
        InstanceImportPaths.EnsureNoLinks(root);
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) throw new IOException("Prism 实例路径不是文件夹。");
        return new Resolution(root, manifest, _os, _architecture).Build();
    }

    /// <summary>单次解析的临时状态，与其他实例或并发调用互不共享。</summary>
    private sealed class Resolution(string root, InstanceImportManifest manifest, string os, string architecture)
    {
        private readonly List<string> _issues = [];
        private readonly List<string> _notes = [];
        private readonly List<PrismMetadataRequest> _requests = [];
        private readonly List<Component> _components = [];
        private readonly Dictionary<string, Component> _byId = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _disabledIds = new(StringComparer.Ordinal);
        private readonly List<(string Name, byte[] Bytes)> _sources = [];
        private readonly List<PrismRuntimeFile> _libraries = [];
        private readonly Dictionary<string, int> _libraryPositions = new(StringComparer.Ordinal);
        private readonly List<string> _gameArguments = [];
        private readonly List<string> _jvmArguments = [];
        private readonly List<string> _tweakers = [];
        private readonly List<string> _traits = [];
        private readonly List<int> _javaMajors = [];
        private string _minecraftVersion = "";
        private string _mainClass = "";
        private string? _minecraftArguments;
        private PrismRuntimeFile? _mainJar;
        private PrismRuntimeFile? _assetIndex;
        private string? _assetIndexId;
        private string Game => manifest.GameDirectory;

        /// <summary>按 mmc-pack 数组顺序合并组件；旧补丁 order 字段不改变用户保存的顺序。</summary>
        internal PrismRuntimePlan Build()
        {
            var pack = ReadSource("mmc-pack.json");
            if (!pack.TryGetProperty("formatVersion", out var format) || !format.TryGetInt32(out var version) || version != 1)
                throw new InvalidDataException("不支持的 mmc-pack.json 格式。");
            if (os == "unsupported" || architecture == "unsupported") _issues.Add("当前操作系统或进程架构尚不支持运行依赖准备。");
            foreach (var entry in PrismRuntimeJson.Array(pack, "components"))
            {
                if (_components.Count >= 1024) throw new InvalidDataException("Prism 组件数量过多。");
                var id = PrismRuntimeJson.Segment(PrismRuntimeJson.Text(entry, "uid") ?? throw new InvalidDataException("组件缺少 uid。"));
                if (!_seenIds.Add(id)) throw new InvalidDataException("Prism 组件重复：" + id);
                if (entry.TryGetProperty("disabled", out var disabled))
                {
                    if (disabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("disabled 必须为布尔值。");
                    if (disabled.GetBoolean()) { _disabledIds.Add(id); continue; }
                }
                var exact = PrismRuntimeJson.Text(entry, "version");
                if (exact is not null) PrismRuntimeJson.Segment(exact);
                if (_byId.ContainsKey(id)) throw new InvalidDataException("Prism 组件重复：" + id);
                var component = new Component(id, exact);
                _components.Add(component);
                _byId.Add(id, component);
            }
            foreach (var component in _components.ToArray()) Load(component);
            // 依赖没有列在原数组时插在使用它的组件前；已有组件的相对顺序始终保留。
            foreach (var component in _components.ToArray()) ExpandDependencies(component, new HashSet<string>(StringComparer.Ordinal));
            foreach (var component in _components)
                if (component.Json is { } json)
                {
                    CheckConflicts(component, json);
                    Apply(component, json);
                }
            if (_minecraftVersion.Length == 0) _issues.Add("没有解析出 Minecraft 组件版本。");
            if (_mainClass.Length == 0) _issues.Add("没有解析出启动主类。");
            if (_mainJar is null) _issues.Add("没有解析出 Minecraft 客户端主文件。");
            if (_assetIndex is null) _issues.Add("没有解析出 Minecraft 资源索引。");
            var files = new List<PrismRuntimeFile>(_libraries);
            if (_mainJar is not null) files.Add(_mainJar);
            if (_assetIndex is not null) files.Add(_assetIndex);
            CheckFileLayout(files);
            InstanceImportPaths.EnsureNoLinks(root);
            _notes.Add($"运行依赖按 {os}/{architecture} 解析；启动时选择的 Java 架构必须与此计划一致。");
            return new PrismRuntimePlan
            {
                InstancePath = root, GameDirectory = Game, RuntimeOs = os, RuntimeArchitecture = architecture,
                MinecraftVersion = _minecraftVersion, MainClass = _mainClass, MinecraftArguments = _minecraftArguments,
                GameArguments = _gameArguments, JvmArguments = _jvmArguments, Tweakers = _tweakers,
                Traits = _traits.Distinct(StringComparer.Ordinal).ToArray(), CompatibleJavaMajors = _javaMajors.Distinct().ToArray(),
                AssetIndexId = _assetIndexId, Files = files, MetadataRequests = _requests,
                BlockingIssues = _issues.Distinct(StringComparer.Ordinal).ToArray(), Notes = _notes.Distinct(StringComparer.Ordinal).ToArray(),
                Fingerprint = Fingerprint()
            };
        }

        /// <summary>优先读取实例自定义补丁；缺少补丁时只能使用指定版本的官方缓存。</summary>
        private void Load(Component component)
        {
            var patch = "patches/" + component.Id + ".json";
            string? source = Exists(patch) ? patch : null;
            if (source is null)
            {
                if (component.RequestedVersion is null)
                {
                    _issues.Add($"自定义组件 {component.Id} 缺少补丁，也没有明确版本；不能用 cachedVersion 猜测下载。");
                    return;
                }
                source = $".eml-runtime/metadata/{component.Id}/{component.RequestedVersion}.json";
                if (!Exists(source))
                {
                    _requests.Add(new(component.Id, component.RequestedVersion, source,
                        $"https://meta.prismlauncher.org/v1/{Uri.EscapeDataString(component.Id)}/{Uri.EscapeDataString(component.RequestedVersion)}.json"));
                    _issues.Add($"组件 {component.Id} {component.RequestedVersion} 的固定版本元数据尚未下载。");
                    return;
                }
            }
            var json = ReadSource(source);
            if (PrismRuntimeJson.Text(json, "uid") != component.Id) throw new InvalidDataException("补丁 uid 与组件不一致：" + source);
            var actualVersion = PrismRuntimeJson.Segment(PrismRuntimeJson.Text(json, "version") ?? throw new InvalidDataException("补丁缺少 version：" + source));
            if (component.RequestedVersion is not null && component.RequestedVersion != actualVersion)
                throw new InvalidDataException("补丁版本与固定组件版本不一致：" + source);
            if (json.TryGetProperty("formatVersion", out var format) && (!format.TryGetInt32(out var number) || number != 1))
                throw new InvalidDataException("不支持的 Prism 补丁格式：" + source);
            component.Version = actualVersion;
            component.Json = json;
        }

        /// <summary>递归补齐明确 requires，检查版本约束和循环；建议版本不会替换已有选择。</summary>
        private void ExpandDependencies(Component component, HashSet<string> stack)
        {
            if (component.Json is not { } json || component.Expanded) return;
            if (!stack.Add(component.Id)) { _issues.Add("组件依赖存在循环：" + component.Id); return; }
            foreach (var requirement in PrismRuntimeJson.Array(json, "requires"))
            {
                var id = PrismRuntimeJson.Segment(PrismRuntimeJson.Text(requirement, "uid") ?? throw new InvalidDataException("requires 缺少 uid。"));
                var exact = PrismRuntimeJson.Text(requirement, "equals");
                var suggested = PrismRuntimeJson.Text(requirement, "suggests");
                if (exact is not null) PrismRuntimeJson.Segment(exact);
                if (suggested is not null) PrismRuntimeJson.Segment(suggested);
                if (_disabledIds.Contains(id)) { _issues.Add($"{component.Id} 需要已禁用组件 {id}。"); continue; }
                if (!_byId.TryGetValue(id, out var dependency))
                {
                    if (_components.Count >= 1024) throw new InvalidDataException("组件依赖数量过多。");
                    dependency = new Component(id, exact ?? suggested);
                    _byId.Add(id, dependency);
                    _components.Insert(_components.IndexOf(component), dependency);
                    Load(dependency);
                }
                var chosen = dependency.Version ?? dependency.RequestedVersion;
                if (exact is not null && chosen != exact)
                    _issues.Add($"{component.Id} 要求 {id}={exact}，当前选择为 {chosen ?? "未知"}。");
                ExpandDependencies(dependency, stack);
            }
            stack.Remove(component.Id);
            component.Expanded = true;
        }

        /// <summary>已启用组件与冲突声明相遇时阻止安装计划成为可启动状态。</summary>
        private void CheckConflicts(Component component, JsonElement json)
        {
            foreach (var conflict in PrismRuntimeJson.Array(json, "conflicts"))
            {
                var id = PrismRuntimeJson.Segment(PrismRuntimeJson.Text(conflict, "uid") ?? throw new InvalidDataException("conflicts 缺少 uid。"));
                var exact = PrismRuntimeJson.Text(conflict, "equals");
                if (_byId.TryGetValue(id, out var other) && (exact is null || (other.Version ?? other.RequestedVersion) == exact))
                    _issues.Add($"组件 {component.Id} 与已启用组件 {id} 冲突。");
            }
        }

        /// <summary>合并启动字段、Java 约束与文件依赖；不支持的安装步骤明确记录为阻断项。</summary>
        private void Apply(Component component, JsonElement json)
        {
            foreach (var property in json.EnumerateObject())
            {
                if ((property.Name.StartsWith('-') || property.Name.StartsWith('+') && property.Name is not ("+jvmArgs" or "+tweakers" or "+traits")) ||
                    property.Name is "jarMods" or "mods" or "mavenFiles" or "agents" or "processors" or "appletClass" or "removeLibraries")
                {
                    if (property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() == 0) continue;
                    _issues.Add($"组件 {component.Id} 含尚未支持的安装或合并字段 {property.Name}。");
                }
            }
            if (PrismRuntimeJson.Text(json, "mainClass") is { Length: > 0 } mainClass) _mainClass = mainClass;
            if (PrismRuntimeJson.Text(json, "minecraftArguments") is { Length: > 0 } arguments) _minecraftArguments = arguments;
            AddStrings(json, "+jvmArgs", _jvmArguments);
            AddStrings(json, "+tweakers", _tweakers);
            AddStrings(json, "+traits", _traits);
            foreach (var major in PrismRuntimeJson.Array(json, "compatibleJavaMajors"))
            {
                if (!major.TryGetInt32(out var number) || number <= 0) throw new InvalidDataException("无效 Java 主版本约束。");
                _javaMajors.Add(number);
            }
            if (json.TryGetProperty("arguments", out var modern))
            {
                AddArguments(modern, "game", _gameArguments);
                AddArguments(modern, "jvm", _jvmArguments);
            }
            if (json.TryGetProperty("logging", out _)) _issues.Add($"组件 {component.Id} 的 logging 配置尚未接入，不能静默丢失日志启动参数。");
            foreach (var library in PrismRuntimeJson.Array(json, "libraries"))
            {
                var file = ParseLibrary(library);
                if (file is not null) AddLibrary(file);
            }
            if (json.TryGetProperty("mainJar", out var mainJar)) _mainJar = ParseLibrary(mainJar, PrismRuntimeFileRole.MainJar);
            if (component.Id == "net.minecraft")
            {
                _minecraftVersion = component.Version!;
                if (json.TryGetProperty("assetIndex", out var assetIndex))
                {
                    _assetIndexId = PrismRuntimeJson.Segment(PrismRuntimeJson.Text(assetIndex, "id") ?? throw new InvalidDataException("assetIndex 缺少 id。"));
                    _assetIndex = new(DownloadRequirement(assetIndex, GamePath("assets/indexes/" + _assetIndexId + ".json")), PrismRuntimeFileRole.AssetIndex);
                }
            }
        }

        /// <summary>根据 Maven 坐标和平台规则解析普通类库、包内本地库或旧式 native 压缩包。</summary>
        private PrismRuntimeFile? ParseLibrary(JsonElement json, PrismRuntimeFileRole role = PrismRuntimeFileRole.Library)
        {
            if (!PrismRuntimeJson.Active(json, os, architecture, _issues)) return null;
            var name = PrismRuntimeJson.Text(json, "name") ?? throw new InvalidDataException("运行库缺少 Maven name。");
            var maven = PrismMavenName.Parse(name);
            var local = PrismRuntimeJson.Text(json, "MMC-hint") == "local";
            var onClassPath = true;
            var excludes = new List<string>();
            if (json.TryGetProperty("natives", out var nativeMap))
            {
                var classifier = PrismRuntimeJson.Text(nativeMap, os + "-" + architecture);
                if (classifier is null && (architecture is "x86" or "x86_64")) classifier = PrismRuntimeJson.Text(nativeMap, os);
                if (classifier is null) return null;
                classifier = classifier.Replace("${arch}", architecture == "x86" ? "32" : "64", StringComparison.Ordinal);
                PrismRuntimeJson.Segment(classifier);
                maven = maven with { Classifier = classifier };
                role = PrismRuntimeFileRole.NativeArchive;
                onClassPath = false;
                if (json.TryGetProperty("extract", out var extract))
                    foreach (var value in PrismRuntimeJson.Array(extract, "exclude"))
                    {
                        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("native 排除项必须为字符串。");
                        var raw = value.GetString()!;
                        var safe = InstanceImportPaths.Relative(raw.TrimEnd('/'));
                        excludes.Add(raw.EndsWith('/') ? safe + "/" : safe);
                    }
            }
            var path = local ? "libraries/" + maven.FileName : GamePath("libraries/" + maven.RelativePath);
            InstanceFileRequirement requirement;
            if (local)
            {
                requirement = manifest.Files.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))
                    ?? new InstanceFileRequirement { Path = path, Source = InstanceFileSource.Embedded };
                requirement = requirement with { Path = path, Source = InstanceFileSource.Embedded, Downloads = [], Required = true };
                if (!Exists(path)) _issues.Add($"包内本地运行库缺失，需要从原整合包恢复：{path}");
                if (requirement.Hashes.Count == 0) _notes.Add("本地运行库没有导入校验依据：" + path);
            }
            else
            {
                if (!json.TryGetProperty("downloads", out var downloads))
                {
                    _issues.Add($"运行库 {name} 没有明确下载元数据；当前不会从坐标猜测 URL。");
                    return null;
                }
                JsonElement artifact;
                if (role == PrismRuntimeFileRole.NativeArchive)
                {
                    if (!downloads.TryGetProperty("classifiers", out var classifiers) || !classifiers.TryGetProperty(maven.Classifier!, out artifact))
                    { _issues.Add($"运行库 {name} 缺少 {maven.Classifier} 的下载声明。"); return null; }
                }
                else if (!downloads.TryGetProperty("artifact", out artifact))
                { _issues.Add($"运行库 {name} 缺少 artifact 下载声明。"); return null; }
                requirement = DownloadRequirement(artifact, path);
            }
            return new(requirement, role) { MavenName = $"{maven.Group}:{maven.Artifact}:{maven.Version}{(maven.Classifier is null ? "" : ":" + maven.Classifier)}{(maven.Extension == "jar" ? "" : "@" + maven.Extension)}", OnClassPath = onClassPath, ExtractExcludes = excludes };
        }

        /// <summary>按同名坐标保留较高版本，同时保留该类库首次出现的 classpath 位置。</summary>
        private void AddLibrary(PrismRuntimeFile file)
        {
            var name = PrismMavenName.Parse(file.MavenName!);
            var identity = file.Role + ":" + name.Identity;
            if (!_libraryPositions.TryGetValue(identity, out var index))
            {
                _libraryPositions.Add(identity, _libraries.Count);
                _libraries.Add(file);
            }
            else
            {
                var existing = _libraries[index];
                var comparison = PrismRuntimeJson.CompareVersions(name.Version, PrismMavenName.Parse(existing.MavenName!).Version);
                if (comparison > 0) _libraries[index] = file;
                else if (comparison == 0 &&
                    ((existing.Requirement.Size is { } size && file.Requirement.Size is { } otherSize && size != otherSize) ||
                     existing.Requirement.Hashes.Any(pair => file.Requirement.Hashes.TryGetValue(pair.Key, out var hash) &&
                         !string.Equals(hash, pair.Value, StringComparison.OrdinalIgnoreCase))))
                    _issues.Add("同版本类库存在相互矛盾的大小或哈希声明：" + file.MavenName);
            }
        }

        /// <summary>转换来源给出的 URL、大小和哈希；空哈希与零大小视为旧元数据中的未知值。</summary>
        private InstanceFileRequirement DownloadRequirement(JsonElement artifact, string path)
        {
            var url = PrismRuntimeJson.Url(PrismRuntimeJson.Text(artifact, "url") ?? throw new InvalidDataException("运行文件缺少下载 URL。"));
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (algorithm, length) in new[] { ("sha1", 40), ("sha256", 64), ("sha512", 128) })
            {
                var hash = PrismRuntimeJson.Text(artifact, algorithm);
                if (string.IsNullOrEmpty(hash)) continue;
                if (hash.Length != length || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("无效运行文件哈希：" + path);
                hashes[algorithm] = hash.ToLowerInvariant();
            }
            long? size = null;
            if (artifact.TryGetProperty("size", out var sizeJson))
            {
                if (!sizeJson.TryGetInt64(out var parsed) || parsed < 0) throw new InvalidDataException("无效运行文件大小。");
                if (parsed > 0) size = parsed;
            }
            if (hashes.Count == 0) _notes.Add("来源没有提供内容哈希，下载只能确认传输完成，不能声明内容校验通过：" + path);
            return new InstanceFileRequirement { Path = path, Source = InstanceFileSource.DirectDownload, Downloads = [url], Size = size, Hashes = hashes };
        }

        /// <summary>复制字符串数组，拒绝无法表示的参数类型。</summary>
        private static void AddStrings(JsonElement source, string key, List<string> target)
        {
            foreach (var item in PrismRuntimeJson.Array(source, key))
            {
                if (item.ValueKind != JsonValueKind.String) throw new InvalidDataException(key + " 必须包含字符串。");
                target.Add(item.GetString()!);
            }
        }

        /// <summary>保留现代游戏和 JVM 参数的边界，并按平台规则选择条件参数。</summary>
        private void AddArguments(JsonElement source, string key, List<string> target)
        {
            var first = target.Count;
            foreach (var item in PrismRuntimeJson.Array(source, key))
            {
                if (item.ValueKind == JsonValueKind.String) { target.Add(item.GetString()!); continue; }
                if (!PrismRuntimeJson.Active(item, os, architecture, _issues)) continue;
                if (!item.TryGetProperty("value", out var value)) throw new InvalidDataException("条件启动参数缺少 value。");
                if (value.ValueKind == JsonValueKind.String) target.Add(value.GetString()!);
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var part in value.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.String) throw new InvalidDataException("启动参数数组只能包含字符串。");
                        target.Add(part.GetString()!);
                    }
                else throw new InvalidDataException("不支持的条件启动参数格式。");
            }
            if (key == "jvm")
            {
                // 启动配置生成器统一提供这两个基础字段；仅去掉完全相同的占位参数，保留自定义值供后续验证。
                for (var index = target.Count - 1; index >= first; index--)
                {
                    if (target[index] == "-Djava.library.path=${natives_directory}") target.RemoveAt(index);
                    else if (target[index] == "${classpath}" && index > first && (target[index - 1] is "-cp" or "-classpath" or "--class-path"))
                    {
                        target.RemoveRange(index - 1, 2);
                        index--;
                    }
                }
            }
        }

        /// <summary>将游戏目录内的路径转换为实例相对路径，不依赖当前工作目录。</summary>
        private string GamePath(string relative) => InstanceImportPaths.Relative(Game == "." ? relative : Game + "/" + relative);

        /// <summary>检查本地来源是否存在；权限错误和目录链接不会被误当成缺少元数据。</summary>
        private bool Exists(string relative)
        {
            var path = InstanceImportPaths.Within(root, relative);
            InstanceImportPaths.EnsureNoLinks(path);
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0) throw new IOException("预期文件的位置是目录：" + relative);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        /// <summary>读取并登记原始元数据，供最终指纹使旧安装结果失效。</summary>
        private JsonElement ReadSource(string relative)
        {
            var (json, bytes) = PrismRuntimeJson.Read(InstanceImportPaths.Within(root, relative));
            _sources.Add((relative, bytes));
            return json;
        }

        /// <summary>拒绝不同依赖写入相同文件或文件与目录冲突，避免下载计划互相覆盖。</summary>
        private static void CheckFileLayout(IReadOnlyList<PrismRuntimeFile> files)
        {
            InstanceImportPaths.ValidateLayout(files.Select(file => (file.Requirement.Path!, false)));
        }

        /// <summary>对元数据原文、目录布局、平台及导入基线计算稳定指纹，不把当前时间加入其中。</summary>
        private string Fingerprint()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var text in new[] { "eml-prism-resolver-v1", Game, os, architecture, JsonSerializer.Serialize(manifest) })
            {
                hash.AppendData(Encoding.UTF8.GetBytes(text));
                hash.AppendData([0]);
            }
            foreach (var (name, bytes) in _sources)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(name));
                hash.AppendData([0]);
                hash.AppendData(bytes);
                hash.AppendData([0]);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }

    /// <summary>保留组件的明确版本、本地解析结果与依赖遍历状态。</summary>
    private sealed class Component(string id, string? requestedVersion)
    {
        internal string Id { get; } = id;
        internal string? RequestedVersion { get; } = requestedVersion;
        internal string? Version { get; set; }
        internal JsonElement? Json { get; set; }
        internal bool Expanded { get; set; }
    }
}
