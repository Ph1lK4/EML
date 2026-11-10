using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.CurseForge;

namespace EML.Infrastructure.Minecraft.Importing;

/// <summary>把可移植的来源与用户选择保存到备份清单，派生缓存不作为游戏内容传播。</summary>
internal static class InstanceBackupManifest
{
    /// <summary>导出期间固定原清单和有效来源，不读取当前文件重新建立可信摘要。</summary>
    internal sealed record Snapshot(InstanceImportManifest Original, InstanceImportManifest Manifest,
        InstanceEffectiveRequirements.Snapshot Requirements);

    /// <summary>只从原始声明、已验证平台映射和明确用户记录构建备份，不把磁盘字节当成新授权。</summary>
    internal static Snapshot Create(string root, InstanceImportManifest manifest)
    {
        var resolution = manifest.SourceFormat.Equals("CurseForge", StringComparison.OrdinalIgnoreCase)
            ? CurseForgeFileStateStore.Read(root, manifest) : null;
        var effective = InstanceEffectiveRequirements.Build(root, manifest, resolution);
        // 先按稳定身份取可信基线，再保留启禁位置；不能把 .disabled 的新路径写回原包声明。
        var beforeManagement = Migration.MigrationDataStore.Apply(effective.Replacements.Requirements, effective.Migration)
            .Where(file => file.Path is not null).ToDictionary(file => file.Path!, StringComparer.OrdinalIgnoreCase);
        var byPath = effective.Requirements.Where(file => file.Path is not null)
            .ToDictionary(file => file.Path!, StringComparer.OrdinalIgnoreCase);
        var byId = resolution?.Files.ToDictionary(entry => (entry.Requirement.ProjectId, entry.Requirement.FileId));
        var files = new List<InstanceFileRequirement>();
        foreach (var original in manifest.Files.Where(file => !InstanceFileChangePolicy.IsDerivedStatePath(file.Path)))
        {
            var requirement = original;
            if (original.Source == InstanceFileSource.CurseForge && byId is not null &&
                byId.TryGetValue((original.ProjectId, original.FileId), out var resolved))
            {
                // 固定官方文件独立保留；重叠时不能与包内文件占用同一个有效目标。
                requirement = resolved.EmbeddedOverride is null ? resolved.Requirement : resolved.Requirement with
                { Path = null, Downloads = [] };
            }
            else if (original.Path is { } path && byPath.TryGetValue(path, out var selected) &&
                selected.Origin != InstanceFileOrigin.MigratedPlayerData)
                requirement = selected;
            if (requirement.Source == InstanceFileSource.Embedded &&
                requirement.Origin != InstanceFileOrigin.UserLocalImport && requirement.ArchiveEntry is not null)
                requirement = requirement with { ArchiveEntry = requirement.Path };
            files.Add(requirement);
        }
        // 玩家用途记录独立携带，不能用迁入内容的摘要覆盖原包及官方来源依据。
        // 额外模组的后续显式用户替换也随备份携带；只采用已确认要求，绝不按当前磁盘重新信任内容。
        var migrated = effective.Migration.Files.Select(file => file.Rule == EML.Core.Minecraft.Migration.GtnhExtraModPolicy.Rule &&
            beforeManagement.TryGetValue(file.Path, out var chosen) && chosen.Origin == InstanceFileOrigin.UserLocalImport
                ? file with { Size = chosen.Size!.Value, Sha256 = chosen.Hashes["sha256"] } : file).ToArray();
        var managed = effective.Mods.Files.Select(file => file with
        {
            Baseline = beforeManagement.TryGetValue(file.Baseline.Path!, out var chosen) ? chosen : file.Baseline
        }).ToArray();
        var backup = manifest with { Files = files,
            MigratedPlayerFiles = migrated.Length == 0 ? null : migrated,
            ManagedMods = managed.Length == 0 ? null : managed };
        InstanceManifestStore.Validate(backup);
        return new(manifest, backup, effective);
    }

    /// <summary>只有来源与用户选择仍与导出开始相同，才发布最终 ZIP。</summary>
    internal static void EnsureUnchanged(string root, Snapshot snapshot) =>
        InstanceEffectiveRequirements.EnsureUnchanged(root, snapshot.Original, snapshot.Requirements);
}
