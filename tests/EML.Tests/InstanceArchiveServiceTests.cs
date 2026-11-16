using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Catalog;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Catalog;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Standard;

namespace EML.Tests;

/// <summary>下载源准备使用虚拟平台、临时目录和真实导入器，不连接网络或触碰玩家实例。</summary>
public sealed class InstanceArchiveServiceTests
{
    /// <summary>缓存复用前重新向平台查询并检查摘要，保留原包使导入后的覆盖文件可以恢复。</summary>
    [Fact]
    public async Task Prepare_RetainsVerifiedPackAndRestoresEmbeddedFileAfterImport()
    {
        using var folder = new TestFolder();
        var bytes = ModrinthArchive();
        var catalog = new Catalog(Release(InstanceCatalogProvider.Modrinth, bytes));
        var downloader = new Downloader(bytes);
        var service = new InstanceArchiveService(folder.Path, catalog, downloader);
        var prepared = await service.PrepareAsync(InstanceCatalogProvider.Modrinth, "project", "version");
        var repeated = await service.PrepareAsync(InstanceCatalogProvider.Modrinth, "project", "version");
        Assert.Equal(prepared.ArchivePath, repeated.ArchivePath);
        Assert.Equal(2, catalog.Calls); Assert.Single(downloader.Requests);
        Assert.Equal("Modrinth", prepared.ExpectedFormat);
        Assert.Equal("Test Pack", prepared.SuggestedName);
        var imported = await new MinecraftInstanceImporter(folder.Path).ImportAsync(prepared.ArchivePath, "imported");
        var embedded = Assert.Single(imported.Manifest.Files, file => file.Path == ".minecraft/config/test.cfg");
        var embeddedPath = Path.Combine(imported.InstancePath, embedded.Path!);
        File.Delete(embeddedPath);
        Assert.True(await new EmbeddedFileRestorer(folder.Path).RestoreAsync(imported.InstancePath,
            imported.Manifest, embedded, CancellationToken.None));
        Assert.Equal("keep this override", await File.ReadAllTextAsync(embeddedPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(prepared.ArchivePath));
    }

    /// <summary>已有原包同大小损坏时另下载新文件，不覆盖可能被旧实例引用的文件。</summary>
    [Fact]
    public async Task Prepare_DoesNotOverwriteCorruptCache()
    {
        using var folder = new TestFolder();
        var bytes = ModrinthArchive();
        var downloader = new Downloader(bytes);
        var service = new InstanceArchiveService(folder.Path, new Catalog(Release(InstanceCatalogProvider.Modrinth, bytes)), downloader);
        var first = await service.PrepareAsync(InstanceCatalogProvider.Modrinth, "project", "version");
        var damaged = bytes.ToArray(); damaged[^1] ^= 1;
        await File.WriteAllBytesAsync(first.ArchivePath, damaged);
        var second = await service.PrepareAsync(InstanceCatalogProvider.Modrinth, "project", "version");
        Assert.NotEqual(first.ArchivePath, second.ArchivePath);
        Assert.Equal(damaged, await File.ReadAllBytesAsync(first.ArchivePath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(second.ArchivePath));
        Assert.Equal(2, downloader.Requests.Count);
    }

    /// <summary>远端文件名中的路径片段只作为展示输入，不会影响缓存落盘位置。</summary>
    [Fact]
    public async Task Prepare_DoesNotUseRemoteFileNameAsDestination()
    {
        using var folder = new TestFolder();
        var bytes = ModrinthArchive();
        var release = Release(InstanceCatalogProvider.Modrinth, bytes) with { FileName = "../../outside.mrpack" };
        var prepared = await new InstanceArchiveService(folder.Path, new Catalog(release), new Downloader(bytes))
            .PrepareAsync(InstanceCatalogProvider.Modrinth, "project", "version");
        Assert.Equal(Path.Combine(folder.Path, "cache", "instance-archives"), Path.GetDirectoryName(prepared.ArchivePath));
        Assert.DoesNotContain("outside", Path.GetFileName(prepared.ArchivePath));
    }

    /// <summary>平台返回另一个项目或版本时，在任何下载前拒绝。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prepare_RejectsMismatchedFixedIdentity(bool wrongProject)
    {
        using var folder = new TestFolder();
        var bytes = ModrinthArchive();
        var release = Release(InstanceCatalogProvider.Modrinth, bytes);
        release = wrongProject ? release with { ProjectId = "other" } : release with { Id = "other" };
        var downloader = new Downloader(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => new InstanceArchiveService(folder.Path, new Catalog(release), downloader)
            .PrepareAsync(InstanceCatalogProvider.Modrinth, "project", "version"));
        Assert.Empty(downloader.Requests);
    }

    /// <summary>下载器即使错误声称校验成功，服务仍对最终文件核对所有平台摘要和大小。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prepare_RejectsIncorrectBytesOrSize(bool differentSize)
    {
        using var folder = new TestFolder();
        var bytes = ModrinthArchive();
        var returned = differentSize ? bytes[..^1] : bytes.Select((value, index) => index == 0 ? (byte)(value ^ 1) : value).ToArray();
        var downloader = new Downloader(returned);
        await Assert.ThrowsAsync<InvalidDataException>(() => new InstanceArchiveService(folder.Path,
            new Catalog(Release(InstanceCatalogProvider.Modrinth, bytes)), downloader)
            .PrepareAsync(InstanceCatalogProvider.Modrinth, "project", "version"));
        Assert.Single(downloader.Requests);
    }

    /// <summary>普通 CurseForge 整合包必须有官方 SHA-1 和大小，不能降级为仅凭可下载 URL。</summary>
    [Theory]
    [InlineData("size")]
    [InlineData("hash")]
    [InlineData("sha256-only")]
    public async Task Prepare_RequiresOfficialCurseForgeIntegrity(string missing)
    {
        using var folder = new TestFolder();
        var bytes = CurseForgeArchive();
        var release = Release(InstanceCatalogProvider.CurseForge, bytes);
        release = missing switch
        {
            "size" => release with { Size = null },
            "hash" => release with { Hashes = new Dictionary<string, string>() },
            _ => release with { Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) } }
        };
        var downloader = new Downloader(bytes);
        await Assert.ThrowsAnyAsync<Exception>(() => new InstanceArchiveService(folder.Path, new Catalog(release), downloader)
            .PrepareAsync(InstanceCatalogProvider.CurseForge, "project", "version"));
        Assert.Empty(downloader.Requests);
    }

