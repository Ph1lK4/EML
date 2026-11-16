using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Infrastructure.Minecraft.Gtnh;

/// <summary>将已验证的资源包或光影登记为独立新增内容，不改原包清单、模组目录或游戏选项。</summary>
public sealed class GtnhContentRegistrar(string? launcherRoot = null) : IGtnhContentRegistrar
{
    private readonly string _launcherRoot = Path.GetFullPath(launcherRoot ?? AppContext.BaseDirectory);
    private const long MaximumBytes = 256L * 1024 * 1024;

    /// <summary>固定官方需求快照后取得实例写锁；运行中、迁移中和模组事务未完成时均拒绝安装。</summary>
    public Task RegisterDownloadedAsync(string instancePath, string stagedFile, InstanceFileRequirement requirement,
        GtnhAddonKind kind, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ModManagementStore.ValidateOnlineBaseline(requirement);
        var baseline = requirement with
        {
            Hashes = requirement.Hashes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            Downloads = requirement.Downloads.ToArray(),
            CurseForgeOrigin = requirement.CurseForgeOrigin is not { } origin ? null : origin with
            { Hashes = origin.Hashes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) }
        };
        return Task.Run(() => RegisterCoreAsync(instancePath, stagedFile, baseline, kind, token), token);
    }

    /// <summary>复制到实例私有暂存文件，校验官方基线后先保存来源再原子移入；中断时保留可补全的可信需求。</summary>
    private async Task RegisterCoreAsync(string instancePath, string sourcePath, InstanceFileRequirement baseline,
        GtnhAddonKind kind, CancellationToken token)
    {
        var root = EmbeddedFileBaseline.ValidateInstance(_launcherRoot, instancePath);
        using var lease = InstanceOperationLease.Acquire(root);
        MigrationJournalStore.EnsureSettled(root);
        ModOperationJournal.EnsureSettled(root);
        if (GtnhIdentity.Read(_launcherRoot, root) is null)
            throw new InvalidOperationException("请选择已识别的 GTNH 实例安装资源包或光影。");
        var manifest = InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName));
        var relative = AddedContentStore.ValidateRequirement(manifest, baseline, kind);
        if (baseline.Size is null or <= 0 or > MaximumBytes)
            throw new InvalidDataException("资源 ZIP 大小必须在 1 字节到 256 MiB 之间。");
        var effective = InstanceEffectiveRequirements.Build(root, manifest);
        if (effective.Requirements.Any(file => file.Path is null))
            throw new InvalidOperationException("实例仍有未解析的文件来源，请先补全实例后再添加资源。");
        var prior = effective.AddedContent.Files.SingleOrDefault(file =>
            string.Equals(file.Baseline.Path, relative, StringComparison.OrdinalIgnoreCase));
        if (prior is not null && (prior.Kind != kind || EmbeddedFileBaseline.RequirementFingerprint(prior.Baseline) !=
            EmbeddedFileBaseline.RequirementFingerprint(baseline)))
            throw new InvalidOperationException("已有同名新增资源的来源与本次不同，现有记录未改变：" + relative);
        var after = prior is not null ? effective.AddedContent.Files :
            effective.AddedContent.Files.Append(new AddedContentFile(kind, baseline)).ToArray();
        AddedContentStore.Validate(manifest, after, effective.Migration.Files);
        if (prior is null && effective.Requirements.Any(file => file.Path is { } path &&
                string.Equals(path, relative, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("不能替换实例已有的内容来源：" + relative);
        var destination = MigrationIO.SafePath(root, relative);
        EnsureUnoccupied(destination);
        var source = Path.GetFullPath(sourcePath);
        InstanceImportPaths.EnsureNoLinks(source);
        var temporaryRelative = ".eml-runtime/added-content/staging/" + Guid.NewGuid().ToString("N") + ".zip";
        var temporary = MigrationIO.SafePath(root, temporaryRelative);
        var created = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            {
                if (input.Length != baseline.Size) throw new InvalidDataException("下载资源的大小与官方记录不一致：" + source);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
                created = true;
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            if (!await InstanceRuntimeInstaller.ExistingFileMatchesAsync(temporary, baseline, token).ConfigureAwait(false))
                throw new InvalidDataException("下载资源未通过官方大小或哈希校验：" + source);
            RequireZip(temporary, kind);
            token.ThrowIfCancellationRequested();
            InstanceEffectiveRequirements.EnsureUnchanged(root, manifest, effective);
            MigrationIO.SafePath(root, relative);
            EnsureUnoccupied(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // 从此开始只完成一次很短的本地提交。来源先落盘，即使进程在移动前退出也不会出现无来源的已安装文件。
            // 移动失败时保留官方记录；检查会报告缺失或不符，补全只恢复缺失文件，不信任或覆盖未知现有内容。
            AddedContentStore.Write(root, manifest, after);
            MigrationIO.SafePath(root, relative);
            MigrationIO.SafePath(root, temporaryRelative);
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (created) CleanStaging(root, temporaryRelative);
        }
    }

    /// <summary>已有文件和目录都保留，哪怕字节与下载一致也不自动接管用户内容。</summary>
    private static void EnsureUnoccupied(string destination)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("目标已存在，未覆盖现有文件或目录：" + destination);
    }

    /// <summary>确认 ZIP 包含该种类的游戏入口；只读取少量元数据，不拆包或改变平台官方字节。</summary>
    private static void RequireZip(string path, GtnhAddonKind kind)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count == 0 || archive.Entries.Count > SafeInstanceArchive.MaxEntries)
                throw new InvalidDataException("资源 ZIP 为空或条目数量超过限制。");
            if (kind == GtnhAddonKind.ResourcePack)
                RequirePackMetadata(archive);
            else if (!archive.Entries.Any(IsReadableShader))
                throw new InvalidDataException("光影 ZIP 根目录的 shaders/ 中缺少非空着色器程序文件。");
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            throw new InvalidDataException(ex.Message + " 请到作者页检查或手动处理压缩结构；EML 不会自动拆包或修改官方文件。", ex);
        }
    }

    /// <summary>资源包必须有唯一的根元数据；兼容 GTNH 的格式 1，也保留其他正整数版本交给游戏判断。</summary>
    private static void RequirePackMetadata(ZipArchive archive)
    {
        const int maximumMetadataBytes = 256 * 1024;
        var metadata = archive.Entries.Where(entry => entry.FullName == "pack.mcmeta").ToArray();
        if (metadata.Length != 1) throw new InvalidDataException("资源包 ZIP 根目录必须包含唯一的 pack.mcmeta，不能位于外层包装文件夹内。");
        var entry = metadata[0];
        if (entry.Length <= 0 || entry.Length > maximumMetadataBytes)
            throw new InvalidDataException("资源包 pack.mcmeta 为空或超过 256 KiB。");
        using var input = entry.Open();
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            if (data.Length + count > maximumMetadataBytes) throw new InvalidDataException("资源包 pack.mcmeta 解压后超过 256 KiB。");
            data.Write(buffer, 0, count);
        }
        var bytes = data.ToArray();
        var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        using var json = JsonDocument.Parse(bytes.AsMemory(offset), new JsonDocumentOptions { MaxDepth = 32 });
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("pack", out var pack) ||
            pack.ValueKind != JsonValueKind.Object || !pack.TryGetProperty("pack_format", out var format) ||
            format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out var version) || version <= 0)
            throw new InvalidDataException("资源包 pack.mcmeta 缺少有效的 pack.pack_format 正整数。");
    }

    /// <summary>只识别 shaders/ 内的真实程序文件，空目录、说明文件和嵌套包装不构成可加载光影。</summary>
    private static bool IsReadableShader(ZipArchiveEntry entry)
    {
        if (!entry.FullName.StartsWith("shaders/", StringComparison.Ordinal) || entry.Length <= 0 ||
            Path.GetExtension(entry.FullName).ToLowerInvariant() is not (".vsh" or ".fsh" or ".gsh" or ".csh")) return false;
        if (entry.FullName.Split('/').Any(part => part.Length == 0 || part is "." or "..") || entry.FullName.Contains('\\')) return false;
        using var input = entry.Open();
        return input.ReadByte() >= 0;
    }

    /// <summary>仅清理本次唯一暂存文件，永不删除目标或用户输入文件。</summary>
    private static void CleanStaging(string root, string relative)
    {
        try
        {
            var path = MigrationIO.SafePath(root, relative);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("无法清理资源暂存文件 {0}：{1}", relative, ex.Message); }
    }
}
