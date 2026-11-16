using System.Security.Cryptography;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Infrastructure.Minecraft.Mods;

/// <summary>单个实例只允许一个待提交操作，先持久化意图再移动文件和发布管理状态。</summary>
internal static class ModOperationJournal
{
    internal const string RelativePath = ".eml-runtime/mods/operation.json";
    internal sealed record Journal
    {
        public int SchemaVersion { get; init; } = 1;
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public required string ManifestFingerprint { get; init; }
        public string? BeforeFingerprint { get; init; }
        public required string AfterFingerprint { get; init; }
        public required IReadOnlyList<ManagedModRecord> Before { get; init; }
        public required IReadOnlyList<ManagedModRecord> After { get; init; }
        public required string RecordId { get; init; }
        public required string Source { get; init; }
        public required string Destination { get; init; }
        public required long Size { get; init; }
        public required string Sha256 { get; init; }
        public string Status { get; init; } = "Pending";
    }

    /// <summary>实例运行和补全只检查小型管理日志，不扫描或计算模组文件摘要。</summary>
    internal static void EnsureSettled(string root)
    {
        var journal = Read(root);
        if (journal is not null && journal.Status != "Completed")
            throw new InvalidOperationException("此实例有未完成的模组操作，请先在本地模组管理中恢复上次操作。");
    }

