using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Tests;

/// <summary>验证含大量玩家文件的实例清单，以及本地保存和备份往返的一致大小限制。</summary>
public sealed class LargeInstanceManifestTests : IDisposable
{
    private const int PlatformLimit = 8 * 1024 * 1024;
    private const int InventoryLimit = 64 * 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-large-manifest-" + Guid.NewGuid().ToString("N"));

    /// <summary>大文件清单完整保存来源、官方摘要和包内基线，不因排版变化改变语义指纹。</summary>
    [Fact]
    public async Task LargeInventoryRoundTripPreservesSourcesAndFingerprints()
    {
        Directory.CreateDirectory(_root);
        var manifest = CreateLargeManifest();
        var path = Path.Combine(_root, InstanceImportManifest.FileName);

        await InstanceManifestStore.WriteAsync(path, manifest, default);

        var bytes = File.ReadAllBytes(path);
        Assert.InRange(bytes.Length, PlatformLimit + 1, InventoryLimit);
        Assert.DoesNotContain((byte)'\n', bytes);
        var saved = InstanceManifestStore.Read(path);
        Assert.Equal(manifest.Files.Count, saved.Files.Count);
        Assert.Equal(EmbeddedFileBaseline.ManifestFingerprint(manifest), EmbeddedFileBaseline.ManifestFingerprint(saved));
    }

