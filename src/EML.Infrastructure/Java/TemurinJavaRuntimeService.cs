using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using EML.Core.Downloads;
using EML.Core.Java;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.IO;

namespace EML.Infrastructure.Java;

/// <summary>从 Eclipse Adoptium 查询 Windows JDK ZIP，校验并安装为启动器私有运行时。</summary>
public sealed class TemurinJavaRuntimeService : IJavaRuntimeService, IDisposable
{
    private const long MaximumArchiveBytes = 512L * 1024 * 1024;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 50000;
    private const int MaximumMetadataBytes = 4 * 1024 * 1024;
    private const string ReceiptName = ".eml-java.json";
    private readonly HttpClient _client;
    private readonly IFileDownloader _downloader;
    private readonly Func<string, CancellationToken, Task<JavaInstallation>> _probe;
    private readonly TimeSpan _metadataTimeout;
    private readonly SemaphoreSlim _installationGate = new(1, 1);
    public string InstallationRoot { get; }

    /// <summary>复用任务中心下载 ZIP；元数据客户端不携带账号信息，也不跟随重定向。</summary>
    public TemurinJavaRuntimeService(string launcherRoot, IFileDownloader downloader)
        : this(launcherRoot, downloader, new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, JavaRuntimeProbe.ProbeAsync) { }

    /// <summary>测试注入内存 HTTP 和版本探测，绝不执行测试压缩包中的程序。</summary>
    internal TemurinJavaRuntimeService(string launcherRoot, IFileDownloader downloader, HttpMessageHandler handler,
        Func<string, CancellationToken, Task<JavaInstallation>> probe, TimeSpan? metadataTimeout = null)
    {
        InstallationRoot = Path.Combine(Path.GetFullPath(launcherRoot), "runtimes", "java");
        _downloader = downloader;
        _probe = probe;
        _metadataTimeout = metadataTimeout ?? TimeSpan.FromSeconds(30);
        if (handler is HttpClientHandler ordinary) ordinary.AllowAutoRedirect = false;
        if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
        _client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("EML/1.0");
    }

