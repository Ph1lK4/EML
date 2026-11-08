using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Migration;

internal sealed record MigrationJournalEntry(string Path, long Size, string Sha256, long? PreviousSize, string? PreviousSha256)
{
    /// <summary>仅新建的额外模组使用独立规则；旧日志省略时保持玩家数据范围。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Rule { get; init; }
}
internal sealed record MigrationJournal
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string GameDirectory { get; init; }
    public required string ManifestFingerprint { get; init; }
    public string? LocalReplacementFingerprint { get; init; }
    public required string SourceName { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "Preparing";
    public IReadOnlyList<MigrationJournalEntry> Files { get; set; } = [];
    public IReadOnlyList<string> NewWorldDirectories { get; set; } = [];
}

/// <summary>写前日志和备份始终保留；未完成事务阻止启动，等待恢复。</summary>
internal static class MigrationJournalStore
{
    internal const string Root = ".eml-runtime/migration/transactions";
    private sealed record Summary(string Id, string Status, DateTimeOffset CreatedAt);
    /// <summary>把严格 GUID 转为本实例事务目录，拒绝从记录引用任意备份路径。</summary>
    internal static string DirectoryPath(string root, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("无效迁移编号。");
        return MigrationIO.SafePath(root, Root + "/" + id);
    }
    /// <summary>验证所有可写目标；唯一允许的内部文件是本次玩家用途记录。</summary>
    internal static MigrationJournal Read(string root, string id)
    {
        var journal = MigrationIO.ReadJson<MigrationJournal>(Path.Combine(DirectoryPath(root, id), "journal.json"));
        if (journal.SchemaVersion != 1 || journal.Id != id || journal.Files is null || journal.Files.Count > MigrationIO.MaximumFiles + 1 ||
            journal.Status is not ("Preparing" or "Applying" or "Completed" or "RollingBack" or "RolledBack" or "RecoveryRequired"))
            throw new InvalidDataException("迁移日志格式无效。");
        if (journal.LocalReplacementFingerprint is { } replacement && !ValidHash(replacement))
            throw new InvalidDataException("迁移日志中的用户选择指纹无效。");
        var manifest = InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName));
        if (manifest.GameDirectory != journal.GameDirectory || EmbeddedFileBaseline.ManifestFingerprint(manifest) != journal.ManifestFingerprint)
            throw new InvalidDataException("迁移日志与当前实例清单不一致，不能自动恢复。");
        var prefix = manifest.GameDirectory == "." ? "" : manifest.GameDirectory + "/";
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (journal.NewWorldDirectories is null || journal.NewWorldDirectories.Count > 4096)
            throw new InvalidDataException("迁移世界记录无效。");
        foreach (var world in journal.NewWorldDirectories)
        {
            var relative = InstanceImportPaths.Relative(world);
            var worldPrefix = prefix + "saves/";
            if (!relative.StartsWith(worldPrefix, StringComparison.OrdinalIgnoreCase) ||
                relative[worldPrefix.Length..].Contains('/') || relative[worldPrefix.Length..].Equals("NEI", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("新世界恢复范围无效。");
        }
        foreach (var entry in journal.Files)
        {
            if (!known.Add(InstanceImportPaths.Relative(entry.Path)) || entry.Size < 0 || !ValidHash(entry.Sha256) ||
                (entry.PreviousSize is null) != (entry.PreviousSha256 is null) || entry.PreviousSize < 0 ||
                entry.PreviousSha256 is { } old && !ValidHash(old)) throw new InvalidDataException("迁移日志文件依据无效。");
            var extraMod = entry.Rule == GtnhExtraModPolicy.Rule;
            if (entry.Rule is not (null or GtnhExtraModPolicy.Rule) || extraMod && (entry.PreviousSize is not null ||
                manifest.Files.Any(file => string.Equals(file.Path, entry.Path, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("额外模组恢复日志不能覆盖原包文件。");
            if (entry.Path != MigrationDataStore.RelativePath && (!entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !(extraMod ? GtnhExtraModPolicy.Allows(entry.Path[prefix.Length..]) : GtnhPlayerDataPolicy.Allows(entry.Path[prefix.Length..]))))
                throw new InvalidDataException("迁移日志指向非玩家数据。");
            if (extraMod && entry.Path == MigrationDataStore.RelativePath) throw new InvalidDataException("模组日志路径无效。");
            MigrationIO.SafePath(root, entry.Path);
        }
        return journal;
    }
    /// <summary>启动只读取小型提交状态，不重读世界文件列表或对存档计算摘要。</summary>
    internal static IReadOnlyList<MigrationRecoveryInfo> List(string root)
    {
        var directory = MigrationIO.SafePath(root, Root);
        if (!System.IO.Directory.Exists(directory)) return [];
        var result = new List<MigrationRecoveryInfo>();
        var count = 0;
        foreach (var path in System.IO.Directory.EnumerateDirectories(directory))
        {
            if (++count > 1000) throw new InvalidDataException("迁移历史过多，请先归档旧记录。");
            InstanceImportPaths.EnsureNoLinks(path);
            if (!File.Exists(Path.Combine(path, "journal.json"))) continue;
            var id = Path.GetFileName(path);
            DirectoryPath(root, id);
            var summaryPath = Path.Combine(path, "state.json");
            if (!File.Exists(summaryPath))
            {
                // 日志已落盘但状态尚未发布时，也必须走恢复检查，不能忽略中断事务。
                result.Add(new(id, "RecoveryRequired", System.IO.Directory.GetCreationTimeUtc(path)));
                continue;
            }
            if (new FileInfo(summaryPath).Length > 4096) throw new InvalidDataException("迁移状态文件过大。");
            var state = MigrationIO.ReadJson<Summary>(summaryPath);
            if (state.Id != id || state.Status is not ("Preparing" or "Applying" or "Completed" or "RollingBack" or "RolledBack" or "RecoveryRequired"))
                throw new InvalidDataException("迁移状态格式无效。");
            result.Add(new(id, state.Status, state.CreatedAt));
        }
        return result.OrderByDescending(value => value.CreatedAt).ToArray();
    }
    /// <summary>只有完整完成或完整恢复的实例才允许后续启动和补全。</summary>
    internal static void EnsureSettled(string root)
    {
        if (List(root).Any(value => value.Status is not ("Completed" or "RolledBack")))
            throw new InvalidOperationException("此实例有未完成的数据迁移，请先使用“恢复上次迁移”。");
    }
    /// <summary>每次目标写入前持久保存日志阶段，支持进程中断后恢复。</summary>
    internal static void Write(string root, MigrationJournal journal)
    {
        MigrationIO.WriteJson(Path.Combine(DirectoryPath(root, journal.Id), "journal.json"), journal);
        // 先持久化完整日志再发布摘要；崩溃留下旧的未完成状态只会要求额外恢复，不会放行半迁移实例。
        MigrationIO.WriteJson(Path.Combine(DirectoryPath(root, journal.Id), "state.json"), new Summary(journal.Id, journal.Status, journal.CreatedAt));
    }
    /// <summary>恢复依据必须是完整 SHA-256。</summary>
    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
}
