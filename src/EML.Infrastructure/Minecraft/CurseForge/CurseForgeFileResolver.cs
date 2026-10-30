using EML.Core.CurseForge;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.CurseForge.Api;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.CurseForge;

/// <summary>通过官方 API 将整合包的固定项目和文件 ID 解析为可校验的本地文件需求。</summary>
internal sealed class CurseForgeFileResolver(ICurseForgeApiClient apiClient)
{
    private const int MinecraftGameId = 432;
    private const int BatchSize = 50;

    /// <summary>复用匹配来源的缓存；缺失元数据或尚未人工补齐的受限文件会在下一轮重新请求。</summary>
    internal async Task<CurseForgeFileResolution> ResolveAsync(string instanceRoot, InstanceImportManifest manifest,
        CancellationToken token = default)
    {
        InstanceManifestStore.Validate(manifest);
        if (!manifest.SourceFormat.Equals("CurseForge", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CurseForge 文件解析器收到其他格式的实例。");
        var cached = CurseForgeFileStateStore.Read(instanceRoot, manifest);
        if (cached is not null && cached.Files.All(file => CanReuseCachedFile(instanceRoot, file))) return cached;
        var requirements = manifest.Files.Where(file => file.Required && file.Source == InstanceFileSource.CurseForge).ToArray();
        if (requirements.Select(file => (file.ProjectId, file.FileId)).Distinct().Count() != requirements.Length)
            throw new InvalidDataException("CurseForge 清单重复声明了同一项目和文件 ID。");
        var fileIds = requirements.Select(file => file.FileId!.Value).Distinct().ToArray();
        var modIds = requirements.Select(file => file.ProjectId!.Value).Distinct().ToArray();
        var files = new Dictionary<long, CurseForgeFile>();
        var mods = new Dictionary<long, CurseForgeMod>();
        foreach (var batch in fileIds.Chunk(BatchSize))
        {
            token.ThrowIfCancellationRequested();
            var response = await apiClient.GetFilesAsync(batch, token).ConfigureAwait(false);
            if (response is null) throw new InvalidDataException("CurseForge API 返回空文件列表。");
            foreach (var file in response)
            {
                if (file is null || !batch.Contains(file.Id) || file.GameId != MinecraftGameId || !files.TryAdd(file.Id, file))
                    throw new InvalidDataException("CurseForge API 返回了重复、非 Minecraft 或未请求的文件 ID。");
            }
        }
        foreach (var batch in modIds.Chunk(BatchSize))
        {
            token.ThrowIfCancellationRequested();
            var response = await apiClient.GetModsAsync(batch, token).ConfigureAwait(false);
            if (response is null) throw new InvalidDataException("CurseForge API 返回空项目列表。");
            foreach (var mod in response)
            {
                if (mod is null || !batch.Contains(mod.Id) || mod.GameId != MinecraftGameId || !mods.TryAdd(mod.Id, mod))
                    throw new InvalidDataException("CurseForge API 返回了重复、非 Minecraft 或未请求的项目 ID。");
            }
        }
        var state = new CurseForgeFileResolution
        {
            Fingerprint = CurseForgeFileStateStore.Fingerprint(manifest),
            Files = requirements.Select(requirement => ResolveOne(manifest, requirement,
                files.GetValueOrDefault(requirement.FileId!.Value), mods.GetValueOrDefault(requirement.ProjectId!.Value))).ToArray()
        };
        await CurseForgeFileStateStore.WriteAsync(instanceRoot, manifest, state, token).ConfigureAwait(false);
        return state;
    }

    /// <summary>已有文件仍可离线校验；生产下载只复用官方 CDN 地址，旧后端缓存需重新向 API 解析。</summary>
    private bool CanReuseCachedFile(string instanceRoot, CurseForgeResolvedFile entry)
    {
        if (entry.BlockReason is not null) return false;
        var file = entry.Requirement;
        if (file.Path is { } path && File.Exists(InstanceImportPaths.Within(instanceRoot, path))) return true;
        if (file.Downloads.Count == 0) return false;
        return apiClient is not CurseForgeApiClient || file.Downloads.All(IsOfficialDownloadUrl);
    }

    /// <summary>核验项目关联和类别，保留明确的人工下载入口，绝不根据文件 ID 猜测 CDN 地址。</summary>
    private CurseForgeResolvedFile ResolveOne(InstanceImportManifest manifest, InstanceFileRequirement original,
        CurseForgeFile? file, CurseForgeMod? mod)
    {
        var requirement = new InstanceFileRequirement
        {
            Source = InstanceFileSource.CurseForge, Required = true, ProjectId = original.ProjectId, FileId = original.FileId
        };
        var entry = new CurseForgeResolvedFile
        {
            Requirement = requirement, ManualDownloadUrl = FilePage(original.ProjectId!.Value, original.FileId!.Value, mod)
        };
        if (file is null || mod is null)
            return entry with { BlockReason = "官方 API 未返回指定项目或文件元数据；请确认文件仍可访问后重试。" };
        if (file.ModId != original.ProjectId || file.Id != original.FileId || mod.Id != original.ProjectId)
            throw new InvalidDataException("CurseForge 文件所属项目与整合包指定的 projectID 不符。");
        if (mod.ClassId == 6945 && !CurseForgeFileStateStore.HasPaxiDataPackDirectory(manifest))
            return entry with { BlockReason = "数据包需要明确的数据包加载目录；当前清单没有同时声明必需的 Paxi 模组和包内 config/paxi/datapack_load_order.json，未自动放入模组或资源包目录。" };
        if (mod.ClassId is not (6 or 12 or 6552 or 6945))
            return entry with { BlockReason = $"暂不支持 CurseForge 项目类别 {mod.ClassId?.ToString() ?? "未知"}，未自动选择安装目录。" };
        var target = CurseForgeFileStateStore.TargetPath(manifest, mod.ClassId.Value, file.FileName);
        if (file.FileLength < 0) throw new InvalidDataException("CurseForge API 返回负数文件大小。");
        var sha1Hashes = file.Hashes?.Where(hash => hash is not null && hash.Algo == 1).Select(hash => hash.Value).ToArray() ?? [];
        if (sha1Hashes.Length > 1 || (sha1Hashes.Length == 1 &&
            (sha1Hashes[0] is null || sha1Hashes[0].Length != 40 || !sha1Hashes[0].All(Uri.IsHexDigit))))
            throw new InvalidDataException("CurseForge API 返回重复或非法 SHA-1。");
        requirement = requirement with
        {
            Path = target, Size = file.FileLength,
            Hashes = sha1Hashes.Length == 1 ? new Dictionary<string, string> { ["sha1"] = sha1Hashes[0].ToLowerInvariant() }
                : new Dictionary<string, string>()
        };
        if (mod.ClassId == 6945)
            requirement = PreserveEmbeddedDataPackBaseline(manifest, requirement);
        entry = entry with { Requirement = requirement, ClassId = mod.ClassId, FileName = file.FileName };
        if (sha1Hashes.Length == 0)
            return entry with { BlockReason = "官方 API 未提供 SHA-1；当前校验器不支持仅有 MD5 的文件，无法确认指定内容。" };
        if (mod.AllowModDistribution == false || !file.IsAvailable || string.IsNullOrWhiteSpace(file.DownloadUrl)) return entry;
        if (apiClient is CurseForgeApiClient && !IsOfficialDownloadUrl(file.DownloadUrl))
            throw new InvalidDataException("CurseForge API 返回了非受信任 CDN 或含凭据的下载地址。");
        // 密钥不进入文件清单，下载器仅在请求受信任 CDN 时通过请求头提供凭据。
        return entry with { Requirement = requirement with { Downloads = [file.DownloadUrl] } };
    }

    /// <summary>同路径包内数据包同时保留导入基线和 API 哈希，只有实际内容符合两者时才可复用。</summary>
    private static InstanceFileRequirement PreserveEmbeddedDataPackBaseline(InstanceImportManifest manifest,
        InstanceFileRequirement requirement)
    {
        var embedded = manifest.Files.SingleOrDefault(file => file.Source == InstanceFileSource.Embedded &&
            string.Equals(file.Path, requirement.Path, StringComparison.OrdinalIgnoreCase));
        if (embedded is null) return requirement;
        if (embedded.Size is null || embedded.Size != requirement.Size || embedded.Hashes.Count == 0)
            throw new InvalidDataException("同路径 Paxi 数据包与包内文件的大小声明不同或缺少校验依据，已保留原文件。");
        var hashes = requirement.Hashes.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var (name, value) in embedded.Hashes)
        {
            if (hashes.TryGetValue(name, out var expected) && !value.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("同路径 Paxi 数据包与包内文件的哈希声明冲突，已保留原文件。");
            hashes[name] = value;
        }
        return requirement with { Hashes = hashes };
    }

    /// <summary>与下载请求签名共用 CDN 地址约束，不接受旧后端、其他主机或包含凭据的地址。</summary>
    private static bool IsOfficialDownloadUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        CurseForgeRequestHeaders.IsCdnUri(uri);

    /// <summary>优先使用 API 给出的官方项目网页，缺少网页时生成官方项目 ID 文件页。</summary>
    private static string FilePage(long projectId, long fileId, CurseForgeMod? mod)
    {
        var page = mod?.Links?.WebsiteUrl;
        if (CurseForgeFileStateStore.IsOfficialPage(page))
        {
            var uri = new Uri(page!);
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/files/" + fileId;
        }
        return $"https://www.curseforge.com/projects/{projectId}/files/{fileId}";
    }
}
