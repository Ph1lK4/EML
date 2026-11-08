using System.Security.Cryptography;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Migration;

/// <summary>迁入数据独立于原包完整性依据；备份只携带用途记录，不携带本机恢复文件。</summary>
internal static class MigrationDataStore
{
    internal const string RelativePath = ".eml-runtime/migration/player-data.json";
    internal sealed record State(int SchemaVersion, string ManifestFingerprint, IReadOnlyList<MigratedPlayerFile> Files);
    internal sealed record Snapshot(IReadOnlyList<MigratedPlayerFile> Files, string? LocalFingerprint);

    /// <summary>合并备份携带记录与当前实例确认记录，原包需求始终保持独立。</summary>
    internal static Snapshot Read(string root, InstanceImportManifest manifest)
    {
        Validate(manifest, manifest.MigratedPlayerFiles ?? []);
        var values = (manifest.MigratedPlayerFiles ?? []).ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var path = MigrationIO.SafePath(root, RelativePath);
        if (Directory.Exists(path)) throw new InvalidDataException("玩家迁移记录路径被目录占用。");
        string? fingerprint = null;
        if (File.Exists(path))
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 64 * 1024 * 1024) throw new InvalidDataException("玩家迁移记录过大。");
            var state = System.Text.Json.JsonSerializer.Deserialize<State>(input, MigrationIO.Json) ?? throw new InvalidDataException("迁移记录为空。");
            if (state.SchemaVersion != 1 || state.ManifestFingerprint != EmbeddedFileBaseline.ManifestFingerprint(manifest))
                throw new InvalidDataException("迁移记录与当前导入清单不一致。");
            Validate(manifest, state.Files);
            foreach (var file in state.Files) values[file.Path] = file;
            input.Position = 0; fingerprint = Convert.ToHexStringLower(SHA256.HashData(input));
        }
        Validate(manifest, values.Values.ToArray());
        return new(values.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(), fingerprint);
    }

    /// <summary>限制可移植记录的规则、摘要、数量及路径，禁止放行任意整合包或运行文件。</summary>
    internal static void Validate(InstanceImportManifest manifest, IReadOnlyList<MigratedPlayerFile>? files)
    {
        if (files is null) throw new InvalidDataException("迁移记录缺少文件数组。");
        if (files.Count > MigrationIO.MaximumFiles) throw new InvalidDataException("迁移记录文件数量过多。");
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = manifest.GameDirectory == "." ? "" : manifest.GameDirectory + "/";
        var originalPaths = files.Any(file => file?.Rule == GtnhExtraModPolicy.Rule)
            ? manifest.Files.Where(file => file.Path is not null).Select(file => file.Path!).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        foreach (var file in files)
        {
            if (file is null || !known.Add(InstanceImportPaths.Relative(file.Path)) || file.Rule is not ("gtnh-player-v1" or GtnhExtraModPolicy.Rule) ||
                !Guid.TryParseExact(file.MigrationId, "N", out _) || file.Size < 0 || file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit) ||
                !file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !(file.Rule == GtnhExtraModPolicy.Rule ? GtnhExtraModPolicy.Allows(file.Path[prefix.Length..]) : GtnhPlayerDataPolicy.Allows(file.Path[prefix.Length..])))
                throw new InvalidDataException("迁移用途记录不在允许范围。");
            if (file.Rule == GtnhExtraModPolicy.Rule && (file.SupersededLocalSha256 is not null || originalPaths.Contains(file.Path)))
                throw new InvalidDataException("额外模组迁移记录不能替代原包已有文件来源。");
            if (file.SupersededLocalSha256 is { } prior && (prior.Length != 64 || !prior.All(Uri.IsHexDigit)))
                throw new InvalidDataException("迁移前的用户选择摘要无效。");
        }
        InstanceImportPaths.ValidateLayout(files.Select(file => (file.Path, false)));
    }

    /// <summary>明确区分可变玩家数据和严格模组来源；后续显式确认的本地替换优先。</summary>
    internal static IReadOnlyList<InstanceFileRequirement> Apply(IReadOnlyList<InstanceFileRequirement> original, Snapshot snapshot)
    {
        var byPath = original.Where(file => file.Path is not null).ToDictionary(file => file.Path!, StringComparer.OrdinalIgnoreCase);
        var migrated = snapshot.Files.Where(file => !byPath.TryGetValue(file.Path, out var selected) ||
            selected.Origin != InstanceFileOrigin.UserLocalImport ||
            selected.Hashes.TryGetValue("sha256", out var selectedHash) && string.Equals(selectedHash, file.SupersededLocalSha256, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var result = original.Where(file => file.Path is null || !migrated.ContainsKey(file.Path)).ToList();
        result.AddRange(migrated.Values.Select(file => new InstanceFileRequirement
        {
            Path = file.Path, Source = InstanceFileSource.Embedded,
            Origin = file.Rule == GtnhExtraModPolicy.Rule ? InstanceFileOrigin.UserLocalImport : InstanceFileOrigin.MigratedPlayerData,
            Role = file.Rule == GtnhExtraModPolicy.Rule ? InstanceFileRole.Content : InstanceFileRole.UserData,
            Required = file.Rule == GtnhExtraModPolicy.Rule, Size = file.Size,
            Hashes = new Dictionary<string, string> { ["sha256"] = file.Sha256 }
        }));
        return result;
    }

    /// <summary>在用户替换层前加入严格模组基线，使后续明确接受的替换对检查、安装和备份一致生效。</summary>
    internal static IReadOnlyList<InstanceFileRequirement> IncludeExtraMods(IReadOnlyList<InstanceFileRequirement> original, Snapshot snapshot)
    {
        var extra = snapshot.Files.Where(file => file.Rule == GtnhExtraModPolicy.Rule).ToArray();
        if (extra.Length == 0) return original;
        var paths = original.Where(file => file.Path is not null).Select(file => file.Path!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (extra.Any(file => paths.Contains(file.Path))) throw new InvalidDataException("迁入模组与现有平台需求冲突，不能覆盖来源。");
        return original.Concat(Apply([], new(extra, snapshot.LocalFingerprint))).ToArray();
    }
}