    /// <summary>只接受与请求完全一致的正式 Windows HotSpot JDK ZIP，以及官方发布的 SHA-256。</summary>
    public async Task<IReadOnlyList<JavaRuntimePackage>> GetPackagesAsync(int majorVersion, string architecture,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_metadataTimeout);
        try { return await GetPackagesCoreAsync(majorVersion, architecture, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Java 官方目录查询超时，请检查网络后重试。", ex); }
    }

    /// <summary>同一个超时令牌覆盖响应头与正文读取，防止慢速正文无限占用查询。</summary>
    private async Task<IReadOnlyList<JavaRuntimePackage>> GetPackagesCoreAsync(int majorVersion, string architecture,
        CancellationToken cancellationToken)
    {
        ValidateMajor(majorVersion);
        architecture = JavaRuntimeProbe.NormalizeArchitecture(architecture);
        var url = $"https://api.adoptium.net/v3/assets/latest/{majorVersion}/hotspot?architecture={architecture}&image_type=jdk&os=windows&vendor=eclipse";
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Java 官方目录请求失败（HTTP {(int)response.StatusCode}）。请检查网络后重试。", null, response.StatusCode);
        var bytes = await ReadLimitedAsync(response.Content, MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Java 官方目录格式不正确。");
        var packages = new List<JavaRuntimePackage>();
        foreach (var asset in document.RootElement.EnumerateArray())
        {
            if (!asset.TryGetProperty("binary", out var binary) || !asset.TryGetProperty("version", out var version) ||
                Text(binary, "os") != "windows" || Text(binary, "image_type") != "jdk" || Text(binary, "jvm_impl") != "hotspot" ||
                Text(binary, "architecture") != architecture || !version.TryGetProperty("major", out var major) ||
                !major.TryGetInt32(out var number) || number != majorVersion) continue;
            if (asset.TryGetProperty("release_type", out var releaseType) && releaseType.GetString() != "ga") continue;
            var semantic = Text(version, "semver");
            if (semantic.Contains("-ea", StringComparison.OrdinalIgnoreCase)) continue;
            if (!binary.TryGetProperty("package", out var item) || !item.TryGetProperty("size", out var size) || !size.TryGetInt64(out var sizeBytes))
                throw new InvalidDataException("Java 官方目录缺少下载信息。");
            var package = new JavaRuntimePackage(majorVersion, semantic, architecture, Text(item, "name"),
                Text(item, "link"), Text(item, "checksum"), sizeBytes);
            ValidatePackage(package);
            packages.Add(package);
        }
        return packages.DistinctBy(package => package.Sha256, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>串行安装，ZIP 完整性、解压布局与 Java 版本全部验证成功后才发布目录。</summary>
    public async Task<JavaInstallation> InstallAsync(JavaRuntimePackage package,
        IProgress<JavaRuntimeInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidatePackage(package);
        await _installationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? work = null;
        try
        {
            InstanceImportPaths.EnsureNoLinks(InstallationRoot);
            Directory.CreateDirectory(InstallationRoot);
            var target = InstanceImportPaths.Within(InstallationRoot,
                $"temurin-{package.MajorVersion}-{package.Architecture}-{package.Sha256.ToLowerInvariant()}");
            if (Directory.Exists(target))
            {
                progress?.Report(new("正在验证已安装的 Java……"));
                return await VerifyInstalledAsync(target, package, cancellationToken).ConfigureAwait(false);
            }
            if (File.Exists(target)) throw new IOException("Java 安装目录被文件占用：" + target);
            work = InstanceImportPaths.Within(InstallationRoot, ".install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var archive = Path.Combine(work, "runtime.zip");
            progress?.Report(new("正在下载 Java……", 0));
            await _downloader.DownloadAsync(new FileDownloadRequest
            {
                DestinationPath = archive, Urls = [package.DownloadUrl], ExpectedSize = package.Size,
                MaximumBytes = MaximumArchiveBytes, Hashes = new Dictionary<string, string> { ["sha256"] = package.Sha256 },
                DisplayName = package.DisplayName, GroupName = "Java 环境", Priority = DownloadPriority.High
            }, new InlineProgress<FileDownloadProgress>(value => progress?.Report(new("正在下载并校验 Java……", value.Percent))), cancellationToken).ConfigureAwait(false);
            await VerifyArchiveAsync(archive, package, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("正在安全解压 Java……"));
            var extracted = await ExtractAsync(archive, Path.Combine(work, "content"), cancellationToken).ConfigureAwait(false);
            var executable = Path.Combine(extracted.Root, "bin", "java.exe");
            if (!File.Exists(executable)) throw new InvalidDataException("Java 压缩包缺少 bin/java.exe。");
            progress?.Report(new("正在验证 Java 版本与架构……"));
            var installation = await ProbeCompatibleAsync(executable, package, cancellationToken).ConfigureAwait(false);
            var receipt = new InstallationReceipt(package.Sha256.ToLowerInvariant(), package.MajorVersion, package.Architecture, extracted.Files);
            await using (var output = new FileStream(Path.Combine(extracted.Root, ReceiptName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(output, receipt, cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            InstanceImportPaths.EnsureNoLinks(target);
            Directory.Move(extracted.Root, target);
            progress?.Report(new("Java 安装完成。", 100));
            return installation with { ExecutablePath = Path.Combine(target, "bin", "java.exe") };
        }
        finally
        {
            try { if (work is not null) DeleteOwnedTemporary(work); }
            finally { _installationGate.Release(); }
        }
    }

    /// <summary>即使注入下载器错误报告成功，也必须按官方大小与 SHA-256 复核 ZIP。</summary>
    private static async Task VerifyArchiveAsync(string archive, JavaRuntimePackage package, CancellationToken token)
    {
        InstanceImportPaths.EnsureNoLinks(archive);
        await using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (input.Length != package.Size || !Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false))
            .Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Java 压缩包大小或 SHA-256 校验失败。");
    }

    /// <summary>验证全部路径后解压；拒绝重复、符号链接、越界、设备名和异常解压体积。</summary>
    private static async Task<(string Root, RuntimeFile[] Files)> ExtractAsync(string archive, string destination, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count is 0 or > MaximumEntries) throw new InvalidDataException("Java 压缩包文件数量异常。");
        var paths = new List<(ZipArchiveEntry Entry, string Path, bool Directory)>();
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var path = InstanceImportPaths.Relative(directory ? entry.FullName[..^1] : entry.FullName);
            var mode = (entry.ExternalAttributes >> 16) & 0xf000;
            if (mode is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Java 压缩包包含链接或特殊文件。");
            if (entry.Length > MaximumExpandedBytes || directory && entry.Length != 0 ||
                (total += entry.Length) > MaximumExpandedBytes)
                throw new InvalidDataException("Java 压缩包解压大小超过限制。");
            paths.Add((entry, path, directory));
        }
        InstanceImportPaths.ValidateLayout(paths.Select(item => (item.Path, item.Directory)));
        var roots = paths.Select(item => item.Path.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length != 1 || paths.Any(item => !item.Directory && !item.Path.Contains('/')))
            throw new InvalidDataException("Java 压缩包必须包含一个独立的 JDK 根目录。");
        var root = InstanceImportPaths.Within(destination, roots[0]);
        if (paths.Any(item => item.Path.Equals(roots[0] + "/" + ReceiptName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Java 压缩包包含保留的安装记录文件。");
        var files = new List<RuntimeFile>();
        foreach (var item in paths)
        {
            token.ThrowIfCancellationRequested();
            var path = InstanceImportPaths.Within(destination, item.Path);
            InstanceImportPaths.EnsureNoLinks(path);
            if (item.Directory) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = item.Entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long length = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                length += count;
                if (length > item.Entry.Length) throw new InvalidDataException("Java ZIP 条目实际大小超过声明。");
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                digest.AppendData(buffer, 0, count);
            }
            if (length != item.Entry.Length) throw new InvalidDataException("Java ZIP 条目内容不完整。");
            files.Add(new(item.Path[(roots[0].Length + 1)..], length, Convert.ToHexStringLower(digest.GetHashAndReset())));
        }
        return (root, files.ToArray());
    }

    /// <summary>复用安装前逐项校验本地基线；发现修改保留原目录并明确失败，不覆盖用户环境。</summary>
    private async Task<JavaInstallation> VerifyInstalledAsync(string root, JavaRuntimePackage package, CancellationToken token)
    {
        InstanceImportPaths.EnsureNoLinks(root);
        var path = Path.Combine(root, ReceiptName);
        InstanceImportPaths.EnsureNoLinks(path);
        if (!File.Exists(path)) throw new IOException("现有 Java 安装缺少校验记录，请手动检查此目录：" + root);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        if (input.Length > 16 * 1024 * 1024) throw new InvalidDataException("Java 安装记录超过大小限制。");
        var receipt = await JsonSerializer.DeserializeAsync<InstallationReceipt>(input, cancellationToken: token).ConfigureAwait(false);
        if (receipt is null || receipt.ArchiveSha256 != package.Sha256.ToLowerInvariant() || receipt.MajorVersion != package.MajorVersion ||
            receipt.Architecture != package.Architecture || receipt.Files is not { Length: > 0 and <= MaximumEntries })
            throw new InvalidDataException("已有 Java 安装记录与官方包不一致：" + root);
        InstanceImportPaths.ValidateLayout(receipt.Files.Select(file => (file.Path, false)));
        if (!receipt.Files.Any(file => file.Path.Equals("bin/java.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Java 安装记录未包含可执行文件。");
        var expectedPaths = receipt.Files.Select(file => file.Path).Append(ReceiptName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var full in EnumerateRegularFiles(root))
            if (!expectedPaths.Contains(Path.GetRelativePath(root, full).Replace('\\', '/')))
                throw new InvalidDataException("Java 安装目录包含未记录文件，已保留原文件：" + full);
        foreach (var file in receipt.Files)
        {
            token.ThrowIfCancellationRequested();
            var full = InstanceImportPaths.Within(root, file.Path);
            InstanceImportPaths.EnsureNoLinks(full);
            await using var content = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (content.Length != file.Size || !Convert.ToHexStringLower(await SHA256.HashDataAsync(content, token).ConfigureAwait(false))
                .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("已安装 Java 文件校验失败，已保留原文件：" + full);
        }
        return await ProbeCompatibleAsync(Path.Combine(root, "bin", "java.exe"), package, token).ConfigureAwait(false);
    }

    /// <summary>安装不能把其他主版本或架构伪装成已满足当前实例需求。</summary>
    private async Task<JavaInstallation> ProbeCompatibleAsync(string executable, JavaRuntimePackage package, CancellationToken token)
    {
        var result = await _probe(executable, token).ConfigureAwait(false);
        if (JavaRuntimeProbe.MajorVersion(result.Version) != package.MajorVersion ||
            JavaRuntimeProbe.NormalizeArchitecture(result.Architecture) != package.Architecture)
            throw new InvalidDataException("下载的 Java 实际版本或架构不符合官方包声明。");
        return result with { ExecutablePath = executable };
    }

    /// <summary>拒绝非官方发布地址、不可信文件名、缺失摘要及过大文件，在写盘前完成检查。</summary>
    private static void ValidatePackage(JavaRuntimePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidateMajor(package.MajorVersion);
        if (JavaRuntimeProbe.NormalizeArchitecture(package.Architecture) != package.Architecture ||
            string.IsNullOrWhiteSpace(package.Version) || package.Version.Length > 100 || package.Version.Any(char.IsControl) ||
            WindowsFileNameRules.InvalidReason(package.FileName) is not null || !package.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            package.Sha256.Length != 64 || !package.Sha256.All(Uri.IsHexDigit) || package.Size is <= 0 or > MaximumArchiveBytes)
            throw new InvalidDataException("Java 安装包元数据不完整或超出限制。");
        if (!Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.StartsWith($"/adoptium/temurin{package.MajorVersion}-binaries/releases/download/", StringComparison.Ordinal) ||
            !Uri.UnescapeDataString(uri.AbsolutePath.Split('/')[^1]).Equals(package.FileName, StringComparison.Ordinal))
            throw new InvalidDataException("Java 下载地址不是对应版本的 Adoptium 官方发布文件。");
    }

    /// <summary>本期提供 Minecraft 常见的五个长期支持主版本，不偷偷改用其他主版本。</summary>
    private static void ValidateMajor(int major)
    {
        if (major is not (8 or 11 or 17 or 21 or 25)) throw new ArgumentOutOfRangeException(nameof(major), "支持下载 Java 8、11、17、21、25。");
    }

    /// <summary>读取小型目录响应，同时限制响应头声明和实际流读取量。</summary>
    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("Java 官方目录超过大小限制。");
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + read > maximum) throw new InvalidDataException("Java 官方目录超过大小限制。");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    /// <summary>元数据字段只允许字符串，其他类型按不完整目录处理。</summary>
    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : "";

    /// <summary>只删除本次随机临时目录；遇到链接保留现场，不递归到链接目标。</summary>
    private void DeleteOwnedTemporary(string path)
    {
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) != InstallationRoot || !Path.GetFileName(full).StartsWith(".install-", StringComparison.Ordinal))
            throw new IOException("拒绝清理非本次 Java 临时目录。");
        if (!Directory.Exists(full)) return;
        InstanceImportPaths.EnsureNoLinks(full);
        _ = EnumerateRegularFiles(full).ToArray();
        Directory.Delete(full, recursive: true);
    }

    /// <summary>逐层检查后再进入目录，绝不递归到符号链接或目录联接的目标。</summary>
    private static IEnumerable<string> EnumerateRegularFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            InstanceImportPaths.EnsureNoLinks(current);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                InstanceImportPaths.EnsureNoLinks(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
                else yield return entry;
            }
        }
    }

    /// <summary>应用关闭并等待安装结束后释放目录查询客户端。</summary>
    public void Dispose() => _client.Dispose();
    private sealed record RuntimeFile(string Path, long Size, string Sha256);
    private sealed record InstallationReceipt(string ArchiveSha256, int MajorVersion, string Architecture, RuntimeFile[] Files);
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        /// <summary>在传输回调线程转交阶段信息，UI 自行负责调度。</summary>
        public void Report(T value) => report(value);
    }
}
