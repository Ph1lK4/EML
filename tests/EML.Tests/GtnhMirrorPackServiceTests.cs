using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using EML.Core.Downloads;
using EML.Infrastructure.Minecraft.Gtnh;

namespace EML.Tests;

/// <summary>以离线目录和真实 ZIP 覆盖镜像解析、导入源验证与本地缓存的边界。</summary>
public sealed class GtnhMirrorPackServiceTests : IDisposable
{
    private const string StableName = "GT_New_Horizons_2.9.0_Java_17-26.zip";
    private const string BetaName = "GT_New_Horizons_2.9.0-beta-3_Java_17-25.zip";
    private const string RootUrl = "https://downloads.gaytnh.com/Multi_mc_downloads/";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-GtnhMirrorTests-" + Guid.NewGuid().ToString("N"));

    /// <summary>从 title 中读取准确字节数，兼容属性顺序和两种引号，忽略页面上舍入后的大小。</summary>
    [Fact]
    public async Task DirectoryUsesExactByteTitleAndNumericVersionOrdering()
    {
        var html = Row(StableName, "747848389") +
            "<tr><td data-id='1' class='name'><span>📦</span><a title='package' href='GT_New_Horizons_2.10.0_Java_17-26.zip'>任意显示名</a></td>" +
            "<td title='1234567 Bytes' class='size extra'>1.2 MB</td></tr>";
        using var handler = new Handler(_ => Html(html)); using var http = new HttpClient(handler);
        using var service = new GtnhMirrorPackService(_root, new Downloader([]), http);
        var releases = await service.GetReleasesAsync();
        Assert.Equal(["2.10.0", "2.9.0"], releases.Select(item => item.Version));
        Assert.Equal(1234567, releases[0].Size); Assert.Equal(747848389, releases[1].Size);
        Assert.Equal("17-26", releases[0].JavaVersion); Assert.False(releases[0].IsPrerelease);
        Assert.Equal(RootUrl, Assert.Single(handler.Requests).AbsoluteUri);
    }

    /// <summary>测试版须显式启用，目录 ID 保留 betas 前缀，正式版优先于相同版本的 RC 和 Beta。</summary>
    [Fact]
    public async Task PrereleaseOptionReadsBetaDirectoryAndKeepsStableFirst()
    {
        using var handler = new Handler(request => Html(request.RequestUri!.AbsolutePath.EndsWith("/betas/", StringComparison.Ordinal)
            ? Row(BetaName, "222") + Row("GT_New_Horizons_2.9.0-RC-2_Java_17-25.zip", "333")
            : Row(StableName, "111") + Row("GT_New_Horizons_2.10.0-beta-1_Java_17-26.zip", "444")));
        using var http = new HttpClient(handler); using var service = new GtnhMirrorPackService(_root, new Downloader([]), http);
        Assert.Equal(StableName, Assert.Single(await service.GetReleasesAsync()).Id);
        Assert.Single(handler.Requests);
        var all = await service.GetReleasesAsync(true);
        Assert.Equal(["2.10.0-beta-1", "2.9.0", "2.9.0-RC-2", "2.9.0-beta-3"], all.Select(item => item.Version));
        Assert.Equal("betas/" + BetaName, all[3].Id); Assert.True(all[3].IsPrerelease);
        Assert.Equal(RootUrl + "betas/", handler.Requests[2].AbsoluteUri);
    }

