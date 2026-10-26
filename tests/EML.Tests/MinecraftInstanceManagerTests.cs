using System.IO.Compression;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft;

namespace EML.Tests;

/// <summary>所有文件操作仅使用唯一临时目录，回收操作由测试替身代替。</summary>
public sealed class MinecraftInstanceManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-management-tests-" + Guid.NewGuid().ToString("N"));
    /// <summary>创建最小可识别实例，返回实例根目录。</summary>
    private string CreateInstance(string name = "Original")
    {
        var path = Path.Combine(_root, "instances", name);
        Directory.CreateDirectory(Path.Combine(path, ".minecraft", "saves", "empty"));
        File.WriteAllText(Path.Combine(path, ".minecraft", "options.txt"), "keep me");
        return path;
    }

    /// <summary>普通实例重命名必须保留文件内容并返回新路径。</summary>
    [Fact]
    public async Task RenamePreservesFiles()
    {
        var original = CreateInstance();
        var path = await new MinecraftInstanceManager(_root).RenameAsync(original, "新实例");
        Assert.False(Directory.Exists(original));
        Assert.Equal(Path.Combine(_root, "instances", "新实例"), path);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(path, ".minecraft", "options.txt")));
    }

    /// <summary>Prism 重命名只更新 General 名称，其他节和设置保持原值。</summary>
    [Fact]
    public async Task RenameUpdatesPrismConfig()
    {
        var original = CreateInstance();
        File.WriteAllText(Path.Combine(original, "mmc-pack.json"), "{}");
        File.WriteAllText(Path.Combine(original, "instance.cfg"), "[General]\r\nname=Old\r\niconKey=default\r\n[Other]\r\nname=Untouched\r\n");
        var path = await new MinecraftInstanceManager(_root).RenameAsync(original, "New");
        var config = File.ReadAllText(Path.Combine(path, "instance.cfg"));
        Assert.Contains("name=New\r\niconKey=default", config);
        Assert.Contains("[Other]\r\nname=Untouched", config);
    }

    /// <summary>缺少 General 节或 name 时自动补齐。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("[Other]\nname=keep")]
    [InlineData("[General]\niconKey=default\n[Other]\nname=keep")]
    public void MissingPrismNameIsInserted(string original)
    {
        var updated = MinecraftInstanceManager.UpdatePrismName(original, "New");
        Assert.Contains("[General]", updated);
        Assert.Contains("name=New", updated);
        if (original.Contains("name=keep")) Assert.Contains("name=keep", updated);
    }

    /// <summary>非法文件名不能导致原实例被移动。</summary>
    [Theory]
    [InlineData("../escape")]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData("trailing.")]
    [InlineData(" spaced ")]
    [InlineData("")]
    public async Task InvalidNameDoesNotMutate(string name)
    {
        var path = CreateInstance();
        await Assert.ThrowsAsync<ArgumentException>(() => new MinecraftInstanceManager(_root).RenameAsync(path, name));
        Assert.True(Directory.Exists(path));
    }

    /// <summary>重名不能覆盖另一个实例。</summary>
    [Fact]
    public async Task CollisionIsRejected()
    {
        var path = CreateInstance();
        var other = CreateInstance("Existing");
        await Assert.ThrowsAsync<IOException>(() => new MinecraftInstanceManager(_root).RenameAsync(path, "Existing"));
        Assert.True(Directory.Exists(path));
        Assert.True(Directory.Exists(other));
    }

    /// <summary>Windows 大小写重命名通过中间目录正确完成。</summary>
    [Fact]
    public async Task CaseOnlyRenameWorks()
    {
        var path = CreateInstance();
        var result = await new MinecraftInstanceManager(_root).RenameAsync(path, "original");
        Assert.Equal("original", new DirectoryInfo(result).Name);
        Assert.DoesNotContain(Directory.GetDirectories(Path.Combine(_root, "instances")), p => p.Contains(".eml-rename-"));
    }

    /// <summary>拒绝 instances 根目录、外部目录以及实例内部目录。</summary>
    [Fact]
    public async Task DeleteRejectsOutOfScopeTargets()
    {
        var path = CreateInstance();
        var manager = new MinecraftInstanceManager(_root, _ => throw new Exception("不应调用回收"));
        foreach (var invalid in new[] { _root, Path.Combine(_root, "instances"), Path.Combine(path, ".minecraft") })
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DeleteAsync(invalid));
        Assert.True(Directory.Exists(path));
    }

    /// <summary>验证回收只接收选中的精确实例路径，不实际调用系统回收站。</summary>
    [Fact]
    public async Task DeleteUsesRecycleCallback()
    {
        var path = CreateInstance();
        var trash = Path.Combine(_root, "test-trash");
        var manager = new MinecraftInstanceManager(_root, actual =>
        {
            Assert.Equal(path, actual);
            Directory.Move(actual, trash);
        });
        await manager.DeleteAsync(path);
        Assert.False(Directory.Exists(path));
        Assert.True(File.Exists(Path.Combine(trash, ".minecraft", "options.txt")));
    }

    /// <summary>ZIP 根层保留游戏目录、配置内容以及空目录。</summary>
    [Fact]
    public async Task ExportPreservesContentsAndEmptyDirectories()
    {
        var path = CreateInstance();
        var destination = Path.Combine(_root, "backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(path, destination);
        using var zip = ZipFile.OpenRead(destination);
        Assert.NotNull(zip.GetEntry(".minecraft/saves/empty/"));
        using var reader = new StreamReader(zip.GetEntry(".minecraft/options.txt")!.Open());
        Assert.Equal("keep me", reader.ReadToEnd());
        Assert.Empty(Directory.GetFiles(_root, ".eml-export-*.tmp"));
    }

    /// <summary>拒绝覆盖旧备份和写入实例内部。</summary>
    [Fact]
    public async Task ExportRejectsUnsafeDestinations()
    {
        var path = CreateInstance();
        var destination = Path.Combine(_root, "backup.zip");
        File.WriteAllText(destination, "original backup");
        var manager = new MinecraftInstanceManager(_root);
        await Assert.ThrowsAsync<IOException>(() => manager.ExportAsync(path, destination));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.ExportAsync(path, Path.Combine(path, "backup.zip")));
        Assert.Equal("original backup", File.ReadAllText(destination));
    }

    /// <summary>开始前取消三种操作都不修改实例或创建导出文件。</summary>
    [Fact]
    public async Task CancellationDoesNotMutate()
    {
        var path = CreateInstance();
        var manager = new MinecraftInstanceManager(_root);
        var token = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.RenameAsync(path, "New", token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.DeleteAsync(path, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ExportAsync(path, Path.Combine(_root, "backup.zip"), cancellationToken: token));
        Assert.True(Directory.Exists(path));
        Assert.False(File.Exists(Path.Combine(_root, "backup.zip")));
    }

    /// <summary>临时 ZIP 已创建后取消，仍须清理临时文件并保留源实例。</summary>
    [Fact]
    public async Task CancellationAfterExportStartsCleansTemporaryFile()
    {
        var path = CreateInstance();
        using var cancellation = new CancellationTokenSource();
        var destination = Path.Combine(_root, "backup.zip");
        var progress = new InlineProgress(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MinecraftInstanceManager(_root).ExportAsync(path, destination, progress, cancellation.Token));
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(_root, ".eml-export-*.tmp"));
        Assert.True(File.Exists(Path.Combine(path, ".minecraft", "options.txt")));
    }

    /// <summary>同步发送测试进度，让取消的时机确定，不依赖线程调度速度。</summary>
    private sealed class InlineProgress(Action<InstanceExportProgress> callback) : IProgress<InstanceExportProgress>
    {
        /// <summary>在导出线程立即调用测试回调。</summary>
        public void Report(InstanceExportProgress value) => callback(value);
    }

    /// <summary>仅清理由本测试创建的 GUID 临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
