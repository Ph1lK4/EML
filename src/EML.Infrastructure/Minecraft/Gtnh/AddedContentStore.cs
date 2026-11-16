using System.Security.Cryptography;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;
using EML.Core.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Mods;

namespace EML.Infrastructure.Minecraft.Gtnh;

/// <summary>独立保存新增资源的官方基线，原始整合包清单保持不变。</summary>
internal static class AddedContentStore
{
    internal const string RelativePath = ".eml-runtime/added-content/state.json";
    internal sealed record State(int SchemaVersion, string ManifestFingerprint, IReadOnlyList<AddedContentFile> Files);
    internal sealed record Snapshot(IReadOnlyList<AddedContentFile> Files, string? LocalFingerprint);

    /// <summary>优先读取绑定当前清单的本机记录，备份导入后使用清单携带的同一份来源。</summary>
    internal static Snapshot Read(string root, InstanceImportManifest manifest)
    {
        var path = MigrationIO.SafePath(root, RelativePath);
        if (Directory.Exists(path)) throw new InvalidDataException("新增资源记录位置被目录占用：" + path);
        var files = manifest.AddedContentFiles ?? [];
        string? fingerprint = null;
        if (File.Exists(path))
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 8 * 1024 * 1024) throw new InvalidDataException("新增资源记录超过 8 MiB。");
            var state = JsonSerializer.Deserialize<State>(input, MigrationIO.Json)
                ?? throw new InvalidDataException("新增资源记录为空。");
            if (state.SchemaVersion != 1 || state.ManifestFingerprint != EmbeddedFileBaseline.ManifestFingerprint(manifest))
                throw new InvalidDataException("新增资源记录与当前导入清单不一致。");
            files = state.Files;
            input.Position = 0;
            fingerprint = Convert.ToHexStringLower(SHA256.HashData(input));
        }
        Validate(manifest, files, MigrationDataStore.Read(root, manifest).Files);
        return new(files, fingerprint);
    }

    /// <summary>调用者持有实例锁；先持久化官方需求，再原子移入已验证文件，中断后缺失项仍能补全。</summary>
    internal static void Write(string root, InstanceImportManifest manifest, IReadOnlyList<AddedContentFile> files)
    {
        Validate(manifest, files, MigrationDataStore.Read(root, manifest).Files);
        EmbeddedFileBaseline.EnsureManifestUnchanged(root, manifest);
        var state = new State(1, EmbeddedFileBaseline.ManifestFingerprint(manifest), files);
        if (JsonSerializer.SerializeToUtf8Bytes(state, MigrationIO.Json).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("新增资源记录超过 8 MiB。");
        MigrationIO.WriteJson(MigrationIO.SafePath(root, RelativePath), state);
    }

    /// <summary>只接受游戏根目录的资源 ZIP，不能接管原包、迁入文件或把模组伪装成资源。</summary>
    internal static void Validate(InstanceImportManifest manifest, IReadOnlyList<AddedContentFile>? files,
        IReadOnlyList<MigratedPlayerFile>? migrated = null)
    {
        if (files is null || files.Count > 4096) throw new InvalidDataException("无效或过多的新增资源记录。");
        var original = manifest.Files.Where(file => file?.Path is not null).Select(file => file.Path!)
            .Concat((migrated ?? manifest.MigratedPlayerFiles ?? []).Select(file => file.Path)).ToArray();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in files)
        {
            if (record is null || record.Baseline is null) throw new InvalidDataException("新增资源缺少来源记录。");
            var path = ValidateRequirement(manifest, record.Baseline, record.Kind);
            if (!known.Add(path) || original.Any(prior => Conflicts(prior, path)))
                throw new InvalidDataException("新增资源不能覆盖原包或玩家迁入文件：" + path);
        }
    }

    /// <summary>路径范围与官方来源分别验证，不用本地文件的摘要替换平台摘要。</summary>
    internal static string ValidateRequirement(InstanceImportManifest manifest, InstanceFileRequirement baseline, GtnhAddonKind kind)
    {
        var folder = kind switch
        {
            GtnhAddonKind.ResourcePack => "resourcepacks",
            GtnhAddonKind.Shader => "shaderpacks",
            _ => throw new InvalidDataException("模组和光影加载器不能登记为资源包或光影 ZIP。")
        };
        ModManagementStore.ValidateOnlineBaseline(baseline);
        var path = InstanceImportPaths.Relative(baseline.Path!);
        var prefix = (manifest.GameDirectory == "." ? "" : InstanceImportPaths.Relative(manifest.GameDirectory) + "/") + folder + "/";
        if (path != baseline.Path || !path.StartsWith(prefix, StringComparison.Ordinal) ||
            path[prefix.Length..].Contains('/') || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("资源只能安装到游戏目录下的 " + folder + " 根目录 ZIP 文件：" + path);
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        if (baseline.ModrinthOrigin is { } modrinth && modrinth.FileName != fileName ||
            baseline.GithubReleaseOrigin is { } github && !GithubResourceFileName.IsAllowed(kind, github, fileName))
            throw new InvalidDataException("资源目标文件名与平台固定文件身份不一致。");
        return path;
    }

    /// <summary>独立附加新增资源，路径冲突时拒绝，绝不合并两个来源的校验集合。</summary>
    internal static IReadOnlyList<InstanceFileRequirement> IncludeAdditions(IReadOnlyList<InstanceFileRequirement> original, Snapshot snapshot)
    {
        if (snapshot.Files.Count == 0) return original;
        var additions = snapshot.Files.Select(file => file.Baseline).ToArray();
        if (additions.Any(file => original.Any(prior => prior.Path is not null && Conflicts(prior.Path, file.Path!))))
            throw new InvalidDataException("新增资源与现有文件来源冲突。");
        return original.Concat(additions).ToArray();
    }

    /// <summary>供各平台补全器允许恢复明确登记的跨平台资源。</summary>
    internal static IReadOnlySet<string> OnlineDownloadPaths(Snapshot snapshot) =>
        snapshot.Files.Select(file => file.Baseline.Path!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>相同文件以及文件占用另一路径父目录均视为冲突。</summary>
    private static bool Conflicts(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);
}
