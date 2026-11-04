using EML.Core.Modrinth;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Modrinth.Api;

namespace EML.Infrastructure.Minecraft.CurseForge;

/// <summary>记录备用文件的固定来源；原 CurseForge 身份、路径与哈希仍保存在 Requirement 中。</summary>
internal sealed record CurseForgeModrinthFile
{
    public string ProjectId { get; init; } = "";
    public string VersionId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public string? Sha512 { get; init; }
    public long Size { get; init; }
    public string DownloadUrl { get; init; } = "";
}

/// <summary>只为缺失且无 CurseForge 下载地址的文件查询同内容备用来源，不按名称猜测或升级版本。</summary>
internal sealed class CurseForgeModrinthFallbackResolver(IModrinthApiClient apiClient)
{
    private const int BatchSize = 50;

    /// <summary>批量查找缺失文件；查询失败保留原人工入口，取消则立即传播给安装器。</summary>
    internal async Task<CurseForgeFileResolution> ResolveAsync(string instanceRoot, CurseForgeFileResolution state,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var files = state.Files.ToArray();
        var candidates = Enumerable.Range(0, files.Length).Where(index => NeedsLookup(instanceRoot, files[index])).ToArray();
        foreach (var batch in candidates.Chunk(BatchSize))
        {
            token.ThrowIfCancellationRequested();
            var hashes = batch.Select(index => files[index].Requirement.Hashes["sha1"].ToLowerInvariant()).Distinct().ToArray();
            IReadOnlyDictionary<string, ModrinthVersion> versions;
            try
            {
                versions = await apiClient.GetVersionsByHashesAsync(hashes, "sha1", token).ConfigureAwait(false);
                if (versions is null || versions.Any(pair => !hashes.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) || pair.Value is null))
                    throw new InvalidDataException("Modrinth 哈希查询包含未请求或空的结果。");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException or InvalidDataException or OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                var message = ex switch
                {
                    ModrinthApiException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "Modrinth 备用来源暂时限流，可稍后再次补全。",
                    InvalidDataException => "Modrinth 返回的文件信息未通过验证，未采用备用来源。",
                    _ => "Modrinth 备用来源暂时查询失败，可稍后再次补全。"
                };
                foreach (var index in batch) files[index] = files[index] with { FallbackMessage = message };
                continue;
            }
            foreach (var index in batch)
            {
                token.ThrowIfCancellationRequested();
                var entry = files[index];
                var hash = entry.Requirement.Hashes["sha1"];
                var matches = versions.Where(pair => pair.Key.Equals(hash, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 0)
                {
                    files[index] = entry with { FallbackMessage = "Modrinth 未找到 SHA-1 一致的文件，仍需提供清单指定版本。" };
                    continue;
                }
                try
                {
                    if (matches.Length != 1) throw new InvalidDataException("Modrinth 哈希结果重复。");
                    files[index] = Match(entry, matches[0].Value);
                }
                catch (InvalidDataException)
                {
                    files[index] = entry with { FallbackMessage = "Modrinth 候选文件的哈希、大小或来源不符合要求，未自动替换。" };
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return state with { Files = files };
    }

    /// <summary>已有文件交给原校验流程处理；只对可验证的必需缺失文件发起备用查询。</summary>
    private static bool NeedsLookup(string instanceRoot, CurseForgeResolvedFile entry)
    {
        var file = entry.Requirement;
        if (entry.EmbeddedOverride is not null || entry.BlockReason is not null || !file.Required || file.Downloads.Count != 0 ||
            file.Path is null || file.Size is null || !file.Hashes.ContainsKey("sha1")) return false;
        var path = InstanceImportPaths.Within(instanceRoot, file.Path);
        InstanceImportPaths.EnsureNoLinks(path);
        return !File.Exists(path);
    }

    /// <summary>选中唯一同哈希且同大小的文件，忽略 primary 标志并保留原整合包目标与全部校验依据。</summary>
    private static CurseForgeResolvedFile Match(CurseForgeResolvedFile entry, ModrinthVersion version)
    {
        var original = entry.Requirement;
        var sha1 = original.Hashes["sha1"];
        if (!IsId(version.ProjectId) || !IsId(version.Id) || version.Files is null)
            throw new InvalidDataException("Modrinth 版本缺少固定身份或文件列表。");
        var matches = version.Files.Where(file => file is not null && file.Hashes is not null &&
            file.Hashes.TryGetValue("sha1", out var hash) && sha1.Equals(hash, StringComparison.OrdinalIgnoreCase) &&
            file.Size == original.Size).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("未找到唯一同哈希、同大小的 Modrinth 文件。");
        var file = matches[0];
        var source = new CurseForgeModrinthFile
        {
            ProjectId = version.ProjectId, VersionId = version.Id, FileName = file.Filename,
            Sha1 = sha1.ToLowerInvariant(), Size = file.Size, DownloadUrl = file.Url,
            Sha512 = file.Hashes.TryGetValue("sha512", out var sha512) ? sha512 : null
        };
        if (!IsValidSource(source)) throw new InvalidDataException("Modrinth 下载来源不符合官方文件地址约束。");
        var hashes = original.Hashes.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (source.Sha512 is { } extraHash)
        {
            if (hashes.TryGetValue("sha512", out var expected) && !expected.Equals(extraHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Modrinth SHA-512 与原文件依据冲突。");
            hashes["sha512"] = extraHash.ToLowerInvariant();
        }
        return entry with
        {
            Requirement = original with
            {
                Origin = InstanceFileOrigin.ModrinthDownload, Downloads = [source.DownloadUrl], Hashes = hashes,
                CurseForgeOrigin = CurseForgeFileStateStore.OfficialOrigin(original)
            },
            ModrinthFallback = source, FallbackMessage = null
        };
    }

    /// <summary>解析与缓存共用的来源校验；只接受官方 CDN 的固定项目、版本和文件路径。</summary>
    internal static bool IsValidSource(CurseForgeModrinthFile source)
    {
        if (!IsId(source.ProjectId) || !IsId(source.VersionId) || source.Size < 0 || !IsHash(source.Sha1, 40) ||
            (source.Sha512 is not null && !IsHash(source.Sha512, 128)) || string.IsNullOrWhiteSpace(source.FileName)) return false;
        try
        {
            if (InstanceImportPaths.Relative(source.FileName) != source.FileName || source.FileName.Contains('/')) return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { return false; }
        return Uri.TryCreate(source.DownloadUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase) && uri.Port == 443 &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            Uri.UnescapeDataString(uri.AbsolutePath) == $"/data/{source.ProjectId}/versions/{source.VersionId}/{source.FileName}";
    }

    /// <summary>平台固定 ID 使用有界字母数字，不允许路径分隔符或编码片段。</summary>
    private static bool IsId(string? value) => value is { Length: > 0 and <= 128 } && value.All(char.IsAsciiLetterOrDigit);

    /// <summary>核验哈希的固定长度及十六进制格式。</summary>
    private static bool IsHash(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);
}
