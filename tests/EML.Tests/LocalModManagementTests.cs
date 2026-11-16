using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;

namespace EML.Tests;

/// <summary>在独立临时实例中验证模组管理与检查、备份和迁移，不读取真实玩家数据。</summary>
public sealed partial class LocalModManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-local-mods-" + Guid.NewGuid().ToString("N"));
    private sealed record Instance(string Path, string Game, string Relative);

    /// <summary>建立带 GTNH 识别特征的最小实例，让迁移与模组管理使用真实入口。</summary>
    private async Task<Instance> CreateAsync(string name = "Test", string game = ".minecraft")
    {
        var path = Path.Combine(_root, "instances", name);
        var directory = game == "." ? path : Path.Combine(path, game);
        Directory.CreateDirectory(directory);
        Write(directory, "config/txloader/load/mainmenu/version.txt", "GTNH 2.9.0 (test)");
        Write(directory, "config/GregTech/GregTech.cfg", "fixture");
        Write(directory, "config/bartworks.cfg", "fixture");
        await InstanceManifestStore.WriteAsync(Path.Combine(path, InstanceImportManifest.FileName), new()
        {
            Name = name, GameDirectory = game, SourceFormat = "Prism", MinecraftVersion = "1.7.10",
            Components = [new("minecraft", "1.7.10"), new("net.minecraftforge", "10.13.4.1614")]
        }, default);
        return new(path, directory, game);
    }

    /// <summary>以实例根目录读取原始清单，避免按文件现状重新建立校验依据。</summary>
    private static InstanceImportManifest Manifest(Instance instance) =>
        InstanceManifestStore.Read(Path.Combine(instance.Path, InstanceImportManifest.FileName));

    /// <summary>将游戏相对路径转换成清单使用的实例相对路径。</summary>
    private static string Relative(Instance instance, string gamePath) =>
        instance.Relative == "." ? gamePath : instance.Relative + "/" + gamePath;

    /// <summary>写入测试私有目录并返回全路径。</summary>
    private static string Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>创建只有元数据和文本的无害模组归档，不执行 Java 代码。</summary>
    private static string Mod(string root, string relative, string id = "example", string version = "1.0",
        string? metadata = null, string metadataEntry = "mcmod.info")
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry(metadataEntry).Open(), new UTF8Encoding(false));
        writer.Write(metadata ?? JsonSerializer.Serialize(new[] { new { modid = id, name = "Example Mod", version } }));
        return path;
    }

    /// <summary>计算已知测试文件的摘要，供独立断言使用。</summary>
    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>建立严格的官方来源基线，后续模组操作不能改变其 SHA-1 要求。</summary>
    private static async Task<InstanceFileRequirement> DeclareOfficialAsync(Instance instance, string gamePath, string content = "official")
    {
        var file = new InstanceFileRequirement
        {
            Path = Relative(instance, gamePath), Source = InstanceFileSource.CurseForge,
            Origin = InstanceFileOrigin.CurseForgeDownload, ProjectId = 1, FileId = 2,
            Size = Encoding.UTF8.GetByteCount(content),
            Hashes = new Dictionary<string, string>
            {
                ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(content)))
            }
        };
        await InstanceManifestStore.WriteAsync(Path.Combine(instance.Path, InstanceImportManifest.FileName),
            Manifest(instance) with { Files = [file] }, default);
        return file;
    }

    /// <summary>任何意外网络请求都立即失败，避免测试静默下载或访问用户凭证。</summary>
    private sealed class NoDownload : IFileDownloader
    {
        public Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request,
            IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("测试禁止网络下载。");
    }

    /// <summary>隔离运行依赖安装，仅测试实际内容检查与补全决策。</summary>
    private sealed class EmptyInstaller : IPrismInstanceInstaller
    {
        public Task<PrismInstallResult> InstallAsync(string instancePath,
            IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PrismInstallResult { SourceFormat = "Prism" });
    }

    /// <summary>只清理本测试创建的唯一临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
