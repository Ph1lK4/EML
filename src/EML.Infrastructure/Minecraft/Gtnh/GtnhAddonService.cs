using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EML.Core.Downloads;
using EML.Core.CurseForge;
using EML.Core.Modrinth;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;
using EML.Core.Minecraft.Mods;
using EML.Core.Minecraft.Mods.Online;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Infrastructure.Minecraft.Gtnh;

/// <summary>只查询人工整理的原作者发行来源，按固定资产下载，模组与资源使用各自的事务和来源记录。</summary>
public sealed partial class GtnhAddonService : IGtnhAddonService, IDisposable
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const long MaximumAssetBytes = 256L * 1024 * 1024;
    private readonly string _launcherRoot;
    private readonly IFileDownloader _downloader;
    private readonly IOnlineModRegistrar _registrar;
    private readonly IOnlineModCatalog? _onlineCatalog;
    private readonly IModrinthApiClient? _modrinth;
    private readonly ICurseForgeApiClient? _curseForge;
    private readonly IGtnhContentRegistrar? _contentRegistrar;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset Time, IReadOnlyList<GtnhAddonRelease> Releases)> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IssuedRelease> _issued = new(StringComparer.OrdinalIgnoreCase);
    private sealed record IssuedRelease(GtnhAddonRelease Release, string SelectionFingerprint, string Version,
        string ManifestFingerprint, DateTimeOffset Time);

    /// <summary>共享下载器与管理器；测试可注入受控 HTTP，不需要访问真实账号或实例。</summary>
    public GtnhAddonService(string launcherRoot, IFileDownloader downloader, IOnlineModRegistrar registrar, HttpClient? http = null,
        IOnlineModCatalog? onlineCatalog = null, IModrinthApiClient? modrinth = null,
        ICurseForgeApiClient? curseForge = null, IGtnhContentRegistrar? contentRegistrar = null)
    {
        _launcherRoot = Path.GetFullPath(launcherRoot); _downloader = downloader; _registrar = registrar;
        _onlineCatalog = onlineCatalog;
        _modrinth = modrinth; _curseForge = curseForge; _contentRegistrar = contentRegistrar;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public IReadOnlyList<GtnhAddon> Addons => GtnhAddonCatalog.Items;

    /// <summary>识别真实 GTNH 后读取有界发行列表；每个返回文件绑定本次目标实例及导入记录。</summary>
    public async Task<IReadOnlyList<GtnhAddonRelease>> GetReleasesAsync(string instancePath, string addonId, CancellationToken token = default)
    {
        var info = await Task.Run(() => RequireGtnh(instancePath), token).ConfigureAwait(false);
        var addon = Addons.SingleOrDefault(item => item.Id == addonId) ?? throw new ArgumentException("未收录的 GTNH 推荐内容。");
        if (!addon.CanQueryReleases) return [];
        IReadOnlyList<GtnhAddonRelease> releases;
        await _readLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(addon.Id, out var cached) && DateTimeOffset.UtcNow - cached.Time < TimeSpan.FromMinutes(2))
                releases = cached.Releases;
            else
            {
                var result = new List<GtnhAddonRelease>();
                if (addon.Provider == GtnhAddonProvider.Modrinth)
                    result.AddRange(await ReadModrinthReleasesAsync(addon, token).ConfigureAwait(false));
                else if (addon.Provider == GtnhAddonProvider.CurseForge && !IsModKind(addon.Kind))
                    result.AddRange(await ReadCurseForgeContentReleasesAsync(addon, token).ConfigureAwait(false));
                else if (addon.Provider == GtnhAddonProvider.CurseForge)
                    result.AddRange(await ReadCurseForgeReleasesAsync(addon, token).ConfigureAwait(false));
                else if (addon.ReleaseTag is { Length: > 0 } tag)
                {
                    using var document = await ReadJsonAsync($"https://api.github.com/repos/{addon.Repository}/releases/tags/{Uri.EscapeDataString(tag)}", token).ConfigureAwait(false);
                    if (document.RootElement.ValueKind != JsonValueKind.Object || Text(document.RootElement, "tag_name") != tag)
                        throw new InvalidDataException("发行响应与指定标签不一致。");
                    result.AddRange(ParseRelease(addon, document.RootElement));
                }
                else for (var page = 1; page <= 4; page++)
                {
                    using var document = await ReadJsonAsync($"https://api.github.com/repos/{addon.Repository}/releases?per_page=30&page={page}", token).ConfigureAwait(false);
                    if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("发行列表格式异常。");
                    foreach (var release in document.RootElement.EnumerateArray()) result.AddRange(ParseRelease(addon, release));
                    if (document.RootElement.GetArrayLength() < 30) break;
                }
                releases = Array.AsReadOnly(result.DistinctBy(item => (item.AssetId, item.PlatformVersionId, item.FileName)).OrderByDescending(item => item.Published).ToArray());
                _cache[addon.Id] = (DateTimeOffset.UtcNow, releases);
            }
        }
        finally { _readLock.Release(); }
        var manifest = InstanceManifestStore.Read(Path.Combine(info.InstancePath, InstanceImportManifest.FileName));
        var fingerprint = EmbeddedFileBaseline.ManifestFingerprint(manifest);
        foreach (var old in _issued.Where(pair => DateTimeOffset.UtcNow - pair.Value.Time > TimeSpan.FromMinutes(30)).Select(pair => pair.Key))
            _issued.TryRemove(old, out _);
        foreach (var release in releases)
            _issued[Key(info.InstancePath, release)] = new(release, SelectionFingerprint(release), info.Version, fingerprint, DateTimeOffset.UtcNow);
        return releases;
    }

    /// <summary>只安装此前展示且未改变的固定资产；下载到独立暂存区，双重校验成功后才交给管理器。</summary>
    public async Task<GtnhAddonInstallResult> InstallAsync(string instancePath, GtnhAddonRelease release, bool compatibilityConfirmed,
        IProgress<FileDownloadProgress>? progress = null, CancellationToken token = default)
    {
        string? staging = null; string? file = null; string? destination = null;
        var entered = false;
        try
        {
            ArgumentNullException.ThrowIfNull(release);
            if (!compatibilityConfirmed) throw new InvalidOperationException("请先阅读发行说明并确认适用于当前 GTNH 版本及客户端/服务端。");
            if (!release.CanInstall) throw new InvalidOperationException(release.Problem ?? "发行文件缺少可验证的官方摘要。");
            await _installLock.WaitAsync(token).ConfigureAwait(false); entered = true;
            var info = await Task.Run(() => RequireGtnh(instancePath), token).ConfigureAwait(false);
            if (!_issued.TryRemove(Key(info.InstancePath, release), out var issued) || issued.Release != release ||
                issued.SelectionFingerprint != SelectionFingerprint(release) ||
                DateTimeOffset.UtcNow - issued.Time > TimeSpan.FromMinutes(30) || issued.Version != info.Version)
                throw new InvalidOperationException("发行文件或目标已变化，或已使用此安装选择，请重新读取发行版本。");
            var manifest = InstanceManifestStore.Read(Path.Combine(info.InstancePath, InstanceImportManifest.FileName));
            if (issued.ManifestFingerprint != EmbeddedFileBaseline.ManifestFingerprint(manifest))
                throw new InvalidOperationException("实例导入记录已经变化，请重新选择目标和发行版本。");
            if (!IsModKind(release.Kind) && _contentRegistrar is null)
                throw new InvalidOperationException("资源与光影安装服务尚未配置，不能将此文件安装到 mods 目录。");
            using (InstanceOperationLease.Acquire(info.InstancePath)) { }
            var baseline = release.Provider is GtnhAddonProvider.CurseForge or GtnhAddonProvider.Modrinth
                ? SnapshotRequirement(release.DownloadRequirement ?? throw new InvalidDataException("缺少固定平台文件来源。")) with
                { Path = DestinationPath(manifest, release) }
                : new InstanceFileRequirement
            {
                Path = DestinationPath(manifest, release), Source = InstanceFileSource.DirectDownload,
                Origin = InstanceFileOrigin.GithubReleaseDownload, GithubReleaseOrigin = Origin(release),
                Role = InstanceFileRole.Content, Required = true, Size = release.Size,
                Hashes = new Dictionary<string, string> { ["sha256"] = release.Sha256 }, Downloads = [release.DownloadUrl]
            };
            baseline = SnapshotRequirement(baseline);
            if (IsModKind(release.Kind)) ModManagementStore.ValidateOnlineBaseline(baseline);
            else ValidateContentRequirement(baseline);
            destination = MigrationIO.SafePath(info.InstancePath, baseline.Path!);
            var relative = ".eml-runtime/gtnh-addons/" + Guid.NewGuid().ToString("N");
            staging = MigrationIO.SafePath(_launcherRoot, relative);
            if (Directory.Exists(staging) || File.Exists(staging)) throw new IOException("本次下载暂存位置被占用。");
            Directory.CreateDirectory(staging);
            file = MigrationIO.SafePath(_launcherRoot, relative + "/" + release.FileName);
            await _downloader.DownloadAsync(new()
            {
                DestinationPath = file, Urls = baseline.Downloads, ExpectedSize = baseline.Size,
                Hashes = InstanceFileIntegrity.HashesFor(baseline), MaximumBytes = MaximumAssetBytes,
                DisplayName = release.FileName, GroupName = Path.GetFileName(info.InstancePath) + " · GTNH 推荐内容"
            }, progress, token).ConfigureAwait(false);
            if (!await InstanceRuntimeInstaller.ExistingFileMatchesAsync(file, baseline, token).ConfigureAwait(false))
                throw new InvalidDataException("下载文件没有通过所选来源的官方大小与摘要校验。");
            // 下载期间实例可能被外部替换；提交前重新识别且核对原始清单，管理器再取得写锁检查冲突。
            var current = RequireGtnh(info.InstancePath);
            if (current.Version != issued.Version || issued.ManifestFingerprint != EmbeddedFileBaseline.ManifestFingerprint(
                InstanceManifestStore.Read(Path.Combine(current.InstancePath, InstanceImportManifest.FileName))))
                throw new InvalidOperationException("下载期间目标实例已变化，文件未安装。");
            if (IsModKind(release.Kind)) await _registrar.RegisterDownloadedAsync(info.InstancePath, file, baseline, token).ConfigureAwait(false);
            else await _contentRegistrar!.RegisterDownloadedAsync(info.InstancePath, file, baseline, release.Kind, token).ConfigureAwait(false);
            return new(true, false, $"已安装 {Path.GetFileName(destination)}。" + (IsModKind(release.Kind)
                ? "可在本地模组管理中禁用或移除；此结果不代表整合包兼容性检查已通过。"
                : "请在游戏中选择启用，并按作者说明核对所需模组或光影加载器。")) { DestinationPath = destination };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(false, true, "已取消下载与安装。") { DestinationPath = destination }; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException or HttpRequestException or UnauthorizedAccessException or TimeoutException)
        { return new(false, false, "安装未完成：" + ex.Message) { DestinationPath = destination }; }
        finally
        {
            if (file is not null) CleanStaging(file, staging!);
            if (entered) _installLock.Release();
        }
    }

    /// <summary>读取 GTNH 内容标识而不是实例名字，普通 Forge 1.7.10 也不能冒充 GTNH。</summary>
    private EML.Core.Minecraft.Migration.GtnhInstanceInfo RequireGtnh(string path) =>
        GtnhIdentity.Read(_launcherRoot, path) ?? throw new InvalidOperationException("该实例未被识别为 GTNH，请选择已导入的 GTNH 实例。");

    /// <summary>只接收官方 API 的有界 JSON；限流、改名跳转与超时均给出可操作提示。</summary>
    private async Task<JsonDocument> ReadJsonAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("EML-GTNH/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != new Uri(url))
                throw new InvalidDataException("GitHub 请求发生仓库或地址跳转，请核对原作者仓库后更新目录，不会自动切换下载来源。");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("GitHub 暂时拒绝请求或达到访问频率限制，请稍后重试。");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"无法读取原作者发行列表（HTTP {(int)response.StatusCode}），请稍后重试或查看项目主页。");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("发行列表超过允许大小。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("发行列表超过允许大小。");
                output.Write(buffer, 0, read);
            }
            return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("读取 GitHub 发行列表超时，请稍后重试。"); }
    }

    /// <summary>发行列表按内容类型选择 JAR 或 ZIP；无官方摘要的旧文件可见但不可自动安装。</summary>
    private static IEnumerable<GtnhAddonRelease> ParseRelease(GtnhAddon addon, JsonElement release)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) yield break;
        var id = Number(release, "id"); var tag = Text(release, "tag_name");
        if (id <= 0 || tag.Length == 0 || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) yield break;
        var notes = Text(release, "body"); if (notes.Length > 12000) notes = notes[..12000] + "\n（更多内容请查看原作者发行说明）";
        var prerelease = release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
        DateTimeOffset.TryParse(Text(release, "published_at"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var published);
        foreach (var asset in assets.EnumerateArray())
        {
            var name = Text(asset, "name"); var size = Number(asset, "size"); var assetId = Number(asset, "id");
            if (!HasExpectedExtension(name, addon.Kind) || new[] { "-sources", "-javadoc", "-dev", "-api", "-deobf", "-predowngrade" }
                .Any(suffix => name.Contains(suffix, StringComparison.OrdinalIgnoreCase))) continue;
            if (addon.AssetNamePrefix is { Length: > 0 } prefix && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (addon.AssetNamePattern is { Length: > 0 } pattern &&
                !Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) continue;
            var origin = new GithubReleaseFileOrigin { Repository = addon.Repository, Tag = tag, FileName = name, ReleaseId = id, AssetId = assetId };
            if (!InstanceFileIntegrity.ValidGithubOrigin(origin)) continue;
            var expected = InstanceFileIntegrity.GithubDownloadUrl(origin);
            if (!SameDownload(Text(asset, "browser_download_url"), expected)) continue;
            var digest = Text(asset, "digest");
            var hash = digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..].ToLowerInvariant() : "";
            var problem = hash.Length != 64 || !hash.All(Uri.IsHexDigit) ? "GitHub 未提供可验证的官方 SHA-256，请通过原作者页面核对并手动处理。" :
                size is <= 0 or > MaximumAssetBytes ? "发行文件大小无效或超过 256 MiB，本页不自动安装。" : null;
            yield return new()
            {
                AddonId = addon.Id, Kind = addon.Kind, Repository = addon.Repository, ReleaseId = id, AssetId = assetId,
                Tag = tag, FileName = name, Size = size, Sha256 = hash, DownloadUrl = expected,
                ReleaseUrl = "https://github.com/" + addon.Repository + "/releases/tag/" + Uri.EscapeDataString(tag),
                Published = published, Prerelease = prerelease, Notes = notes + "\n\n本页不自动安装前置依赖；请核对作者说明、当前 GTNH 版本及所需加载器。", Problem = problem
            };
        }
    }

    /// <summary>官方字段只允许指向所选仓库和标签的资产，不接受任意外部 URL。</summary>
    private static bool SameDownload(string value, string expected) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com" &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        Uri.UnescapeDataString(uri.AbsolutePath) == Uri.UnescapeDataString(new Uri(expected).AbsolutePath);
    /// <summary>不把缺失或类型错误的字段转换成有效标识。</summary>
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var part) && part.ValueKind == JsonValueKind.String ? part.GetString() ?? "" : "";
    /// <summary>发行和资产编号必须保持 64 位整数精度。</summary>
    private static long Number(JsonElement value, string name) => value.TryGetProperty(name, out var part) && part.ValueKind == JsonValueKind.Number && part.TryGetInt64(out var number) ? number : 0;
    /// <summary>签发记录绑定规范化实例路径和固定资产，不能换目标安装。</summary>
    private static string Key(string path, GtnhAddonRelease release) => Path.GetFullPath(path) + "|" + release.AddonId + "|" + release.Provider + "|" + release.ReleaseId + "|" + release.AssetId + "|" + release.PlatformVersionId + "|" + release.FileName;
    /// <summary>固定整个发行选择，防止可变字典在展示后被修改而绕过身份比较。</summary>
    private static string SelectionFingerprint(GtnhAddonRelease release) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(release)));
    /// <summary>持久化实际下载资产的固定身份，供补全、备份和迁移保留同一来源。</summary>
    private static GithubReleaseFileOrigin Origin(GtnhAddonRelease release) => new()
    {
        Repository = release.Repository, Tag = release.Tag, FileName = release.FileName,
        ReleaseId = release.ReleaseId, AssetId = release.AssetId
    };
    /// <summary>仅清理本次私有目录里的明确暂存文件，不递归删除实例或用户模组。</summary>
    private static void CleanStaging(string file, string directory)
    {
        try
        {
            InstanceImportPaths.EnsureNoLinks(file);
            if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    /// <summary>只释放本服务自己创建的 HTTP 客户端；共享下载器和管理器由窗口持有。</summary>
    public void Dispose() { if (_ownsHttp) _http.Dispose(); _readLock.Dispose(); _installLock.Dispose(); }
}