    /// <summary>已限制第三方下载的版本不能由缓存或猜测地址绕过限制。</summary>
    [Fact]
    public async Task Prepare_RejectsUnavailableReleaseBeforeDownload()
    {
        using var folder = new TestFolder();
        var bytes = CurseForgeArchive();
        var release = Release(InstanceCatalogProvider.CurseForge, bytes) with { Problem = "作者未开放第三方下载", DownloadUrls = [] };
        var downloader = new Downloader(bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new InstanceArchiveService(folder.Path, new Catalog(release), downloader)
            .PrepareAsync(InstanceCatalogProvider.CurseForge, "project", "version"));
        Assert.Empty(downloader.Requests);
    }

    /// <summary>客户端 CurseForge ZIP 沿用真实格式识别，向下载器传递完整官方校验要求。</summary>
    [Fact]
    public async Task Prepare_CurseForgeArchiveUsesOfficialSizeAndSha1()
    {
        using var folder = new TestFolder();
        var bytes = CurseForgeArchive();
        var release = Release(InstanceCatalogProvider.CurseForge, bytes);
        var downloader = new Downloader(bytes);
        var prepared = await new InstanceArchiveService(folder.Path, new Catalog(release), downloader)
            .PrepareAsync(InstanceCatalogProvider.CurseForge, "project", "version");
        Assert.Equal("CurseForge", prepared.ExpectedFormat);
        var request = Assert.Single(downloader.Requests);
        Assert.Equal(bytes.Length, request.ExpectedSize);
        Assert.Equal(release.Hashes["sha1"], request.Hashes["sha1"]);
    }

