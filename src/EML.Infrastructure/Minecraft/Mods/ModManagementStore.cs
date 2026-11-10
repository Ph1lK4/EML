using System.Security.Cryptography;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;
using EML.Core.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;

namespace EML.Infrastructure.Minecraft.Mods;

/// <summary>保存明确的模组管理选择；原清单、迁入基线与用户操作保持独立。</summary>
internal static class ModManagementStore
{
    internal const string RelativePath = ".eml-runtime/mods/state.json";
    internal sealed record State(int SchemaVersion, string ManifestFingerprint, IReadOnlyList<ManagedModRecord> Files);
    internal sealed record Snapshot(IReadOnlyList<ManagedModRecord> Files, string? LocalFingerprint);

    /// <summary>读取绑定当前清单的本地状态，或备份携带的选择；不读取文件重建可信摘要。</summary>
    internal static Snapshot Read(string root, InstanceImportManifest manifest)
    {
        var path = MigrationIO.SafePath(root, RelativePath);
        if (Directory.Exists(path)) throw new InvalidDataException("模组管理记录位置被目录占用。");
        var files = manifest.ManagedMods ?? [];
        string? fingerprint = null;
        if (File.Exists(path))
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 8 * 1024 * 1024) throw new InvalidDataException("模组管理记录超过 8 MiB。");
            var state = System.Text.Json.JsonSerializer.Deserialize<State>(input, MigrationIO.Json)
                ?? throw new InvalidDataException("模组管理记录为空。");
            if (state.SchemaVersion != 1 || state.ManifestFingerprint != EmbeddedFileBaseline.ManifestFingerprint(manifest))
                throw new InvalidDataException("模组管理记录与当前导入清单不一致。");
            files = state.Files;
            input.Position = 0;
            fingerprint = Convert.ToHexStringLower(SHA256.HashData(input));
        }
        Validate(manifest, files, MigrationDataStore.Read(root, manifest).Files);
        return new(files, fingerprint);
    }

    /// <summary>验证后原子发布用户选择；调用者必须已取得实例锁并协调物理文件事务。</summary>
    internal static void Write(string root, InstanceImportManifest manifest, IReadOnlyList<ManagedModRecord> files)
    {
        Validate(manifest, files, MigrationDataStore.Read(root, manifest).Files);
        EmbeddedFileBaseline.EnsureManifestUnchanged(root, manifest);
        MigrationIO.WriteJson(MigrationIO.SafePath(root, RelativePath), new State(1,
            EmbeddedFileBaseline.ManifestFingerprint(manifest), files));
    }

    /// <summary>限定记录只能控制额外模组，不能隐藏或替换原包声明的文件。</summary>
    internal static void Validate(InstanceImportManifest manifest, IReadOnlyList<ManagedModRecord>? files,
        IReadOnlyList<MigratedPlayerFile>? migrated = null)
    {
        if (files is null || files.Count > 4096) throw new InvalidDataException("无效或过多的模组管理记录。");
        var original = manifest.Files.Where(file => file?.Path is not null)
            .Select(file => LocalModPathPolicy.EnabledPath(file.Path!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inherited = (migrated ?? manifest.MigratedPlayerFiles ?? []).Where(file => file.Rule == GtnhExtraModPolicy.Rule)
            .ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files)
        {
            if (item is null || !Guid.TryParseExact(item.Id, "N", out _) || !ids.Add(item.Id) || !Enum.IsDefined(item.State) ||
                item.Baseline is not { Path: { } path } baseline ||
                !LocalModPathPolicy.Allows(path, manifest.GameDirectory, manifest.MinecraftVersion) ||
                !known.Add(LocalModPathPolicy.EnabledPath(InstanceImportPaths.Relative(path))) ||
                original.Contains(LocalModPathPolicy.EnabledPath(path)) ||
                baseline.Role is not (null or InstanceFileRole.Content) || !baseline.Required || baseline.Size is null or < 0 ||
                baseline.Size > SafeInstanceArchive.MaxUncompressedBytes || baseline.ArchiveEntry is not null)
                throw new InvalidDataException("模组管理记录不能改变原包来源，或缺少有效的玩家模组基线。");
            ValidateBaseline(baseline);
            if (!item.Added && baseline.Origin != InstanceFileOrigin.UserLocalImport &&
                (!inherited.TryGetValue(path, out var prior) || prior.OnlineModBaseline is not { } online ||
                 EmbeddedFileBaseline.RequirementFingerprint(online) != EmbeddedFileBaseline.RequirementFingerprint(baseline)))
                throw new InvalidDataException("迁入在线模组必须保留迁移记录中的官方来源基线。");
            if (item.Added ? inherited.Keys.Any(value => LocalModPathPolicy.EnabledPath(value).Equals(
                    LocalModPathPolicy.EnabledPath(path), StringComparison.OrdinalIgnoreCase)) : !inherited.ContainsKey(path))
                throw new InvalidDataException("模组管理记录与迁入模组来源不一致。");
        }
    }

    /// <summary>只允许明确的用户导入或平台下载，官方哈希不会因纳入管理而降级为本地摘要。</summary>
    private static void ValidateBaseline(InstanceFileRequirement baseline)
    {
        if (baseline.Source == InstanceFileSource.Embedded && baseline.Origin == InstanceFileOrigin.UserLocalImport)
        {
            if (baseline.Hashes is null || baseline.Downloads is null)
                throw new InvalidDataException("用户模组缺少完整性记录。");
            _ = InstanceFileIntegrity.HashesFor(baseline);
            return;
        }
        ValidateOnlineBaseline(baseline);
    }

    /// <summary>登记在线模组前验证平台固定身份、合法地址和完整官方摘要，不接受混合来源的要求。</summary>
    internal static void ValidateOnlineBaseline(InstanceFileRequirement baseline)
    {
        if (baseline.Path is not { Length: > 0 } path || baseline.Size is null or < 0 ||
            baseline.Size > SafeInstanceArchive.MaxUncompressedBytes || !baseline.Required ||
            baseline.Role is not (null or InstanceFileRole.Content) || baseline.ArchiveEntry is not null ||
            baseline.Hashes is not { Count: > 0 } || baseline.Downloads is null)
            throw new InvalidDataException("在线模组缺少目标、大小或官方完整性要求。");
        InstanceImportPaths.Relative(path);
        foreach (var (algorithm, hash) in baseline.Hashes)
        {
            var length = algorithm switch { "sha1" => 40, "sha256" => 64, "sha512" => 128, _ => 0 };
            if (length == 0 || hash is null || hash.Length != length || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("在线模组包含无效哈希。");
        }
        foreach (var url in baseline.Downloads)
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
                throw new InvalidDataException("在线模组下载地址必须是 HTTPS。");
        if (baseline.Source == InstanceFileSource.CurseForge && baseline.Origin == InstanceFileOrigin.CurseForgeDownload)
        {
            if (baseline.ProjectId is null or <= 0 || baseline.FileId is null or <= 0 ||
                !baseline.Hashes.ContainsKey("sha1") || baseline.ModrinthOrigin is not null)
                throw new InvalidDataException("CurseForge 模组缺少固定文件身份或官方 SHA-1。");
        }
        else if (baseline.Source == InstanceFileSource.DirectDownload && baseline.Origin == InstanceFileOrigin.ModrinthDownload)
        {
            if (baseline.Downloads.Count == 0 || baseline.ModrinthOrigin is not { } origin ||
                !ValidId(origin.ProjectId) || !ValidId(origin.VersionId) ||
                string.IsNullOrWhiteSpace(origin.FileName) || InstanceImportPaths.Relative(origin.FileName).Contains('/') ||
                !baseline.Hashes.ContainsKey("sha512") && !baseline.Hashes.ContainsKey("sha1"))
                throw new InvalidDataException("Modrinth 模组缺少固定版本、文件身份或官方摘要。");
        }
        else if (baseline.Source == InstanceFileSource.DirectDownload && baseline.Origin == InstanceFileOrigin.GithubReleaseDownload)
        {
            // 固定身份、地址和唯一官方 SHA-256 由共享完整性策略验证，不能降级为本地导入。
            _ = InstanceFileIntegrity.HashesFor(baseline);
        }
        else throw new InvalidDataException("模组下载的获取方式与实际内容来源不一致。");
        ValidateOnlineSourceLink(baseline);
        _ = InstanceFileIntegrity.HashesFor(baseline);
    }

    /// <summary>同内容备用来源必须保留一致的固定身份、大小与 SHA-1；仅验证关联，不混合两份校验集合。</summary>
    private static void ValidateOnlineSourceLink(InstanceFileRequirement baseline)
    {
        if (baseline.CurseForgeOrigin is { } original)
        {
            if (original.ProjectId <= 0 || original.FileId <= 0 || baseline.ProjectId != original.ProjectId ||
                baseline.FileId != original.FileId || baseline.Size != original.Size || original.Hashes is null ||
                !original.Hashes.TryGetValue("sha1", out var originalHash) || originalHash is not { Length: 40 } ||
                !originalHash.All(Uri.IsHexDigit) || !baseline.Hashes.TryGetValue("sha1", out var actualHash) ||
                !actualHash.Equals(originalHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("在线备用文件与原 CurseForge 文件的固定身份、大小或 SHA-1 不一致。");
        }
        else if (baseline.Origin == InstanceFileOrigin.ModrinthDownload && (baseline.ProjectId is not null || baseline.FileId is not null))
            throw new InvalidDataException("Modrinth 备用来源携带 CurseForge 编号时必须保留原文件关联依据。");
    }

    /// <summary>固定平台标识只接受短的不含路径字符的值，不能持久化任意请求表达式。</summary>
    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    /// <summary>在显式用户替换层之前加入新增模组的原始基线，移除项也保留来源身份。</summary>
    internal static IReadOnlyList<InstanceFileRequirement> IncludeAdditions(IReadOnlyList<InstanceFileRequirement> original, Snapshot snapshot)
    {
        var added = snapshot.Files.Where(file => file.Added).Select(file => file.Baseline).ToArray();
        if (added.Length == 0) return original;
        var known = original.Where(file => file.Path is not null).Select(file => LocalModPathPolicy.EnabledPath(file.Path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (added.Any(file => known.Contains(LocalModPathPolicy.EnabledPath(file.Path!))))
            throw new InvalidDataException("玩家新增模组与原有文件来源冲突。");
        return original.Concat(added).ToArray();
    }

    /// <summary>只改变管理对象的存放位置或加载要求，沿用其已经选定的可信内容摘要。</summary>
    internal static IReadOnlyList<InstanceFileRequirement> Apply(IReadOnlyList<InstanceFileRequirement> original, Snapshot snapshot)
    {
        var records = snapshot.Files.ToDictionary(file => file.Baseline.Path!, StringComparer.OrdinalIgnoreCase);
        var result = new List<InstanceFileRequirement>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in original)
        {
            if (file.Path is not { } path || !records.TryGetValue(path, out var record)) { result.Add(file); continue; }
            seen.Add(path);
            if (file.Origin is not (InstanceFileOrigin.UserLocalImport or InstanceFileOrigin.CurseForgeDownload or InstanceFileOrigin.ModrinthDownload or InstanceFileOrigin.GithubReleaseDownload))
                throw new InvalidDataException("受保护的整合包模组不能应用玩家管理选择。");
            if (CurrentPath(record) is { } current) result.Add(file with { Path = current });
        }
        if (records.Keys.Any(path => !seen.Contains(path))) throw new InvalidDataException("管理的模组缺少原始来源要求。");
        return result;
    }

    /// <summary>返回游戏目录中的实际位置；回收项没有有效安装目标，也不能自动下载恢复。</summary>
    internal static string? CurrentPath(ManagedModRecord record) => record.State switch
    {
        LocalModState.Enabled => LocalModPathPolicy.EnabledPath(record.Baseline.Path!),
        LocalModState.Disabled => LocalModPathPolicy.EnabledPath(record.Baseline.Path!) + ".disabled",
        _ => null
    };

    /// <summary>仅显式登记或迁入的在线模组可跨整合包平台补全；回收项不提供下载目标。</summary>
    internal static IReadOnlySet<string> OnlineDownloadPaths(Snapshot snapshot, MigrationDataStore.Snapshot migration) =>
        snapshot.Files.Where(record => record.Baseline.Origin is InstanceFileOrigin.CurseForgeDownload or InstanceFileOrigin.ModrinthDownload or InstanceFileOrigin.GithubReleaseDownload)
            .Select(CurrentPath).Where(path => path is not null).Select(path => path!)
            .Concat(migration.Files.Where(file => file.OnlineModBaseline is not null).Select(file => file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>回收区使用记录 ID 隔离文件；此内容不随备份输出。</summary>
    internal static string TrashPath(ManagedModRecord record) => ".eml-runtime/mods/trash/" + record.Id + "/" +
        Path.GetFileName(LocalModPathPolicy.EnabledPath(record.Baseline.Path!));
}