    /// <summary>超过旧限制的清单经过真实导出和重新导入后，仍保留所有文件及各自来源要求。</summary>
    [Fact]
    public async Task LargeInventorySurvivesExportAndReimport()
    {
        var source = Path.Combine(_root, "instances", "Original");
        Directory.CreateDirectory(source);
        var manifest = CreateLargeManifest();
        await InstanceManifestStore.WriteAsync(Path.Combine(source, InstanceImportManifest.FileName), manifest, default);
        foreach (var file in manifest.Files.TakeLast(4))
        {
            var path = Path.Combine(source, file.Path!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "data");
        }
        var backup = Path.Combine(_root, "backup.zip");

        await new MinecraftInstanceManager(_root).ExportAsync(source, backup);
        using (var archive = ZipFile.OpenRead(backup))
            Assert.True(archive.GetEntry(InstanceImportManifest.FileName)!.Length > PlatformLimit);
        var restored = await new MinecraftInstanceImporter(_root).ImportAsync(backup, "Restored");

        Assert.Equal(manifest.Files.Count, restored.Manifest.Files.Count);
        // 导出会显式补上共享策略计算的 Role；来源和完整性字段必须全部保持。
        var expected = manifest.Files.Select(file => file with
        { Role = InstanceFileChangePolicy.GetRole(file, manifest.GameDirectory, manifest.SourceFormat) });
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(restored.Manifest.Files));
        foreach (var file in manifest.Files.TakeLast(4))
            Assert.Equal("data", File.ReadAllText(Path.Combine(restored.InstancePath, file.Path!)));
    }

    /// <summary>已有缩进清单仍可读取，重新保存后原包定位状态绑定的指纹保持不变。</summary>
    [Fact]
    public async Task LegacyIndentedManifestRemainsCompatibleWithSourceRecord()
    {
        Directory.CreateDirectory(_root);
        var manifest = new InstanceImportManifest { SourceArchive = "original.zip", Files = CreateLargeManifest().Files.TakeLast(4).ToArray() };
        var path = Path.Combine(_root, InstanceImportManifest.FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
        }));
        var originalArchive = Path.Combine(_root, manifest.SourceArchive);
        await InstanceImportSourceStore.WriteAsync(_root, manifest, originalArchive, default);

        await InstanceManifestStore.WriteAsync(path, InstanceManifestStore.Read(path), default);

        Assert.Equal(originalArchive, InstanceImportSourceStore.Read(_root, InstanceManifestStore.Read(path)));
    }

    /// <summary>放宽 EML 总清单上限不改变 Prism、Modrinth、CurseForge 平台元数据的 8 MiB 限制。</summary>
    [Theory]
    [InlineData("mmc-pack.json")]
    [InlineData("modrinth.index.json")]
    [InlineData("manifest.json")]
    public async Task OversizedPlatformMetadataIsStillRejected(string marker)
    {
        var archive = CreateOversizedArchive(marker, PlatformLimit + 1);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new MinecraftInstanceImporter(_root).InspectAsync(archive));
        Assert.Contains("8 MiB", error.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "instances")));
    }

    /// <summary>本地清单和备份清单共用明确的 64 MiB 上限，超限在解析或解压前失败。</summary>
    [Fact]
    public async Task OversizedInventoryIsRejectedLocallyAndInArchive()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, InstanceImportManifest.FileName);
        using (var file = File.Create(path)) file.SetLength(InventoryLimit + 1L);
        Assert.Contains("64 MiB", Assert.Throws<InvalidDataException>(() => InstanceManifestStore.Read(path)).Message);

        var archive = CreateOversizedArchive(InstanceImportManifest.FileName, InventoryLimit + 1);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new MinecraftInstanceImporter(_root).InspectAsync(archive));
        Assert.Contains("64 MiB", error.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "instances")));
    }

    /// <summary>生成超大清单时同样报错，不能输出后续读取器不接受的清单。</summary>
    [Fact]
    public void OversizedGeneratedInventoryIsRejected()
    {
        var manifest = new InstanceImportManifest { Notes = Enumerable.Repeat(new string('x', 1024), 65_536).ToArray() };
        Assert.Contains("64 MiB", Assert.Throws<InvalidDataException>(() => InstanceManifestStore.Serialize(manifest)).Message);
    }

    /// <summary>使用长地图路径模拟大量本地记录，并混合包内、官方、备用和用户替换来源。</summary>
    private static InstanceImportManifest CreateLargeManifest()
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("data")));
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("data")));
        var files = Enumerable.Range(0, 20_000).Select(index =>
        {
            var path = $".minecraft/journeymap/data/mp/a-server-with-a-long-display-name-and-address/dimension-with-a-long-name/day/region/tile-{index:D6}.png";
            return new InstanceFileRequirement { Path = path, ArchiveEntry = path, Source = InstanceFileSource.Embedded,
                Size = 4, Hashes = new Dictionary<string, string> { ["sha256"] = sha256 } };
        }).ToList();
        files.Add(new() { Path = ".minecraft/config/embedded.zip", ArchiveEntry = ".minecraft/config/embedded.zip",
            Source = InstanceFileSource.Embedded, Origin = InstanceFileOrigin.EmbeddedOverride, Size = 4,
            Hashes = new Dictionary<string, string> { ["sha256"] = sha256 },
            CurseForgeOrigin = new() { ProjectId = 1, FileId = 2, Size = 5, Hashes = new Dictionary<string, string> { ["sha1"] = new('a', 40) } } });
        files.Add(new() { Path = ".minecraft/mods/official.jar", Source = InstanceFileSource.CurseForge,
            Origin = InstanceFileOrigin.CurseForgeDownload, ProjectId = 3, FileId = 4, Size = 4,
            Hashes = new Dictionary<string, string> { ["sha1"] = sha1 } });
        files.Add(new() { Path = ".minecraft/mods/modrinth.jar", Source = InstanceFileSource.DirectDownload,
            Origin = InstanceFileOrigin.ModrinthDownload, Size = 4, Required = false,
            Downloads = ["https://example.com/mod.jar"], Hashes = new Dictionary<string, string> { ["sha1"] = sha1 } });
        files.Add(new() { Path = ".minecraft/mods/user.jar", Source = InstanceFileSource.Embedded,
            Origin = InstanceFileOrigin.UserLocalImport, Size = 4, Hashes = new Dictionary<string, string> { ["sha256"] = sha256 } });
        return new() { Name = "Original", Files = files };
    }

    /// <summary>分块生成可高度压缩的大 JSON，用于验证读取前的未压缩大小限制。</summary>
    private string CreateOversizedArchive(string marker, int length)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var output = archive.CreateEntry(marker).Open();
        output.Write("{}"u8);
        var buffer = Enumerable.Repeat((byte)' ', 8192).ToArray();
        for (var remaining = length - 2; remaining > 0;)
        {
            var count = Math.Min(remaining, buffer.Length);
            output.Write(buffer, 0, count);
            remaining -= count;
        }
        return path;
    }

    /// <summary>只清理由本测试创建的唯一临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