    /// <summary>目录只接收固定客户端文件，不采用服务端、latest、外站、子目录或附带查询参数的链接。</summary>
    [Theory]
    [InlineData("GT_New_Horizons_2.9.0_Client_Java_17-26.zip")]
    [InlineData("GT_New_Horizons_2.9.0_Server_Java_17-26.zip")]
    [InlineData("GT_New_Horizons_latest_Java_17-26.zip")]
    [InlineData("GT_New_Horizons_2.9.0-nightly_Java_17-26.zip")]
    [InlineData("GT_New_Horizons_2.9.0-April_Java_17-26.zip")]
    [InlineData("../GT_New_Horizons_2.9.0_Java_17-26.zip")]
    [InlineData("betas/GT_New_Horizons_2.9.0_Java_17-26.zip")]
    [InlineData("https://example.com/GT_New_Horizons_2.9.0_Java_17-26.zip")]
    [InlineData("https://downloads.gaytnh.com/other/GT_New_Horizons_2.9.0_Java_17-26.zip")]
    [InlineData("http://downloads.gaytnh.com/Multi_mc_downloads/GT_New_Horizons_2.9.0_Java_17-26.zip")]
    [InlineData("https://user@downloads.gaytnh.com/Multi_mc_downloads/GT_New_Horizons_2.9.0_Java_17-26.zip")]
    [InlineData("GT_New_Horizons_2.9.0_Java_17-26.zip?download=true")]
    [InlineData("GT_New_Horizons_2.9.0_Java_17-26.zip#file")]
    [InlineData("GT_New_Horizons_2.9.0_Java_17-26.zip\n")]
    [InlineData("https://downloads.gaytnh.com/Multi_mc_downloads/GT_New_Horizons_2.9.0_Java_17-26.zip\n")]
    public async Task UnsupportedDirectoryLinksAreExcluded(string href)
    {
        using var handler = new Handler(_ => Html(Row(href, "100"))); using var http = new HttpClient(handler);
        using var service = new GtnhMirrorPackService(_root, new Downloader([]), http);
        Assert.Empty(await service.GetReleasesAsync());
    }

    /// <summary>允许同目录绝对 HTTPS 链接，但外部显示名称不会改变文件身份。</summary>
    [Fact]
    public async Task SameDirectoryAbsoluteLinkProducesSimpleIdentity()
    {
        using var handler = new Handler(_ => Html(Row(RootUrl + StableName, "100"))); using var http = new HttpClient(handler);
        using var service = new GtnhMirrorPackService(_root, new Downloader([]), http);
        Assert.Equal(StableName, Assert.Single(await service.GetReleasesAsync()).Id);
    }

    /// <summary>缺少精确大小或超出上限的条目不进入可下载列表。</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("8589934593")]
    [InlineData("9223372036854775808")]
    [InlineData("713.2 MB")]
    [InlineData("100 Bytes\n")]
    public async Task InvalidDirectorySizesAreExcluded(string size)
    {
        using var handler = new Handler(_ => Html(Row(StableName, size))); using var http = new HttpClient(handler);
        using var service = new GtnhMirrorPackService(_root, new Downloader([]), http);
        Assert.Empty(await service.GetReleasesAsync());
    }

