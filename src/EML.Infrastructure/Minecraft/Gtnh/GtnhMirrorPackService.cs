using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EML.Core.Downloads;
using EML.Core.Minecraft.Catalog;
using EML.Core.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Gtnh;

/// <summary>只读取固定 GTNH 镜像目录；本地摘要用于发现缓存变化，不冒充官方文件哈希。</summary>
public sealed partial class GtnhMirrorPackService : IGtnhPackService, IDisposable
{
    private const long MaximumArchiveBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumDirectoryBytes = 2 * 1024 * 1024;
    private const int MaximumRecordBytes = 4096;
    private static readonly Uri DirectoryUri = new("https://downloads.gaytnh.com/Multi_mc_downloads/");
    private static readonly JsonSerializerOptions RecordOptions = new() { MaxDepth = 8 };
    private readonly string _cacheRoot;
    private readonly IFileDownloader _downloader;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>注入共享下载中心；自行创建的目录客户端禁止重定向，注入客户端由调用方释放。</summary>
    public GtnhMirrorPackService(string launcherRoot, IFileDownloader downloader, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherRoot);
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _cacheRoot = Path.GetFullPath(Path.Combine(launcherRoot, "cache", "gtnh-mirror-packs"));
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    }

    /// <summary>查询根目录及可选测试版目录，按数字版本排序，不把 latest 或服务端包混入列表。</summary>
    public async Task<IReadOnlyList<GtnhPackRelease>> GetReleasesAsync(bool includePrereleases = false,
        CancellationToken cancellationToken = default)
    {
        var releases = (await ReadDirectoryAsync(false, cancellationToken).ConfigureAwait(false)).ToList();
        if (includePrereleases)
            releases.AddRange(await ReadDirectoryAsync(true, cancellationToken).ConfigureAwait(false));
        return releases.Where(item => includePrereleases || !item.IsPrerelease)
            .OrderByDescending(item => System.Version.Parse(item.Version.Split('-')[0]))
            .ThenBy(item => item.IsPrerelease)
            .ThenByDescending(item => PrereleaseOrder(item.Version))
            .ThenBy(item => item.JavaVersion, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>下载前重新查询精确目录项；原包与缓存记录只新增，不覆盖被旧实例引用的源文件。</summary>
    public async Task<PreparedInstanceArchive> PrepareAsync(string releaseId,
        IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var betaDirectory = ValidateReleaseId(releaseId);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadDirectoryAsync(betaDirectory, cancellationToken).ConfigureAwait(false);
            var release = entries.SingleOrDefault(item => item.Id.Equals(releaseId, StringComparison.Ordinal))
                ?? throw new InvalidDataException("所选 GTNH 客户端包已从镜像目录消失或缺少有效大小，请刷新版本列表。");
            return await Task.Run(() => PrepareCoreAsync(release, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { _operationLock.Release(); }
    }

    /// <summary>复核可用缓存；没有可信本地记录的旧文件保留，改用下一个稳定目标继续下载。</summary>
    private async Task<PreparedInstanceArchive> PrepareCoreAsync(GtnhPackRelease release,
        IProgress<FileDownloadProgress>? progress, CancellationToken token)
    {
        InstanceImportPaths.EnsureNoLinks(_cacheRoot);
        Directory.CreateDirectory(_cacheRoot);
        InstanceImportPaths.EnsureNoLinks(_cacheRoot);
        var identityBytes = Encoding.UTF8.GetBytes("gtnh-mirror-v1\n" + release.Id + "\n" + release.Size.ToString(CultureInfo.InvariantCulture));
        var identity = Convert.ToHexStringLower(SHA256.HashData(identityBytes));
        for (var attempt = 0; attempt < 1024; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var suffix = attempt == 0 ? "" : "-" + attempt.ToString(CultureInfo.InvariantCulture);
            var archivePath = InstanceImportPaths.Within(_cacheRoot, identity + suffix + ".zip");
            var recordPath = archivePath + ".local-sha256.json";
            InstanceImportPaths.EnsureNoLinks(archivePath);
            InstanceImportPaths.EnsureNoLinks(recordPath);
            if (File.Exists(archivePath) && File.Exists(recordPath) &&
                await MatchesLocalCacheAsync(archivePath, recordPath, release, token).ConfigureAwait(false))
            {
                progress?.Report(new(release.Size, release.Size, 0) { Phase = FileDownloadPhase.Completed, FromCache = true });
                return Prepared(archivePath, release);
            }
            // 路径可能已被玩家修改或被已有实例引用；即使只剩记录，也不重新占用它。
            if (File.Exists(archivePath) || Directory.Exists(archivePath) || File.Exists(recordPath) || Directory.Exists(recordPath))
                continue;
            await _downloader.DownloadAsync(new()
            {
                DestinationPath = archivePath,
                Urls = [new Uri(DirectoryUri, release.Id).AbsoluteUri],
                ExpectedSize = release.Size,
                MaximumBytes = MaximumArchiveBytes,
                DisplayName = release.FileName,
                GroupName = "GTNH 本体 · 国内镜像",
                Priority = DownloadPriority.High
                // 镜像目录未公布哈希；不能把本地生成的摘要传成远端完整性要求。
            }, progress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            progress?.Report(new(release.Size, release.Size, 0) { Phase = FileDownloadPhase.Verifying });
            var digest = await ValidateArchiveAsync(archivePath, release, token).ConfigureAwait(false);
            await WriteLocalRecordAsync(recordPath, new(1, release.Id, release.Size, digest, "LocalCacheOnly"), token).ConfigureAwait(false);
            progress?.Report(new(release.Size, release.Size, 0) { Phase = FileDownloadPhase.Completed });
            return Prepared(archivePath, release);
        }
        throw new IOException("此 GTNH 版本已有过多不可复用缓存文件，请检查启动器 cache/gtnh-mirror-packs 目录。");
    }

    /// <summary>导入结果明确标记 Prism，名称只作可编辑建议，不直接作为落盘目录。</summary>
    private static PreparedInstanceArchive Prepared(string path, GtnhPackRelease release) =>
        new(path, InstanceImportPaths.SuggestName("GTNH " + release.Version + " Java " + release.JavaVersion), "Prism");

    /// <summary>只读取有界 HTML；HTTP 重定向或注入客户端已跟随到其他地址时均拒绝。</summary>
    private async Task<IReadOnlyList<GtnhPackRelease>> ReadDirectoryAsync(bool betaDirectory, CancellationToken token)
    {
        var uri = betaDirectory ? new Uri(DirectoryUri, "betas/") : DirectoryUri;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("EML/1.0");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } actual && actual != uri)
            throw new InvalidDataException("GTNH 镜像目录发生了未允许的重定向。");
        if (response.Content.Headers.ContentLength is > MaximumDirectoryBytes)
            throw new InvalidDataException("GTNH 镜像目录过大，已停止读取。");
        if (response.Content.Headers.ContentType?.MediaType is { } mediaType &&
            !mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GTNH 镜像目录返回了非 HTML 内容。");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > MaximumDirectoryBytes)
                throw new InvalidDataException("GTNH 镜像目录过大，已停止读取。");
            buffer.Write(chunk, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return ParseDirectory(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)), uri, betaDirectory, token);
    }

    /// <summary>仅使用同一行 name 链接和 size 的精确 Bytes 值，不从显示名称或四舍五入大小推断。</summary>
    private static IReadOnlyList<GtnhPackRelease> ParseDirectory(string html, Uri directory, bool betaDirectory, CancellationToken token)
    {
        var result = new Dictionary<string, GtnhPackRelease>(StringComparer.Ordinal);
        foreach (Match row in RowPattern().Matches(html))
        {
            token.ThrowIfCancellationRequested();
            string? nameCell = null, sizeText = null;
            var nameCount = 0;
            var sizeCount = 0;
            foreach (Match cell in CellPattern().Matches(row.Groups[1].Value))
            {
                var classes = Attribute(cell.Groups[1].Value, "class")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
                if (classes.Contains("name", StringComparer.OrdinalIgnoreCase)) { nameCount++; nameCell = cell.Groups[2].Value; }
                if (classes.Contains("size", StringComparer.OrdinalIgnoreCase)) { sizeCount++; sizeText = Attribute(cell.Groups[1].Value, "title"); }
            }
            if (nameCount != 1 || sizeCount != 1 || nameCell is null || sizeText is null) continue;
            var links = LinkPattern().Matches(nameCell);
            var sizeMatch = SizePattern().Match(sizeText);
            if (links.Count != 1 || !sizeMatch.Success || !long.TryParse(sizeMatch.Groups[1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var size) || size is <= 0 or > MaximumArchiveBytes) continue;
            var href = Attribute(links[0].Groups[1].Value, "href");
            if (!TryFileName(href, directory, out var fileName)) continue;
            var match = FilePattern().Match(fileName);
            if (!match.Success) continue;
            var id = (betaDirectory ? "betas/" : "") + fileName;
            var release = new GtnhPackRelease
            {
                Id = id, FileName = fileName, Size = size,
                Version = match.Groups["version"].Value, JavaVersion = match.Groups["java"].Value,
                IsPrerelease = betaDirectory || match.Groups["stage"].Success
            };
            if (result.TryGetValue(id, out var previous) && previous != release)
                throw new InvalidDataException("GTNH 镜像目录对同一文件给出了冲突的大小，请稍后刷新。");
            result[id] = release;
        }
        return result.Values.ToArray();
    }

    /// <summary>只接收当前目录的简单文件名或完全一致的 HTTPS 地址，拒绝跨目录、查询参数和外站。</summary>
    private static bool TryFileName(string? href, Uri directory, out string fileName)
    {
        fileName = "";
        if (string.IsNullOrWhiteSpace(href) || href.Length > 512 || href != href.Trim()) return false;
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme != "https" || absolute.IdnHost != directory.IdnHost || !absolute.IsDefaultPort ||
                absolute.UserInfo.Length != 0 || absolute.Query.Length != 0 || absolute.Fragment.Length != 0 ||
                !absolute.AbsolutePath.StartsWith(directory.AbsolutePath, StringComparison.Ordinal)) return false;
            fileName = absolute.AbsolutePath[directory.AbsolutePath.Length..];
            return FilePattern().IsMatch(fileName) && absolute.AbsoluteUri == new Uri(directory, fileName).AbsoluteUri;
        }
        if (!FilePattern().IsMatch(href)) return false;
        fileName = href;
        return true;
    }

    /// <summary>拒绝任意路径或 URL，公开下载入口只接受已支持的固定文件 ID。</summary>
    private static bool ValidateReleaseId(string releaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        var betaDirectory = releaseId.StartsWith("betas/", StringComparison.Ordinal);
        var fileName = betaDirectory ? releaseId[6..] : releaseId;
        if (releaseId.Length > 256 || !FilePattern().IsMatch(fileName))
            throw new ArgumentException("GTNH 下载标识必须是目录中支持的 Prism 客户端文件。", nameof(releaseId));
        return betaDirectory;
    }

    /// <summary>从带引号的 HTML 属性取单一值，实体解码不允许把重复属性解释成两个来源。</summary>
    private static string? Attribute(string attributes, string name)
    {
        string? value = null;
        foreach (Match attribute in AttributePattern().Matches(attributes))
        {
            if (!attribute.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (value is not null) return null;
            value = WebUtility.HtmlDecode(attribute.Groups[2].Success ? attribute.Groups[2].Value : attribute.Groups[3].Value);
        }
        return value;
    }

    /// <summary>预发布版本按 RC、Beta、Alpha 和序号排序，正式版由调用方优先排列。</summary>
    private static int PrereleaseOrder(string version)
    {
        var match = StagePattern().Match(version);
        if (!match.Success) return 0;
        var weight = match.Groups[1].Value.ToLowerInvariant() switch { "rc" => 3, "beta" => 2, _ => 1 };
        return weight * 10000 + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>本地记录只能证明已保存的同一缓存未变化，缺失或无效时必须重新下载。</summary>
    private static async Task<bool> MatchesLocalCacheAsync(string archivePath, string recordPath,
        GtnhPackRelease release, CancellationToken token)
    {
        try
        {
            await using var input = OpenRead(recordPath);
            if (input.Length > MaximumRecordBytes) return false;
            var record = await JsonSerializer.DeserializeAsync<LocalCacheRecord>(input, RecordOptions, token).ConfigureAwait(false);
            if (record is null || record.SchemaVersion != 1 || record.IntegrityKind != "LocalCacheOnly" ||
                record.ReleaseId != release.Id || record.Size != release.Size || record.Sha256 is not { Length: 64 } ||
                !record.Sha256.All(Uri.IsHexDigit)) return false;
            var actual = await ValidateArchiveAsync(archivePath, release, token).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(record.Sha256), Convert.FromHexString(actual));
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException or FormatException) { return false; }
    }

    /// <summary>重新验证精确大小、Prism 清单与安全布局，并计算独立的本地缓存摘要。</summary>
    private static async Task<string> ValidateArchiveAsync(string path, GtnhPackRelease release, CancellationToken token)
    {
        InstanceImportPaths.EnsureNoLinks(path);
        await using var input = OpenRead(path);
        if (input.Length != release.Size || input.Length is <= 0 or > MaximumArchiveBytes)
            throw new InvalidDataException("GTNH 镜像原包大小与目录不一致，不能导入。");
        var digest = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var plan = InstanceArchiveReader.Read(archive, release.FileName, token);
        if (!plan.Manifest.SourceFormat.Equals("Prism", StringComparison.Ordinal))
            throw new InvalidDataException("GTNH 镜像文件不是 Prism 客户端实例包，不能自动导入。");
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>本地摘要记录以新文件方式保存；中断留下的记录无法通过读取校验，不覆盖旧记录。</summary>
    private static async Task WriteLocalRecordAsync(string path, LocalCacheRecord record, CancellationToken token)
    {
        InstanceImportPaths.EnsureNoLinks(path);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(output, record, RecordOptions, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    /// <summary>读取期间禁止并发写入和删除，防止计算摘要与解析清单时内容变化。</summary>
    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>记录本地缓存基线，不含官方摘要标记，也不写入实例的远端文件校验要求。</summary>
    private sealed record LocalCacheRecord(int SchemaVersion, string ReleaseId, long Size, string Sha256, string IntegrityKind);

    /// <summary>调用方须先等待工作结束；仅释放服务自己创建的 HTTP 客户端。</summary>
    public void Dispose() { if (_ownsHttp) _http.Dispose(); _operationLock.Dispose(); }

    /// <summary>按目录表格行读取条目，不在相邻行之间拼接文件名和大小。</summary>
    [GeneratedRegex(@"<tr\b[^>]*>([\s\S]*?)</tr\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex RowPattern();
    /// <summary>分离单元格属性和内容，后续只接受 name 与 size 两类字段。</summary>
    [GeneratedRegex(@"<td\b([^>]*)>([\s\S]*?)</td\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CellPattern();
    /// <summary>统计名称单元格中的链接，多个候选链接会使该条目失效。</summary>
    [GeneratedRegex(@"<a\b([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LinkPattern();
    /// <summary>仅提取带引号的 HTML 属性，兼容单引号和双引号。</summary>
    [GeneratedRegex("([A-Za-z][A-Za-z0-9_-]*)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AttributePattern();
    /// <summary>精确读取字节数，拒绝换行、舍入单位及额外内容。</summary>
    [GeneratedRegex(@"\A([0-9]+) Bytes\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SizePattern();
    /// <summary>只接受数字版本和 Java 标记的固定客户端文件名，锚点不允许末尾换行。</summary>
    [GeneratedRegex(@"\AGT_New_Horizons_(?<version>[0-9]{1,4}(?:\.[0-9]{1,4}){1,3}(?:-(?<stage>alpha|beta|RC)-?[0-9]{1,4})?)_Java_(?<java>[0-9]{1,3}(?:-[0-9]{1,3})?)\.zip\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex FilePattern();
    /// <summary>提取正式版本之后的预发布阶段和序号，用于列表排序。</summary>
    [GeneratedRegex(@"-(alpha|beta|RC)-?([0-9]{1,4})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex StagePattern();
}