    /// <summary>有正确下载摘要也不能信任包内路径，目录穿越在准备导入源时被拒绝。</summary>
    [Fact]
    public async Task Prepare_RejectsUnsafeArchiveEntries()
    {
        using var folder = new TestFolder();
        var bytes = Archive(("../outside.txt", "unsafe"), ("modrinth.index.json", ModrinthManifest));
        await Assert.ThrowsAsync<InvalidDataException>(() => new InstanceArchiveService(folder.Path,
            new Catalog(Release(InstanceCatalogProvider.Modrinth, bytes)), new Downloader(bytes))
            .PrepareAsync(InstanceCatalogProvider.Modrinth, "project", "version"));
        Assert.False(File.Exists(Path.Combine(folder.Path, "outside.txt")));
    }

    /// <summary>另一平台的压缩包不能只靠扩展名进入所选平台的自动补全流程。</summary>
    [Fact]
    public async Task Prepare_RejectsMismatchedArchiveFormat()
    {
        using var folder = new TestFolder();
        var bytes = CurseForgeArchive();
        await Assert.ThrowsAsync<InvalidDataException>(() => new InstanceArchiveService(folder.Path,
            new Catalog(Release(InstanceCatalogProvider.Modrinth, bytes)), new Downloader(bytes))
            .PrepareAsync(InstanceCatalogProvider.Modrinth, "project", "version"));
    }

