using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.CurseForge;

/// <summary>保存固定文件的解析结果及官方人工下载入口；未解析项仍保留原项目和文件 ID。</summary>
internal sealed record CurseForgeResolvedFile
{
    public InstanceFileRequirement Requirement { get; init; } = new();
    /// <summary>经导入清单确认的原包优先内容；其校验依据独立于平台文件的 Requirement。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InstanceFileRequirement? EmbeddedOverride { get; init; }
    public int? ClassId { get; init; }
    public string? FileName { get; init; }
    public string? ManualDownloadUrl { get; init; }
    public string? BlockReason { get; init; }
    public CurseForgeModrinthFile? ModrinthFallback { get; init; }
    public string? FallbackMessage { get; init; }
}

/// <summary>独立于导入清单保存 API 解析快照；指纹变化后不得复用旧文件映射。</summary>
internal sealed record CurseForgeFileResolution
{
    public int SchemaVersion { get; init; } = 2;
    public string Fingerprint { get; init; } = "";
    public IReadOnlyList<CurseForgeResolvedFile> Files { get; init; } = [];
}

/// <summary>只读检查和下载流程共用的 CurseForge 文件缓存，不改写原 eml-instance.json。</summary>
internal static class CurseForgeFileStateStore
{
    internal const string RelativePath = ".eml-runtime/curseforge/files.json";
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 64,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    /// <summary>仅返回当前清单对应的缓存；不存在或来源改变返回 null，损坏缓存拒绝使用。</summary>
    internal static CurseForgeFileResolution? Read(string instanceRoot, InstanceImportManifest manifest)
    {
        var path = InstanceImportPaths.Within(instanceRoot, RelativePath);
        InstanceImportPaths.EnsureNoLinks(path);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumBytes) throw new InvalidDataException("CurseForge 文件缓存超过 8 MiB。");
            var state = JsonSerializer.Deserialize<CurseForgeFileResolution>(input, Options)
                ?? throw new InvalidDataException("CurseForge 文件缓存为空。");
            if (state.SchemaVersion is not (1 or 2) || state.Fingerprint is null || state.Fingerprint.Length != 64 ||
                !state.Fingerprint.All(Uri.IsHexDigit)) throw new InvalidDataException("CurseForge 文件缓存格式无效。");
            if (state.Fingerprint != Fingerprint(manifest)) return null;
            state = UpgradeLegacy(manifest, state);
            Validate(manifest, state);
            foreach (var file in state.Files.Where(file => file.Requirement.Path is not null))
                InstanceImportPaths.EnsureNoLinks(InstanceImportPaths.Within(instanceRoot, file.Requirement.Path!));
            return state;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException ex) { throw new InvalidDataException("CurseForge 文件缓存 JSON 损坏。", ex); }
    }

    /// <summary>再次确认导入声明未变后原子替换自有缓存，失败时清理本次临时文件。</summary>
    internal static async Task WriteAsync(string instanceRoot, InstanceImportManifest manifest,
        CurseForgeFileResolution state, CancellationToken token)
    {
        Validate(manifest, state);
        EnsureManifestUnchanged(instanceRoot, state.Fingerprint);
        var path = InstanceImportPaths.Within(instanceRoot, RelativePath);
        InstanceImportPaths.EnsureNoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("CurseForge 文件缓存超过 8 MiB。");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            EnsureManifestUnchanged(instanceRoot, state.Fingerprint);
            InstanceImportPaths.EnsureNoLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                InstanceImportPaths.EnsureNoLinks(temporary);
                File.Delete(temporary);
            }
        }
    }

    /// <summary>对全部导入声明生成来源指纹，文件 ID、必需标记及游戏目录变化都会使缓存失效。</summary>
    internal static string Fingerprint(InstanceImportManifest manifest) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest)));

    /// <summary>安装阶段重复核对声明，避免下载期间切换文件列表后误报告完成。</summary>
    internal static void EnsureManifestUnchanged(string instanceRoot, string fingerprint)
    {
        var current = InstanceManifestStore.Read(Path.Combine(instanceRoot, InstanceImportManifest.FileName));
        if (Fingerprint(current) != fingerprint) throw new IOException("准备过程中实例清单发生变化，请重新补全。");
    }

    /// <summary>按项目类别和明确的全局数据包加载配置确定目录，不根据 JAR 或 ZIP 扩展名猜测用途。</summary>
    internal static string TargetPath(InstanceImportManifest manifest, int classId, string fileName)
    {
        var name = InstanceImportPaths.Relative(fileName);
        if (name.Contains('/')) throw new InvalidDataException("CurseForge 文件名必须是单个安全文件名。");
        var directory = classId switch
        {
            6 => "mods", 12 => "resourcepacks", 6552 => "shaderpacks",
            6945 when HasPaxiDataPackDirectory(manifest) => "config/paxi/datapacks",
            6945 => throw new InvalidDataException("数据包需要明确的数据包加载目录；请确认整合包声明了 Paxi 及其加载顺序配置。"),
            _ => throw new InvalidDataException($"暂不支持 CurseForge 项目类别 {classId}，无法确定安全安装目录。")
        };
        var gameDirectory = manifest.GameDirectory;
        return InstanceImportPaths.Relative((gameDirectory == "." ? "" : gameDirectory + "/") + directory + "/" + name);
    }

    /// <summary>只有固定 Paxi 项目和包内加载顺序文件同时存在，才确认数据包应放入其全局目录。</summary>
    internal static bool HasPaxiDataPackDirectory(InstanceImportManifest manifest)
    {
        // 作者官方项目：Paxi Fabric 为 418881；Paxi Forge/NeoForge 为 515708。
        // 两个项目明确约定 config/paxi/datapacks，并支持直接读取 ZIP 数据包。
        var hasLoader = manifest.Files.Any(file => file.Required && file.Source == InstanceFileSource.CurseForge &&
            file.ProjectId is 418881 or 515708);
        var marker = (manifest.GameDirectory == "." ? "" : manifest.GameDirectory + "/") + "config/paxi/datapack_load_order.json";
        return hasLoader && manifest.Files.Any(file => file.Required && file.Source == InstanceFileSource.Embedded &&
            string.Equals(file.Path, marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>限定人工下载入口为 CurseForge 官方 HTTPS 网页，不接受任意跳转地址。</summary>
    internal static bool IsOfficialPage(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort &&
        (uri.Host.Equals("www.curseforge.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("curseforge.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>按原包内容优先生成安装计划：保留 Embedded 需求，屏蔽同路径的平台下载。</summary>
    internal static IReadOnlyList<InstanceFileRequirement> EffectiveRequirements(InstanceImportManifest manifest,
        CurseForgeFileResolution state)
    {
        Validate(manifest, state);
        return SelectRequirements(manifest, state);
    }

    /// <summary>映射已验证的来源，确保每个目标只有一份生效的完整性要求。</summary>
    private static IReadOnlyList<InstanceFileRequirement> SelectRequirements(InstanceImportManifest manifest,
        CurseForgeFileResolution state)
    {
        var entries = state.Files.ToDictionary(entry => (entry.Requirement.ProjectId, entry.Requirement.FileId));
        var embedded = state.Files.Where(entry => entry.EmbeddedOverride is not null)
            .ToDictionary(entry => entry.EmbeddedOverride!.Path!, entry => entry.EmbeddedOverride!, StringComparer.OrdinalIgnoreCase);
        return manifest.Files
            .Where(file => file.Source != InstanceFileSource.CurseForge || !file.Required ||
                !entries.TryGetValue((file.ProjectId, file.FileId), out var entry) || entry.EmbeddedOverride is null)
            .Select(file => file.Source == InstanceFileSource.Embedded && file.Path is { } path && embedded.TryGetValue(path, out var baseline)
                ? baseline
                : file.Source == InstanceFileSource.CurseForge && file.Required && entries.TryGetValue((file.ProjectId, file.FileId), out var entry)
                    ? entry.Requirement : file).ToArray();
    }

    /// <summary>只接受导入清单中带原 ZIP 条目、大小和 SHA-256 的包内文件；不从路径名称推测来源。</summary>
    internal static InstanceFileRequirement? ImportedOverride(InstanceImportManifest manifest, InstanceFileRequirement official)
    {
        if (official.Path is null) return null;
        var embedded = manifest.Files.SingleOrDefault(file => file.Source == InstanceFileSource.Embedded &&
            string.Equals(file.Path, official.Path, StringComparison.OrdinalIgnoreCase));
        if (embedded is null) return null;
        if (embedded.Origin == InstanceFileOrigin.UserLocalImport)
        {
            _ = InstanceFileIntegrity.HashesFor(embedded);
            return embedded with { Required = embedded.Required || official.Required, CurseForgeOrigin = OfficialOrigin(official) };
        }
        if (string.IsNullOrWhiteSpace(embedded.ArchiveEntry) || embedded.Size is null ||
            !embedded.Hashes.TryGetValue("sha256", out var sha256) ||
            embedded.Origin is not (null or InstanceFileOrigin.EmbeddedOverride))
            throw new InvalidDataException("同路径文件缺少可确认的原包条目、大小或 SHA-256 导入基线，已保留原文件。");
        return embedded with
        {
            Origin = InstanceFileOrigin.EmbeddedOverride,
            Required = embedded.Required || official.Required,
            Hashes = new Dictionary<string, string> { ["sha256"] = sha256 },
            Downloads = [], CurseForgeOrigin = OfficialOrigin(official)
        };
    }

    /// <summary>平台身份和官方摘要仅用于溯源，不能成为另一份原包内容的强制校验条件。</summary>
    internal static CurseForgeFileOrigin OfficialOrigin(InstanceFileRequirement official) => new()
    {
        ProjectId = official.ProjectId!.Value, FileId = official.FileId!.Value, Size = official.Size,
        Hashes = official.Hashes.TryGetValue("sha1", out var sha1)
            ? new Dictionary<string, string> { ["sha1"] = sha1 } : new Dictionary<string, string>()
    };

    /// <summary>先核验旧缓存确实保留导入基线，再在内存拆分来源；不改写原实例清单或旧缓存。</summary>
    private static CurseForgeFileResolution UpgradeLegacy(InstanceImportManifest manifest, CurseForgeFileResolution state)
    {
        if (state.SchemaVersion != 1) return state;
        Validate(manifest, state, legacy: true);
        var upgraded = state with
        {
            SchemaVersion = 2,
            Files = state.Files.Select(entry =>
            {
                var requirement = entry.Requirement with
                {
                    Origin = entry.ModrinthFallback is null ? InstanceFileOrigin.CurseForgeDownload : InstanceFileOrigin.ModrinthDownload,
                    Hashes = entry.Requirement.Hashes.Where(pair => pair.Key == "sha1" ||
                        pair.Key == "sha512" && entry.ModrinthFallback?.Sha512 is not null)
                        .ToDictionary(pair => pair.Key, pair => pair.Value),
                    CurseForgeOrigin = entry.ModrinthFallback is null ? null : OfficialOrigin(entry.Requirement)
                };
                var embedded = ImportedOverride(manifest, requirement);
                // 旧缓存可能给原包内容选了平台备用文件；原包优先后不再携带该下载地址。
                return entry with
                {
                    Requirement = embedded is null ? requirement : requirement with
                    {
                        Origin = InstanceFileOrigin.CurseForgeDownload, Downloads = [], CurseForgeOrigin = null,
                        Hashes = OfficialOrigin(requirement).Hashes
                    },
                    EmbeddedOverride = embedded,
                    ModrinthFallback = embedded is null ? entry.ModrinthFallback : null,
                    FallbackMessage = embedded is null ? entry.FallbackMessage : null
                };
            }).ToArray()
        };
        return upgraded;
    }

    /// <summary>校验固定平台引用和独立原包基线，拒绝未知同路径冲突、缓存篡改及跨来源摘要混合。</summary>
    private static void Validate(InstanceImportManifest manifest, CurseForgeFileResolution state, bool legacy = false)
    {
        InstanceManifestStore.Validate(manifest);
        if (!manifest.SourceFormat.Equals("CurseForge", StringComparison.OrdinalIgnoreCase) ||
            state.SchemaVersion != (legacy ? 1 : 2) || state.Fingerprint != Fingerprint(manifest) || state.Files is null)
            throw new InvalidDataException("CurseForge 缓存与当前导入清单不一致。");
        var source = manifest.Files.Where(file => file.Source == InstanceFileSource.CurseForge && file.Required).ToArray();
        var expected = source.Select(file => (file.ProjectId, file.FileId)).ToHashSet();
        if (expected.Count != source.Length || state.Files.Count != source.Length)
            throw new InvalidDataException("CurseForge 文件引用重复或缓存条目不完整。");
        foreach (var entry in state.Files)
        {
            if (entry?.Requirement is not { } file || file.Source != InstanceFileSource.CurseForge || !file.Required ||
                !expected.Remove((file.ProjectId, file.FileId)) || file.Downloads is null || file.Hashes is null ||
                file.ArchiveEntry is not null || file.Downloads.Count > 1 || !IsOfficialPage(entry.ManualDownloadUrl))
                throw new InvalidDataException("CurseForge 文件缓存含未知条目或无效下载入口。");
            if (legacy ? file.Origin is not null || file.CurseForgeOrigin is not null || entry.EmbeddedOverride is not null
                : file.Origin != (entry.ModrinthFallback is null ? InstanceFileOrigin.CurseForgeDownload : InstanceFileOrigin.ModrinthDownload))
                throw new InvalidDataException("CurseForge 缓存文件来源与版本或下载依据不符。");
            if (entry.BlockReason is { Length: > 4096 }) throw new InvalidDataException("CurseForge 缓存问题描述过长。");
            if (entry.FallbackMessage is { Length: > 4096 }) throw new InvalidDataException("备用来源提示过长。");
            if (entry.ModrinthFallback is null && file.Downloads.Any(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Modrinth 下载缓存缺少精确文件来源依据。");
            if (entry.ModrinthFallback is { } fallback &&
                (entry.BlockReason is not null || !CurseForgeModrinthFallbackResolver.IsValidSource(fallback) ||
                 file.Downloads.Count != 1 || file.Downloads[0] != fallback.DownloadUrl || file.Size != fallback.Size ||
                 !file.Hashes.TryGetValue("sha1", out var sha1) || !fallback.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase) ||
                 (fallback.Sha512 is not null && (!file.Hashes.TryGetValue("sha512", out var sha512) ||
                     !fallback.Sha512.Equals(sha512, StringComparison.OrdinalIgnoreCase)))))
                throw new InvalidDataException("Modrinth 备用来源与原文件校验依据不一致。");
            if (file.Path is not null)
            {
                if (entry.ClassId is not { } classId || entry.FileName is null ||
                    file.Path != TargetPath(manifest, classId, entry.FileName))
                    throw new InvalidDataException("CurseForge 缓存目标不符合项目类别。");
            }
            if (entry.BlockReason is null && (file.Path is null || file.Size is null ||
                entry.EmbeddedOverride is null && !file.Hashes.ContainsKey("sha1")))
                throw new InvalidDataException("CurseForge 可用文件缓存缺少目标、大小或 SHA-1。");
            if (entry.BlockReason is not null && file.Downloads.Count != 0)
                throw new InvalidDataException("不可解析的 CurseForge 文件不能携带自动下载地址。");
        }
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in state.Files)
        {
            var file = entry.Requirement;
            var declared = source.Single(item => item.ProjectId == file.ProjectId && item.FileId == file.FileId);
            EnsureOfficialBaseline(declared, file);
            if (file.Path is { } path && !targets.Add(path))
                throw new InvalidDataException("CurseForge 平台文件指向重复目标。");
            var embedded = ImportedOverride(manifest, file);
            if (legacy)
            {
                if (embedded is not null && (entry.ClassId != 6945 || entry.BlockReason is not null ||
                    embedded.Size != file.Size || !file.Hashes.TryGetValue("sha256", out var hash) ||
                    !hash.Equals(embedded.Hashes["sha256"], StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("旧缓存缺少同路径原包文件的完整导入基线。");
            }
            else
            {
                if (!SameBaseline(embedded, entry.EmbeddedOverride))
                    throw new InvalidDataException("CurseForge 缓存的原包覆盖文件基线与导入记录不符。");
                if (embedded is not null && (entry.ModrinthFallback is not null || file.Downloads.Count != 0))
                    throw new InvalidDataException("原包覆盖文件不能自动采用平台文件替代。");
                if (file.CurseForgeOrigin is not null &&
                    (entry.ModrinthFallback is null || !SameOfficialOrigin(file.CurseForgeOrigin, OfficialOrigin(file))))
                    throw new InvalidDataException("平台备用文件的 CurseForge 来源元数据不符。");
            }
            if (file.Hashes.Keys.Any(key => key != "sha1" &&
                !(key == "sha512" && entry.ModrinthFallback?.Sha512 is not null) &&
                !(legacy && embedded is not null && key == "sha256")))
                throw new InvalidDataException("平台文件校验混入了其他来源的摘要。");
        }
        // 通用验证仍检查官方条目的摘要格式、大小和 URL；再检查生效计划的文件目录冲突。
        InstanceManifestStore.Validate(manifest with
        {
            Files = state.Files.Select(entry => entry.Requirement).ToArray()
        });
        if (!legacy) InstanceManifestStore.Validate(manifest with { Files = SelectRequirements(manifest, state) });
    }

    /// <summary>备份保留的官方依据不能被后续解析或缓存悄悄改写；原包覆盖只影响生效来源。</summary>
    internal static void EnsureOfficialBaseline(InstanceFileRequirement original, InstanceFileRequirement resolved)
    {
        if (original.Path is not null && !string.Equals(original.Path, resolved.Path, StringComparison.OrdinalIgnoreCase) ||
            original.Size is { } size && resolved.Size != size ||
            original.Hashes.TryGetValue("sha1", out var sha1) &&
            (!resolved.Hashes.TryGetValue("sha1", out var actual) || !sha1.Equals(actual, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("平台解析结果与清单保留的官方路径、大小或 SHA-1 不一致。");
    }

    /// <summary>逐项对照原包基线，缓存不得改变路径、ZIP 条目、大小或独立的 SHA-256。</summary>
    private static bool SameBaseline(InstanceFileRequirement? expected, InstanceFileRequirement? actual)
    {
        if (expected is null || actual is null) return expected is null && actual is null;
        return expected.Path == actual.Path && expected.Source == actual.Source && expected.Origin == actual.Origin &&
            expected.Required == actual.Required && expected.Size == actual.Size && expected.ArchiveEntry == actual.ArchiveEntry &&
            expected.ProjectId == actual.ProjectId && expected.FileId == actual.FileId && actual.Downloads is { Count: 0 } &&
            SameHashes(expected.Hashes, actual.Hashes) && SameOfficialOrigin(expected.CurseForgeOrigin, actual.CurseForgeOrigin);
    }

    /// <summary>元数据的项目、文件、大小和官方摘要都必须与固定平台引用一致。</summary>
    private static bool SameOfficialOrigin(CurseForgeFileOrigin? expected, CurseForgeFileOrigin? actual) =>
        expected is null || actual is null ? expected is null && actual is null :
        expected.ProjectId == actual.ProjectId && expected.FileId == actual.FileId && expected.Size == actual.Size &&
        SameHashes(expected.Hashes, actual.Hashes);

    /// <summary>按算法与内容比较摘要，忽略十六进制大小写，不依赖字典插入顺序。</summary>
    private static bool SameHashes(IReadOnlyDictionary<string, string>? expected, IReadOnlyDictionary<string, string>? actual) =>
        expected is not null && actual is not null && expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var hash) &&
            string.Equals(pair.Value, hash, StringComparison.OrdinalIgnoreCase));
}