    /// <summary>暂存路径只能由本次操作的 GUID 派生，不能由归档或外部文件名决定。</summary>
    internal static string StagingPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("模组操作编号无效。");
        return ".eml-runtime/mods/staging/" + id + "/payload";
    }

    /// <summary>清理失败添加的暂存文件前确认它未被待恢复日志引用；坏日志由调用者保守保留。</summary>
    internal static bool ReferencesStaging(string root, string id)
    {
        var journal = Read(root);
        return journal is { Status: "Pending" } && (string.Equals(journal.Id, id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(journal.Source, StagingPath(id), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>核对当前状态后写入意图，随后完成移动；提交阶段不响应取消以避免半完成状态。</summary>
    internal static async Task CommitAsync(string root, InstanceImportManifest manifest, ModManagementStore.Snapshot before,
        IReadOnlyList<ManagedModRecord> after, string id, string kind, string recordId,
        string source, string destination, long size, string sha256, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureSettled(root);
        if (ModManagementStore.Read(root, manifest).LocalFingerprint != before.LocalFingerprint)
            throw new IOException("模组管理记录在操作期间发生变化，请刷新后重试。");
        EmbeddedFileBaseline.EnsureManifestUnchanged(root, manifest);
        var journal = new Journal
        {
            Id = id, Kind = kind, ManifestFingerprint = EmbeddedFileBaseline.ManifestFingerprint(manifest),
            BeforeFingerprint = before.LocalFingerprint, AfterFingerprint = StateFingerprint(manifest, after),
            Before = before.Files, After = after, RecordId = recordId, Source = source, Destination = destination,
            Size = size, Sha256 = sha256
        };
        Validate(root, manifest, journal);
        var sourcePath = MigrationIO.SafePath(root, source);
        var destinationPath = MigrationIO.SafePath(root, destination);
        if (Directory.Exists(sourcePath) || Directory.Exists(destinationPath))
            throw new IOException("模组操作位置被目录占用，本次没有开始提交。");
        if (!source.Equals(destination, StringComparison.OrdinalIgnoreCase) && File.Exists(destinationPath))
            throw new IOException("目标已存在文件，已保留两边内容，本次没有开始提交。");
        await VerifyAsync(sourcePath, journal, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        EmbeddedFileBaseline.EnsureManifestUnchanged(root, manifest);
        if (ModManagementStore.Read(root, manifest).LocalFingerprint != before.LocalFingerprint)
            throw new IOException("模组管理记录在提交前发生变化，请刷新后重试。");
        MigrationIO.WriteJson(MigrationIO.SafePath(root, RelativePath), journal);
        await FinishAsync(root, manifest, journal, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>按持久化意图继续一次中断操作，任何文件或管理状态冲突均保留现场并停止。</summary>
    internal static async Task RecoverAsync(string root, InstanceImportManifest manifest, CancellationToken token)
    {
        var journal = Read(root);
        if (journal is null || journal.Status == "Completed") return;
        Validate(root, manifest, journal);
        await FinishAsync(root, manifest, journal, token).ConfigureAwait(false);
    }

    /// <summary>仅当源和目标恰有一个可信副本时继续移动；绝不覆盖同名文件或重建未知摘要。</summary>
    private static async Task FinishAsync(string root, InstanceImportManifest manifest, Journal journal, CancellationToken token)
    {
        var state = ModManagementStore.Read(root, manifest);
        var alreadyCommitted = state.LocalFingerprint == journal.AfterFingerprint;
        if (!alreadyCommitted && state.LocalFingerprint != journal.BeforeFingerprint)
            throw new IOException("模组管理记录已有外部变化，不能自动恢复；现有文件均已保留。");
        if (RecordsFingerprint(state.Files) != RecordsFingerprint(alreadyCommitted ? journal.After : journal.Before))
            throw new InvalidDataException("模组事务的前后记录与当前状态不一致，不能自动恢复。");
        var source = MigrationIO.SafePath(root, journal.Source);
        var destination = MigrationIO.SafePath(root, journal.Destination);
        var same = journal.Source.Equals(journal.Destination, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(source) || Directory.Exists(destination)) throw new IOException("模组操作位置被目录占用，已保留现场。");
        if (same)
            await VerifyAsync(source, journal, token).ConfigureAwait(false);
        else
        {
            var sourceExists = File.Exists(source);
            var destinationExists = File.Exists(destination);
            if (sourceExists == destinationExists)
                throw new IOException(sourceExists ? "恢复位置与原位置同时有文件，不能覆盖；请检查冲突后重试。" : "模组操作的源和目标均不存在，无法恢复。");
            await VerifyAsync(sourceExists ? source : destination, journal, token).ConfigureAwait(false);
            if (sourceExists)
            {
                if (alreadyCommitted) throw new IOException("管理状态已提交但文件被移回原处，不能自动覆盖外部操作。");
                token.ThrowIfCancellationRequested();
                EmbeddedFileBaseline.EnsureManifestUnchanged(root, manifest);
                if (ModManagementStore.Read(root, manifest).LocalFingerprint != journal.BeforeFingerprint)
                    throw new IOException("恢复期间管理记录发生变化，请重新检查。");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                MigrationIO.SafePath(root, journal.Source);
                MigrationIO.SafePath(root, journal.Destination);
                File.Move(source, destination, overwrite: false);
                // 移动后再次核对只针对当前模组，外部程序改写不能被顺势认可为新基线。
                await VerifyAsync(destination, journal, CancellationToken.None).ConfigureAwait(false);
            }
        }
        if (!alreadyCommitted)
        {
            if (ModManagementStore.Read(root, manifest).LocalFingerprint != journal.BeforeFingerprint)
                throw new IOException("提交期间管理记录发生变化，文件已保留，需恢复操作。");
            ModManagementStore.Write(root, manifest, journal.After);
        }
        MigrationIO.WriteJson(MigrationIO.SafePath(root, RelativePath), journal with { Status = "Completed" });
    }

    /// <summary>恢复使用事务开始时的可信大小和 SHA-256，拒绝悄悄接受已经修改的文件。</summary>
    private static async Task VerifyAsync(string path, Journal journal, CancellationToken token)
    {
        var snapshot = await MigrationIO.SnapshotAsync(path, token).ConfigureAwait(false);
        if (snapshot is null || snapshot.Value.Size != journal.Size || !snapshot.Value.Hash.Equals(journal.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("模组文件缺失或已被修改，操作未覆盖任何文件；请先检查或恢复可信副本。");
        // SHA-256 只用于确认事务中移动的字节；在线来源仍必须通过其官方完整性要求。
        var baseline = journal.After.Single(record => record.Id == journal.RecordId).Baseline;
        if (baseline.Origin is InstanceFileOrigin.CurseForgeDownload or InstanceFileOrigin.ModrinthDownload or InstanceFileOrigin.GithubReleaseDownload &&
            !await InstanceRuntimeInstaller.ExistingFileMatchesAsync(path, baseline, token).ConfigureAwait(false))
            throw new IOException("在线模组未通过其官方大小或哈希校验，事务不会继续提交。");
    }

    /// <summary>读取有界日志，损坏状态不能被当作没有未完成操作。</summary>
    private static Journal? Read(string root)
    {
        var path = MigrationIO.SafePath(root, RelativePath);
        if (Directory.Exists(path)) throw new InvalidDataException("模组事务日志位置被目录占用。");
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 20 * 1024 * 1024) throw new InvalidDataException("模组事务日志过大。");
        var journal = JsonSerializer.Deserialize<Journal>(input, MigrationIO.Json) ?? throw new InvalidDataException("模组事务日志为空。");
        if (journal.SchemaVersion != 1 || !Guid.TryParseExact(journal.Id, "N", out _) || journal.Status is not ("Pending" or "Completed"))
            throw new InvalidDataException("模组事务日志格式无效。");
        return journal;
    }

    /// <summary>日志只允许改变一个额外模组，并限制每种操作的源、目标和前后状态。</summary>
    private static void Validate(string root, InstanceImportManifest manifest, Journal journal)
    {
        if (journal.ManifestFingerprint != EmbeddedFileBaseline.ManifestFingerprint(manifest) ||
            !Guid.TryParseExact(journal.Id, "N", out _) || !Guid.TryParseExact(journal.RecordId, "N", out _) ||
            journal.Size < 0 || !Hash(journal.Sha256) || journal.BeforeFingerprint is { } beforeHash && !Hash(beforeHash) ||
            !Hash(journal.AfterFingerprint)) throw new InvalidDataException("模组事务日志与当前实例不一致。");
        var migrated = MigrationDataStore.Read(root, manifest).Files;
        ModManagementStore.Validate(manifest, journal.Before, migrated);
        ModManagementStore.Validate(manifest, journal.After, migrated);
        if (StateFingerprint(manifest, journal.After) != journal.AfterFingerprint)
            throw new InvalidDataException("模组事务提交状态摘要不一致。");
        var before = journal.Before.SingleOrDefault(record => record.Id == journal.RecordId);
        var after = journal.After.SingleOrDefault(record => record.Id == journal.RecordId)
            ?? throw new InvalidDataException("模组事务缺少目标记录。");
        if (RecordsFingerprint(journal.Before.Where(record => record.Id != journal.RecordId)) !=
            RecordsFingerprint(journal.After.Where(record => record.Id != journal.RecordId)))
            throw new InvalidDataException("一次模组事务不能改变其他模组。");
        if (before is not null && (before.Added != after.Added ||
            EmbeddedFileBaseline.RequirementFingerprint(before.Baseline) != EmbeddedFileBaseline.RequirementFingerprint(after.Baseline)))
            throw new InvalidDataException("启禁与回收操作不能修改模组来源或原始摘要。");
        var oldPath = before is null ? after.Baseline.Path! : ModManagementStore.CurrentPath(before);
        var newPath = ModManagementStore.CurrentPath(after);
        var valid = journal.Kind switch
        {
            "Add" => before is null && after.Added && after.State != LocalModState.Removed &&
                journal.Source == StagingPath(journal.Id) && journal.Destination == newPath,
            "Adopt" => before is null && after.Added && after.State != LocalModState.Removed &&
                journal.Source == after.Baseline.Path && journal.Destination == journal.Source && newPath == journal.Source,
            "Enable" => (before is not null || !after.Added) && before?.State != LocalModState.Removed &&
                after.State == LocalModState.Enabled && journal.Source == oldPath && journal.Destination == newPath,
            "Disable" => (before is not null || !after.Added) && before?.State != LocalModState.Removed &&
                after.State == LocalModState.Disabled && journal.Source == oldPath && journal.Destination == newPath,
            "Remove" => (before is not null || !after.Added) && before?.State != LocalModState.Removed &&
                after.State == LocalModState.Removed && journal.Source == oldPath && journal.Destination == ModManagementStore.TrashPath(after),
            "Restore" => before?.State == LocalModState.Removed && after.State ==
                (before.RemovedWasDisabled ? LocalModState.Disabled : LocalModState.Enabled) &&
                journal.Source == ModManagementStore.TrashPath(before) && journal.Destination == newPath,
            _ => false
        };
        if (!valid) throw new InvalidDataException("模组事务的文件范围或状态转换无效。");
        if (journal.Kind is "Add" or "Adopt" && (after.Baseline.Size != journal.Size ||
            after.Baseline.Origin == InstanceFileOrigin.UserLocalImport &&
            !after.Baseline.Hashes["sha256"].Equals(journal.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("新增模组的事务摘要与内容基线不一致。");
        MigrationIO.SafePath(root, journal.Source);
        MigrationIO.SafePath(root, journal.Destination);
    }

    /// <summary>预测原子状态文件的精确摘要，用于识别已经提交但日志尚未标记完成的中断。</summary>
    private static string StateFingerprint(InstanceImportManifest manifest, IReadOnlyList<ManagedModRecord> files) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new ModManagementStore.State(1,
            EmbeddedFileBaseline.ManifestFingerprint(manifest), files), MigrationIO.Json)));

    /// <summary>稳定比较除本次对象外的记录，防止恢复时顺带覆盖其他选择。</summary>
    private static string RecordsFingerprint(IEnumerable<ManagedModRecord> records) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(records.OrderBy(record => record.Id), MigrationIO.Json)));

    /// <summary>只接受完整 SHA-256，损坏日志不得放宽恢复校验。</summary>
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
