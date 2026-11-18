using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.External;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Tests;

/// <summary>以隔离临时目录验证目录复制和事务边界，不扫描玩家电脑或访问网络。</summary>
public sealed class MinecraftFolderImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-folder-import-tests-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "other-launcher", "instance");
    private string Launcher => Path.Combine(_root, "eml");
    private ExternalMinecraftInstance Candidate => new("本地测试包", "Prism Launcher", Source,
        Path.Combine(Source, ".minecraft"), "1.7.10", "Forge");

    /// <summary>创建最小 Prism 实例和真实游戏载荷，源与目标分别处于独立目录。</summary>
    public MinecraftFolderImporterTests()
    {
        Directory.CreateDirectory(Launcher);
        Write(".minecraft/mods/测试模组.jar", "local mod");
        Write(".minecraft/options.txt", "lang:zh_CN");
        Write(".minecraft/saves/世界，测试/level.dat", "local world");
        Write("mmc-pack.json", "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}");
    }

    /// <summary>测试适配器仅声明两个受控复制根，不执行自动发现。</summary>
    private ExternalInstanceDefinition Definition() => new(Candidate, new()
    {
        Name = Candidate.Name, SourceFormat = "Prism", GameDirectory = ".minecraft", MinecraftVersion = "1.7.10",
        Components = [new("net.minecraft", "1.7.10")]
    }, [new(Path.Combine(Source, ".minecraft"), ".minecraft", []), new(Path.Combine(Source, "mmc-pack.json"), "mmc-pack.json", [])],
        new Dictionary<string, string> { ["instance.cfg"] = "[General]\nname=Old\n" });

    /// <summary>允许单个用例提供假适配器或小额度，其他行为使用生产复制服务。</summary>
    private MinecraftFolderImporter Service(InstanceArchiveLimits? limits = null,
        Func<ExternalMinecraftInstance, CancellationToken, ExternalInstanceDefinition>? read = null) =>
        new(Launcher, read ?? ((_, _) => Definition()), limits ?? InstanceArchiveLimits.Default);

    /// <summary>写入专属测试源，不覆盖真实实例。</summary>
    private string Write(string relative, string content)
    {
        var path = Path.Combine(Source, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>未提交的导入不能出现在实例列表，也不能残留自己的暂存目录。</summary>
    private void AssertNoImport()
    {
        foreach (var name in new[] { "instances", ".eml-imports" })
        {
            var directory = Path.Combine(Launcher, name);
            if (Directory.Exists(directory)) Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
    }

    /// <summary>直接复制保留中文路径和玩家数据，不压缩、不写源，所有文件拥有独立的本地基线。</summary>
    [Fact]
    public async Task CopiesPrismWithoutZipOrSourceChangesAndRecordsOwnHashes()
    {
        var before = Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        var service = Service();
        var preview = await service.InspectAsync(Candidate);
        AssertNoImport();
        var result = await service.ImportAsync(Candidate, "导入副本（中文）");
        Assert.Equal("导入副本（中文）", result.Manifest.Name);
        Assert.Equal("Prism", result.Manifest.SourceFormat);
        Assert.Equal("", result.Manifest.SourceArchive);
        Assert.Contains("name=导入副本（中文）", File.ReadAllText(Path.Combine(result.InstancePath, "instance.cfg")));
        Assert.Equal("local world", File.ReadAllText(Path.Combine(result.GameDirectory, "saves", "世界，测试", "level.dat")));
        Assert.Equal(preview.Import.EmbeddedFileCount, result.Manifest.Files.Count);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(before.Count, Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories).Count());
        Assert.Empty(Directory.EnumerateFiles(Launcher, "*.zip", SearchOption.AllDirectories));
        foreach (var file in result.Manifest.Files)
        {
            var bytes = File.ReadAllBytes(Path.Combine(result.InstancePath, file.Path!));
            Assert.Equal(InstanceFileSource.Embedded, file.Source);
            Assert.Equal(InstanceFileOrigin.LocalFolderImport, file.Origin);
            Assert.Equal(bytes.LongLength, file.Size);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), Assert.Single(file.Hashes).Value);
            Assert.Equal("sha256", Assert.Single(file.Hashes).Key);
            Assert.Null(file.ArchiveEntry);
            Assert.Null(file.CurseForgeOrigin);
            Assert.Empty(file.Downloads);
        }
        var saved = InstanceManifestStore.Read(Path.Combine(result.InstancePath, InstanceImportManifest.FileName));
        Assert.Equal(result.Manifest.Files.Count, saved.Files.Count);
    }

    /// <summary>已知账号数据库和根启动钩子不迁移，游戏自己的脚本和配置仍然保留。</summary>
    [Fact]
    public async Task SkipsCredentialsAndLauncherHooksWithoutReadingThem()
    {
        var account = Write(".minecraft/launcher_accounts.json", "private token sentinel");
        Write(".minecraft/accounts.json", "private account sentinel");
        Write(".minecraft/app.db", "private sqlite sentinel");
        Write(".minecraft/start.bat", "dangerous hook sentinel");
        Write(".minecraft/scripts/recipes.zs", "legitimate game recipe");
        Write(".minecraft/config/菜单，设置.cfg", "legitimate config");
        // 如果导入意外读取凭证文件，此独占句柄会让测试失败。
        using var locked = new FileStream(account, FileMode.Open, FileAccess.Read, FileShare.None);
        var service = Service();
        var preview = await service.InspectAsync(Candidate);
        Assert.Contains(account, preview.SkippedItems);
        var result = await service.ImportAsync(Candidate, "copy");
        Assert.False(File.Exists(Path.Combine(result.GameDirectory, "launcher_accounts.json")));
        Assert.False(File.Exists(Path.Combine(result.GameDirectory, "app.db")));
        Assert.False(File.Exists(Path.Combine(result.GameDirectory, "start.bat")));
        Assert.True(File.Exists(Path.Combine(result.GameDirectory, "scripts", "recipes.zs")));
        Assert.True(File.Exists(Path.Combine(result.GameDirectory, "config", "菜单，设置.cfg")));
    }

    /// <summary>玩家删除旧世界、地图、截图和日志不应阻断启动；同目录中的受保护内容仍是必需项。</summary>
    [Fact]
    public async Task PlayerDataIsOptionalButProtectedContentAndConfigurationRemainRequired()
    {
        var playerFiles = new[]
        {
            "saves/世界，测试/level.dat", "saves/世界，测试/playerdata/player.dat", "journeymap/data/mp/map.json",
            "visualprospecting/world/ores.dat", "TCNodeTracker/world/nodes.dat", "screenshots/测试.png",
            "XaeroWaypoints/server/waypoints.txt", "XaeroWorldMap/server/cache.dat", "logs/latest.log", "logs/old.log.gz"
        };
        var requiredFiles = new[]
        {
            "mods/required.jar", "config/game.cfg", "options.txt", "saves/world/datapacks/test/data/recipe.json",
            "saves/world/mod.jar", "saves/world/backup.zip", "journeymap/script.js", "screenshots/not-image.dll",
            "scripts/recipes.zs", "backups/world.zip"
        };
        foreach (var path in playerFiles.Concat(requiredFiles)) Write(".minecraft/" + path, "test payload");
        var service = Service();
        var preview = await service.InspectAsync(Candidate);
        var result = await service.ImportAsync(Candidate, "copy");
        Assert.Equal(preview.Import.EmbeddedFileCount, result.Manifest.Files.Count);
        foreach (var path in playerFiles)
        {
            var requirement = Assert.Single(result.Manifest.Files, file => file.Path == ".minecraft/" + path);
            Assert.False(requirement.Required);
            Assert.True(InstanceFileChangePolicy.IsMutableFile(requirement, result.Manifest.GameDirectory, result.Manifest.SourceFormat));
        }
        foreach (var path in requiredFiles)
            Assert.True(Assert.Single(result.Manifest.Files, file => file.Path == ".minecraft/" + path).Required);
        Assert.True(Assert.Single(result.Manifest.Files, file => file.Path == "mmc-pack.json").Required);
        Assert.True(Assert.Single(result.Manifest.Files, file => file.Path == "instance.cfg").Required);
    }

    /// <summary>既有实例或同名文件不覆盖，原始文件始终保持完整。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefusesExistingDestination(bool directory)
    {
        var target = Path.Combine(Launcher, "instances", "existing");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (directory) { Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "keep.txt"), "keep"); }
        else File.WriteAllText(target, "keep");
        await Assert.ThrowsAsync<IOException>(() => Service().ImportAsync(Candidate, "existing"));
        Assert.Equal("keep", File.ReadAllText(directory ? Path.Combine(target, "keep.txt") : target));
        Assert.False(Directory.Exists(Path.Combine(Launcher, ".eml-imports")));
    }

    /// <summary>取消发生在复制开始前或最后确认时，都不能发布半成品。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRemovesOnlyStaging(bool atFinalValidation)
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(value =>
        {
            if (!atFinalValidation || value.FileName == "正在确认源文件未变化") cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ImportAsync(Candidate, "cancelled", progress, cancellation.Token));
        AssertNoImport();
        Assert.Equal("local mod", File.ReadAllText(Path.Combine(Source, ".minecraft", "mods", "测试模组.jar")));
    }

    /// <summary>复制期间新增、删除或改写文件都应阻止提交。</summary>
    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("change")]
    public async Task SourceMutationBeforeCommitFails(string mutation)
    {
        var source = Path.Combine(Source, ".minecraft", "options.txt");
        var progress = new ImmediateProgress(value =>
        {
            if (value.FileName != "正在确认源文件未变化") return;
            if (mutation == "add") Write(".minecraft/mods/new.jar", "new mod");
            else if (mutation == "delete") File.Delete(source);
            else File.AppendAllText(source, "\nchanged");
        });
        await Assert.ThrowsAsync<IOException>(() => Service().ImportAsync(Candidate, "changed", progress));
        AssertNoImport();
    }

    /// <summary>适配器的运行组件变化同样必须阻止提交，不能只检查游戏文件长度。</summary>
    [Fact]
    public async Task MetadataMutationBeforeCommitFails()
    {
        var changed = false;
        var service = Service(read: (_, _) => changed
            ? Definition() with { Manifest = Definition().Manifest with { MinecraftVersion = "1.12.2" } }
            : Definition());
        var progress = new ImmediateProgress(value => { if (value.FileName == "正在确认源文件未变化") changed = true; });
        await Assert.ThrowsAsync<IOException>(() => service.ImportAsync(Candidate, "changed", progress));
        AssertNoImport();
    }

    /// <summary>适配器未提供已验证的来源快照时，不能将已有 EML 清单重新解释为本地来源。</summary>
    [Fact]
    public async Task RejectsEmlSourceManifestWithoutVerifiedSnapshot()
    {
        Write(InstanceImportManifest.FileName, "do not reinterpret this source");
        await Assert.ThrowsAsync<InvalidDataException>(() => Service().InspectAsync(Candidate));
        AssertNoImport();
    }

    /// <summary>不能将适配器提供的官方要求覆盖为本地摘要。</summary>
    [Fact]
    public async Task RejectsPreexistingRequirements()
    {
        var definition = Definition();
        var service = Service(read: (_, _) => definition with
        {
            Manifest = definition.Manifest with
            {
                Files = [new() { Path = ".minecraft/mods/official.jar", Source = InstanceFileSource.CurseForge, ProjectId = 1, FileId = 2 }]
            }
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(Candidate, "copy"));
        AssertNoImport();
    }

    /// <summary>目录和生成清单都计入相同额度，超限预览不创建目录。</summary>
    [Theory]
    [InlineData(3, 100_000)]
    [InlineData(100, 1)]
    public async Task RejectsEntryAndByteLimits(int entries, long bytes)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(new(entries, bytes)).InspectAsync(Candidate));
        AssertNoImport();
    }

    /// <summary>字节预算包括新清单，刚好达到预算允许，少一字节拒绝。</summary>
    [Fact]
    public async Task CountsGeneratedManifestInByteBudget()
    {
        var preview = await Service().InspectAsync(Candidate);
        var exact = await Service(new(100, preview.TotalBytes)).InspectAsync(Candidate);
        Assert.Equal(preview.TotalBytes, exact.TotalBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(new(100, preview.TotalBytes - 1)).InspectAsync(Candidate));
    }

    /// <summary>目标路径不接受越界或文件与父目录冲突。</summary>
    [Theory]
    [InlineData("../escape")]
    [InlineData(".minecraft/options.txt/child")]
    public async Task RejectsInvalidGeneratedLayout(string target)
    {
        var definition = Definition();
        var service = Service(read: (_, _) => definition with { GeneratedFiles = new Dictionary<string, string> { [target] = "bad" } });
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(Candidate, "copy"));
        AssertNoImport();
    }

    /// <summary>原目录与目标容器包含关系必须拒绝，避免递归拷贝自身。</summary>
    [Fact]
    public async Task RefusesSourceContainingDestination()
    {
        var service = new MinecraftFolderImporter(Path.Combine(Source, "nested-eml"), (_, _) => Definition(), InstanceArchiveLimits.Default);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(Candidate, "copy"));
        Assert.False(Directory.Exists(Path.Combine(Source, "nested-eml")));
    }

    /// <summary>另一个进程仍写入世界锁时不能导入，以免复制正在变化的存档。</summary>
    [Fact]
    public async Task RefusesLockedWorldWithoutChangingSource()
    {
        var path = Write(".minecraft/saves/world/session.lock", "locked");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => Service().ImportAsync(Candidate, "copy"));
        AssertNoImport();
    }

    /// <summary>复制期间打开源文件禁止写共享，无法读取时回滚而不修改它。</summary>
    [Fact]
    public async Task LockedPayloadRollsBack()
    {
        var path = Path.Combine(Source, ".minecraft", "mods", "测试模组.jar");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => Service().ImportAsync(Candidate, "copy"));
        AssertNoImport();
    }

    /// <summary>源中的目录联接不能将实例外的资料带入副本。</summary>
    [Fact]
    public async Task RejectsSourceJunctionWithoutTraversingIt()
    {
        var external = Path.Combine(_root, "not-game-data");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "private.txt"), "private");
        var link = Path.Combine(Source, ".minecraft", "linked");
        CreateDirectoryLink(link, external);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => Service().InspectAsync(Candidate));
            await Assert.ThrowsAsync<IOException>(() => Service().ImportAsync(Candidate, "copy"));
            AssertNoImport();
            Assert.Equal("private", File.ReadAllText(Path.Combine(external, "private.txt")));
        }
        finally { Directory.Delete(link); }
    }

    /// <summary>支持显式跳过启动器私有目录，但保留同级合法空文件夹。</summary>
    [Fact]
    public async Task PreservesEmptyDirectoriesAndRespectsRootExclusions()
    {
        Directory.CreateDirectory(Path.Combine(Source, ".minecraft", "screenshots", "空目录"));
        Write(".minecraft/cache/private.bin", "private cache");
        var definition = Definition();
        var service = Service(read: (_, _) => definition with
        {
            Roots = definition.Roots.Select(root => root.TargetPath == ".minecraft"
                ? root with { ExcludedTopLevelNames = ["cache"] } : root).ToArray()
        });
        var result = await service.ImportAsync(Candidate, "copy");
        Assert.True(Directory.Exists(Path.Combine(result.GameDirectory, "screenshots", "空目录")));
        Assert.False(Directory.Exists(Path.Combine(result.GameDirectory, "cache")));
    }

    /// <summary>进度回调自身失败不应发布半成品；清理只处理本次随机暂存区。</summary>
    [Fact]
    public async Task ProgressFailureCleansOnlyOwnedStaging()
    {
        var unrelated = Path.Combine(Launcher, ".eml-imports", "unrelated");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "keep");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ImportAsync(Candidate, "copy",
            new ImmediateProgress(_ => throw new InvalidOperationException("progress failure"))));
        Assert.Equal(new[] { unrelated }, Directory.GetDirectories(Path.Combine(Launcher, ".eml-imports")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelated, "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(Launcher, "instances", "copy")));
    }

    /// <summary>同步回调让取消、源变化等竞争条件在测试中可重复。</summary>
    private sealed class ImmediateProgress(Action<InstanceImportProgress> report) : IProgress<InstanceImportProgress>
    {
        /// <summary>立即执行注入的测试回调，不引入线程池时序。</summary>
        public void Report(InstanceImportProgress value) => report(value);
    }

    /// <summary>Windows 使用无需管理员符号链接权限的目录联接，其他系统使用目录符号链接。</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
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

    /// <summary>只删除本用例随机临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
