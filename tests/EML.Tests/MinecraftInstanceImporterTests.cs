using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Tests;

/// <summary>使用自己创建的 GUID 临时目录验证导入，不接触用户现有实例，也不访问下载地址。</summary>
public sealed class MinecraftInstanceImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-import-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>打包最小测试数据；允许重复条目，用于验证恶意压缩包的拒绝行为。</summary>
    private string CreateArchive(params (string Path, string Content)[] entries)
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            var entry = archive.CreateEntry(path);
            if (path.EndsWith('/')) continue;
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return archivePath;
    }

    /// <summary>生成 Modrinth 的标准文件需求，内容只用于计算测试哈希，URL 不会被请求。</summary>
    private static object ModrinthFile(string path, string content, string client = "required", string url = "https://example.com/test.jar") => new
    {
        path,
        hashes = new
        {
            sha1 = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(content))),
            sha512 = Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(content)))
        },
        env = new { client, server = "required" },
        downloads = new[] { url },
        fileSize = Encoding.UTF8.GetByteCount(content)
    };

    /// <summary>创建 Modrinth 索引，保留 Minecraft 和 Fabric 的组件需求。</summary>
    private static string ModrinthIndex(params object[] files) => JsonSerializer.Serialize(new
    {
        formatVersion = 1,
        game = "minecraft",
        versionId = "1.0.0",
        name = "MR 测试包",
        files,
        dependencies = new Dictionary<string, string> { ["minecraft"] = "1.20.1", ["fabric-loader"] = "0.16.10" }
    });

    /// <summary>生成 CurseForge 清单，并使用非默认的 overrides 路径验证字段解析。</summary>
    private static string CurseForgeManifest() => JsonSerializer.Serialize(new
    {
        minecraft = new { version = "1.7.10", modLoaders = new[] { new { id = "forge-10.13.4.1614", primary = true } } },
        manifestType = "minecraftModpack",
        manifestVersion = 1,
        name = "CF 测试包",
        version = "1.0",
        author = "EML tests",
        files = new[]
        {
            new { projectID = 12345L, fileID = 23456L, required = true },
            new { projectID = 34567L, fileID = 45678L, required = false }
        },
        overrides = "custom-overrides"
    });

    /// <summary>生成 Prism 的组件清单，游戏版本与加载器版本分别保留。</summary>
    private const string PrismPack = """
        {"formatVersion":1,"components":[
          {"uid":"net.minecraft","version":"1.7.10"},
          {"uid":"net.minecraftforge","version":"10.13.4.1614"}
        ]}
        """;

    /// <summary>读取生成的持久化清单，验证它是可独立使用的 JSON 文件。</summary>
    private static JsonDocument ReadSavedManifest(InstanceImportResult result) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(result.InstancePath, InstanceImportManifest.FileName)));

    /// <summary>失败或取消后，导入不能留下可被扫描到的实例和未清理的临时工作目录。</summary>
    private void AssertNothingImported()
    {
        var instances = Path.Combine(_root, "instances");
        if (Directory.Exists(instances)) Assert.Empty(Directory.EnumerateFileSystemEntries(instances));
        var staging = Path.Combine(_root, ".eml-imports");
        if (Directory.Exists(staging)) Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    /// <summary>Prism 压缩包可带一层外包装；导入后同步用户选择的实例名并保留组件信息。</summary>
    [Fact]
    public async Task ImportsWrappedPrismAndSynchronizesInstanceName()
    {
        var path = CreateArchive(
            ("Wrapped/mmc-pack.json", PrismPack),
            ("Wrapped/instance.cfg", "[General]\nname=Old name\niconKey=default\n[Other]\nname=keep\n"),
            ("Wrapped/.minecraft/config/test.cfg", "setting=true"));
        var importer = new MinecraftInstanceImporter(_root);
        var preview = await importer.InspectAsync(path);
        Assert.Equal("Old name", preview.SuggestedName);
        Assert.Equal("1.7.10", preview.MinecraftVersion);
        AssertNothingImported();

        var result = await importer.ImportAsync(path, "我的 Prism");
        Assert.Equal(Path.Combine(_root, "instances", "我的 Prism"), result.InstancePath);
        Assert.Equal(Path.Combine(result.InstancePath, ".minecraft"), result.GameDirectory);
        Assert.Equal("setting=true", File.ReadAllText(Path.Combine(result.GameDirectory, "config", "test.cfg")));
        Assert.True(File.Exists(Path.Combine(result.InstancePath, "mmc-pack.json")));
        var cfg = File.ReadAllText(Path.Combine(result.InstancePath, "instance.cfg"));
        Assert.Contains("name=我的 Prism", cfg);
        Assert.Contains("iconKey=default", cfg);
        Assert.Contains("[Other]\nname=keep", cfg);
        Assert.Contains(result.Manifest.Components, component => component.Id == "net.minecraftforge" && component.Version == "10.13.4.1614");
        Assert.False(Directory.Exists(Path.Combine(result.InstancePath, "Wrapped")));
    }

    /// <summary>Modrinth 只导入包内文件，远程需求保留 URL、哈希、大小和可选标志，服务器专用文件不导入。</summary>
    [Fact]
    public async Task ImportsModrinthAndPreservesDownloadRequirements()
    {
        var path = CreateArchive(
            ("modrinth.index.json", ModrinthIndex(
                ModrinthFile("mods/required.jar", "required"),
                ModrinthFile("mods/optional.jar", "optional", "optional"),
                ModrinthFile("mods/server.jar", "server", "unsupported"))),
            ("overrides/config/test.cfg", "config value"));
        var mrpackPath = Path.ChangeExtension(path, ".mrpack");
        File.Move(path, mrpackPath);
        var importer = new MinecraftInstanceImporter(_root);
        var preview = await importer.InspectAsync(mrpackPath);
        Assert.Equal("MR 测试包", preview.SuggestedName);
        Assert.Equal("1.20.1", preview.MinecraftVersion);
        Assert.Equal(2, preview.DownloadFileCount);

        var result = await importer.ImportAsync(mrpackPath, "Modrinth");
        var remote = result.Manifest.Files.Where(file => file.Source == InstanceFileSource.DirectDownload).ToArray();
        Assert.Equal(2, remote.Length);
        var required = Assert.Single(remote, file => file.Path == ".minecraft/mods/required.jar");
        Assert.True(required.Required);
        Assert.Equal(Encoding.UTF8.GetByteCount("required"), required.Size);
        Assert.Equal("https://example.com/test.jar", Assert.Single(required.Downloads));
        Assert.Equal(40, required.Hashes["sha1"].Length);
        Assert.Equal(128, required.Hashes["sha512"].Length);
        Assert.False(Assert.Single(remote, file => file.Path == ".minecraft/mods/optional.jar").Required);
        Assert.DoesNotContain(result.Manifest.Files, file => file.Path?.EndsWith("server.jar") == true);
        Assert.Contains(result.Manifest.Components, component => component.Id == "fabric-loader" && component.Version == "0.16.10");
        Assert.Equal("config value", File.ReadAllText(Path.Combine(result.GameDirectory, "config", "test.cfg")));
        Assert.False(File.Exists(Path.Combine(result.GameDirectory, "mods", "required.jar")));
        Assert.True(File.Exists(Path.Combine(result.InstancePath, "modrinth.index.json")));
        Assert.Equal("NotChecked", result.Manifest.IntegrityStatus);
    }

    /// <summary>客户端覆盖文件优先于通用覆盖文件；被覆盖的下载需求必须移除，避免未来下载器覆盖用户配置。</summary>
    [Fact]
    public async Task ModrinthClientOverridesReplaceGeneralOverridesAndRemoteRequirements()
    {
        var path = CreateArchive(
            ("modrinth.index.json", ModrinthIndex(ModrinthFile("config/shared.txt", "remote"))),
            ("overrides/config/shared.txt", "general"),
            ("client-overrides/config/shared.txt", "client wins"),
            ("server-overrides/config/server.txt", "server only"));
        var result = await new MinecraftInstanceImporter(_root).ImportAsync(path, "Overrides");
        Assert.Equal("client wins", File.ReadAllText(Path.Combine(result.GameDirectory, "config", "shared.txt")));
        Assert.False(File.Exists(Path.Combine(result.GameDirectory, "config", "server.txt")));
        var requirement = Assert.Single(result.Manifest.Files, file => file.Path == ".minecraft/config/shared.txt");
        Assert.Equal(InstanceFileSource.Embedded, requirement.Source);
        Assert.Equal("client-overrides/config/shared.txt", requirement.ArchiveEntry);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("client wins"))), requirement.Hashes["sha256"]);
    }

    /// <summary>CurseForge 项目与文件 ID 保留为待解析需求；不能在没有 API 数据时编造文件路径。</summary>
    [Fact]
    public async Task ImportsCurseForgeWithCustomOverridesAndUnresolvedIds()
    {
        var path = CreateArchive(
            ("manifest.json", CurseForgeManifest()),
            ("custom-overrides/config/test.cfg", "curseforge config"));
        var importer = new MinecraftInstanceImporter(_root);
        var preview = await importer.InspectAsync(path);
        Assert.Equal("CF 测试包", preview.SuggestedName);
        Assert.Equal(2, preview.UnresolvedFileCount);
        var result = await importer.ImportAsync(path, "CurseForge");
        Assert.Equal("1.7.10", result.Manifest.MinecraftVersion);
        Assert.Equal("curseforge config", File.ReadAllText(Path.Combine(result.GameDirectory, "config", "test.cfg")));
        Assert.True(File.Exists(Path.Combine(result.InstancePath, "manifest.json")));
        var files = result.Manifest.Files.Where(file => file.Source == InstanceFileSource.CurseForge).ToArray();
        Assert.Equal(2, files.Length);
        var required = Assert.Single(files, file => file.ProjectId == 12345);
        Assert.Equal(23456, required.FileId);
        Assert.True(required.Required);
        Assert.Null(required.Path);
        Assert.Null(required.Size);
        Assert.Empty(required.Hashes);
        Assert.Empty(required.Downloads);
        Assert.False(Assert.Single(files, file => file.ProjectId == 34567).Required);
    }

    /// <summary>通用 ZIP 可以包含实例目录或直接包含游戏目录；两种布局统一为实例下的 .minecraft。</summary>
    [Theory]
    [InlineData(".minecraft/")]
    [InlineData("")]
    [InlineData("Wrapped/.minecraft/")]
    [InlineData("Wrapped/")]
    public async Task ImportsGenericZipWithEitherGameLayout(string prefix)
    {
        var path = CreateArchive(
            (prefix + "versions/1.7.10/1.7.10.json", "{\"id\":\"1.7.10\",\"type\":\"release\"}"),
            (prefix + "mods/test.jar", "test bytes"),
            (prefix + "saves/empty/", ""));
        var result = await new MinecraftInstanceImporter(_root).ImportAsync(path, "通用实例");
        Assert.Equal("test bytes", File.ReadAllText(Path.Combine(result.GameDirectory, "mods", "test.jar")));
        Assert.True(File.Exists(Path.Combine(result.GameDirectory, "versions", "1.7.10", "1.7.10.json")));
        Assert.True(Directory.Exists(Path.Combine(result.GameDirectory, "saves", "empty")));
        Assert.Equal(Path.Combine(result.InstancePath, ".minecraft"), result.GameDirectory);
    }

    /// <summary>保存包内文件的实际长度与 SHA256，清单中的路径不依赖当前机器的绝对目录。</summary>
    [Fact]
    public async Task PersistsEmbeddedHashAndPortableMetadata()
    {
        const string content = "中文配置\noption=true";
        var path = CreateArchive((".minecraft/config/test.cfg", content));
        var result = await new MinecraftInstanceImporter(_root).ImportAsync(path, "Hash check");
        var requirement = Assert.Single(result.Manifest.Files, file => file.Path == ".minecraft/config/test.cfg");
        Assert.Equal(InstanceFileSource.Embedded, requirement.Source);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), requirement.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), requirement.Hashes["sha256"]);
        Assert.Equal(".minecraft/config/test.cfg", requirement.ArchiveEntry);
        Assert.Equal(".minecraft", result.Manifest.GameDirectory);
        Assert.Equal(Path.GetFileName(path), result.Manifest.SourceArchive);
        using var document = ReadSavedManifest(result);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Hash check", root.GetProperty("name").GetString());
        Assert.Equal("NotChecked", root.GetProperty("integrityStatus").GetString());
        var savedFile = Assert.Single(root.GetProperty("files").EnumerateArray(), file => file.GetProperty("path").GetString() == requirement.Path);
        Assert.Equal("Embedded", savedFile.GetProperty("source").GetString());
        Assert.Equal(requirement.Hashes["sha256"], savedFile.GetProperty("hashes").GetProperty("sha256").GetString());
    }

    /// <summary>四种导入格式均保留中文、全角标点及合法空格，磁盘名称和恢复条目不能被裁剪或替换。</summary>
    [Theory]
    [InlineData("Zip")]
    [InlineData("Prism")]
    [InlineData("Modrinth")]
    [InlineData("CurseForge")]
    public async Task ImportsLegalUnicodePathsWithoutRenaming(string format)
    {
        string[] gamePaths =
        [
            "journeymap/data/mp/中文服务器（生存）/waypoints/出生点，。！？：；“”‘’（）【】《》、·…—.json",
            "config/中文.配置/带 空格.cfg",
            "config/ 前导空格/ 文件.cfg",
            "config/\u3000全角空格\u3000/\u3000文件.json\u3000",
            "config/\u00a0不换行空格\u00a0/\u00a0文件.json\u00a0",
            "config/全角符号＜＞：＂｜？＊／＼.cfg",
            "config/半角括号(保留)和全角括号（保留）.cfg",
            "config/重音é.cfg",
            "config/重音e\u0301.cfg"
        ];
        var archivePrefix = format switch
        {
            "Modrinth" => "overrides/",
            "CurseForge" => "custom-overrides/",
            _ => ".minecraft/"
        };
        var entries = gamePaths.Select((path, index) => (Path: archivePrefix + path, Content: "原样保留 " + index)).ToList();
        entries.Add((archivePrefix + "screenshots/\u3000空目录\u3000/", ""));
        if (format == "Prism") entries.Add(("mmc-pack.json", PrismPack));
        if (format == "Modrinth") entries.Add(("modrinth.index.json", ModrinthIndex()));
        if (format == "CurseForge") entries.Add(("manifest.json", CurseForgeManifest()));
        var archivePath = CreateArchive(entries.ToArray());
        var importer = new MinecraftInstanceImporter(_root);

        var preview = await importer.InspectAsync(archivePath);
        Assert.Equal(format, preview.Format);
        AssertNothingImported();
        var imported = await importer.ImportAsync(archivePath, "中文实例（测试）");
        using var saved = ReadSavedManifest(imported);
        for (var index = 0; index < gamePaths.Length; index++)
        {
            var relative = ".minecraft/" + gamePaths[index];
            var content = "原样保留 " + index;
            var actual = Path.Combine(imported.InstancePath, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(content, File.ReadAllText(actual));
            Assert.Contains(Path.GetFileName(actual), Directory.EnumerateFiles(Path.GetDirectoryName(actual)!).Select(Path.GetFileName));
            var requirement = Assert.Single(imported.Manifest.Files, file => file.Path == relative);
            Assert.Equal(InstanceFileSource.Embedded, requirement.Source);
            Assert.Equal(archivePrefix + gamePaths[index], requirement.ArchiveEntry);
            Assert.Equal(Encoding.UTF8.GetByteCount(content), requirement.Size);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), requirement.Hashes["sha256"]);
            var savedFile = Assert.Single(saved.RootElement.GetProperty("files").EnumerateArray(), file => file.GetProperty("path").GetString() == relative);
            Assert.Equal(requirement.ArchiveEntry, savedFile.GetProperty("archiveEntry").GetString());
        }
        Assert.Contains("\u3000空目录\u3000", Directory.EnumerateDirectories(Path.Combine(imported.GameDirectory, "screenshots")).Select(Path.GetFileName));
    }

    /// <summary>尚待下载的 Modrinth 文件也保留合法 Unicode 路径，避免补全时找不到原清单指定的文件。</summary>
    [Theory]
    [InlineData("mods/中文模组（测试）：增强.jar")]
    [InlineData("config/\u3000中文配置\u3000/\u00a0文件\u00a0")]
    [InlineData("config/ 前导空格/ 文件.cfg")]
    public async Task ModrinthRequirementsPreserveLegalUnicodePaths(string gamePath)
    {
        var path = CreateArchive(("modrinth.index.json", ModrinthIndex(ModrinthFile(gamePath, "待下载内容"))));
        var importer = new MinecraftInstanceImporter(_root);
        Assert.Equal(1, (await importer.InspectAsync(path)).DownloadFileCount);
        var result = await importer.ImportAsync(path, "中文远程需求");
        var requirement = Assert.Single(result.Manifest.Files, file => file.Source == InstanceFileSource.DirectDownload);
        Assert.Equal(".minecraft/" + gamePath, requirement.Path);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("待下载内容"))), requirement.Hashes["sha1"]);
        using var saved = ReadSavedManifest(result);
        Assert.Contains(saved.RootElement.GetProperty("files").EnumerateArray(), file =>
            file.GetProperty("path").GetString() == ".minecraft/" + gamePath);
        Assert.False(File.Exists(Path.Combine(result.GameDirectory, gamePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    /// <summary>含中文和边缘空格的路径经过备份往返后，仍能按精确条目恢复缺失文件并保持原始哈希。</summary>
    [Fact]
    public async Task UnicodePathsSurviveBackupAndEmbeddedRecovery()
    {
        const string gamePath = "journeymap/data/mp/\u3000中文服务器\u3000/waypoints/ 出生点（家）：位置.json\u00a0";
        const string content = "{\"name\":\"出生点\"}";
        var originalArchive = CreateArchive((".minecraft/" + gamePath, content));
        var importer = new MinecraftInstanceImporter(_root);
        var original = await importer.ImportAsync(originalArchive, "中文原实例");
        var baseline = Assert.Single(original.Manifest.Files, file => file.Path == ".minecraft/" + gamePath);
        var backup = Path.Combine(_root, "中文备份（保留文件名）.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(original.InstancePath, backup);
        var restored = await importer.ImportAsync(backup, "中文恢复实例");
        var requirement = Assert.Single(restored.Manifest.Files, file => file.Path == baseline.Path);
        Assert.Equal(baseline.Hashes["sha256"], requirement.Hashes["sha256"]);
        Assert.Equal(baseline.Size, requirement.Size);
        var destination = Path.Combine(restored.GameDirectory, gamePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(content, File.ReadAllText(destination));

        File.Delete(destination);
        Assert.True(await new EmbeddedFileRestorer(_root).RestoreAsync(restored.InstancePath, restored.Manifest, requirement, CancellationToken.None));
        Assert.Equal(content, File.ReadAllText(destination));
        Assert.Contains(Path.GetFileName(destination), Directory.EnumerateFiles(Path.GetDirectoryName(destination)!).Select(Path.GetFileName));
    }

    /// <summary>真正非法的字符应报告具体路径段和码点，避免用户误以为中文本身不受支持。</summary>
    [Theory]
    [InlineData("中文:坐标.json", "U+003A")]
    [InlineData("中文?位置.json", "U+003F")]
    [InlineData("中文\t位置.json", "U+0009")]
    [InlineData("中文文件.json ", "U+0020")]
    [InlineData("中文目录.", "U+002E")]
    public async Task InvalidUnicodePathReportsActualOffendingCharacter(string segment, string codePoint)
    {
        var path = CreateArchive((".minecraft/config/" + segment, "内容"));
        var importer = new MinecraftInstanceImporter(_root);
        var previewError = await Assert.ThrowsAsync<InvalidDataException>(() => importer.InspectAsync(path));
        Assert.Contains("中文", previewError.Message);
        Assert.Contains(codePoint, previewError.Message);
        var importError = await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(path, "不能导入"));
        Assert.Contains(codePoint, importError.Message);
        AssertNothingImported();
    }

    /// <summary>拒绝穿越、绝对路径、反斜杠穿越、数据流和 Windows 设备名，不把 ZIP 内路径直接传给磁盘操作。</summary>
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/escape.txt")]
    [InlineData("C:/escape.txt")]
    [InlineData(".minecraft\\..\\..\\escape.txt")]
    [InlineData(".minecraft/options.txt:payload")]
    [InlineData(".minecraft/CON.txt")]
    [InlineData(".minecraft/CON .txt")]
    [InlineData(".minecraft/LPT¹.txt")]
    [InlineData(".minecraft/./file.txt")]
    [InlineData(".minecraft/trailing /file.txt")]
    [InlineData(".minecraft/trailing./file.txt")]
    public async Task RejectsUnsafeArchivePaths(string maliciousPath)
    {
        var path = CreateArchive((".minecraft/config/good.cfg", "safe"), (maliciousPath, "unsafe"));
        await Assert.ThrowsAnyAsync<Exception>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, "Unsafe"));
        AssertNothingImported();
    }

    /// <summary>Windows 不区分大小写，因此同一路径的两个大小写变体不能默默覆盖。</summary>
    [Fact]
    public async Task RejectsDuplicateCaseInsensitiveEntries()
    {
        var path = CreateArchive((".minecraft/config/test.cfg", "first"), (".minecraft/config/TEST.cfg", "second"));
        await Assert.ThrowsAnyAsync<Exception>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, "Duplicates"));
        AssertNothingImported();
    }

    /// <summary>已有实例始终保留原内容，导入不能借由同名实例覆盖存档。</summary>
    [Fact]
    public async Task ExistingInstanceIsNotOverwritten()
    {
        var path = CreateArchive((".minecraft/config/test.cfg", "incoming"));
        var existing = Path.Combine(_root, "instances", "Existing", ".minecraft", "config");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "test.cfg"), "keep original");
        await Assert.ThrowsAnyAsync<IOException>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, "Existing"));
        Assert.Equal("keep original", File.ReadAllText(Path.Combine(existing, "test.cfg")));
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "instances")));
    }

    /// <summary>导入名称必须是合法的单层文件夹名，不能控制导入根路径。</summary>
    [Theory]
    [InlineData("../outside")]
    [InlineData("CON")]
    [InlineData("bad/name")]
    [InlineData(" name ")]
    [InlineData("")]
    public async Task RejectsInvalidInstanceNames(string instanceName)
    {
        var path = CreateArchive((".minecraft/config/test.cfg", "config"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, instanceName));
        AssertNothingImported();
    }

    /// <summary>开始前取消不会创建任何实例；同步进度中取消也必须清理已经创建的临时导入目录。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationLeavesNoPartialInstance(bool cancelOnProgress)
    {
        var path = CreateArchive((".minecraft/mods/large.jar", new string('x', 512 * 1024)));
        using var cancellation = new CancellationTokenSource();
        var reports = 0;
        var progress = new InlineProgress(_ =>
        {
            reports++;
            cancellation.Cancel();
        });
        if (!cancelOnProgress) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MinecraftInstanceImporter(_root).ImportAsync(path, "Cancelled", progress, cancellation.Token));
        if (cancelOnProgress) Assert.True(reports > 0);
        AssertNothingImported();
        Assert.True(File.Exists(path));
    }

    /// <summary>已声明平台格式的损坏清单不能退回通用 ZIP 导入，从而掩盖缺失依赖。</summary>
    [Theory]
    [InlineData("modrinth.index.json")]
    [InlineData("manifest.json")]
    [InlineData("mmc-pack.json")]
    public async Task MalformedPlatformManifestDoesNotFallBackToGenericZip(string manifestName)
    {
        var path = CreateArchive((manifestName, "{ definitely invalid"), (".minecraft/mods/test.jar", "mod"));
        await Assert.ThrowsAnyAsync<Exception>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, "Malformed"));
        AssertNothingImported();
    }

    /// <summary>一个压缩包含多个实例时不能任意选择一个导入。</summary>
    [Fact]
    public async Task MultiplePrismInstancesAreRejected()
    {
        var path = CreateArchive(
            ("One/mmc-pack.json", PrismPack), ("One/.minecraft/config/test.cfg", "one"),
            ("Two/mmc-pack.json", PrismPack), ("Two/.minecraft/config/test.cfg", "two"));
        await Assert.ThrowsAnyAsync<Exception>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, "Multiple"));
        AssertNothingImported();
    }

    /// <summary>任意文档 ZIP 不应被当成 Minecraft 整合包。</summary>
    [Fact]
    public async Task ArbitraryZipIsRejected()
    {
        var path = CreateArchive(("documents/readme.txt", "not a modpack"));
        await Assert.ThrowsAnyAsync<Exception>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, "Documents"));
        AssertNothingImported();
    }

    /// <summary>Modrinth URL 是未来下载器的输入，导入阶段就拒绝本地文件地址和非 HTTPS 链接。</summary>
    [Theory]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("http://example.com/mod.jar")]
    public async Task ModrinthUnsafeDownloadUrlsAreRejected(string url)
    {
        var path = CreateArchive(("modrinth.index.json", ModrinthIndex(ModrinthFile("mods/test.jar", "mod", url: url))));
        await Assert.ThrowsAnyAsync<Exception>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, "Unsafe URL"));
        AssertNothingImported();
    }

    /// <summary>远程文件路径虽然不立即写磁盘，也必须验证，防止未来下载器越过实例目录。</summary>
    [Theory]
    [InlineData("../escape.jar")]
    [InlineData("C:/escape.jar")]
    [InlineData("mods\\..\\..\\escape.jar")]
    public async Task ModrinthUnsafeRequirementPathsAreRejected(string requirementPath)
    {
        var path = CreateArchive(("modrinth.index.json", ModrinthIndex(ModrinthFile(requirementPath, "mod"))));
        await Assert.ThrowsAnyAsync<Exception>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, "Unsafe requirement"));
        AssertNothingImported();
    }

    /// <summary>普通反斜杠分隔符可安全规范化；ZIP 条目和远程需求最终都保存为统一的相对路径。</summary>
    [Fact]
    public async Task NormalizesSafeBackslashSeparators()
    {
        var generic = CreateArchive((".minecraft\\config\\test.cfg", "config"));
        var importer = new MinecraftInstanceImporter(_root);
        var result = await importer.ImportAsync(generic, "Backslash ZIP");
        Assert.Equal("config", File.ReadAllText(Path.Combine(result.GameDirectory, "config", "test.cfg")));
        Assert.Contains(result.Manifest.Files, file => file.Path == ".minecraft/config/test.cfg");

        var modrinth = CreateArchive(("modrinth.index.json", ModrinthIndex(ModrinthFile("mods\\test.jar", "mod"))));
        var mrResult = await importer.ImportAsync(modrinth, "Backslash Modrinth");
        Assert.Contains(mrResult.Manifest.Files, file => file.Path == ".minecraft/mods/test.jar" && file.Source == InstanceFileSource.DirectDownload);
    }

    /// <summary>Modrinth 必需的 SHA1/SHA512 不能缺失，错误长度或非十六进制内容也不能进入安装清单。</summary>
    [Theory]
    [InlineData("sha1", null)]
    [InlineData("sha512", "not-a-hash")]
    public async Task ModrinthInvalidHashesAreRejected(string hashName, string? hashValue)
    {
        var index = JsonNode.Parse(ModrinthIndex(ModrinthFile("mods/test.jar", "mod")))!;
        var hashes = index["files"]![0]!["hashes"]!.AsObject();
        if (hashValue is null) hashes.Remove(hashName);
        else hashes[hashName] = hashValue;
        var path = CreateArchive(("modrinth.index.json", index.ToJsonString()));
        await Assert.ThrowsAnyAsync<Exception>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, "Invalid hash"));
        AssertNothingImported();
    }

    /// <summary>不支持的 Modrinth 索引版本和其他游戏不能被误识别为当前 Minecraft 整合包。</summary>
    [Theory]
    [InlineData(2, "minecraft")]
    [InlineData(1, "another-game")]
    public async Task UnsupportedModrinthFormatIsRejected(int formatVersion, string game)
    {
        var index = JsonNode.Parse(ModrinthIndex())!;
        index["formatVersion"] = formatVersion;
        index["game"] = game;
        var path = CreateArchive(("modrinth.index.json", index.ToJsonString()));
        await Assert.ThrowsAnyAsync<Exception>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, "Unsupported"));
        AssertNothingImported();
    }

    /// <summary>CurseForge 的 overrides 是包内相对目录，不能通过父目录路径绕过安全检查。</summary>
    [Fact]
    public async Task CurseForgeOverridesTraversalIsRejected()
    {
        var manifest = JsonNode.Parse(CurseForgeManifest())!;
        manifest["overrides"] = "../payload";
        var path = CreateArchive(("manifest.json", manifest.ToJsonString()), (".minecraft/config/test.cfg", "config"));
        await Assert.ThrowsAnyAsync<Exception>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, "Unsafe overrides"));
        AssertNothingImported();
    }

    /// <summary>EML 自有清单也属于外部输入：拒绝未知版本、越界游戏目录和越界下载目标。</summary>
    [Theory]
    [InlineData(99, ".minecraft", ".minecraft/mods/test.jar")]
    [InlineData(1, "../outside", ".minecraft/mods/test.jar")]
    [InlineData(1, ".minecraft", "../outside.jar")]
    public async Task ForgedEmlMetadataIsRejected(int schemaVersion, string gameDirectory, string requirementPath)
    {
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion,
            sourceFormat = "Zip",
            name = "Forged",
            gameDirectory,
            files = new[]
            {
                new { path = requirementPath, source = "DirectDownload", required = true, size = 3,
                    hashes = new { sha256 = new string('0', 64) }, downloads = new[] { "https://example.com/mod.jar" } }
            }
        });
        var path = CreateArchive((InstanceImportManifest.FileName, manifest), (".minecraft/config/test.cfg", "config"));
        await Assert.ThrowsAnyAsync<Exception>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, "Forged"));
        AssertNothingImported();
    }

    /// <summary>ZIP 中声明为 Unix 符号链接的条目必须拒绝，不能将链接目标解释成实例内普通文件。</summary>
    [Fact]
    public async Task ArchiveSymbolicLinkIsRejected()
    {
        var path = CreateArchive((".minecraft/config/test.cfg", "config"), (".minecraft/link", "../../outside"));
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            archive.GetEntry(".minecraft/link")!.ExternalAttributes = unchecked((int)0xA1FF0000);
        await Assert.ThrowsAnyAsync<Exception>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, "Linked archive"));
        AssertNothingImported();
    }

    /// <summary>同一路径不能既是普通文件又是其他文件的父目录，须在提交前拒绝矛盾布局。</summary>
    [Fact]
    public async Task FileAndParentDirectoryCollisionIsRejected()
    {
        var path = CreateArchive((".minecraft/config", "file"), (".minecraft/config/settings.cfg", "descendant"));
        await Assert.ThrowsAnyAsync<Exception>(() => new MinecraftInstanceImporter(_root).ImportAsync(path, "Conflicting layout"));
        AssertNothingImported();
    }

    /// <summary>备份保留原始下载和包内基线；玩家配置变化保持原样，由共享变更策略允许。</summary>
    [Fact]
    public async Task ExportAndReimportPreservesPendingRequirementsAndCurrentEmbeddedFiles()
    {
        var path = CreateArchive(
            ("modrinth.index.json", ModrinthIndex(ModrinthFile("mods/missing.jar", "not downloaded"))),
            ("overrides/config/test.cfg", "original configuration"));
        var importer = new MinecraftInstanceImporter(_root);
        var first = await importer.ImportAsync(path, "Original");
        File.WriteAllText(Path.Combine(first.GameDirectory, "config", "test.cfg"), "user changed configuration", new UTF8Encoding(false));
        var backup = Path.Combine(_root, "backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(first.InstancePath, backup);

        var second = await importer.ImportAsync(backup, "Restored");
        Assert.Equal("Restored", second.Manifest.Name);
        var pending = Assert.Single(second.Manifest.Files, file => file.Source == InstanceFileSource.DirectDownload);
        Assert.Equal(".minecraft/mods/missing.jar", pending.Path);
        Assert.Equal("https://example.com/test.jar", Assert.Single(pending.Downloads));
        Assert.Equal("user changed configuration", File.ReadAllText(Path.Combine(second.GameDirectory, "config", "test.cfg")));
        var saved = Assert.Single(second.Manifest.Files, file => file.Path == ".minecraft/config/test.cfg");
        Assert.Equal(InstanceFileSource.Embedded, saved.Source);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("original configuration"))), saved.Hashes["sha256"]);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(second.InstancePath);
        var setting = Assert.Single(report.Files, file => file.Requirement.Path == saved.Path);
        Assert.Equal(InstanceFileState.ChangedSinceImport, setting.State);
        Assert.False(InstanceFileStatePolicy.IsBlocking(setting));
        Assert.False(File.Exists(Path.Combine(second.GameDirectory, "mods", "missing.jar")));
    }

    /// <summary>已有远程文件不改变原安装依据；备份恢复后保留原 URL 和哈希，即使实际文件已被损坏也等待检查器判断。</summary>
    [Theory]
    [InlineData("expected mod bytes")]
    [InlineData("corrupted replacement bytes")]
    public async Task BackupKeepsRemoteRequirementsWhenTheirFilesAlreadyExist(string actualContent)
    {
        const string expectedContent = "expected mod bytes";
        var path = CreateArchive(("modrinth.index.json", ModrinthIndex(ModrinthFile("mods/file.jar", expectedContent))));
        var importer = new MinecraftInstanceImporter(_root);
        var first = await importer.ImportAsync(path, "Original remote");
        Directory.CreateDirectory(Path.Combine(first.GameDirectory, "mods"));
        File.WriteAllText(Path.Combine(first.GameDirectory, "mods", "file.jar"), actualContent, new UTF8Encoding(false));
        var backup = Path.Combine(_root, "remote-backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(first.InstancePath, backup);

        var restored = await importer.ImportAsync(backup, "Restored remote");
        Assert.Equal(actualContent, File.ReadAllText(Path.Combine(restored.GameDirectory, "mods", "file.jar")));
        var requirement = Assert.Single(restored.Manifest.Files, file => file.Path == ".minecraft/mods/file.jar");
        Assert.Equal(InstanceFileSource.DirectDownload, requirement.Source);
        Assert.Equal("https://example.com/test.jar", Assert.Single(requirement.Downloads));
        Assert.Equal(Encoding.UTF8.GetByteCount(expectedContent), requirement.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(expectedContent))), requirement.Hashes["sha1"]);
        Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(expectedContent))), requirement.Hashes["sha512"]);
        Assert.Equal("NotChecked", restored.Manifest.IntegrityStatus);
    }

    /// <summary>游戏配置中的同名 manifest.json 只是普通数据，不能被误当成包根目录的 CurseForge 清单。</summary>
    [Fact]
    public async Task NestedUnrelatedManifestIsCopiedAsOrdinaryGameData()
    {
        var path = CreateArchive(("config/manifest.json", "this is deliberately not JSON"), ("mods/test.jar", "mod bytes"));
        var result = await new MinecraftInstanceImporter(_root).ImportAsync(path, "普通配置");
        Assert.Equal("this is deliberately not JSON", File.ReadAllText(Path.Combine(result.GameDirectory, "config", "manifest.json")));
        Assert.Contains(result.Manifest.Files, file => file.Path == ".minecraft/config/manifest.json" && file.Source == InstanceFileSource.Embedded);
    }

    /// <summary>显式存在的空 .minecraft 目录仍是可导入的实例容器，不要求它已经包含游戏文件。</summary>
    [Fact]
    public async Task ExplicitEmptyMinecraftDirectoryIsAccepted()
    {
        var path = CreateArchive((".minecraft/", ""));
        var importer = new MinecraftInstanceImporter(_root);
        var preview = await importer.InspectAsync(path);
        Assert.Equal(0, preview.EmbeddedFileCount);
        var result = await importer.ImportAsync(path, "Empty instance");
        Assert.True(Directory.Exists(result.GameDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(result.GameDirectory));
        Assert.True(File.Exists(Path.Combine(result.InstancePath, InstanceImportManifest.FileName)));
        Assert.Equal("NotChecked", result.Manifest.IntegrityStatus);
    }

    /// <summary>CurseForge 的自定义覆盖目录可能含同名清单文件，它应作为游戏配置原样复制。</summary>
    [Fact]
    public async Task CurseForgeCustomOverridesMayContainUnrelatedManifest()
    {
        var path = CreateArchive(("manifest.json", CurseForgeManifest()), ("custom-overrides/manifest.json", "ordinary non-JSON payload"));
        var result = await new MinecraftInstanceImporter(_root).ImportAsync(path, "CF nested manifest");
        Assert.Equal("ordinary non-JSON payload", File.ReadAllText(Path.Combine(result.GameDirectory, "manifest.json")));
        Assert.Equal(2, result.Manifest.Files.Count(file => file.Source == InstanceFileSource.CurseForge));
    }

    /// <summary>Prism 配置中的引号用于存储名称，预览时应去除外层引号后显示。</summary>
    [Fact]
    public async Task PrismQuotedNameIsDisplayedWithoutQuotes()
    {
        var path = CreateArchive(("mmc-pack.json", PrismPack), ("instance.cfg", "[General]\nname=\"Quoted name\"\n"));
        var preview = await new MinecraftInstanceImporter(_root).InspectAsync(path);
        Assert.Equal("Quoted name", preview.SuggestedName);
        AssertNothingImported();
    }

    /// <summary>明确命名为 minecraft 的目录即使只有自定义文件也保留其布局，不要求已经安装模组。</summary>
    [Fact]
    public async Task ExplicitMinecraftDirectoryMayContainOnlyCustomFiles()
    {
        var path = CreateArchive(("minecraft/readme.txt", "custom content"));
        var result = await new MinecraftInstanceImporter(_root).ImportAsync(path, "Custom only");
        Assert.Equal(Path.Combine(result.InstancePath, "minecraft"), result.GameDirectory);
        Assert.Equal("custom content", File.ReadAllText(Path.Combine(result.GameDirectory, "readme.txt")));
    }

    /// <summary>EML 备份以自有清单为安装依据，保留的旧平台清单损坏也不会覆盖已保存的待解析需求。</summary>
    [Fact]
    public async Task EmlBackupTakesPrecedenceOverMalformedRetainedPlatformManifest()
    {
        var path = CreateArchive(("manifest.json", CurseForgeManifest()), ("custom-overrides/config/test.cfg", "config"));
        var importer = new MinecraftInstanceImporter(_root);
        var first = await importer.ImportAsync(path, "Original CF");
        File.WriteAllText(Path.Combine(first.InstancePath, "manifest.json"), "stale invalid platform manifest");
        var backup = Path.Combine(_root, "cf-backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(first.InstancePath, backup);

        var restored = await importer.ImportAsync(backup, "Restored CF");
        Assert.Equal("stale invalid platform manifest", File.ReadAllText(Path.Combine(restored.InstancePath, "manifest.json")));
        Assert.Equal(2, restored.Manifest.Files.Count(file => file.Source == InstanceFileSource.CurseForge));
        Assert.Contains(restored.Manifest.Files, file => file.ProjectId == 12345 && file.FileId == 23456);
    }

    /// <summary>同步报告进度，让取消测试在确定的写入阶段触发，不依赖线程调度。</summary>
    private sealed class InlineProgress(Action<InstanceImportProgress> callback) : IProgress<InstanceImportProgress>
    {
        /// <summary>直接在导入工作线程执行测试回调。</summary>
        public void Report(InstanceImportProgress value) => callback(value);
    }

    /// <summary>仅清理由本测试生成的唯一临时根目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
