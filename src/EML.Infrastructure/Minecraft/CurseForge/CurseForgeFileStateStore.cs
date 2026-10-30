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
    public int? ClassId { get; init; }
    public string? FileName { get; init; }
    public string? ManualDownloadUrl { get; init; }
    public string? BlockReason { get; init; }
}

/// <summary>独立于导入清单保存 API 解析快照；指纹变化后不得复用旧文件映射。</summary>
internal sealed record CurseForgeFileResolution
{
    public int SchemaVersion { get; init; } = 1;
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
            if (state.SchemaVersion != 1 || state.Fingerprint is null || state.Fingerprint.Length != 64 ||
                !state.Fingerprint.All(Uri.IsHexDigit)) throw new InvalidDataException("CurseForge 文件缓存格式无效。");
            if (state.Fingerprint != Fingerprint(manifest)) return null;
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

    /// <summary>经完整验证后用合并了包内基线的 Paxi 数据包需求替换重复 Embedded 项，避免重复校验或误报缺失。</summary>
    internal static IReadOnlyList<InstanceFileRequirement> EffectiveRequirements(InstanceImportManifest manifest,
        CurseForgeFileResolution state)
    {
        Validate(manifest, state);
        var entries = state.Files.ToDictionary(entry => (entry.Requirement.ProjectId, entry.Requirement.FileId));
        var combinedPaths = state.Files.Where(entry => entry.ClassId == 6945 && entry.BlockReason is null && entry.Requirement.Path is not null)
            .Select(entry => entry.Requirement.Path!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return manifest.Files.Where(file => file.Source != InstanceFileSource.Embedded || file.Path is null || !combinedPaths.Contains(file.Path))
            .Select(file => file.Source == InstanceFileSource.CurseForge && file.Required && entries.TryGetValue((file.ProjectId, file.FileId), out var entry)
                ? entry.Requirement : file).ToArray();
    }

    /// <summary>校验缓存与每个原始必需引用一一对应，并拒绝与包内文件重名或目录冲突的 API 结果。</summary>
    private static void Validate(InstanceImportManifest manifest, CurseForgeFileResolution state)
    {
        InstanceManifestStore.Validate(manifest);
        if (!manifest.SourceFormat.Equals("CurseForge", StringComparison.OrdinalIgnoreCase) ||
            state.SchemaVersion != 1 || state.Fingerprint != Fingerprint(manifest) || state.Files is null)
            throw new InvalidDataException("CurseForge 缓存与当前导入清单不一致。");
        var source = manifest.Files.Where(file => file.Source == InstanceFileSource.CurseForge && file.Required).ToArray();
        var expected = source.Select(file => (file.ProjectId, file.FileId)).ToHashSet();
        if (expected.Count != source.Length || state.Files.Count != source.Length)
            throw new InvalidDataException("CurseForge 文件引用重复或缓存条目不完整。");
        foreach (var entry in state.Files)
        {
            if (entry?.Requirement is not { } file || file.Source != InstanceFileSource.CurseForge || !file.Required ||
                !expected.Remove((file.ProjectId, file.FileId)) || file.Downloads is null || file.Hashes is null ||
                file.Downloads.Count > 1 || !IsOfficialPage(entry.ManualDownloadUrl))
                throw new InvalidDataException("CurseForge 文件缓存含未知条目或无效下载入口。");
            if (entry.BlockReason is { Length: > 4096 }) throw new InvalidDataException("CurseForge 缓存问题描述过长。");
            if (file.Path is not null)
            {
                if (entry.ClassId is not { } classId || entry.FileName is null ||
                    file.Path != TargetPath(manifest, classId, entry.FileName))
                    throw new InvalidDataException("CurseForge 缓存目标不符合项目类别。");
            }
            if (entry.BlockReason is null && (file.Path is null || file.Size is null ||
                !file.Hashes.ContainsKey("sha1"))) throw new InvalidDataException("CurseForge 可用文件缓存缺少目标、大小或 SHA-1。");
            if (entry.BlockReason is not null && file.Downloads.Count != 0)
                throw new InvalidDataException("不可解析的 CurseForge 文件不能携带自动下载地址。");
        }
        // Paxi 数据包可能同时打包在 overrides 中；只有合并了完整包内基线的条目才允许共享目标。
        var allowedEmbeddedOverlaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in state.Files.Where(entry => entry.Requirement.Path is not null))
        {
            var embedded = manifest.Files.SingleOrDefault(file => file.Source == InstanceFileSource.Embedded &&
                string.Equals(file.Path, entry.Requirement.Path, StringComparison.OrdinalIgnoreCase));
            if (embedded is null) continue;
            if (entry.ClassId != 6945 || entry.BlockReason is not null || embedded.Size is null ||
                embedded.Size != entry.Requirement.Size || embedded.Hashes.Count == 0 ||
                embedded.Hashes.Any(pair => !entry.Requirement.Hashes.TryGetValue(pair.Key, out var value) ||
                    !pair.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("文件需求重复或同路径 Paxi 数据包未保留完整包内校验基线。");
            allowedEmbeddedOverlaps.Add(embedded.Path!);
        }
        // 其他 API 文件仍不可覆盖包内模组或配置；通用校验同时拒绝重复下载项和文件目录冲突。
        InstanceManifestStore.Validate(manifest with
        {
            Files = manifest.Files.Where(file => (file.Source != InstanceFileSource.CurseForge || !file.Required) &&
                    (file.Source != InstanceFileSource.Embedded || file.Path is null || !allowedEmbeddedOverlaps.Contains(file.Path)))
                .Concat(state.Files.Select(file => file.Requirement)).ToArray()
        });
    }
}