    /// <summary>取消传递给下载器，停止等待后不会开始导入或创建 bootstrap 文件。</summary>
    [Fact]
    public async Task Prepare_PropagatesCancellation()
    {
        using var folder = new TestFolder();
        var bytes = ModrinthArchive();
        using var cancellation = new CancellationTokenSource();
        var downloader = new Downloader(bytes) { BeforeWrite = () => cancellation.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InstanceArchiveService(folder.Path,
            new Catalog(Release(InstanceCatalogProvider.Modrinth, bytes)), downloader)
            .PrepareAsync(InstanceCatalogProvider.Modrinth, "project", "version", cancellationToken: cancellation.Token));
        Assert.Empty(Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>官方 JSON 原样包装为标准 ZIP，真实导入后可由运行解析器生成待下载依赖。</summary>
    [Fact]
    public async Task Prepare_VanillaBootstrapWorksWithExistingImporterAndRuntimeResolver()
    {
        using var folder = new TestFolder();
        var bytes = VanillaMetadata("1.20.1");
        var release = Release(InstanceCatalogProvider.Minecraft, bytes) with { Size = null };
        var downloader = new Downloader(bytes);
        var service = new InstanceArchiveService(folder.Path, new Catalog(release), downloader);
        var prepared = await service.PrepareAsync(InstanceCatalogProvider.Minecraft, "1.20.1", "1.20.1");
        Assert.Equal("Zip", prepared.ExpectedFormat);
        Assert.Equal("Minecraft 1.20.1", prepared.SuggestedName);
        Assert.Null(Assert.Single(downloader.Requests).ExpectedSize);
        Assert.Equal(8 * 1024 * 1024, downloader.Requests[0].MaximumBytes);
        using (var archive = ZipFile.OpenRead(prepared.ArchivePath))
        {
            var entry = Assert.Single(archive.Entries);
            Assert.Equal(".minecraft/versions/1.20.1/1.20.1.json", entry.FullName);
            await using var stream = entry.Open(); using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            Assert.Equal(bytes, memory.ToArray());
        }
        var imported = await new MinecraftInstanceImporter(folder.Path).ImportAsync(prepared.ArchivePath, "vanilla");
        var plan = new StandardMinecraftRuntimeResolver("windows", "x86_64").Resolve(imported.InstancePath, imported.Manifest);
        Assert.Empty(plan.BlockingIssues);
        Assert.Equal("1.20.1", plan.MinecraftVersion);
        Assert.Contains(plan.Files, file => file.Role == PrismRuntimeFileRole.MainJar && file.Requirement.Downloads.Count == 1);
        Assert.Contains(plan.Files, file => file.Role == PrismRuntimeFileRole.AssetIndex);
        Assert.False(File.Exists(Path.Combine(imported.GameDirectory, "versions", "1.20.1", "1.20.1.jar")));
        var again = await service.PrepareAsync(InstanceCatalogProvider.Minecraft, "1.20.1", "1.20.1");
        Assert.Equal(prepared.ArchivePath, again.ArchivePath); Assert.Single(downloader.Requests);
    }

    /// <summary>包装缓存被改写时保留旧包，重新包装已验证 JSON，不重新下载或覆盖旧内容。</summary>
    [Fact]
    public async Task Prepare_DoesNotOverwriteCorruptBootstrap()
    {
        using var folder = new TestFolder();
        var bytes = VanillaMetadata("1.20.1");
        var downloader = new Downloader(bytes);
        var service = new InstanceArchiveService(folder.Path, new Catalog(Release(InstanceCatalogProvider.Minecraft, bytes)), downloader);
        var first = await service.PrepareAsync(InstanceCatalogProvider.Minecraft, "1.20.1", "1.20.1");
        await File.WriteAllTextAsync(first.ArchivePath, "keep corrupt content");
        var second = await service.PrepareAsync(InstanceCatalogProvider.Minecraft, "1.20.1", "1.20.1");
        Assert.NotEqual(first.ArchivePath, second.ArchivePath);
        Assert.Equal("keep corrupt content", await File.ReadAllTextAsync(first.ArchivePath));
        Assert.Single(downloader.Requests);
    }

    /// <summary>摘要正确但 JSON 的实际版本错误时，不能把它包装为另一个原版版本。</summary>
    [Fact]
    public async Task Prepare_RejectsWrongVanillaMetadataId()
    {
        using var folder = new TestFolder();
        var bytes = VanillaMetadata("1.19.4");
        await Assert.ThrowsAsync<InvalidDataException>(() => new InstanceArchiveService(folder.Path,
            new Catalog(Release(InstanceCatalogProvider.Minecraft, bytes)), new Downloader(bytes))
            .PrepareAsync(InstanceCatalogProvider.Minecraft, "1.20.1", "1.20.1"));
        Assert.Empty(Directory.EnumerateFiles(folder.Path, "*.zip", SearchOption.AllDirectories));
    }

    /// <summary>作为 ZIP 内路径的原版 ID 必须是单一安全片段。</summary>
    [Theory]
    [InlineData("../1.20.1")]
    [InlineData("a/b")]
    [InlineData("CON")]
    public async Task Prepare_RejectsUnsafeVanillaId(string id)
    {
        using var folder = new TestFolder();
        var bytes = VanillaMetadata(id);
        var release = Release(InstanceCatalogProvider.Minecraft, bytes) with { Id = id, ProjectId = id };
        var downloader = new Downloader(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => new InstanceArchiveService(folder.Path, new Catalog(release), downloader)
            .PrepareAsync(InstanceCatalogProvider.Minecraft, id, id));
        Assert.Empty(downloader.Requests);
    }

    /// <summary>原版版本描述 URL 必须属于官方元数据主机，禁止自定义站点替换官方来源。</summary>
    [Fact]
    public async Task Prepare_RejectsUnofficialVanillaMetadataUrl()
    {
        using var folder = new TestFolder();
        var bytes = VanillaMetadata("1.20.1");
        var release = Release(InstanceCatalogProvider.Minecraft, bytes) with { DownloadUrls = ["https://example.test/version.json"] };
        var downloader = new Downloader(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => new InstanceArchiveService(folder.Path, new Catalog(release), downloader)
            .PrepareAsync(InstanceCatalogProvider.Minecraft, "1.20.1", "1.20.1"));
        Assert.Empty(downloader.Requests);
    }

    private const string ModrinthManifest = """
        {"formatVersion":1,"game":"minecraft","versionId":"v1","name":"Test Pack","files":[],"dependencies":{"minecraft":"1.20.1","fabric-loader":"0.16.0"}}
        """;

    /// <summary>生成含一个覆盖文件的真实格式 mrpack，支持验证恢复来源。</summary>
    private static byte[] ModrinthArchive() => Archive(("modrinth.index.json", ModrinthManifest), ("overrides/config/test.cfg", "keep this override"));

    /// <summary>生成可被生产解析器识别的 CurseForge 客户端清单。</summary>
    private static byte[] CurseForgeArchive() => Archive(("manifest.json", """
        {"manifestType":"minecraftModpack","manifestVersion":1,"name":"Test Pack","minecraft":{"version":"1.20.1","modLoaders":[]},"files":[],"overrides":"overrides"}
        """));

    /// <summary>生成真实 ZIP 字节，不创建磁盘文件或执行包内内容。</summary>
    private static byte[] Archive(params (string Path, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            {
                using var output = archive.CreateEntry(file.Path).Open();
                output.Write(Encoding.UTF8.GetBytes(file.Text));
            }
        return buffer.ToArray();
    }

    /// <summary>最小官方标准描述仍保留固定客户端与资源索引要求。</summary>
    private static byte[] VanillaMetadata(string id) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        id, mainClass = "net.minecraft.client.main.Main", libraries = Array.Empty<object>(),
        arguments = new { game = new[] { "--username", "${auth_player_name}" }, jvm = Array.Empty<string>() },
        javaVersion = new { majorVersion = 17 },
        downloads = new { client = new { url = "https://piston-data.mojang.com/v1/objects/client.jar", size = 100, sha1 = new string('a', 40) } },
        assetIndex = new { id = "1.20", url = "https://piston-meta.mojang.com/v1/packages/assets.json", size = 50, sha1 = new string('b', 40) }
    });

    /// <summary>平台返回的固定版本由测试字节计算真实摘要，URL 只用于断言。</summary>
    private static InstanceCatalogRelease Release(InstanceCatalogProvider provider, byte[] bytes) => new()
    {
        Provider = provider, ProjectId = provider == InstanceCatalogProvider.Minecraft ? "1.20.1" : "project",
        Id = provider == InstanceCatalogProvider.Minecraft ? "1.20.1" : "version", Name = "Test Pack", VersionNumber = "v1",
        FileName = provider switch { InstanceCatalogProvider.Modrinth => "test.mrpack", InstanceCatalogProvider.CurseForge => "test.zip", _ => "1.20.1.json" },
        Size = bytes.Length, Hashes = new Dictionary<string, string> { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(bytes)) },
        DownloadUrls = [provider == InstanceCatalogProvider.Minecraft ? "https://piston-meta.mojang.com/v1/packages/metadata.json" : "https://example.test/archive"]
    };

