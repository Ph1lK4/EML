using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Downloads;
using EML.Core.Java;
using EML.Infrastructure.Java;

namespace EML.Tests;

/// <summary>Java 下载和安装全部使用内存 HTTP、ZIP 与假探测，不联网也不执行 Java。</summary>
public sealed class JavaRuntimeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-java-runtime-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>目录查询限定版本架构及官方 SHA-256，忽略其他平台和预览版本。</summary>
    [Fact]
    public async Task CatalogUsesOfficialEndpointAndFiltersUnrelatedAssets()
    {
        var package = Package(Archive());
        using var handler = new Handler(JsonSerializer.Serialize(new[] { Asset(package), Asset(package, os: "linux"), Asset(package, architecture: "x86"), Asset(package, release: "ea") }));
        using var service = Service(handler, new Downloader(Archive()));
        var result = await service.GetPackagesAsync(21, "x64");
        Assert.Equal(package, Assert.Single(result));
        Assert.Equal("api.adoptium.net", handler.LastUri!.Host);
        Assert.Equal("/v3/assets/latest/21/hotspot", handler.LastUri.AbsolutePath);
        Assert.Contains("image_type=jdk", handler.LastUri.Query);
        Assert.Contains("architecture=x64", handler.LastUri.Query);
    }

    /// <summary>官方无此版本架构时返回空目录，不下载其他架构作为替代。</summary>
    [Fact]
    public async Task CatalogNotFoundReturnsEmpty()
    {
        using var handler = new Handler("", HttpStatusCode.NotFound);
        using var service = Service(handler, new Downloader([]));
        Assert.Empty(await service.GetPackagesAsync(25, "x86"));
    }

    /// <summary>目录请求不接受重定向、服务器错误或异常结构。</summary>
    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CatalogRejectsHttpErrors(HttpStatusCode status)
    {
        using var handler = new Handler("[]", status);
        using var service = Service(handler, new Downloader([]));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.GetPackagesAsync(21, "x64"));
        Assert.Equal(status, error.StatusCode);
    }

    /// <summary>缺失 SHA-256、非官方地址和超大目录不能进入可下载安装列表。</summary>
    [Theory]
    [InlineData("hash")]
    [InlineData("url")]
    [InlineData("size")]
    public async Task CatalogRejectsInvalidPackageMetadata(string kind)
    {
        var package = Package(Archive());
        package = kind switch
        {
            "hash" => package with { Sha256 = "" },
            "url" => package with { DownloadUrl = "https://example.test/java.zip" },
            _ => package with { Size = long.MaxValue }
        };
        using var handler = new Handler(JsonSerializer.Serialize(new[] { Asset(package) }));
        using var service = Service(handler, new Downloader([]));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetPackagesAsync(21, "x64"));
    }

    /// <summary>拒绝超大官方目录响应，不解析或安装其内容。</summary>
    [Fact]
    public async Task CatalogRejectsOversizedBody()
    {
        using var handler = new Handler(new string(' ', 4 * 1024 * 1024 + 1));
        using var service = Service(handler, new Downloader([]));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetPackagesAsync(21, "x64"));
    }

    /// <summary>正文停滞也受查询超时约束；用户取消与网络超时分别报告。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledCatalogBodyDistinguishesTimeoutFromUserCancel(bool userCancel)
    {
        using var cancellation = new CancellationTokenSource();
        if (userCancel) cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        using var service = new TemurinJavaRuntimeService(_root, new Downloader([]), new StalledBodyHandler(),
            (_, _) => throw new InvalidOperationException("查询不能运行 Java。"),
            userCancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(50));
        var query = service.GetPackagesAsync(21, "x64", cancellation.Token);
        if (userCancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.WaitAsync(TimeSpan.FromSeconds(10)));
        else
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => query.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("官方目录查询超时", error.Message);
        }
    }

    /// <summary>下载安装走公共传输器，发布路径、大小摘要和优先级均可核对。</summary>
    [Fact]
    public async Task InstallVerifiesArchiveAndPublishesManagedJava()
    {
        var bytes = Archive();
        var package = Package(bytes);
        var downloader = new Downloader(bytes);
        using var handler = new Handler("[]");
        using var service = Service(handler, downloader);
        var stages = new List<string>();
        var result = await service.InstallAsync(package, new ProgressSink(value => stages.Add(value.Stage)));

        Assert.StartsWith(service.InstallationRoot + Path.DirectorySeparatorChar, result.ExecutablePath);
        Assert.True(File.Exists(result.ExecutablePath));
        Assert.Equal("21.0.8", result.Version);
        var request = Assert.Single(downloader.Requests);
        Assert.Equal(package.Sha256, request.Hashes["sha256"]);
        Assert.Equal(package.Size, request.ExpectedSize);
        Assert.Equal("Java 环境", request.GroupName);
        Assert.Equal(DownloadPriority.High, request.Priority);
        Assert.Contains(stages, value => value.Contains("安装完成"));
        Assert.Empty(Directory.GetDirectories(service.InstallationRoot, ".install-*"));
    }

    /// <summary>同一官方包重复安装先验证本地文件再复用，不再次下载。</summary>
    [Fact]
    public async Task ExistingInstallationIsValidatedAndReused()
    {
        var bytes = Archive();
        var downloader = new Downloader(bytes);
        using var handler = new Handler("[]");
        using var service = Service(handler, downloader);
        var first = await service.InstallAsync(Package(bytes));
        var second = await service.InstallAsync(Package(bytes));
        Assert.Equal(first, second);
        Assert.Single(downloader.Requests);
    }

    /// <summary>已安装文件改变时拒绝复用并保留修改，不能覆盖用户现有 Java。</summary>
    [Fact]
    public async Task ExistingModifiedJavaIsPreservedAndRejected()
    {
        var bytes = Archive();
        var downloader = new Downloader(bytes);
        using var handler = new Handler("[]");
        using var service = Service(handler, downloader);
        var installed = await service.InstallAsync(Package(bytes));
        await File.WriteAllTextAsync(installed.ExecutablePath, "user-change");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(Package(bytes)));
        Assert.Equal("user-change", await File.ReadAllTextAsync(installed.ExecutablePath));
        Assert.Single(downloader.Requests);
    }

    /// <summary>未记录的 DLL 等额外文件不能在本地复用时静默进入运行环境。</summary>
    [Fact]
    public async Task ExistingUnexpectedFileIsPreservedAndRejected()
    {
        var bytes = Archive();
        using var handler = new Handler("[]");
        using var service = Service(handler, new Downloader(bytes));
        var installed = await service.InstallAsync(Package(bytes));
        var added = Path.Combine(Path.GetDirectoryName(installed.ExecutablePath)!, "unexpected.dll");
        await File.WriteAllTextAsync(added, "user-file");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(Package(bytes)));
        Assert.Equal("user-file", await File.ReadAllTextAsync(added));
    }

    /// <summary>下载器即使声称已校验，错误内容也不能解压或执行。</summary>
    [Fact]
    public async Task WrongArchiveHashIsRejectedBeforeProbe()
    {
        var bytes = Archive();
        var package = Package(bytes) with { Sha256 = new string('a', 64) };
        using var handler = new Handler("[]");
        using var service = new TemurinJavaRuntimeService(_root, new Downloader(bytes), handler,
            (_, _) => throw new InvalidOperationException("禁止探测未校验内容"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(package));
        Assert.Empty(Directory.GetDirectories(service.InstallationRoot));
    }

    /// <summary>不可信 ZIP 路径、重复路径及保留文件不得写入正式安装目录。</summary>
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("jdk/bin/java.exe:stream")]
    [InlineData("jdk/CON")]
    [InlineData("jdk/BIN/Java.exe")]
    [InlineData("jdk/.eml-java.json")]
    [InlineData("other/extra.txt")]
    public async Task InvalidArchiveLayoutIsRejected(string extra)
    {
        var bytes = Archive(extra);
        using var handler = new Handler("[]");
        using var service = Service(handler, new Downloader(bytes));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(Package(bytes)));
        Assert.Empty(Directory.GetDirectories(service.InstallationRoot));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    /// <summary>符号链接条目不能作为 Java 文件安装。</summary>
    [Fact]
    public async Task SymlinkEntryIsRejected()
    {
        var bytes = Archive("jdk/link", link: true);
        using var handler = new Handler("[]");
        using var service = Service(handler, new Downloader(bytes));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(Package(bytes)));
    }

    /// <summary>探测主版本或架构不匹配时不发布环境，也不保留半安装目录。</summary>
    [Theory]
    [InlineData("17.0.1", "x64")]
    [InlineData("21.0.8", "x86")]
    public async Task WrongRuntimeIdentityIsNotPublished(string version, string architecture)
    {
        var bytes = Archive();
        using var handler = new Handler("[]");
        using var service = new TemurinJavaRuntimeService(_root, new Downloader(bytes), handler,
            (path, _) => Task.FromResult(new JavaInstallation(path, version, architecture)));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(Package(bytes)));
        Assert.Empty(Directory.GetDirectories(service.InstallationRoot));
    }

    /// <summary>探测阶段取消会清理临时下载与解压文件，不发布 Java。</summary>
    [Fact]
    public async Task CanceledInstallCleansOnlyItsTemporaryFiles()
    {
        var bytes = Archive();
        using var cancel = new CancellationTokenSource();
        using var handler = new Handler("[]");
        using var service = new TemurinJavaRuntimeService(_root, new Downloader(bytes), handler,
            (_, _) => { cancel.Cancel(); return Task.FromCanceled<JavaInstallation>(cancel.Token); });
        Directory.CreateDirectory(service.InstallationRoot);
        var marker = Path.Combine(service.InstallationRoot, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InstallAsync(Package(bytes), cancellationToken: cancel.Token));
        Assert.Equal("keep", await File.ReadAllTextAsync(marker));
        Assert.Empty(Directory.GetDirectories(service.InstallationRoot));
    }

    /// <summary>同一服务同时安装相同包会串行复用完整安装，不生成重复环境。</summary>
    [Fact]
    public async Task ConcurrentInstallOfSamePackageDownloadsOnce()
    {
        var bytes = Archive();
        var downloader = new Downloader(bytes);
        using var handler = new Handler("[]");
        using var service = Service(handler, downloader);
        var results = await Task.WhenAll(service.InstallAsync(Package(bytes)), service.InstallAsync(Package(bytes)));
        Assert.Equal(results[0], results[1]);
        Assert.Single(downloader.Requests);
    }

    /// <summary>构造带受控探测结果的服务，不会运行测试 ZIP 中的文本 java.exe。</summary>
    private TemurinJavaRuntimeService Service(HttpMessageHandler handler, IFileDownloader downloader) => new(_root, downloader, handler,
        (path, _) => Task.FromResult(new JavaInstallation(path, "21.0.8", "x64")));

    /// <summary>为测试 ZIP 生成与官方结构相同的声明。</summary>
    private static JavaRuntimePackage Package(byte[] bytes) => new(21, "21.0.8+9.0.LTS", "x64", "jdk.zip",
        "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.8%2B9/jdk.zip", Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);

    /// <summary>生成最小 JDK 布局，可额外加入不可信条目。</summary>
    private static byte[] Archive(string? extra = null, bool link = false)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("jdk/bin/java.exe").Open())) writer.Write("fake-java");
            if (extra is not null)
            {
                var entry = zip.CreateEntry(extra);
                if (link) entry.ExternalAttributes = 0xa000 << 16;
                using var writer = new StreamWriter(entry.Open());
                writer.Write("test");
            }
        }
        return memory.ToArray();
    }

    /// <summary>构造官方目录字段，供过滤和校验用例重用。</summary>
    private static object Asset(JavaRuntimePackage package, string os = "windows", string architecture = "x64", string release = "ga") => new
    {
        release_type = release,
        version = new { major = package.MajorVersion, semver = package.Version },
        binary = new { os, architecture, image_type = "jdk", jvm_impl = "hotspot",
            package = new { name = package.FileName, link = package.DownloadUrl, checksum = package.Sha256, size = package.Size } }
    };

    /// <summary>只删除本测试随机创建的目录，不处理用户实例或已安装 Java。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    /// <summary>将内存 ZIP 写入请求指定的新路径，并记录下载中心所需信息。</summary>
    private sealed class Downloader(byte[] data) : IFileDownloader
    {
        public List<FileDownloadRequest> Requests { get; } = [];
        public async Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await File.WriteAllBytesAsync(request.DestinationPath, data, cancellationToken);
            progress?.Report(new(data.Length, data.Length, 1));
            return new(request.DestinationPath, data.Length, true);
        }
    }

    /// <summary>只返回静态 HTTP 数据，记录查询地址，不访问网络。</summary>
    private sealed class Handler(string text, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>同步记录测试进度，避免线程池回调晚于断言。</summary>
    private sealed class ProgressSink(Action<JavaRuntimeInstallProgress> report) : IProgress<JavaRuntimeInstallProgress>
    { public void Report(JavaRuntimeInstallProgress value) => report(value); }

    /// <summary>立即返回成功响应头，但正文永不自行完成。</summary>
    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody()) });
    }

    /// <summary>模拟响应正文中断，必须由同一个查询令牌取消读取。</summary>
    private sealed class StalledBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
