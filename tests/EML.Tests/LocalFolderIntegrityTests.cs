using System.Security.Cryptography;
using System.Text;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Tests;

/// <summary>目录复制有独立来源；本地快照不冒充平台摘要，正常设置变化与严格内容保持区分。</summary>
public sealed class LocalFolderIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-folder-integrity-" + Guid.NewGuid().ToString("N"));
    private string Instance => Path.Combine(_root, "instances", "original");

    /// <summary>每项测试只创建独立临时实例，不访问用户的游戏目录。</summary>
    public LocalFolderIntegrityTests() => Directory.CreateDirectory(Instance);

    /// <summary>本地复制也必须有自己的大小和 SHA-256，不能混用平台的摘要。</summary>
    [Fact]
    public void FolderOriginRequiresItsOwnIntegrity()
    {
        var requirement = Requirement(".minecraft/mods/test.jar", "base");
        Assert.Same(requirement.Hashes, InstanceFileIntegrity.HashesFor(requirement));
        Assert.Throws<InvalidDataException>(() => InstanceFileIntegrity.HashesFor(requirement with { Size = null }));
        Assert.Throws<InvalidDataException>(() => InstanceFileIntegrity.HashesFor(requirement with
        { Hashes = new Dictionary<string, string> { ["sha1"] = new('a', 40) } }));
        Assert.Throws<InvalidDataException>(() => InstanceFileIntegrity.HashesFor(requirement with
        { Downloads = ["https://example.invalid/test.jar"] }));
    }

    /// <summary>只允许熟悉的设置和世界数据改变；模组及配置目录中的数据包不能借本地来源绕过校验。</summary>
    [Theory]
    [InlineData(".minecraft/options.txt", true)]
    [InlineData(".minecraft/config/settings.cfg", true)]
    [InlineData(".minecraft/saves/World/level.dat", true)]
    [InlineData(".minecraft/journeymap/data/mp/server/waypoints/home.json", true)]
    [InlineData(".minecraft/screenshots/中文截图.png", true)]
    [InlineData(".minecraft/saves/World/datapacks/test/data/rules.json", false)]
    [InlineData(".minecraft/mods/test.jar", false)]
    [InlineData(".minecraft/config/paxi/datapacks/test.zip", false)]
    public async Task CheckerAndInstallerAgreeOnChangedLocalFiles(string path, bool mutable)
    {
        var requirement = Requirement(path, "base");
        var manifest = await WriteManifestAsync(requirement);
        Write(path, "user changes");
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(Instance);
        Assert.Equal(mutable, report.ContentReady);
        var issues = await InstanceContentVerifier.VerifyRequiredAsync(Instance, manifest, [requirement], CancellationToken.None);
        Assert.Equal(mutable, issues.Count == 0);
        Assert.False(InstanceFileChangePolicy.IsMutableFile(requirement with { Origin = InstanceFileOrigin.UserLocalImport }, ".minecraft"));
    }

    /// <summary>缺失模组不从旧目录静默补回，更不能换成同名的远端文件。</summary>
    [Fact]
    public async Task MissingLocalContentRemainsMissingWithoutArchive()
    {
        var requirement = Requirement(".minecraft/mods/test.jar", "base");
        var manifest = await WriteManifestAsync(requirement);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(Instance);
        Assert.Equal(InstanceFileState.MissingEmbeddedFile, Assert.Single(report.Files).State);
        Assert.False(report.ContentReady);
        Assert.False(await new EmbeddedFileRestorer(_root).RestoreAsync(Instance, manifest, requirement, CancellationToken.None));
    }

    /// <summary>目录导入中的空更新标记有自身快照依据，游戏消耗它不会误判模组损坏。</summary>
    [Fact]
    public async Task EmptyUpdateMarkerMayBeConsumed()
    {
        var requirement = Requirement(".minecraft/config/modpack-update", "");
        await WriteManifestAsync(requirement);
        var report = await new MinecraftInstanceChecker(_root).CheckAsync(Instance);
        Assert.True(report.ContentReady);
        Assert.Equal(InstanceFileState.Consumed, Assert.Single(report.Files).State);
    }

    /// <summary>用户明确接受本地模组替换后，检查器与安装器均使用该选择，原始基线仍保留。</summary>
    [Fact]
    public async Task AcceptedLocalReplacementIsRecognizedByLaterChecks()
    {
        var requirement = Requirement(".minecraft/mods/test.jar", "base");
        await WriteManifestAsync(requirement);
        Write(requirement.Path!, "changed");
        Assert.False((await new MinecraftInstanceChecker(_root).CheckAsync(Instance)).ContentReady);
        await new InstanceLocalReplacementService(_root).AcceptExistingAsync(Instance, requirement.Path!);
        Assert.True((await new MinecraftInstanceChecker(_root).CheckAsync(Instance)).ContentReady);
        Assert.Equal(InstanceFileOrigin.LocalFolderImport,
            Assert.Single(InstanceManifestStore.Read(Path.Combine(Instance, InstanceImportManifest.FileName)).Files).Origin);
    }

    /// <summary>备份与回导保留目录来源及原始内容摘要，并可从新备份恢复缺失文件。</summary>
    [Fact]
    public async Task BackupRoundTripPreservesOriginAndSupportsRestoration()
    {
        var requirement = Requirement(".minecraft/mods/test.jar", "base");
        await WriteManifestAsync(requirement);
        Write(requirement.Path!, "base");
        var archive = Path.Combine(_root, "backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(Instance, archive);
        var restored = await new MinecraftInstanceImporter(_root).ImportAsync(archive, "restored");
        var file = Assert.Single(restored.Manifest.Files);
        Assert.Equal(InstanceFileOrigin.LocalFolderImport, file.Origin);
        Assert.Equal(requirement.Hashes["sha256"], file.Hashes["sha256"]);
        Assert.NotNull(file.ArchiveEntry);
        var copied = Path.Combine(restored.InstancePath, file.Path!);
        File.Delete(copied);
        Assert.True(await new EmbeddedFileRestorer(_root).RestoreAsync(restored.InstancePath, restored.Manifest, file, CancellationToken.None));
        Assert.Equal("base", File.ReadAllText(copied));
    }

    /// <summary>把实际复制的字节登记为本地快照，而不是主动替换官方固定文件。</summary>
    private static InstanceFileRequirement Requirement(string path, string content) => new()
    {
        Path = path, Source = InstanceFileSource.Embedded, Origin = InstanceFileOrigin.LocalFolderImport,
        Size = Encoding.UTF8.GetByteCount(content),
        Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))) }
    };

    /// <summary>按生产规则保存测试清单，不生成来源恢复位置。</summary>
    private async Task<InstanceImportManifest> WriteManifestAsync(InstanceFileRequirement requirement)
    {
        var manifest = new InstanceImportManifest { Name = "original", Files = [requirement] };
        await InstanceManifestStore.WriteAsync(Path.Combine(Instance, InstanceImportManifest.FileName), manifest, CancellationToken.None);
        return manifest;
    }

    /// <summary>只在当前测试实例内部写入载荷。</summary>
    private void Write(string path, string content)
    {
        var full = Path.Combine(Instance, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>仅删除本测试拥有的随机临时根目录。</summary>
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