    /// <summary>同一文件的重复条目可合并，大小相互矛盾时必须拒绝而非任意择一。</summary>
    [Fact]
    public async Task ConflictingDuplicateDirectoryEntriesFail()
    {
        var html = Row(StableName, "100") + Row(StableName, "100");
        using var handler = new Handler(_ => Html(html)); using var http = new HttpClient(handler);
        using var service = new GtnhMirrorPackService(_root, new Downloader([]), http);
        Assert.Single(await service.GetReleasesAsync());
        html += Row(StableName, "101");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetReleasesAsync());
    }

    /// <summary>目录内容类型、重定向和声明或实际超过限制的响应均在解析前失败。</summary>
    [Theory]
    [InlineData("content-type")]
    [InlineData("declared-size")]
    [InlineData("streamed-size")]
    [InlineData("followed-redirect")]
    [InlineData("redirect-status")]
    public async Task UnsafeDirectoryResponseIsRejected(string kind)
    {
        using var handler = new Handler(_ =>
        {
            var response = Html(kind == "streamed-size" ? new string('x', 2 * 1024 * 1024 + 1) : Row(StableName, "100"));
            if (kind == "content-type") response.Content.Headers.ContentType = new("application/json");
            if (kind == "declared-size") response.Content.Headers.ContentLength = 2 * 1024 * 1024 + 1;
            if (kind == "streamed-size") response.Content.Headers.ContentLength = 1;
            if (kind == "followed-redirect") response.RequestMessage = new(HttpMethod.Get, "https://example.com/");
            if (kind == "redirect-status") response.StatusCode = HttpStatusCode.Redirect;
            return response;
        });
        using var http = new HttpClient(handler); using var service = new GtnhMirrorPackService(_root, new Downloader([]), http);
        if (kind == "redirect-status") await Assert.ThrowsAsync<HttpRequestException>(() => service.GetReleasesAsync());
        else await Assert.ThrowsAsync<InvalidDataException>(() => service.GetReleasesAsync());
    }

    /// <summary>公开下载入口在联网前拒绝任意路径、URL、查询参数和尾随换行。</summary>
    [Theory]
    [InlineData("../GT_New_Horizons_2.9.0_Java_17-26.zip")]
    [InlineData("betas/../GT_New_Horizons_2.9.0_Java_17-26.zip")]
    [InlineData("https://downloads.gaytnh.com/Multi_mc_downloads/GT_New_Horizons_2.9.0_Java_17-26.zip")]
    [InlineData("GT_New_Horizons_2.9.0_Java_17-26.zip?download=true")]
    [InlineData("GT_New_Horizons_2.9.0_Java_17-26.zip\n")]
    public async Task InvalidPublicReleaseIdFailsBeforeNetwork(string id)
    {
        using var handler = new Handler(_ => Html("")); using var http = new HttpClient(handler); var downloader = new Downloader([]);
        using var service = new GtnhMirrorPackService(_root, downloader, http);
        await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareAsync(id));
        Assert.Empty(handler.Requests); Assert.Empty(downloader.Requests);
    }

    /// <summary>准备下载时重新核对精确目录项，已消失的版本不能使用旧列表继续下载。</summary>
    [Fact]
    public async Task PreparationRequeriesAndRejectsDisappearedRelease()
    {
        var html = Row(StableName, "100");
        using var handler = new Handler(_ => Html(html)); using var http = new HttpClient(handler); var downloader = new Downloader([]);
        using var service = new GtnhMirrorPackService(_root, downloader, http);
        var release = Assert.Single(await service.GetReleasesAsync()); html = "<table></table>";
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(release.Id));
        Assert.Equal(2, handler.Requests.Count); Assert.Empty(downloader.Requests);
    }

    /// <summary>有效 Prism 原包逐字节保存并使用共享任务中心，未发布官方摘要时不伪造远端哈希。</summary>
    [Fact]
    public async Task ValidPrismPackageUsesDownloadCenterAndKeepsLocalDigestSeparate()
    {
        var bytes = PrismArchive(); var downloader = new Downloader(bytes); var progress = new ProgressRecorder();
        using var handler = new Handler(_ => Html(Row(StableName, bytes.Length.ToString()))); using var http = new HttpClient(handler);
        using var service = new GtnhMirrorPackService(_root, downloader, http);
        var result = await service.PrepareAsync(StableName, progress);
        Assert.Equal("Prism", result.ExpectedFormat); Assert.Contains("2.9.0", result.SuggestedName);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result.ArchivePath));
        var request = Assert.Single(downloader.Requests);
        Assert.Equal(RootUrl + StableName, Assert.Single(request.Urls)); Assert.Equal(bytes.Length, request.ExpectedSize);
        Assert.Equal(8L * 1024 * 1024 * 1024, request.MaximumBytes); Assert.Empty(request.Hashes);
        Assert.Equal("GTNH 本体 · 国内镜像", request.GroupName); Assert.Equal(DownloadPriority.High, request.Priority);
        using var record = JsonDocument.Parse(await File.ReadAllTextAsync(result.ArchivePath + ".local-sha256.json"));
        Assert.Equal("LocalCacheOnly", record.RootElement.GetProperty("IntegrityKind").GetString());
        Assert.Equal(64, record.RootElement.GetProperty("Sha256").GetString()!.Length);
        Assert.Equal(FileDownloadPhase.Completed, progress.Items[^1].Phase); Assert.False(progress.Items[^1].FromCache);
    }

    /// <summary>测试版下载严格使用测试目录，不能退回根目录同名文件。</summary>
    [Fact]
    public async Task BetaPreparationUsesExactBetaDirectory()
    {
        var bytes = PrismArchive(); var downloader = new Downloader(bytes);
        using var handler = new Handler(_ => Html(Row(BetaName, bytes.Length.ToString()))); using var http = new HttpClient(handler);
        using var service = new GtnhMirrorPackService(_root, downloader, http);
        await service.PrepareAsync("betas/" + BetaName);
        Assert.Equal(RootUrl + "betas/", Assert.Single(handler.Requests).AbsoluteUri);
        Assert.Equal(RootUrl + "betas/" + BetaName, Assert.Single(Assert.Single(downloader.Requests).Urls));
    }

    /// <summary>下载器的成功结果仍需实际大小、Prism 格式和安全布局复核，失败不建立可信缓存记录。</summary>
    [Theory]
    [InlineData("size")]
    [InlineData("not-zip")]
    [InlineData("generic")]
    [InlineData("traversal")]
    public async Task InvalidDownloadedArchiveFailsWithoutTrustedRecord(string kind)
    {
        var bytes = kind switch
        {
            "not-zip" => Encoding.UTF8.GetBytes("this is not a zip file"),
            "generic" => Archive(new() { [".minecraft/options.txt"] = "lang:zh_CN" }),
            "traversal" => PrismArchive(extraPath: "../escape.txt"),
            _ => PrismArchive()
        };
        var downloader = new Downloader(bytes); var expectedSize = bytes.Length + (kind == "size" ? 1 : 0);
        using var handler = new Handler(_ => Html(Row(StableName, expectedSize.ToString()))); using var http = new HttpClient(handler);
        using var service = new GtnhMirrorPackService(_root, downloader, http);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(StableName));
        var path = Assert.Single(downloader.Requests).DestinationPath;
        Assert.True(File.Exists(path)); Assert.False(File.Exists(path + ".local-sha256.json"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    /// <summary>再次启动服务也可复用完整原包，每次复用仍重新核对目录和本地摘要。</summary>
    [Fact]
    public async Task CompletedCacheIsReusedAcrossServiceInstances()
    {
        var bytes = PrismArchive(); var downloader = new Downloader(bytes); var progress = new ProgressRecorder();
        using var handler = new Handler(_ => Html(Row(StableName, bytes.Length.ToString()))); using var http = new HttpClient(handler);
        string firstPath;
        using (var first = new GtnhMirrorPackService(_root, downloader, http))
            firstPath = (await first.PrepareAsync(StableName)).ArchivePath;
        using var second = new GtnhMirrorPackService(_root, downloader, http);
        Assert.Equal(firstPath, (await second.PrepareAsync(StableName, progress)).ArchivePath);
        Assert.Single(downloader.Requests); Assert.Equal(2, handler.Requests.Count);
        Assert.True(progress.Items[^1].FromCache); Assert.Equal(FileDownloadPhase.Completed, progress.Items[^1].Phase);
    }

    /// <summary>等大小的缓存改动也不能靠文件长度蒙混通过，旧原包保留并下载到新路径。</summary>
    [Fact]
    public async Task SameSizeChangedCacheIsPreservedAndNotReused()
    {
        var bytes = PrismArchive("first"); var changed = PrismArchive("other"); Assert.Equal(bytes.Length, changed.Length);
        var downloader = new Downloader(bytes);
        using var handler = new Handler(_ => Html(Row(StableName, bytes.Length.ToString()))); using var http = new HttpClient(handler);
        using var service = new GtnhMirrorPackService(_root, downloader, http);
        var first = await service.PrepareAsync(StableName); await File.WriteAllBytesAsync(first.ArchivePath, changed);
        var second = await service.PrepareAsync(StableName);
        Assert.NotEqual(first.ArchivePath, second.ArchivePath); Assert.Equal(2, downloader.Requests.Count);
        Assert.Equal(changed, await File.ReadAllBytesAsync(first.ArchivePath)); Assert.Equal(bytes, await File.ReadAllBytesAsync(second.ArchivePath));
    }

    /// <summary>没有有效本地记录时不只凭大小接管旧文件，不会覆盖它或已有的损坏记录。</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("invalid-json")]
    [InlineData("empty-record")]
    [InlineData("wrong-kind")]
    public async Task MissingOrInvalidCacheRecordPreservesOldArchive(string kind)
    {
        var bytes = PrismArchive(); var downloader = new Downloader(bytes);
        using var handler = new Handler(_ => Html(Row(StableName, bytes.Length.ToString()))); using var http = new HttpClient(handler);
        using var service = new GtnhMirrorPackService(_root, downloader, http);
        var first = await service.PrepareAsync(StableName); var recordPath = first.ArchivePath + ".local-sha256.json";
        if (kind == "missing") File.Delete(recordPath);
        else if (kind == "invalid-json") await File.WriteAllTextAsync(recordPath, "{unfinished");
        else if (kind == "empty-record") await File.WriteAllTextAsync(recordPath, "{}");
        else await File.WriteAllTextAsync(recordPath, (await File.ReadAllTextAsync(recordPath)).Replace("LocalCacheOnly", "OfficialHash", StringComparison.Ordinal));
        var oldRecord = File.Exists(recordPath) ? await File.ReadAllTextAsync(recordPath) : null;
        var second = await service.PrepareAsync(StableName);
        Assert.NotEqual(first.ArchivePath, second.ArchivePath); Assert.Equal(2, downloader.Requests.Count);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first.ArchivePath));
        Assert.Equal(oldRecord, File.Exists(recordPath) ? await File.ReadAllTextAsync(recordPath) : null);
    }

    /// <summary>相同版本目录大小发生变化时采用新缓存身份，已被实例引用的旧原包继续保留。</summary>
    [Fact]
    public async Task ChangedRemoteSizeCreatesSeparateCacheIdentity()
    {
        var bytes = PrismArchive("first"); var downloader = new Downloader(bytes); var directorySize = bytes.Length;
        using var handler = new Handler(_ => Html(Row(StableName, directorySize.ToString()))); using var http = new HttpClient(handler);
        using var service = new GtnhMirrorPackService(_root, downloader, http);
        var first = await service.PrepareAsync(StableName);
        downloader.Bytes = PrismArchive("second package has a different size"); directorySize = downloader.Bytes.Length;
        var second = await service.PrepareAsync(StableName);
        Assert.NotEqual(first.ArchivePath, second.ArchivePath); Assert.Equal(bytes, await File.ReadAllBytesAsync(first.ArchivePath));
        Assert.Equal(downloader.Bytes, await File.ReadAllBytesAsync(second.ArchivePath)); Assert.Equal(2, downloader.Requests.Count);
    }

    /// <summary>未生成完整文件的取消保持目标路径稳定，下一次重试仍不伪造续传所需官方哈希。</summary>
    [Fact]
    public async Task CancelledDownloadCanRetryStableDestinationWithoutInventingHash()
    {
        var bytes = PrismArchive(); using var cancellation = new CancellationTokenSource(); var downloader = new Downloader(bytes);
        downloader.BeforeWrite = (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        using var handler = new Handler(_ => Html(Row(StableName, bytes.Length.ToString()))); using var http = new HttpClient(handler);
        using var service = new GtnhMirrorPackService(_root, downloader, http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(StableName, cancellationToken: cancellation.Token));
        downloader.BeforeWrite = null; var result = await service.PrepareAsync(StableName);
        Assert.Equal(downloader.Requests[0].DestinationPath, result.ArchivePath); Assert.Equal(2, downloader.Requests.Count);
        Assert.All(downloader.Requests, request => Assert.Empty(request.Hashes));
    }

    /// <summary>调用前已取消的查询和准备不访问网络或下载器。</summary>
    [Fact]
    public async Task AlreadyCancelledOperationsDoNotStartWork()
    {
        using var handler = new Handler(_ => Html("")); using var http = new HttpClient(handler); var downloader = new Downloader([]);
        using var service = new GtnhMirrorPackService(_root, downloader, http); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetReleasesAsync(cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(StableName, cancellationToken: cancellation.Token));
        Assert.Empty(handler.Requests); Assert.Empty(downloader.Requests);
    }

    /// <summary>释放服务不释放调用方提供的共享 HTTP 客户端。</summary>
    [Fact]
    public async Task DisposingServiceLeavesInjectedHttpClientUsable()
    {
        using var handler = new Handler(_ => Html("")); using var http = new HttpClient(handler);
        var service = new GtnhMirrorPackService(_root, new Downloader([]), http); service.Dispose();
        using var response = await http.GetAsync(RootUrl); Assert.True(response.IsSuccessStatusCode);
    }

    /// <summary>同一服务的并发准备串行处理目标发布，后一请求复用前一请求的完整结果。</summary>
    [Fact]
    public async Task ConcurrentPreparationDownloadsOnlyOnce()
    {
        var bytes = PrismArchive(); var downloader = new Downloader(bytes);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        downloader.BeforeWrite = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        using var handler = new Handler(_ => Html(Row(StableName, bytes.Length.ToString()))); using var http = new HttpClient(handler);
        using var service = new GtnhMirrorPackService(_root, downloader, http);
        var first = service.PrepareAsync(StableName); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = service.PrepareAsync(StableName); release.TrySetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Equal(results[0].ArchivePath, results[1].ArchivePath); Assert.Single(downloader.Requests);
    }

    /// <summary>生成目录真实格式的单行，显示大小故意不准确以防被误作完整性依据。</summary>
    private static string Row(string href, string size) =>
        $"<tr><td class=\"name\"><a href=\"{WebUtility.HtmlEncode(href)}\">download</a></td><td class=\"time\">-</td><td class=\"size\" title=\"{WebUtility.HtmlEncode(size)} Bytes\">0.1 MB</td></tr>";

    /// <summary>返回有明确 HTML 类型的内存响应，测试无需真实镜像连接。</summary>
    private static HttpResponseMessage Html(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/html") };

    /// <summary>构造包含实例清单和配置的合法 Prism 包，可额外插入危险布局用于回归。</summary>
    private static byte[] PrismArchive(string config = "test setting", string? extraPath = null)
    {
        var entries = new Dictionary<string, string>
        {
            ["mmc-pack.json"] = "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}",
            ["instance.cfg"] = "name=GTNH Test", [".minecraft/config/test.cfg"] = config
        };
        if (extraPath is not null) entries[extraPath] = "must not be extracted";
        return Archive(entries);
    }

    /// <summary>不压缩小型测试 ZIP，使等长度配置改动保持精确相同的原包大小。</summary>
    private static byte[] Archive(Dictionary<string, string> entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var pair in entries)
            {
                var entry = archive.CreateEntry(pair.Key, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(pair.Value);
            }
        return stream.ToArray();
    }

    /// <summary>仅清理测试自己创建的唯一临时目录。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    /// <summary>记录真实请求地址并提供可控目录响应。</summary>
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        /// <summary>响应可模拟重定向后的地址；默认保留原始请求地址。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request.RequestUri!);
            var result = response(request); result.RequestMessage ??= request; return Task.FromResult(result);
        }
    }

    /// <summary>只模拟共享下载器落盘，故意不执行大小和结构校验以验证服务的独立检查。</summary>
    private sealed class Downloader(byte[] bytes) : IFileDownloader
    {
        public byte[] Bytes { get; set; } = bytes;
        public List<FileDownloadRequest> Requests { get; } = [];
        public Func<FileDownloadRequest, CancellationToken, Task>? BeforeWrite { get; set; }
        /// <summary>记录完整下载请求，可在写入前挂起或取消。</summary>
        public async Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request,
            IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request);
            if (BeforeWrite is not null) await BeforeWrite(request, cancellationToken);
            await File.WriteAllBytesAsync(request.DestinationPath, Bytes, cancellationToken);
            progress?.Report(new(Bytes.Length, request.ExpectedSize, 1));
            return new(request.DestinationPath, Bytes.Length, false);
        }
    }

    /// <summary>同步记录进度，避免测试依赖线程池异步回调的到达顺序。</summary>
    private sealed class ProgressRecorder : IProgress<FileDownloadProgress>
    {
        public List<FileDownloadProgress> Items { get; } = [];
        /// <summary>保存服务已报告的传输、验证及缓存命中状态。</summary>
        public void Report(FileDownloadProgress value) => Items.Add(value);
    }
}