    /// <summary>只提供固定版本查询，记录重查次数以防缓存绕开平台状态。</summary>
    private sealed class Catalog(InstanceCatalogRelease release) : IInstanceCatalog
    {
        public int Calls { get; private set; }
        /// <summary>返回测试预设的固定版本，保留取消与调用计数。</summary>
        public Task<InstanceCatalogRelease> GetReleaseAsync(InstanceCatalogProvider provider, string projectId, string releaseId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(release); }
        /// <summary>准备导入源不应调用目录搜索。</summary>
        public Task<InstanceCatalogPage> SearchAsync(InstanceCatalogQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>准备导入源只用精确版本，不遍历版本列表。</summary>
        public Task<InstanceVersionPage> GetVersionsAsync(InstanceCatalogProject project, string gameVersion = "", string loader = "", int offset = 0,
            int limit = 20, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>故意不做摘要校验的下载替身，用来确认服务不会盲信下载器成功标记。</summary>
    private sealed class Downloader(byte[] bytes) : IFileDownloader
    {
        public List<FileDownloadRequest> Requests { get; } = [];
        public Action? BeforeWrite { get; init; }
        /// <summary>按新文件方式写入测试字节，允许测试在写入前请求取消。</summary>
        public async Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request); BeforeWrite?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            await using var file = new FileStream(request.DestinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await file.WriteAsync(bytes, cancellationToken);
            return new(request.DestinationPath, bytes.Length, true);
        }
    }

    /// <summary>每项测试独占临时目录，只清理自身生成的文件。</summary>
    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "eml-instance-archive-" + Guid.NewGuid().ToString("N"));
        /// <summary>生成唯一的测试目录。</summary>
        public TestFolder() => Directory.CreateDirectory(Path);
        /// <summary>释放仅由本测试生成的临时内容。</summary>
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
