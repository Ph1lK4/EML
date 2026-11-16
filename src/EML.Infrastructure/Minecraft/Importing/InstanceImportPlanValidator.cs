using System.Text;
using EML.Core.Minecraft;

namespace EML.Infrastructure.Minecraft.Importing;

/// <summary>导入与备份复核共用的纯计划检查；不创建实例、不下载文件，也不重新授予内容可信来源。</summary>
internal static class InstanceImportPlanValidator
{
    // 占位摘要只用于预测 JSON 字节数；真实导入始终使用流式计算的摘要，不保存此值。
    private static readonly IReadOnlyDictionary<string, string> SizingHashes =
        new Dictionary<string, string> { ["sha256"] = new('0', 64) };
    private const int MaximumPrismConfigurationBytes = 1024 * 1024;

    /// <summary>剔除本机派生状态，验证需求与将要写入的文件布局；保留原有导入处理顺序。</summary>
    internal static InstanceArchivePlan Prepare(InstanceArchivePlan plan)
    {
        var prepared = plan with
        {
            Copies = plan.Copies.Where(copy => !InstanceFileChangePolicy.IsDerivedStatePath(copy.TargetPath)).ToArray(),
            Manifest = plan.Manifest with
            {
                Files = plan.Manifest.Files.Where(file => file.Path is null ||
                    !InstanceFileChangePolicy.IsDerivedStatePath(file.Path)).ToArray()
            }
        };
        InstanceManifestStore.Validate(prepared.Manifest);
        var copiedPaths = prepared.Copies.Select(copy => copy.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        InstanceImportPaths.ValidateLayout(prepared.Copies.Select(copy => (copy.TargetPath, copy.Directory))
            .Concat(prepared.Manifest.Files.Where(file => file.Path is not null && !copiedPaths.Contains(file.Path))
                .Select(file => (file.Path!, false)))
            .Append((InstanceImportManifest.FileName, false)));
        return prepared;
    }

    /// <summary>以实际提取记录生成待保存清单；备份仅搬运来源声明，未知文件不会自动成为可信替换。</summary>
    internal static InstanceImportManifest BuildManifest(InstanceArchivePlan plan,
        IReadOnlyDictionary<string, InstanceFileRequirement> files, string name, string archiveFileName)
    {
        List<InstanceFileRequirement> remaining;
        if (plan.PreserveRemoteRequirements)
        {
            remaining = plan.Manifest.Files.Select(file => file.Source == InstanceFileSource.Embedded &&
                file.Origin != InstanceFileOrigin.UserLocalImport && file.ArchiveEntry is not null &&
                file.Path is { } path && files.TryGetValue(path, out var copiedFile)
                    ? file with { ArchiveEntry = copiedFile.ArchiveEntry } : file).ToList();
        }
        else
        {
            remaining = plan.Manifest.Files.Where(file => file.Path is null || !files.ContainsKey(file.Path)).ToList();
            remaining.AddRange(files.Values);
        }
        return plan.Manifest with
        {
            Name = name, SourceArchive = Path.GetFileName(archiveFileName), IntegrityStatus = "NotChecked", Files = remaining
        };
    }

    /// <summary>预测恢复时生成的清单能否保存；只检查结构、数量和编码大小，不保证载荷完好或游戏可运行。</summary>
    internal static void ValidateRestoreMetadata(InstanceArchivePlan plan, string? instanceName = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var name = InstanceImportPaths.InstanceName(instanceName ?? InstanceImportPaths.SuggestName(plan.Manifest.Name));
        var files = new Dictionary<string, InstanceFileRequirement>(StringComparer.OrdinalIgnoreCase);
        foreach (var copy in plan.Copies)
        {
            token.ThrowIfCancellationRequested();
            if (copy.Directory) continue;
            files.Add(copy.TargetPath, new InstanceFileRequirement
            {
                Path = copy.TargetPath, Source = InstanceFileSource.Embedded, Size = copy.Entry.Length,
                Hashes = SizingHashes, ArchiveEntry = copy.Entry.FullName.Replace('\\', '/')
            });
        }
        var manifest = BuildManifest(plan, files, name, plan.Manifest.SourceArchive);
        if (manifest.SourceFormat == "Prism")
            manifest = IncludeGeneratedPrismConfiguration(plan, manifest, name, token);
        // ReadEml 已追加恢复说明，BuildManifest 已应用恢复名称；不能只验证 ZIP 内旧清单的长度。
        _ = InstanceManifestStore.Serialize(manifest);
        token.ThrowIfCancellationRequested();
    }

    /// <summary>模拟 Prism 名称同步对清单的影响，严格来源不改动；只在缺少需求时计入新生成的配置。</summary>
    private static InstanceImportManifest IncludeGeneratedPrismConfiguration(InstanceArchivePlan plan,
        InstanceImportManifest manifest, string name, CancellationToken token)
    {
        var requirement = manifest.Files.SingleOrDefault(file =>
            string.Equals(file.Path, "instance.cfg", StringComparison.OrdinalIgnoreCase));
        if (requirement is not null && !InstanceFileChangePolicy.IsMutableConfiguration(requirement, ".minecraft", "Prism"))
            return manifest;
        var copy = plan.Copies.SingleOrDefault(copy => !copy.Directory &&
            string.Equals(copy.TargetPath, "instance.cfg", StringComparison.OrdinalIgnoreCase));
        if (copy is not null && copy.Entry.Length > MaximumPrismConfigurationBytes)
            throw new InvalidDataException("Prism 配置过大。");
        if (requirement is not null) return manifest;
        var original = copy is null ? "" : ReadPrismConfiguration(copy, token);
        var updated = MinecraftInstanceManager.UpdatePrismName(original, name);
        var generated = new InstanceFileRequirement
        {
            Path = "instance.cfg", Source = InstanceFileSource.Embedded,
            Size = Encoding.UTF8.GetByteCount(updated), Hashes = SizingHashes, ArchiveEntry = null
        };
        return manifest with { Files = [.. manifest.Files, generated] };
    }

    /// <summary>仅为未登记的 Prism 配置读取至多 1 MiB，使用与真实名称同步相同的 BOM 检测规则。</summary>
    private static string ReadPrismConfiguration(InstanceArchiveCopy copy, CancellationToken token)
    {
        using var input = copy.Entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if (count > MaximumPrismConfigurationBytes - memory.Length || count > copy.Entry.Length - memory.Length)
                throw new InvalidDataException("Prism 配置超过声明长度或大小上限。");
            memory.Write(buffer, 0, count);
        }
        if (memory.Length != copy.Entry.Length) throw new InvalidDataException("Prism 配置未完整读取。");
        memory.Position = 0;
        using var reader = new StreamReader(memory, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
