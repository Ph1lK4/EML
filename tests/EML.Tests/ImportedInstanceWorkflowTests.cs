using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Prism;

namespace EML.Tests;

/// <summary>跨导入、扫描、生成和管理验证目录与版本契约，所有数据限于独立临时目录。</summary>
public sealed class ImportedInstanceWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-import-workflow-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>真实导入自定义布局后，重命名、导出和回收均认可同一个已验证游戏目录。</summary>
    [Theory]
    [InlineData("data/game")]
    [InlineData(".")]
    public async Task ImportedCustomGameDirectorySupportsAllManagementOperations(string gameDirectory)
    {
        var imported = await ImportCustomLayoutAsync(gameDirectory);
        var scanned = Assert.Single(await ScanAsync());
        Assert.Equal(imported.GameDirectory, scanned.GameDirectory);
        var recycled = Path.Combine(_root, "recycled-test-instance");
        var manager = new MinecraftInstanceManager(_root, path => Directory.Move(path, recycled));

        var renamed = await manager.RenameAsync(imported.InstancePath, "Renamed");
        var current = Assert.Single(await ScanAsync());
        Assert.Equal(renamed, current.InstancePath);
        Assert.Equal("Renamed", current.Name);
        var gameRelative = gameDirectory == "." ? "config/example.cfg" : gameDirectory + "/config/example.cfg";
        Assert.Equal("original configuration", File.ReadAllText(Path.Combine(renamed, gameRelative)));

        var backup = Path.Combine(_root, "custom-backup.zip");
        await manager.ExportAsync(renamed, backup);
        using (var archive = ZipFile.OpenRead(backup))
        {
            Assert.NotNull(archive.GetEntry(InstanceImportManifest.FileName));
            Assert.NotNull(archive.GetEntry(gameRelative));
        }
        await manager.DeleteAsync(renamed);
        Assert.False(Directory.Exists(renamed));
        Assert.Equal("original configuration", File.ReadAllText(Path.Combine(recycled, gameRelative)));

        var restored = await new MinecraftInstanceImporter(_root).ImportAsync(backup, "Restored");
        Assert.Equal(gameDirectory, restored.Manifest.GameDirectory);
        Assert.Equal(restored.GameDirectory, Assert.Single(await ScanAsync()).GameDirectory);
    }

    /// <summary>有效清单仍可识别暂缺游戏目录的实例，允许用户重命名和管理待修复内容。</summary>
    [Fact]
    public async Task MissingCustomGameDirectoryDoesNotInvalidateInstanceIdentity()
    {
        var imported = await ImportCustomLayoutAsync("data/game");
        Directory.Move(imported.GameDirectory, Path.Combine(_root, "temporarily-moved-game"));

        var renamed = await new MinecraftInstanceManager(_root).RenameAsync(imported.InstancePath, "NeedsRepair");

        Assert.True(Directory.Exists(renamed));
        Assert.Contains("导入清单指定的游戏目录不存在。", Assert.Single(await ScanAsync()).Warnings);
    }

    /// <summary>已有传统目录标记不能绕过损坏、越界或不支持版本的导入清单。</summary>
    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":2,\"gameDirectory\":\"data/game\"}")]
    [InlineData("{\"gameDirectory\":\"../outside\"}")]
    public async Task InvalidManifestBlocksAllManagementOperations(string manifestJson)
    {
        var imported = await ImportCustomLayoutAsync("data/game");
        Directory.CreateDirectory(Path.Combine(imported.InstancePath, ".minecraft"));
        File.WriteAllText(Path.Combine(imported.InstancePath, InstanceImportManifest.FileName), manifestJson);

        await AssertManagementRejectedAsync(imported.InstancePath);

        var scanned = Assert.Single(await ScanAsync());
        Assert.Equal("未知", scanned.Format);
        Assert.Empty(scanned.Versions);
    }

    /// <summary>自定义游戏目录的中间联接必须同时被扫描器和三种管理操作拒绝。</summary>
    [Fact]
    public async Task LinkedCustomGameDirectoryIsNotAcceptedByManagement()
    {
        var imported = await ImportCustomLayoutAsync("data/game");
        var link = Path.Combine(imported.InstancePath, "data");
        var outside = Path.Combine(_root, "link-target");
        Directory.Move(link, outside);
        CreateDirectoryLink(link, outside);
        try
        {
            await AssertManagementRejectedAsync(imported.InstancePath);
            var scanned = Assert.Single(await ScanAsync());
            Assert.Equal("未知", scanned.Format);
            Assert.Empty(scanned.Versions);
            Assert.Equal("original configuration", File.ReadAllText(Path.Combine(outside, "game", "config", "example.cfg")));
        }
        finally
        {
            // 只移除测试创建的链接本身，避免临时目录清理沿链接递归到目标。
            Directory.Delete(link);
        }
    }

    /// <summary>即使附带有效导入清单，也不能对 instances 的间接子目录执行管理操作。</summary>
    [Fact]
    public async Task ValidManifestCannotExpandDirectChildManagementScope()
    {
        var imported = await ImportCustomLayoutAsync("data/game");
        var nested = Path.Combine(imported.InstancePath, "nested");
        Directory.CreateDirectory(nested);
        File.Copy(Path.Combine(imported.InstancePath, InstanceImportManifest.FileName), Path.Combine(nested, InstanceImportManifest.FileName));

        await AssertManagementRejectedAsync(nested);

        Assert.True(Directory.Exists(imported.InstancePath));
    }

    /// <summary>真实导入及配置生成后，从 EML 显式本体字段识别 Fabric 版本，不误用生成配置 ID。</summary>
    [Theory]
    [InlineData("Modrinth")]
    [InlineData("CurseForge")]
    public async Task MaterializedFabricProfileDisplaysMinecraftVersion(string sourceFormat)
    {
        var imported = await ImportPlatformAsync(sourceFormat);
        var main = RuntimeFile(imported.InstancePath, ".minecraft/cache/client.jar", "client", PrismRuntimeFileRole.MainJar);
        var loader = RuntimeFile(imported.InstancePath, ".minecraft/cache/fabric.jar", "fabric", PrismRuntimeFileRole.Library) with
        {
            MavenName = "net.fabricmc:fabric-loader:0.16.10", OnClassPath = true
        };
        var prepared = await new PrismRuntimeMaterializer().PrepareAsync(new()
        {
            InstancePath = imported.InstancePath, SourceFormat = sourceFormat, VersionIdPrefix = "EML-" + sourceFormat,
            MinecraftVersion = "1.20.1", MainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
            Fingerprint = "0123456789abcdef", Files = [main, loader]
        });
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(imported.InstancePath, prepared.LaunchProfilePath)));
        Assert.False(json.RootElement.TryGetProperty("clientVersion", out _));
        Assert.False(json.RootElement.TryGetProperty("inheritsFrom", out _));

        var scanned = Assert.Single(await ScanAsync());

        Assert.Equal("1.20.1", scanned.MinecraftVersionDisplay);
        Assert.Equal("Fabric 0.16.10", scanned.LoaderDisplay);
        Assert.StartsWith("EML-" + sourceFormat, Assert.Single(scanned.VersionIds));
        Assert.Equal(Path.Combine(imported.InstancePath, prepared.LaunchProfilePath.Replace('/', Path.DirectorySeparatorChar)),
            Assert.Single(scanned.Versions).MetadataPath);
        Assert.DoesNotContain(scanned.Warnings, warning => warning.Contains("无法从元数据确认 Minecraft 版本"));
    }

    /// <summary>旧配置没有本体字段时使用已验证导入声明补齐未知值，保留实际配置 ID 与加载器。</summary>
    [Theory]
    [InlineData("Modrinth")]
    [InlineData("CurseForge")]
    public async Task ExistingUnknownVersionFallsBackToImportedDeclaration(string sourceFormat)
    {
        var imported = await ImportPlatformAsync(sourceFormat);
        WriteVersion(imported.GameDirectory, "legacy-profile", """
            {"id":"legacy-profile","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient",
             "libraries":[{"name":"net.fabricmc:fabric-loader:0.16.10"}]}
            """);

        var scanned = Assert.Single(await ScanAsync());

        Assert.Equal("1.20.1", scanned.MinecraftVersionDisplay);
        Assert.Equal("legacy-profile", Assert.Single(scanned.VersionIds));
        Assert.Equal("Fabric 0.16.10", scanned.LoaderDisplay);
        Assert.Contains(scanned.Warnings, warning => warning.Contains("显示导入清单声明的 1.20.1"));
    }

    /// <summary>真实已知版本优先于历史导入声明和 EML 辅助字段，未知的另一条配置仍可单独回退。</summary>
    [Fact]
    public async Task ImportedFallbackDoesNotOverwriteKnownConflictingVersion()
    {
        var imported = await ImportPlatformAsync("Modrinth");
        WriteVersion(imported.GameDirectory, "known", """
            {"id":"known","clientVersion":"1.21.1","emlMinecraftVersion":"1.18.2",
             "mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient"}
            """);
        WriteVersion(imported.GameDirectory, "unknown", """
            {"id":"unknown","mainClass":"custom.Main"}
            """);

        var scanned = Assert.Single(await ScanAsync());

        Assert.Equal("1.21.1", scanned.Versions.Single(version => version.VersionId == "known").MinecraftVersion);
        Assert.Equal("1.20.1", scanned.Versions.Single(version => version.VersionId == "unknown").MinecraftVersion);
    }

    /// <summary>损坏的导入清单不能提供版本回退，也不能借版本目录绕过清单校验。</summary>
    [Fact]
    public async Task InvalidManifestCannotSupplyVersionFallback()
    {
        var imported = await ImportPlatformAsync("Modrinth");
        WriteVersion(imported.GameDirectory, "unknown", """{"id":"unknown","mainClass":"custom.Main"}""");
        File.WriteAllText(Path.Combine(imported.InstancePath, InstanceImportManifest.FileName),
            """{"schemaVersion":2,"minecraftVersion":"9.9.9"}""");

        var scanned = Assert.Single(await ScanAsync());

        Assert.Equal("未知", scanned.MinecraftVersionDisplay);
        Assert.Empty(scanned.Versions);
    }

    /// <summary>写入一个真实 EML 备份并调用生产导入器，验证布局来自清单而非手工伪造扫描结果。</summary>
    private Task<InstanceImportResult> ImportCustomLayoutAsync(string gameDirectory)
    {
        var manifest = new InstanceImportManifest { SourceFormat = "Zip", GameDirectory = gameDirectory, MinecraftVersion = "1.20.1" };
        var relative = gameDirectory == "." ? "config/example.cfg" : gameDirectory + "/config/example.cfg";
        return new MinecraftInstanceImporter(_root).ImportAsync(CreateArchive(
            (InstanceImportManifest.FileName, JsonSerializer.Serialize(manifest, JsonOptions)),
            (relative, "original configuration")), "Imported");
    }

    /// <summary>通过原生平台清单导入最小实例，不访问网络，也不复制生成后的 EML 清单。</summary>
    private Task<InstanceImportResult> ImportPlatformAsync(string sourceFormat)
    {
        var marker = sourceFormat == "Modrinth" ? "modrinth.index.json" : "manifest.json";
        var contents = sourceFormat == "Modrinth" ? """
            {"formatVersion":1,"game":"minecraft","versionId":"1","name":"Fixture","files":[],
             "dependencies":{"minecraft":"1.20.1","fabric-loader":"0.16.10"}}
            """ : """
            {"manifestType":"minecraftModpack","manifestVersion":1,"name":"Fixture","version":"1","files":[],
             "minecraft":{"version":"1.20.1","modLoaders":[{"id":"fabric-0.16.10","primary":true}]},"overrides":"overrides"}
            """;
        return new MinecraftInstanceImporter(_root).ImportAsync(CreateArchive(
            (marker, contents), ("overrides/config/example.cfg", "config")), "Imported");
    }

    /// <summary>创建唯一 ZIP，只装入本测试声明的内容。</summary>
    private string CreateArchive(params (string Path, string Contents)[] entries)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry.Path).Open(), new UTF8Encoding(false));
            writer.Write(entry.Contents);
        }
        return path;
    }

    /// <summary>创建带真实大小和摘要的运行文件，供生产配置生成器校验及复制。</summary>
    private static PrismRuntimeFile RuntimeFile(string instance, string relative, string content, PrismRuntimeFileRole role)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var path = Path.Combine(instance, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return new(new()
        {
            Path = relative, Source = InstanceFileSource.Embedded, Size = bytes.Length,
            Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) }
        }, role);
    }

    /// <summary>建立一条标准版本元数据，保留实例真实导入清单供扫描器独立读取。</summary>
    private static void WriteVersion(string game, string id, string json)
    {
        var folder = Path.Combine(game, "versions", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, id + ".json"), json);
    }

    /// <summary>确认管理器在移动、导出或调用回收替身前拒绝目标。</summary>
    private async Task AssertManagementRejectedAsync(string instance)
    {
        var recycled = false;
        var manager = new MinecraftInstanceManager(_root, _ => recycled = true);
        var destination = Path.Combine(_root, "rejected-export.zip");
        await Assert.ThrowsAnyAsync<Exception>(() => manager.RenameAsync(instance, "Rejected"));
        await Assert.ThrowsAnyAsync<Exception>(() => manager.ExportAsync(instance, destination));
        await Assert.ThrowsAnyAsync<Exception>(() => manager.DeleteAsync(instance));
        Assert.True(Directory.Exists(instance));
        Assert.False(Directory.Exists(Path.Combine(_root, "instances", "Rejected")));
        Assert.False(File.Exists(destination));
        Assert.False(recycled);
    }

    /// <summary>扫描本测试的启动器根目录，避免访问桌面真实实例。</summary>
    private Task<IReadOnlyList<MinecraftInstance>> ScanAsync() => new LauncherRootMinecraftInstanceLocator(_root).FindAllAsync();

    /// <summary>Windows 建立无需符号链接权限的联接，其他系统创建目录符号链接。</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    /// <summary>仅清理当前测试自行创建的唯一临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
