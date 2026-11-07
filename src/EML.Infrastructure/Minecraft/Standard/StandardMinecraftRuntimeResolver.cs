using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Prism;

namespace EML.Infrastructure.Minecraft.Standard;

/// <summary>读取通用 ZIP 中已安装的标准版本目录，接入统一运行准备流程；不联网或扫描游戏内容。</summary>
public sealed class StandardMinecraftRuntimeResolver : IPrismRuntimeResolver
{
    private readonly string _os;
    private readonly string _architecture;
    private readonly string _osVersion;
    private readonly IReadOnlyDictionary<string, bool> _features;

    /// <summary>固定当前平台和功能条件，供库规则与启动参数规则共同使用。</summary>
    public StandardMinecraftRuntimeResolver(string? runtimeOs = null, string? runtimeArchitecture = null,
        IReadOnlyDictionary<string, bool>? features = null, string? runtimeOsVersion = null)
    {
        _os = runtimeOs ?? (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "unsupported");
        _architecture = runtimeArchitecture ?? RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64", Architecture.X86 => "x86", Architecture.Arm64 => "arm64", _ => "unsupported"
        };
        _osVersion = runtimeOsVersion ?? Environment.OSVersion.Version.ToString();
        _features = features is null ? new Dictionary<string, bool>() : new Dictionary<string, bool>(features, StringComparer.Ordinal);
        PrismRuntimeJson.Segment(_os);
        PrismRuntimeJson.Segment(_architecture);
    }

    /// <summary>只接受导入清单中的 ZIP 游戏目录，拒绝链接及无法唯一选择的版本。</summary>
    public PrismRuntimePlan Resolve(string instancePath, InstanceImportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        InstanceManifestStore.Validate(manifest);
        if (!manifest.SourceFormat.Equals("Zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("标准版本解析器只处理 Zip 实例。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instancePath));
        InstanceImportPaths.EnsureNoLinks(root);
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) throw new IOException("实例路径不是目录。");
        return new Resolution(root, manifest, _os, _architecture, _osVersion, _features).Build();
    }

    /// <summary>保存一次标准版本合并的局部状态，不缓存其他实例的选择。</summary>
    private sealed class Resolution(string root, InstanceImportManifest manifest, string os, string architecture,
        string osVersion, IReadOnlyDictionary<string, bool> features)
    {
        private readonly Dictionary<string, Profile> _profiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PrismRuntimeFile> _files = [];
        private readonly Dictionary<string, int> _libraryPositions = new(StringComparer.Ordinal);
        private readonly List<string> _issues = [];
        private readonly List<string> _notes = [];
        private readonly List<string> _gameArguments = [];
        private readonly List<string> _jvmArguments = [];
        private readonly Dictionary<string, InstanceFileRequirement> _baseline = manifest.Files.Where(file => file.Path is not null)
            .ToDictionary(file => file.Path!, StringComparer.OrdinalIgnoreCase);
        private string _mainClass = "";
        private string _minecraft = "";
        private string? _assetId;
        private string? _loggingArgument;
        private int _javaMajor = 8;

        /// <summary>选择唯一继承叶节点，按父到子合并配置，保留加载器主类和全部启动参数。</summary>
        internal PrismRuntimePlan Build()
        {
            if (os == "unsupported" || architecture == "unsupported") _issues.Add("当前操作系统或架构尚不支持运行准备。");
            LoadProfiles();
            var parents = _profiles.Values.Select(profile => PrismRuntimeJson.Text(profile.Json, "inheritsFrom"))
                .Where(value => value is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var leaves = _profiles.Values.Where(profile => !parents.Contains(profile.Id)).ToArray();
            if (leaves.Length != 1)
                throw new InvalidDataException(leaves.Length == 0 ? "没有唯一可启动的标准版本，可能存在循环继承。" :
                    "ZIP 包含多个独立版本，请将要启动的版本作为独立实例导入：" + string.Join("、", leaves.Select(profile => profile.Id)));
            var chain = Inheritance(leaves[0]);
            _minecraft = manifest.MinecraftVersion ?? chain[0].Id;
            string? jarId = null;
            JsonElement? client = null;
            foreach (var profile in chain)
            {
                var json = profile.Json;
                foreach (var field in new[] { "processors", "jarMods", "data" })
                    if (json.TryGetProperty(field, out var value) && !IsEmpty(value))
                        _issues.Add("标准版本仍包含未执行的安装步骤，不能当作已安装完成：" + field);
                if (PrismRuntimeJson.Text(json, "mainClass") is { Length: > 0 } main) _mainClass = main;
                if (PrismRuntimeJson.Text(json, "minecraftArguments") is { } legacy)
                { _gameArguments.Clear(); _gameArguments.AddRange(SplitLegacyArguments(legacy)); }
                if (json.TryGetProperty("arguments", out var arguments))
                { AddArguments(arguments, "game", _gameArguments); AddArguments(arguments, "jvm", _jvmArguments); }
                foreach (var library in PrismRuntimeJson.Array(json, "libraries")) AddLibrary(library);
                if (json.TryGetProperty("javaVersion", out var java))
                {
                    if (!java.TryGetProperty("majorVersion", out var major) || !major.TryGetInt32(out _javaMajor) || _javaMajor is < 8 or > 100)
                        throw new InvalidDataException("标准版本的 Java 主版本无效。");
                }
                if (json.TryGetProperty("downloads", out var downloads) && downloads.TryGetProperty("client", out var declaredClient))
                { client = declaredClient; jarId = profile.Id; }
                else if (jarId is null) jarId = profile.Id;
                if (PrismRuntimeJson.Text(json, "jar") is { } redirected)
                {
                    jarId = VersionSegment(redirected);
                    client = _profiles.TryGetValue(jarId, out var owner) && owner.Json.TryGetProperty("downloads", out var ownerDownloads) &&
                        ownerDownloads.TryGetProperty("client", out var ownerClient) ? ownerClient : null;
                }
                if (json.TryGetProperty("assetIndex", out var asset))
                {
                    _assetId = VersionSegment(PrismRuntimeJson.Text(asset, "id") ?? throw new InvalidDataException("资源索引缺少 id。"));
                    SetSingleton(new(Artifact(asset, GamePath("assets/indexes/" + _assetId + ".json")), PrismRuntimeFileRole.AssetIndex));
                }
                else if (_assetId is null && PrismRuntimeJson.Text(json, "assets") is { } assets)
                {
                    _assetId = VersionSegment(assets);
                    SetSingleton(new(LocalRequirement(GamePath("assets/indexes/" + _assetId + ".json")), PrismRuntimeFileRole.AssetIndex));
                }
                if (json.TryGetProperty("logging", out var logging) && logging.TryGetProperty("client", out var log))
                {
                    if (PrismRuntimeJson.Text(log, "type") is { } type && type != "log4j2-xml") _issues.Add("不支持的日志配置类型：" + type);
                    if (!log.TryGetProperty("file", out var logFile)) throw new InvalidDataException("日志配置缺少 file。");
                    var id = VersionSegment(PrismRuntimeJson.Text(logFile, "id") ?? throw new InvalidDataException("日志配置缺少 id。"));
                    _loggingArgument = PrismRuntimeJson.Text(log, "argument") ?? throw new InvalidDataException("日志配置缺少参数。");
                    SetSingleton(new(Artifact(logFile, GamePath("assets/log_configs/" + id)), PrismRuntimeFileRole.Logging));
                }
            }
            var jarPath = GamePath($"versions/{jarId}/{jarId}.jar");
            _files.Add(new(client is { } artifact ? Artifact(artifact, jarPath) : LocalRequirement(jarPath), PrismRuntimeFileRole.MainJar)
                { OnClassPath = true });
            if (_mainClass.Length == 0) _issues.Add("标准版本没有声明启动主类。");
            if (_assetId is null) _issues.Add("标准版本没有可验证的资源索引。");
            InstanceImportPaths.ValidateLayout(_files.Select(file => (file.Requirement.Path!, false)));
            _notes.Add("使用 ZIP 中唯一的标准版本 " + leaves[0].Id + "；只按其固定依赖准备运行环境。");
            return new()
            {
                InstancePath = root, SourceFormat = "Zip", VersionIdPrefix = "EML-Standard", GameDirectory = manifest.GameDirectory,
                MinecraftVersion = _minecraft, RuntimeOs = os, RuntimeArchitecture = architecture, MainClass = _mainClass,
                GameArguments = _gameArguments, JvmArguments = _jvmArguments, CompatibleJavaMajors = [_javaMajor],
                AssetIndexId = _assetId, LoggingArgument = _loggingArgument, Files = _files,
                BlockingIssues = _issues.Distinct().ToArray(), Notes = _notes.Distinct().ToArray(), Fingerprint = Fingerprint()
            };
        }

        /// <summary>仅枚举一层版本描述，忽略 EML 派生版本并限制元数据总量。</summary>
        private void LoadProfiles()
        {
            var versions = InstanceImportPaths.Within(root, GamePath("versions"));
            InstanceImportPaths.EnsureNoLinks(versions);
            if (!Directory.Exists(versions)) throw new DirectoryNotFoundException("ZIP 中没有 versions 目录，请导入完整的标准 Minecraft 实例。");
            var count = 0;
            long bytesRead = 0;
            foreach (var directory in Directory.EnumerateDirectories(versions).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (++count > 1024) throw new InvalidDataException("实例版本目录过多。");
                InstanceImportPaths.EnsureNoLinks(directory);
                var id = VersionSegment(Path.GetFileName(directory));
                var path = Path.Combine(directory, id + ".json");
                if (!File.Exists(path)) continue;
                var (json, bytes) = PrismRuntimeJson.Read(path);
                if ((bytesRead += bytes.LongLength) > 32 * 1024 * 1024) throw new InvalidDataException("标准版本元数据总量过大。");
                if (IsGeneratedLaunchProfile(json)) continue;
                if (PrismRuntimeJson.Text(json, "id") is { } declared && declared != id)
                    throw new InvalidDataException("版本描述 id 与目录不一致：" + id);
                _profiles.Add(id, new(id, GamePath($"versions/{id}/{id}.json"), json, bytes));
            }
            if (_profiles.Count == 0) throw new InvalidDataException("ZIP 中没有可解析的标准版本描述。");
        }

        /// <summary>仅凭两项有效生成标记识别派生版本，不把 EML 前缀或单个自定义字段当作生成依据。</summary>
        private static bool IsGeneratedLaunchProfile(JsonElement json) =>
            json.TryGetProperty("emlRuntimeFingerprint", out var hash) && hash.ValueKind == JsonValueKind.String &&
            hash.GetString() is { Length: 64 } fingerprint && fingerprint.All(Uri.IsHexDigit) &&
            json.TryGetProperty("emlLaunchProfile", out var path) && path.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(path.GetString());

        /// <summary>沿继承关系读取已安装的父版本，拒绝缺失父项、循环和过深继承。</summary>
        private IReadOnlyList<Profile> Inheritance(Profile leaf)
        {
            var result = new List<Profile>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = leaf;
            while (true)
            {
                if (!visited.Add(current.Id) || result.Count >= 32) throw new InvalidDataException("版本继承循环或层数过多。");
                result.Add(current);
                if (PrismRuntimeJson.Text(current.Json, "inheritsFrom") is not { } parent) break;
                parent = VersionSegment(parent);
                if (!_profiles.TryGetValue(parent, out current!)) throw new InvalidDataException("缺少继承的标准版本：" + parent);
            }
            result.Reverse();
            return result;
        }

        /// <summary>同时解析普通库与本机库；优先使用元数据声明的摘要，未声明摘要时才采用导入基线。</summary>
        private void AddLibrary(JsonElement library)
        {
            if (!Active(library)) return;
            var name = PrismRuntimeJson.Text(library, "name") ?? throw new InvalidDataException("类库缺少 Maven 名称。");
            var maven = PrismMavenName.Parse(name);
            var hasDownloads = library.TryGetProperty("downloads", out var downloads);
            var hasArtifact = hasDownloads && downloads.TryGetProperty("artifact", out _);
            var path = GamePath("libraries/" + maven.RelativePath);
            if (hasDownloads && downloads.TryGetProperty("artifact", out var artifact))
                PutLibrary(new(Artifact(artifact, path), PrismRuntimeFileRole.Library) { MavenName = name, OnClassPath = true });
            else if (!hasDownloads)
            {
                var hashes = ReadHashes(library);
                InstanceFileRequirement requirement;
                if (hashes.Count == 0 && HasLocalBaseline(path)) requirement = LocalRequirement(path);
                else
                {
                    var repository = PrismRuntimeJson.Url(PrismRuntimeJson.Text(library, "url") ?? "https://libraries.minecraft.net/");
                    if (!repository.EndsWith('/') || new Uri(repository).Query.Length != 0 || new Uri(repository).Fragment.Length != 0)
                        throw new InvalidDataException("Maven 仓库地址必须是无查询参数的目录 URL。");
                    requirement = new() { Path = path, Source = InstanceFileSource.DirectDownload, Size = ReadSize(library), Hashes = hashes,
                        Downloads = [new Uri(new Uri(repository), maven.RelativePath).AbsoluteUri] };
                }
                PutLibrary(new(requirement, PrismRuntimeFileRole.Library) { MavenName = name, OnClassPath = true });
            }
            if (!library.TryGetProperty("natives", out var natives))
            {
                if (hasDownloads && !hasArtifact) throw new InvalidDataException("类库缺少 artifact 或 natives：" + name);
                return;
            }
            var classifier = PrismRuntimeJson.Text(natives, os + "-" + architecture);
            if (classifier is null && architecture is "x86" or "x86_64") classifier = PrismRuntimeJson.Text(natives, os);
            if (classifier is null)
            {
                if (PrismRuntimeJson.Text(natives, os) is not null) _issues.Add("本机库没有当前架构的版本：" + name);
                return;
            }
            if (architecture is not ("x86" or "x86_64") && classifier.Contains("${arch}", StringComparison.Ordinal))
            { _issues.Add("本机库的 arch 占位符不支持当前架构：" + name); return; }
            classifier = PrismRuntimeJson.Segment(classifier.Replace("${arch}", architecture == "x86" ? "32" : "64", StringComparison.Ordinal));
            var nativeMaven = maven with { Classifier = classifier };
            var nativePath = GamePath("libraries/" + nativeMaven.RelativePath);
            var nativeRequirement = hasDownloads && downloads.TryGetProperty("classifiers", out var classifiers) && classifiers.TryGetProperty(classifier, out var native)
                ? Artifact(native, nativePath) : LocalRequirement(nativePath);
            var excludes = new List<string>();
            if (library.TryGetProperty("extract", out var extract))
                foreach (var exclusion in PrismRuntimeJson.Array(extract, "exclude"))
                {
                    if (exclusion.ValueKind != JsonValueKind.String) throw new InvalidDataException("本机库排除项必须为字符串。");
                    var raw = exclusion.GetString()!;
                    var safe = InstanceImportPaths.Relative(raw.TrimEnd('/'));
                    excludes.Add(raw.EndsWith('/') ? safe + "/" : safe);
                }
            PutLibrary(new(nativeRequirement, PrismRuntimeFileRole.NativeArchive)
            { MavenName = $"{maven.Group}:{maven.Artifact}:{maven.Version}:{classifier}", ExtractExcludes = excludes });
        }

        /// <summary>子版本覆盖同身份的父版本依赖，始终保留一份来源要求，不拼接不同来源哈希。</summary>
        private void PutLibrary(PrismRuntimeFile file)
        {
            var identity = file.Role + ":" + PrismMavenName.Parse(file.MavenName!).Identity;
            if (_libraryPositions.TryGetValue(identity, out var position)) _files[position] = file;
            else { _libraryPositions[identity] = _files.Count; _files.Add(file); }
        }

        /// <summary>更新可继承的唯一资源或日志声明，旧索引不再参与当前运行计划。</summary>
        private void SetSingleton(PrismRuntimeFile file)
        {
            var index = _files.FindIndex(value => value.Role == file.Role);
            if (index >= 0) _files[index] = file; else _files.Add(file);
        }

        /// <summary>按顺序处理平台与布尔功能规则，未知功能默认关闭。</summary>
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
                        if (property.Name is not ("name" or "arch" or "version")) throw new InvalidDataException("不支持的平台规则：" + property.Name);
                    if (PrismRuntimeJson.Text(system, "name") is { } name) matches &= name == os;
                    if (PrismRuntimeJson.Text(system, "arch") is { } arch)
                        matches &= arch switch { "x86_64" or "amd64" => architecture == "x86_64", "arm64" or "aarch64" => architecture == "arm64", _ => architecture == arch };
                    if (PrismRuntimeJson.Text(system, "version") is { } version)
                        matches &= Regex.IsMatch(osVersion, version, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                }
                if (rule.TryGetProperty("features", out var conditions))
                    foreach (var condition in conditions.EnumerateObject())
                    {
                        if (condition.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("功能规则必须为布尔值。");
                        matches &= (features.TryGetValue(condition.Name, out var enabled) && enabled) == condition.Value.GetBoolean();
                    }
                var action = PrismRuntimeJson.Text(rule, "action");
                if (action is not ("allow" or "disallow")) throw new InvalidDataException("不支持的规则 action。");
                if (matches) allowed = action == "allow";
            }
            return allowed;
        }

        /// <summary>保留参数边界并移除统一生成器会添加的 classpath 和 natives 参数。</summary>
        private void AddArguments(JsonElement source, string name, List<string> target)
        {
            foreach (var entry in PrismRuntimeJson.Array(source, name))
            {
                if (entry.ValueKind == JsonValueKind.String) { target.Add(entry.GetString()!); continue; }
                if (entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException("启动参数格式无效。");
                if (!Active(entry)) continue;
                if (!entry.TryGetProperty("value", out var value)) throw new InvalidDataException("条件参数缺少 value。");
                if (value.ValueKind == JsonValueKind.String) target.Add(value.GetString()!);
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var part in value.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.String) throw new InvalidDataException("参数数组必须为字符串。");
                        target.Add(part.GetString()!);
                    }
                else throw new InvalidDataException("条件参数 value 格式无效。");
            }
            if (name != "jvm") return;
            for (var index = target.Count - 1; index >= 0; index--)
                if (target[index] == "-Djava.library.path=${natives_directory}") target.RemoveAt(index);
                else if (target[index] == "${classpath}" && index > 0 && target[index - 1] is "-cp" or "-classpath" or "--class-path")
                { target.RemoveRange(index - 1, 2); index--; }
        }

        /// <summary>转换标准 artifact；官方摘要独立使用，不与 ZIP 基线合并。</summary>
        private InstanceFileRequirement Artifact(JsonElement artifact, string path)
        {
            var hashes = ReadHashes(artifact);
            var url = PrismRuntimeJson.Text(artifact, "url");
            if (hashes.Count == 0 && HasLocalBaseline(path)) return LocalRequirement(path);
            if (string.IsNullOrWhiteSpace(url))
            {
                if (hashes.Count == 0) return LocalRequirement(path);
                return new() { Path = path, Source = InstanceFileSource.DirectDownload, Size = ReadSize(artifact), Hashes = hashes };
            }
            return new() { Path = path, Source = InstanceFileSource.DirectDownload, Downloads = [PrismRuntimeJson.Url(url)],
                Size = ReadSize(artifact), Hashes = hashes };
        }

        /// <summary>本地自定义运行文件必须已有导入基线，不能用当前字节重新建立信任。</summary>
        private InstanceFileRequirement LocalRequirement(string path)
        {
            if (!_baseline.TryGetValue(path, out var baseline) || baseline.Source != InstanceFileSource.Embedded ||
                baseline.Size is null || !baseline.Hashes.ContainsKey("sha256"))
                throw new InvalidDataException("本地运行文件没有可信导入基线：" + path);
            return baseline;
        }

        /// <summary>检查是否存在可用的原包 SHA-256 基线，不读取目标文件。</summary>
        private bool HasLocalBaseline(string path) => _baseline.TryGetValue(path, out var baseline) &&
            baseline.Source == InstanceFileSource.Embedded && baseline.Size is not null && baseline.Hashes.ContainsKey("sha256");

        /// <summary>解析已知 SHA 摘要，拒绝无效长度或非十六进制内容。</summary>
        private static IReadOnlyDictionary<string, string> ReadHashes(JsonElement value)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, length) in new[] { ("sha1", 40), ("sha256", 64), ("sha512", 128) })
            {
                var hash = PrismRuntimeJson.Text(value, name);
                if (string.IsNullOrEmpty(hash)) continue;
                if (hash.Length != length || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("运行文件摘要无效：" + name);
                result[name] = hash.ToLowerInvariant();
            }
            return result;
        }

        /// <summary>将历史零大小视为未知，其余大小必须是非负整数。</summary>
        private static long? ReadSize(JsonElement value)
        {
            if (!value.TryGetProperty("size", out var size)) return null;
            if (!size.TryGetInt64(out var number) || number < 0) throw new InvalidDataException("运行文件大小无效。");
            return number == 0 ? null : number;
        }

        /// <summary>安全支持中文及带空格的本地版本目录，禁止路径分隔符及越界片段。</summary>
        private static string VersionSegment(string value)
        {
            var safe = InstanceImportPaths.Relative(value);
            if (safe.Contains('/')) throw new InvalidDataException("版本标识不能包含路径：" + value);
            return safe;
        }

        /// <summary>将游戏内路径转换为实例根相对路径。</summary>
        private string GamePath(string path) => InstanceImportPaths.Relative(manifest.GameDirectory == "." ? path : manifest.GameDirectory + "/" + path);

        /// <summary>允许空安装步骤声明，非空步骤必须由专用安装器处理。</summary>
        private static bool IsEmpty(JsonElement value) => value.ValueKind == JsonValueKind.Null ||
            value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0 ||
            value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any();

        /// <summary>解析旧版命令行的引号和空白，不经过 shell，不展开玩家参数。</summary>
        private static IReadOnlyList<string> SplitLegacyArguments(string text)
        {
            if (text.Length > 1024 * 1024) throw new InvalidDataException("旧版启动参数过长。");
            var result = new List<string>();
            var current = new StringBuilder();
            var quote = '\0';
            var started = false;
            for (var i = 0; i < text.Length; i++)
            {
                var value = text[i];
                if (value < 32 && !char.IsWhiteSpace(value) || value is '\r' or '\n') throw new InvalidDataException("启动参数包含控制字符。");
                if (value == '\\' && quote != '\0' && i + 1 < text.Length && (text[i + 1] == quote || text[i + 1] == '\\'))
                { current.Append(text[++i]); started = true; continue; }
                if (quote != '\0') { if (value == quote) quote = '\0'; else current.Append(value); continue; }
                if (value is '\'' or '"') { quote = value; started = true; continue; }
                if (char.IsWhiteSpace(value))
                { if (started) { result.Add(current.ToString()); current.Clear(); started = false; } }
                else { current.Append(value); started = true; }
            }
            if (quote != '\0') throw new InvalidDataException("旧版启动参数引号未闭合。");
            if (started) result.Add(current.ToString());
            return result;
        }

        /// <summary>只对描述、导入基线及平台求指纹；不散列全部模组、配置或资源内容。</summary>
        private string Fingerprint()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var value in new[] { "eml-standard-resolver-v1", os, architecture, osVersion, JsonSerializer.Serialize(manifest),
                JsonSerializer.Serialize(features.OrderBy(pair => pair.Key, StringComparer.Ordinal)) })
            { hash.AppendData(Encoding.UTF8.GetBytes(value)); hash.AppendData([0]); }
            foreach (var profile in _profiles.Values.OrderBy(profile => profile.Id, StringComparer.Ordinal))
            { hash.AppendData(Encoding.UTF8.GetBytes(profile.Path)); hash.AppendData([0]); hash.AppendData(profile.Bytes); hash.AppendData([0]); }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        /// <summary>保留版本描述原始字节，用于确定合并顺序和生成状态的有效性。</summary>
        private sealed record Profile(string Id, string Path, JsonElement Json, byte[] Bytes);
    }
}
