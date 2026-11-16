using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft;

namespace EML.Tests;

/// <summary>验证临时 ZIP 发布边界；源实例及目标备份都只使用独立测试目录。</summary>
public sealed class InstanceExportFinalizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-export-finalization-" + Guid.NewGuid().ToString("N"));

    /// <summary>文件已压缩后发生新增、移除或同长度修改，也不能将混合时点的备份发布。</summary>
    [Theory]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("same-length")]
    [InlineData("directory-added")]
    public async Task FinalSourceChangeCheckPreventsPublishing(string change)
    {
        var source = CreateInstance();
        var file = Path.Combine(source, ".minecraft", "options.txt");
        var destination = Path.Combine(_root, "changed.zip");
        var changed = false;
        var progress = new InlineProgress(value =>
        {
            if (value.FileName != "检查备份恢复兼容性") return;
            changed = true;
            switch (change)
            {
                case "added": File.WriteAllText(Path.Combine(source, ".minecraft", "new.txt"), "new"); break;
                case "removed": File.Delete(file); break;
                case "directory-added": Directory.CreateDirectory(Path.Combine(source, ".minecraft", "new-folder")); break;
                case "same-length":
                    var previous = File.GetLastWriteTimeUtc(file);
                    File.WriteAllText(file, "edit");
                    File.SetLastWriteTimeUtc(file, previous.AddSeconds(2));
                    break;
            }
        });

        await Assert.ThrowsAnyAsync<IOException>(() => Manager().ExportAsync(source, destination, progress));

        Assert.True(changed);
        Assert.False(File.Exists(destination));
        AssertNoTemporaryArchive();
        if (change == "same-length") Assert.Equal("edit", File.ReadAllText(file));
        if (change == "added") Assert.True(File.Exists(Path.Combine(source, ".minecraft", "new.txt")));
        if (change == "removed") Assert.False(File.Exists(file));
        if (change == "directory-added") Assert.True(Directory.Exists(Path.Combine(source, ".minecraft", "new-folder")));
    }

    /// <summary>源目录虽可识别，但包含损坏的平台元数据时，导入结构复核会拒绝发布 ZIP。</summary>
    [Fact]
    public async Task RestoreStructureFailureDoesNotPublishBackup()
    {
        var source = CreateInstance();
        File.WriteAllText(Path.Combine(source, "mmc-pack.json"), "not valid json");
        var destination = Path.Combine(_root, "invalid-prism.zip");

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Manager().ExportAsync(source, destination));

        Assert.False(File.Exists(destination));
        Assert.Equal("not valid json", File.ReadAllText(Path.Combine(source, "mmc-pack.json")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(source, ".minecraft", "options.txt")));
        AssertNoTemporaryArchive();
    }

    /// <summary>写入阶段出现 I/O 异常时只能清理本次临时文件，不应产生正式备份。</summary>
    [Fact]
    public async Task SimulatedIoFailureBeforeCommitCleansTemporaryArchive()
    {
        var source = CreateInstance();
        var destination = Path.Combine(_root, "io-failure.zip");
        var progress = new InlineProgress(_ => throw new IOException("模拟写入阶段 I/O 故障。"));

        await Assert.ThrowsAsync<IOException>(() => Manager().ExportAsync(source, destination, progress));

        Assert.False(File.Exists(destination));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(source, ".minecraft", "options.txt")));
        AssertNoTemporaryArchive();
    }

    /// <summary>恢复兼容性检查开始时取消，仍然不能提交已经写好的临时备份。</summary>
    [Fact]
    public async Task CancellationAtVerificationLeavesNoFinalBackup()
    {
        var source = CreateInstance();
        var destination = Path.Combine(_root, "cancelled.zip");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value.FileName == "检查备份恢复兼容性") cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Manager().ExportAsync(source, destination, progress, cancellation.Token));

        Assert.False(File.Exists(destination));
        AssertNoTemporaryArchive();
    }

    /// <summary>正式备份已经发布后，外部完成通知失败不得将操作结果改报为失败。</summary>
    [Fact]
    public async Task CompletionNotificationFailureDoesNotInvalidatePublishedBackup()
    {
        var source = CreateInstance();
        var destination = Path.Combine(_root, "complete.zip");
        var notified = false;
        var progress = new InlineProgress(value =>
        {
            if (value.FileName != "完成") return;
            notified = true;
            throw new InvalidOperationException("模拟 UI 已关闭。");
        });

        await Manager().ExportAsync(source, destination, progress);

        Assert.True(notified);
        Assert.True(File.Exists(destination));
        var restored = await new MinecraftInstanceImporter(_root).ImportAsync(destination, "Restored");
        Assert.Equal("keep", File.ReadAllText(Path.Combine(restored.GameDirectory, "options.txt")));
        AssertNoTemporaryArchive();
    }

    /// <summary>创建可识别的普通实例，避免访问真实游戏文件。</summary>
    private string CreateInstance()
    {
        var source = Path.Combine(_root, "instances", "Original");
        Directory.CreateDirectory(Path.Combine(source, ".minecraft"));
        File.WriteAllText(Path.Combine(source, ".minecraft", "options.txt"), "keep");
        return source;
    }

    /// <summary>测试使用的管理器不允许调用回收站。</summary>
    private MinecraftInstanceManager Manager() =>
        new(_root, _ => throw new InvalidOperationException("不允许回收测试目录以外的实例。"));

    /// <summary>断言本次导出没有遗留临时 ZIP。</summary>
    private void AssertNoTemporaryArchive() => Assert.Empty(Directory.GetFiles(_root, ".eml-export-*.tmp"));

    /// <summary>同步触发阶段通知，保证故障和取消的注入时机可重复。</summary>
    private sealed class InlineProgress(Action<InstanceExportProgress> callback) : IProgress<InstanceExportProgress>
    {
        /// <summary>执行测试回调，不依赖 UI 消息循环。</summary>
        public void Report(InstanceExportProgress value) => callback(value);
    }

    /// <summary>仅清理本测试的唯一临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
