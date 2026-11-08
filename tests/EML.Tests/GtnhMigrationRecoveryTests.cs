using System.Diagnostics;
using EML.Infrastructure.Minecraft.Migration;

namespace EML.Tests;

public sealed partial class GtnhMigrationTests
{
    /// <summary>世界中新产生的文件也属于后续进度，不能仅比对旧文件就拆散当前世界。</summary>
    [Fact]
    public async Task RollbackPreservesNewFilesInsideMigratedWorld()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "saves/World/level.dat", "old");
        var result = await Service.MigrateAsync(await PlanAsync(old, target, "world:World"), false);
        Write(target.Game, "saves/World/playerdata/new.dat", "new player");
        Assert.Equal("RecoveryRequired", (await Service.RestoreAsync(target.Path, result.Id)).Status);
        Assert.Equal("old", Read(target.Game, "saves/World/level.dat"));
        Assert.Equal("new player", Read(target.Game, "saves/World/playerdata/new.dat"));
        MigrationJournalStore.EnsureSettled(target.Path);
    }

    /// <summary>未改动的新世界恢复后清理空目录，下一次迁移无需生成多余副本名称。</summary>
    [Fact]
    public async Task RollbackRemovesOnlyEmptyNewWorldDirectories()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "saves/World/level.dat", "old"); Write(old.Game, "saves/World/data/quest.dat", "quest");
        var result = await Service.MigrateAsync(await PlanAsync(old, target, "world:World"), false);
        Assert.Equal("RolledBack", (await Service.RestoreAsync(target.Path, result.Id)).Status);
        Assert.False(Directory.Exists(Path.Combine(target.Game, "saves/World")));
        Assert.All((await PlanAsync(old, target, "world:World")).Files, f => Assert.StartsWith("saves/World/", f.TargetPath));
    }

    /// <summary>暂存中源目录新增文件会使预览失效，禁止提交不完整的世界快照。</summary>
    [Fact]
    public async Task SourceAddedDuringStagingInvalidatesPlan()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new"); Write(old.Game, "saves/World/level.dat", "old");
        var result = await Service.MigrateAsync(await PlanAsync(old, target, "world:World"), false,
            new Callback(p => { if (p.Stage == "暂存并校验") Write(old.Game, "saves/World/new.dat", "new"); }));
        Assert.Equal("RolledBack", result.Status);
        Assert.False(File.Exists(Path.Combine(target.Game, "saves/World/level.dat")));
    }

    /// <summary>目标被外部移动时不能重建旧目录，日志留在移动后实例供用户恢复。</summary>
    [Fact]
    public async Task TargetMovedDuringCommitIsNotRecreated()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new"); Write(old.Game, "servers.dat", "old");
        var moved = Path.Combine(_root, "instances", "moved");
        var result = await Service.MigrateAsync(await PlanAsync(old, target, "servers"), false,
            new Callback(p => { if (p.Stage == "提交迁移" && p.Completed == 0) Directory.Move(target.Path, moved); }));
        Assert.Equal("RecoveryRequired", result.Status);
        Assert.False(Directory.Exists(target.Path));
        Assert.Equal("RolledBack", (await Service.RestoreAsync(moved, result.Id)).Status);
    }

    /// <summary>旧数据不能迁入更低的 GTNH 基础版本，避免把更新方向选反。</summary>
    [Fact]
    public async Task DowngradeIsRejected()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new"); Write(old.Game, "servers.dat", "old");
        Write(target.Game, "config/txloader/load/mainmenu/version.txt", "GTNH 2.8.4");
        await Assert.ThrowsAsync<InvalidOperationException>(() => PlanAsync(old, target, "servers"));
    }

    /// <summary>目录联接不能把地图复制范围扩大到实例以外。</summary>
    [Fact]
    public async Task JunctionIsRejectedWithoutTouchingLinkedData()
    {
        if (!OperatingSystem.IsWindows()) return;
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        var outside = Path.Combine(_root, "external"); Write(outside, "map", "outside");
        var link = Path.Combine(old.Game, "journeymap");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link); start.ArgumentList.Add(outside);
        using var process = Process.Start(start)!; await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => PlanAsync(old, target, "maps"));
            Assert.Equal("outside", Read(outside, "map"));
        }
        finally { Directory.Delete(link); }
    }
}
