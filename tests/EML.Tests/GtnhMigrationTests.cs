using System.Security.Cryptography;
using System.Text;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;

namespace EML.Tests;

/// <summary>以独立临时实例验证真实文件迁移，不读取或修改用户存档。</summary>
public sealed partial class GtnhMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-migration-" + Guid.NewGuid().ToString("N"));
    private GtnhInstanceMigrationService Service => new(_root);
    private sealed record Instance(string Path, string Game, string Relative);

    /// <summary>名称不授予资格，包内版本与独立特征共同决定是否开放 GTNH 迁移。</summary>
    [Fact]
    public async Task RecognitionRequiresPackEvidence()
    {
        var instance = await CreateAsync("GTNH 2.9.0", ".minecraft");
        var info = await Service.InspectAsync(instance.Path);
        Assert.Equal("2.9.0", info?.Version);
        File.Delete(Path.Combine(instance.Game, "config/txloader/load/mainmenu/version.txt"));
        Assert.Null(await Service.InspectAsync(instance.Path));
        Write(instance.Game, "config/txloader/load/mainmenu/version.txt", "Another pack 2.9.0");
        Assert.Null(await Service.InspectAsync(instance.Path));
    }

    /// <summary>受支持标识可来自不同导入格式的游戏目录，而不是固定 .minecraft 路径。</summary>
    [Theory]
    [InlineData(".")]
    [InlineData("minecraft")]
    [InlineData(".minecraft")]
    public async Task GameDirectoryLayoutsAreSupported(string layout)
    {
        var old = await CreateAsync("old", layout); var target = await CreateAsync("new", ".minecraft");
        Write(old.Game, "journeymap/data/map.json", "map");
        var result = await Service.MigrateAsync(await PlanAsync(old, target, "maps"), false);
        Assert.Equal("Completed", result.Status);
        Assert.Equal("map", Read(target.Game, "journeymap/data/map.json"));
    }

    /// <summary>联机模式默认不带入世界；NEI 仅复制全局记录。</summary>
    [Fact]
    public async Task ServerModeRecommendsOnlyClientRecords()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        foreach (var path in new[] { "journeymap/map", "options.txt", "servers.dat", "saves/World/level.dat", "saves/NEI/global/bookmarks.ini", "saves/NEI/other/bookmarks.ini" })
            Write(old.Game, path, path == "options.txt" ? "lang:zh_CN" : "data");
        var groups = await Service.GetGroupsAsync(old.Path, GtnhMigrationMode.ServerClient);
        Assert.Equal(new[] { "language", "maps", "nei" }, groups.Where(g => g.Recommended).Select(g => g.Id).Order().ToArray());
        var plan = await Service.PreviewAsync(new(old.Path, target.Path, GtnhMigrationMode.ServerClient,
            groups.Where(g => g.Recommended).Select(g => g.Id).ToArray(), true, true));
        Assert.Contains(plan.Files, f => f.SourcePath == "saves/NEI/global/bookmarks.ini");
        Assert.DoesNotContain(plan.Files, f => f.SourcePath.StartsWith("saves/World") || f.SourcePath.Contains("NEI/other"));
    }

    /// <summary>语言迁移保留新版其他设置、BOM 和 CRLF，不复制旧配置或汉化文件。</summary>
    [Fact]
    public async Task LanguageOnlyPreservesTargetOptionsAndLocalization()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "options.txt", "lang:zh_CN\nrenderDistance:5\nresourcePacks:[old-translation]\n");
        Write(target.Game, "options.txt", "\uFEFFrenderDistance:12\r\nlang:en_US\r\nresourcePacks:[]\r\n");
        Write(old.Game, "config/localization.lang", "old language");
        Write(target.Game, "config/localization.lang", "new language");
        Write(old.Game, "resourcepacks/chinese.zip", "old translation");
        Write(old.Game, "mods/chinese.jar", "old mod");
        var original = File.ReadAllBytes(Path.Combine(old.Game, "options.txt"));
        var result = await Service.MigrateAsync(await PlanAsync(old, target, "language"), false);
        Assert.Equal("Completed", result.Status);
        Assert.Equal(Encoding.UTF8.GetBytes("\uFEFFrenderDistance:12\r\nlang:zh_CN\r\nresourcePacks:[]\r\n"), File.ReadAllBytes(Path.Combine(target.Game, "options.txt")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(old.Game, "options.txt")));
        Assert.Equal("new language", Read(target.Game, "config/localization.lang"));
        Assert.False(File.Exists(Path.Combine(target.Game, "resourcepacks/chinese.zip")));
        Assert.False(File.Exists(Path.Combine(target.Game, "mods/chinese.jar")));
    }

    /// <summary>没有语言项时明确报错；不会把不完整 options 或重复值写入新版。</summary>
    [Theory]
    [InlineData("renderDistance:12")]
    [InlineData("lang:zh_CN\nlang:en_US")]
    [InlineData("lang:../bad")]
    public void InvalidSourceLanguageIsRejected(string text) =>
        Assert.Throws<InvalidDataException>(() => MigrationIO.MergeLanguage(Encoding.UTF8.GetBytes(text), []));

    /// <summary>目标无语言项时追加；重复语言项阻止操作而非任意选一项。</summary>
    [Fact]
    public void MissingLanguageIsAddedAndDuplicateTargetIsRejected()
    {
        Assert.Equal("volume:1\r\nlang:zh_CN\r\n", Encoding.UTF8.GetString(MigrationIO.MergeLanguage(
            Encoding.UTF8.GetBytes("lang:zh_CN"), Encoding.UTF8.GetBytes("volume:1\r\n"))));
        Assert.Throws<InvalidDataException>(() => MigrationIO.MergeLanguage(Encoding.UTF8.GetBytes("lang:zh_CN"),
            Encoding.UTF8.GetBytes("lang:en_US\nlang:en_US")));
    }

    /// <summary>同名世界完整另存，目标中原有及源端独有文件不会混成一个世界。</summary>
    [Fact]
    public async Task SameNameWorldsNeverMerge()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "saves/World/level.dat", "old world"); Write(old.Game, "saves/World/data/quest.dat", "quest");
        Write(target.Game, "saves/World/level.dat", "new world"); Write(target.Game, "saves/World/new.dat", "new progress");
        var plan = await PlanAsync(old, target, "world:World");
        Assert.All(plan.Files, f => Assert.StartsWith("saves/World (迁入 1)/", f.TargetPath));
        var result = await Service.MigrateAsync(plan, true);
        Assert.Equal("Completed", result.Status);
        Assert.Equal("new world", Read(target.Game, "saves/World/level.dat"));
        Assert.Equal("old world", Read(target.Game, "saves/World (迁入 1)/level.dat"));
        Assert.False(File.Exists(Path.Combine(target.Game, "saves/World (迁入 1)/new.dat")));
        Assert.False(File.Exists(Path.Combine(target.Game, "saves/World/data/quest.dat")));
    }

    /// <summary>不确认覆盖时保留新版；确认覆盖会备份并允许恢复。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictsRequireExplicitOverwrite(bool overwrite)
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "servers.dat", "old servers"); Write(target.Game, "servers.dat", "new servers");
        var result = await Service.MigrateAsync(await PlanAsync(old, target, "servers"), overwrite);
        Assert.Equal(overwrite ? "old servers" : "new servers", Read(target.Game, "servers.dat"));
        Assert.Equal("old servers", Read(old.Game, "servers.dat"));
        if (!overwrite) { Assert.Equal("NoChanges", result.Status); return; }
        Assert.Equal("Completed", result.Status);
        var restored = await Service.RestoreAsync(target.Path, result.Id);
        Assert.Equal("RolledBack", restored.Status);
        Assert.Equal("new servers", Read(target.Game, "servers.dat"));
    }

    /// <summary>相同文件不会再次写入，也不会建立无意义的恢复事务。</summary>
    [Fact]
    public async Task IdenticalFilesAreSkipped()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "servers.dat", "same"); Write(target.Game, "servers.dat", "same");
        var plan = await PlanAsync(old, target, "servers");
        Assert.Equal(MigrationFileAction.Identical, Assert.Single(plan.Files).Action);
        Assert.Equal("NoChanges", (await Service.MigrateAsync(plan, true)).Status);
        Assert.Null(await Service.GetRecoveryAsync(target.Path));
    }

    /// <summary>旧版或新版在预览后改变时，拒绝使用过期许可并保留所有原件。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StalePreviewIsRejected(bool sourceChanged)
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "servers.dat", "old"); Write(target.Game, "servers.dat", "new");
        var plan = await PlanAsync(old, target, "servers");
        Write(sourceChanged ? old.Game : target.Game, "servers.dat", "changed");
        await Assert.ThrowsAsync<IOException>(() => Service.MigrateAsync(plan, true));
        Assert.Equal(sourceChanged ? "new" : "changed", Read(target.Game, "servers.dat"));
    }

    /// <summary>必须确认游戏退出和新版试运行；同一实例不能作为双方。</summary>
    [Fact]
    public async Task PreviewRequiresDistinctInstancesAndConfirmations()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new"); Write(old.Game, "servers.dat", "old");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.PreviewAsync(new(old.Path, target.Path,
            GtnhMigrationMode.FullClient, ["servers"], false, true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.PreviewAsync(new(old.Path, target.Path,
            GtnhMigrationMode.FullClient, ["servers"], true, false)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.PreviewAsync(new(old.Path, old.Path,
            GtnhMigrationMode.FullClient, ["servers"], true, true)));
    }

    /// <summary>任何自造组或非玩家路径都不能扩大复制及可变文件策略。</summary>
    [Theory]
    [InlineData("mods/a.jar")]
    [InlineData("config/translation.lang")]
    [InlineData("scripts/recipes.zs")]
    [InlineData("../options.txt")]
    [InlineData("saves/../../mods/a.jar")]
    [InlineData(".eml-runtime/runtime.json")]
    public void ExcludedPathsCannotBecomePlayerData(string path) => Assert.False(GtnhPlayerDataPolicy.Allows(path));

    /// <summary>复制过程中取消会恢复目标，源和备份始终保留。</summary>
    [Theory]
    [InlineData("暂存并校验", 1)]
    [InlineData("提交迁移", 1)]
    public async Task CancellationRestoresAppliedFiles(string stage, int index)
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "journeymap/a", "old a"); Write(old.Game, "journeymap/b", "old b");
        Write(target.Game, "journeymap/a", "new a");
        using var cancel = new CancellationTokenSource();
        var progress = new Callback(p => { if (p.Stage == stage && p.Completed == index) cancel.Cancel(); });
        var result = await Service.MigrateAsync(await PlanAsync(old, target, "maps"), true, progress, cancel.Token);
        Assert.Equal("RolledBack", result.Status);
        Assert.Equal("new a", Read(target.Game, "journeymap/a"));
        Assert.False(File.Exists(Path.Combine(target.Game, "journeymap/b")));
        Assert.False(File.Exists(Path.Combine(target.Path, MigrationDataStore.RelativePath)));
        Assert.Equal("old a", Read(old.Game, "journeymap/a"));
    }

    /// <summary>迁移后继续游玩产生的新内容不能被恢复操作覆盖。</summary>
    [Fact]
    public async Task RollbackPreservesSubsequentGameplay()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "saves/World/level.dat", "old progress");
        var result = await Service.MigrateAsync(await PlanAsync(old, target, "world:World"), false);
        Write(target.Game, "saves/World/level.dat", "new progress");
        var restored = await Service.RestoreAsync(target.Path, result.Id);
        Assert.Equal("RecoveryRequired", restored.Status);
        Assert.Equal("new progress", Read(target.Game, "saves/World/level.dat"));
        MigrationJournalStore.EnsureSettled(target.Path); // 完成后的恢复预检失败不阻断继续游玩。
        Assert.Equal("Completed", (await Service.GetRecoveryAsync(target.Path))?.Status);
    }

    /// <summary>只恢复最近一笔迁移，依次回退后恢复前一次玩家记录。</summary>
    [Fact]
    public async Task RepeatedMigrationsRestoreInReverseOrder()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(target.Game, "servers.dat", "initial"); Write(old.Game, "servers.dat", "first");
        var first = await Service.MigrateAsync(await PlanAsync(old, target, "servers"), true);
        Write(old.Game, "servers.dat", "second");
        var second = await Service.MigrateAsync(await PlanAsync(old, target, "servers"), true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.RestoreAsync(target.Path, first.Id));
        Assert.Equal("RolledBack", (await Service.RestoreAsync(target.Path, second.Id)).Status);
        Assert.Equal("first", Read(target.Game, "servers.dat"));
        Assert.Equal(first.Id, (await Service.GetRecoveryAsync(target.Path))?.Id);
        Assert.Equal("RolledBack", (await Service.RestoreAsync(target.Path, first.Id)).Status);
        Assert.Equal("initial", Read(target.Game, "servers.dat"));
    }

    /// <summary>构建只有 GTNH 特征的临时实例，无网络、Java 或真实游戏依赖。</summary>
    private async Task<Instance> CreateAsync(string name, string game = ".minecraft")
    {
        var path = Path.Combine(_root, "instances", name); var directory = game == "." ? path : Path.Combine(path, game);
        Directory.CreateDirectory(directory);
        Write(directory, "config/txloader/load/mainmenu/version.txt", "GTNH 2.9.0 (test)");
        Write(directory, "config/GregTech/GregTech.cfg", "fixture"); Write(directory, "config/bartworks.cfg", "fixture");
        await InstanceManifestStore.WriteAsync(Path.Combine(path, InstanceImportManifest.FileName),
            new() { Name = name, GameDirectory = game, SourceFormat = "Prism", MinecraftVersion = "1.7.10" }, default);
        return new(path, directory, game);
    }
    /// <summary>为已选组生成真正的只读生产计划。</summary>
    private Task<InstanceMigrationPlan> PlanAsync(Instance source, Instance target, params string[] groups) =>
        Service.PreviewAsync(new(source.Path, target.Path, GtnhMigrationMode.FullClient, groups, true, true));
    /// <summary>仅在测试私有根目录创建数据。</summary>
    private static void Write(string root, string path, string value)
    {
        var full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Encoding.UTF8.GetBytes(value));
    }
    /// <summary>读取测试文件进行逐字节内容比较。</summary>
    private static string Read(string root, string path) => File.ReadAllText(Path.Combine(root, path));
    /// <summary>摘要由已知测试字符串建立，不来自目标文件。</summary>
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    /// <summary>同步进度替身用于在确定提交阶段注入取消或外部修改。</summary>
    private sealed class Callback(Action<InstanceMigrationProgress> action) : IProgress<InstanceMigrationProgress>
    {
        public void Report(InstanceMigrationProgress value) => action(value);
    }
    /// <summary>只清理本测试创建的唯一临时目录。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
