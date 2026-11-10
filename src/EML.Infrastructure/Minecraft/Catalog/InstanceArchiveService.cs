using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using EML.Core.Downloads;
using EML.Core.Minecraft.Catalog;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Catalog;

/// <summary>准备经过平台校验的导入源，缓存原包供以后恢复内嵌文件，不写入现有实例。</summary>
public sealed class InstanceArchiveService : IInstanceArchiveService
{
    private const long MaximumArchiveBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumMetadataBytes = 8 * 1024 * 1024;
    private readonly string _cacheRoot;
    private readonly IInstanceCatalog _catalog;
    private readonly IFileDownloader _downloader;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>将导入源放在启动器自己的缓存目录，所有远端名字只用作显示。</summary>
    public InstanceArchiveService(string launcherRoot, IInstanceCatalog catalog, IFileDownloader downloader)
    {
        _cacheRoot = Path.GetFullPath(Path.Combine(launcherRoot, "cache", "instance-archives"));
        _catalog = catalog;
        _downloader = downloader;
    }

    /// <summary>重新解析固定版本身份，串行校验缓存和下载；取消不删除已有原包。</summary>
    public async Task<PreparedInstanceArchive> PrepareAsync(InstanceCatalogProvider provider, string projectId, string releaseId,
        IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(provider) || string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(releaseId) ||
            projectId.Length > 256 || releaseId.Length > 256)
            throw new ArgumentException("下载项目或发布版本标识无效。");
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var release = await _catalog.GetReleaseAsync(provider, projectId, releaseId, cancellationToken).ConfigureAwait(false);
            ValidateRelease(release, provider, projectId, releaseId);
            return await Task.Run(() => PrepareCoreAsync(release, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { _operationLock.Release(); }
    }

    /// <summary>复核缓存或下载完整文件，平台包继续交给同一套导入解析器检查格式与路径。</summary>
    private async Task<PreparedInstanceArchive> PrepareCoreAsync(InstanceCatalogRelease release,
        IProgress<FileDownloadProgress>? progress, CancellationToken token)
    {
        InstanceImportPaths.EnsureNoLinks(_cacheRoot);
        Directory.CreateDirectory(_cacheRoot);
        InstanceImportPaths.EnsureNoLinks(_cacheRoot);
        var format = release.Provider switch
        {
            InstanceCatalogProvider.Modrinth => "Modrinth", InstanceCatalogProvider.CurseForge => "CurseForge", _ => "Zip"
        };
        var extension = release.Provider switch
        {
            InstanceCatalogProvider.Modrinth => ".mrpack", InstanceCatalogProvider.CurseForge => ".zip", _ => ".json"
        };
        var identity = CacheIdentity(release);
        var canonical = InstanceImportPaths.Within(_cacheRoot, identity + extension);
        var path = canonical;
        if (!await MatchesAsync(path, release, token).ConfigureAwait(false))
        {
            // 损坏缓存仍保留；新下载使用另一个名字，避免覆盖用户手动修改或被实例引用的原包。
            if (File.Exists(path) || Directory.Exists(path)) path = UniquePath(identity, extension);
            var request = new FileDownloadRequest
            {
                DestinationPath = path, Urls = release.DownloadUrls, ExpectedSize = release.Size,
                DisplayName = release.Name, GroupName = "实例下载 · " + release.Provider,
                Priority = DownloadPriority.High,
                Hashes = release.Hashes,
                MaximumBytes = release.Provider == InstanceCatalogProvider.Minecraft ? MaximumMetadataBytes : MaximumArchiveBytes
            };
            await _downloader.DownloadAsync(request, progress, token).ConfigureAwait(false);
            if (!await MatchesAsync(path, release, token).ConfigureAwait(false))
                throw new InvalidDataException("下载结果未通过原平台大小和摘要校验，不能导入。");
        }
        token.ThrowIfCancellationRequested();
        if (release.Provider == InstanceCatalogProvider.Minecraft)
            path = await CreateVanillaArchiveAsync(path, identity, release, token).ConfigureAwait(false);
        using (var input = OpenRead(path))
        using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
        {
            var plan = InstanceArchiveReader.Read(archive, Path.GetFileName(path), token);
            if (!string.Equals(plan.Manifest.SourceFormat, format, StringComparison.Ordinal))
                throw new InvalidDataException("下载内容的整合包格式与所选平台不一致，不能自动导入。");
        }
        return new(path, InstanceImportPaths.SuggestName(release.Provider == InstanceCatalogProvider.Minecraft
            ? "Minecraft " + release.Id : release.Name), format);
    }

    /// <summary>固定版本必须有对应平台的完整性要求，不能把缺少授权或摘要当作可直接下载。</summary>
    private static void ValidateRelease(InstanceCatalogRelease release, InstanceCatalogProvider provider, string projectId, string releaseId)
    {
        if (release.Provider != provider || release.ProjectId != projectId || release.Id != releaseId)
            throw new InvalidDataException("平台返回的发布版本不属于所选项目。");
        if (!release.CanDownload) throw new InvalidOperationException(release.Problem ?? "此发布版本没有可校验的官方下载地址。");
        if (release.Size is < 0 || release.Size > MaximumArchiveBytes ||
            (provider != InstanceCatalogProvider.Minecraft && release.Size is null or 0))
            throw new InvalidDataException("整合包缺少有效的官方文件大小。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (algorithm, hash) in release.Hashes)
        {
            var length = algorithm.ToLowerInvariant() switch { "sha1" => 40, "sha256" => 64, "sha512" => 128, _ => 0 };
            if (length == 0 || !names.Add(algorithm) || hash?.Length != length || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("平台文件的摘要算法或内容无效。");
        }
        if ((provider is InstanceCatalogProvider.CurseForge or InstanceCatalogProvider.Minecraft && !names.Contains("sha1")) ||
            (provider == InstanceCatalogProvider.Modrinth && !names.Contains("sha1") && !names.Contains("sha512")))
            throw new InvalidDataException("下载缺少该平台的可信文件摘要。");
        foreach (var url in release.DownloadUrls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || !uri.IsDefaultPort)
                throw new InvalidDataException("平台下载地址不是有效的 HTTPS 地址。");
            if (provider == InstanceCatalogProvider.Minecraft &&
                uri.IdnHost is not ("piston-meta.mojang.com" or "launchermeta.mojang.com"))
                throw new InvalidDataException("原版版本描述必须来自 Mojang 官方元数据地址。");
        }
        var expected = provider switch
        {
            InstanceCatalogProvider.Modrinth => ".mrpack", InstanceCatalogProvider.CurseForge => ".zip", _ => ".json"
        };
        if (!release.FileName.EndsWith(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("发布文件扩展名与实例来源不一致。");
        if (provider == InstanceCatalogProvider.Minecraft)
        {
            var id = InstanceImportPaths.Relative(release.Id);
            if (id.Contains('/') || id.Length > 128 || release.Size > MaximumMetadataBytes)
                throw new InvalidDataException("原版版本标识或元数据大小无效。");
        }
    }

    /// <summary>缓存名称只取固定身份与可信要求的摘要，不使用远端提供的文件路径。</summary>
    private static string CacheIdentity(InstanceCatalogRelease release)
    {
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            release.Provider, release.ProjectId, release.Id, release.Size,
            Hashes = release.Hashes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new { Algorithm = pair.Key.ToLowerInvariant(), Hash = pair.Value.ToLowerInvariant() }).ToArray()
        });
        return release.Provider.ToString().ToLowerInvariant() + "-" + Convert.ToHexStringLower(SHA256.HashData(identity));
    }

    /// <summary>新目标始终位于缓存目录且名字不可预测，已有内容不会被复用为写入目标。</summary>
    private string UniquePath(string identity, string extension) =>
        InstanceImportPaths.Within(_cacheRoot, identity + "-" + Guid.NewGuid().ToString("N") + extension);

    /// <summary>缓存与新下载使用相同的大小和全部摘要校验，读取时拒绝目录链接。</summary>
    private static async Task<bool> MatchesAsync(string path, InstanceCatalogRelease release, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        InstanceImportPaths.EnsureNoLinks(path);
        if (!File.Exists(path)) return false;
        await using var input = OpenRead(path);
        var maximum = release.Provider == InstanceCatalogProvider.Minecraft ? MaximumMetadataBytes : MaximumArchiveBytes;
        if (input.Length > maximum || release.Size is { } size && input.Length != size) return false;
        foreach (var (name, expected) in release.Hashes)
        {
            input.Position = 0;
            var actual = name.ToLowerInvariant() switch
            {
                "sha1" => await SHA1.HashDataAsync(input, token).ConfigureAwait(false),
                "sha256" => await SHA256.HashDataAsync(input, token).ConfigureAwait(false),
                "sha512" => await SHA512.HashDataAsync(input, token).ConfigureAwait(false),
                _ => throw new InvalidDataException("不支持的平台摘要算法。")
            };
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected))) return false;
        }
        return true;
    }

    /// <summary>仅包装原始官方描述，不生成依赖完成状态；客户端、类库和资源由现有 ZIP 安装器补全。</summary>
    private async Task<string> CreateVanillaArchiveAsync(string metadataPath, string identity, InstanceCatalogRelease release, CancellationToken token)
    {
        byte[] bytes;
        await using (var input = OpenRead(metadataPath))
        {
            if (input.Length > MaximumMetadataBytes) throw new InvalidDataException("原版版本描述过大。");
            bytes = new byte[(int)input.Length];
            await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        }
        // 在包装前校验实际读取的字节，避免原始 JSON 在前一次校验后被外部程序改写。
        foreach (var (algorithm, expected) in release.Hashes)
        {
            var actual = algorithm.ToLowerInvariant() switch
            {
                "sha1" => SHA1.HashData(bytes), "sha256" => SHA256.HashData(bytes), "sha512" => SHA512.HashData(bytes),
                _ => throw new InvalidDataException("不支持的平台摘要算法。")
            };
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected)))
                throw new InvalidDataException("原版版本描述在准备期间发生变化。");
        }
        ValidateVanillaMetadata(bytes, release.Id);
        var entryName = $".minecraft/versions/{release.Id}/{release.Id}.json";
        var destination = InstanceImportPaths.Within(_cacheRoot, identity + "-bootstrap.zip");
        if (BootstrapMatches(destination, entryName, bytes, token)) return destination;
        if (File.Exists(destination) || Directory.Exists(destination)) destination = UniquePath(identity + "-bootstrap", ".zip");
        var temporary = UniquePath(identity + "-bootstrap", ".partial");
        var created = false;
        try
        {
            InstanceImportPaths.EnsureNoLinks(temporary);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            InstanceImportPaths.EnsureNoLinks(destination);
            File.Move(temporary, destination, overwrite: false);
            return destination;
        }
        finally
        {
            // 只清理由本次创建的随机临时文件，不能删除已存在或已发布的原包。
            try { if (created && File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>官方描述必须与所选版本对应，且含可下载客户端和资源索引；不接受加载器或继承配置。</summary>
    private static void ValidateVanillaMetadata(byte[] bytes, string id)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var identifier) ||
            identifier.ValueKind != JsonValueKind.String || identifier.GetString() != id || root.TryGetProperty("inheritsFrom", out _) ||
            !root.TryGetProperty("mainClass", out var main) || main.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(main.GetString()) ||
            !root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("downloads", out var downloads) || downloads.ValueKind != JsonValueKind.Object ||
            !downloads.TryGetProperty("client", out var client) || !root.TryGetProperty("assetIndex", out var assetIndex))
            throw new InvalidDataException("原版版本描述缺少完整客户端运行信息，或版本与选择不符。");
        ValidateArtifact(client);
        ValidateArtifact(assetIndex);
    }

    /// <summary>官方运行文件必须有固定大小、SHA-1 和 HTTPS 下载地址，不能只凭文件名猜测。</summary>
    private static void ValidateArtifact(JsonElement artifact)
    {
        if (artifact.ValueKind != JsonValueKind.Object || !artifact.TryGetProperty("size", out var size) ||
            !size.TryGetInt64(out var length) || length <= 0 || !artifact.TryGetProperty("sha1", out var sha1) ||
            sha1.ValueKind != JsonValueKind.String || sha1.GetString() is not { Length: 40 } hash || !hash.All(Uri.IsHexDigit) ||
            !artifact.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
            throw new InvalidDataException("官方运行文件缺少可验证的大小、摘要或下载地址。");
    }

    /// <summary>包装缓存只允许一个预期 JSON 条目，并逐字节核对官方描述，损坏时另建文件。</summary>
    private static bool BootstrapMatches(string path, string entryName, byte[] expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        InstanceImportPaths.EnsureNoLinks(path);
        if (!File.Exists(path)) return false;
        try
        {
            using var input = OpenRead(path);
            if (input.Length > MaximumMetadataBytes * 2L) return false;
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            var entries = SafeInstanceArchive.ReadEntries(archive, token);
            if (entries.Count != 1 || entries[0].Path != entryName || entries[0].Entry.Length != expected.Length) return false;
            using var stream = entries[0].Entry.Open();
            var actual = new byte[expected.Length];
            stream.ReadExactly(actual);
            return actual.AsSpan().SequenceEqual(expected) && stream.ReadByte() == -1;
        }
        catch (InvalidDataException) { return false; }
        catch (EndOfStreamException) { return false; }
    }

    /// <summary>只读打开文件并禁止外部写入和删除，验证期间不跟随目录链接。</summary>
    private static FileStream OpenRead(string path)
    {
        InstanceImportPaths.EnsureNoLinks(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
    }
}
