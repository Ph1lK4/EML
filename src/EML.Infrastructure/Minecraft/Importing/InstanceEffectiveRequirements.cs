using System.Security.Cryptography;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.CurseForge;
using EML.Infrastructure.Minecraft.Migration;

namespace EML.Infrastructure.Minecraft.Importing;

/// <summary>为检查、安装和备份统一选择实际内容来源；原始声明和其他来源摘要不会互相混合。</summary>
internal static class InstanceEffectiveRequirements
{
    /// <summary>一轮操作的来源、用户选择及清单快照；结束时必须确认仍有效。</summary>
    internal sealed record Snapshot(IReadOnlyList<InstanceFileRequirement> Requirements,
        IReadOnlyList<string> ContentIssues, IReadOnlyList<string> Notes,
        string ManifestFingerprint, InstanceLocalReplacementService.AppliedRequirements Replacements,
        bool IsCurseForge, string? ResolutionFingerprint, MigrationDataStore.Snapshot Migration);

    /// <summary>只读选取平台固定需求、原包覆盖及用户替换，最后排除 EML 派生状态。</summary>
    internal static Snapshot Build(string root, InstanceImportManifest manifest,
        CurseForgeFileResolution? resolution = null)
    {
        InstanceManifestStore.Validate(manifest);
        MigrationJournalStore.EnsureSettled(root);
        var issues = new List<string>();
        var notes = new List<string>();
        IReadOnlyList<InstanceFileRequirement> files = manifest.Files;
        var isCurseForge = manifest.SourceFormat.Equals("CurseForge", StringComparison.OrdinalIgnoreCase);
        if (isCurseForge)
        {
            resolution ??= CurseForgeFileStateStore.Read(root, manifest);
            if (resolution is null)
                notes.Add("CurseForge 尚未取得文件解析缓存；保留清单中的官方依据，未解析引用需补全后再检查。");
            else
            {
                files = CurseForgeFileStateStore.EffectiveRequirements(manifest, resolution);
                var selected = files.Where(file => file.Source == InstanceFileSource.CurseForge)
                    .Select(file => (file.ProjectId, file.FileId)).ToHashSet();
                foreach (var entry in resolution.Files.Where(entry => selected.Contains(
                             (entry.Requirement.ProjectId, entry.Requirement.FileId))))
                {
                    if (entry.BlockReason is { } reason && entry.Requirement.Required)
                        issues.Add($"CurseForge {entry.Requirement.ProjectId}/{entry.Requirement.FileId}：{reason}");
                    if (entry.Requirement.Downloads.Count == 0 && entry.ManualDownloadUrl is { } page)
                        notes.Add($"需要手动提供时请放入 {entry.Requirement.Path ?? "待确认目录"}：{page}");
                    if (entry.FallbackMessage is { } message) notes.Add(message);
                }
                var fallbackCount = resolution.Files.Count(entry => entry.ModrinthFallback is not null);
                if (fallbackCount > 0) notes.Add($"{fallbackCount} 个文件记录了 Modrinth 同内容来源，仍保留官方完整性依据。");
            }
        }
        var replacements = InstanceLocalReplacementService.ApplyWithSnapshot(root, manifest, files);
        var migration = MigrationDataStore.Read(root, manifest);
        var effective = MigrationDataStore.Apply(replacements.Requirements, migration)
            .Where(file => !InstanceFileChangePolicy.IsDerivedStatePath(file.Path))
            .Select(file => file with { Role = InstanceFileChangePolicy.GetRole(file, manifest.GameDirectory, manifest.SourceFormat) })
            .ToArray();
        // 原始清单及可移植迁移记录已分别验证；此处验证展开后的单一有效视图，不再把迁入模组当成原包文件。
        InstanceManifestStore.Validate(manifest with { Files = effective, MigratedPlayerFiles = null });
        return new(effective, issues.Distinct().ToArray(), notes.Distinct().ToArray(),
            EmbeddedFileBaseline.ManifestFingerprint(manifest), replacements, isCurseForge, Fingerprint(resolution), migration);
    }

    /// <summary>防止安装期间改动清单、平台映射或用户选择后，沿用旧检查结果宣称就绪。</summary>
    internal static void EnsureUnchanged(string root, InstanceImportManifest manifest, Snapshot snapshot)
    {
        MigrationJournalStore.EnsureSettled(root);
        var current = InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName));
        if (EmbeddedFileBaseline.ManifestFingerprint(current) != snapshot.ManifestFingerprint)
            throw new IOException("实例清单在操作期间发生变化，请重新检查。");
        InstanceLocalReplacementService.EnsureUnchanged(root, manifest, snapshot.Replacements);
        if (MigrationDataStore.Read(root, manifest).LocalFingerprint != snapshot.Migration.LocalFingerprint)
            throw new IOException("玩家数据迁移记录在操作期间发生变化，请重新检查。");
        if (snapshot.IsCurseForge && Fingerprint(CurseForgeFileStateStore.Read(root, manifest)) != snapshot.ResolutionFingerprint)
            throw new IOException("平台文件来源在操作期间发生变化，请重新检查。");
    }

    /// <summary>仅比较完整已验证映射，不将文件内容摘要混入另一来源的校验要求。</summary>
    private static string? Fingerprint(CurseForgeFileResolution? resolution) => resolution is null ? null :
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(resolution)));
}
