using System.Security.Cryptography;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Migration;

/// <summary>两实例之间的 GTNH 玩家数据复制；先暂存及备份，再提交，失败可恢复。</summary>
public sealed class GtnhInstanceMigrationService(string launcherRoot) : IMinecraftInstanceMigrationService
{
    private readonly string _root = Path.GetFullPath(launcherRoot);
    private readonly GtnhMigrationPlanner _planner = new(launcherRoot);
    /// <summary>只读确认 GTNH 特征，未知包返回 null。</summary>
    public Task<GtnhInstanceInfo?> InspectAsync(string instancePath, CancellationToken token = default) =>
        Task.Run(() => GtnhIdentity.Read(_root, instancePath), token);
    /// <summary>后台枚举可迁移数据组，不计算文件内容摘要。</summary>
    public Task<IReadOnlyList<MigrationGroup>> GetGroupsAsync(string sourceInstance, GtnhMigrationMode mode, CancellationToken token = default) =>
        Task.Run(() => _planner.Groups(sourceInstance, mode, token), token);
    /// <summary>读取双方文件生成预览；未完成的迁移必须先恢复。</summary>
    public Task<InstanceMigrationPlan> PreviewAsync(InstanceMigrationRequest request, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            MigrationJournalStore.EnsureSettled(EmbeddedFileBaseline.ValidateInstance(_root, request.SourceInstance));
            MigrationJournalStore.EnsureSettled(EmbeddedFileBaseline.ValidateInstance(_root, request.TargetInstance));
            return await _planner.BuildAsync(request, token);
        }, token);
    /// <summary>锁定两个实例后复核预览，写入自己的事务目录，再原子提交各文件。</summary>
    public Task<InstanceMigrationResult> MigrateAsync(InstanceMigrationPlan plan, bool overwriteConflicts,
        IProgress<InstanceMigrationProgress>? progress = null, CancellationToken token = default) =>
        Task.Run(() => MigrateCoreAsync(plan, overwriteConflicts, progress, token), token);

    /// <summary>迁移的全部文件先复制校验；用户数据原件和旧实例从不删除。</summary>
    private async Task<InstanceMigrationResult> MigrateCoreAsync(InstanceMigrationPlan proposed, bool overwrite,
        IProgress<InstanceMigrationProgress>? progress, CancellationToken token)
    {
        var source = EmbeddedFileBaseline.ValidateInstance(_root, proposed.Request.SourceInstance);
        var target = EmbeddedFileBaseline.ValidateInstance(_root, proposed.Request.TargetInstance);
        using var lease = InstanceOperationLease.Acquire(source, target);
        MigrationJournalStore.EnsureSettled(source); MigrationJournalStore.EnsureSettled(target);
        progress?.Report(new("重新确认迁移预览", "", 0, proposed.Files.Count));
        var plan = await _planner.BuildAsync(proposed.Request, token);
        if (plan.Fingerprint != proposed.Fingerprint) throw new IOException("源或目标文件在预览后发生变化，请重新预览。");
        var pending = plan.Files.Where(file => file.Action is not (MigrationFileAction.Identical or MigrationFileAction.Skip) &&
            (file.Action != MigrationFileAction.Conflict || overwrite)).ToArray();
        if (pending.Length == 0) return new("", "NoChanges", 0, plan.Files.Count, "", ["所选文件均相同或保留新版，没有修改实例。"]);
        var space = pending.Aggregate(0L, (sum, file) => checked(sum + file.Size + (file.PreviousSize ?? 0)));
        var drive = new DriveInfo(Path.GetPathRoot(target)!);
        if (drive.AvailableFreeSpace < checked(space + 192L * 1024 * 1024)) throw new IOException("磁盘空间不足以同时保存迁移暂存与恢复备份。");
        var manifest = InstanceManifestStore.Read(Path.Combine(target, InstanceImportManifest.FileName));
        var beforeData = MigrationDataStore.Read(target, manifest);
        var previousReplacements = InstanceLocalReplacementService.ApplyWithSnapshot(target, manifest, manifest.Files);
        var priorSelections = previousReplacements.Requirements
            .Where(file => file.Path is not null).ToDictionary(file => file.Path!, StringComparer.OrdinalIgnoreCase);
        var journal = new MigrationJournal
        {
            Id = Guid.NewGuid().ToString("N"), GameDirectory = manifest.GameDirectory,
            LocalReplacementFingerprint = previousReplacements.ReplacementFingerprint,
            ManifestFingerprint = EmbeddedFileBaseline.ManifestFingerprint(manifest), SourceName = plan.Source.Name
        };
        var directory = MigrationJournalStore.DirectoryPath(target, journal.Id);
        MigrationJournalStore.Write(target, journal);
        var entries = new List<MigrationJournalEntry>();
        try
        {
            var migrated = beforeData.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < pending.Length; index++)
            {
                var file = pending[index];
                token.ThrowIfCancellationRequested();
                progress?.Report(new("暂存并校验", file.SourcePath, index, pending.Length));
                var from = MigrationIO.SafePath(plan.Source.GameDirectory, file.SourcePath);
                var destination = MigrationIO.SafePath(plan.Target.GameDirectory, file.TargetPath);
                await RequireSnapshotAsync(from, file.SourceSize, file.SourceSha256, token);
                await RequireSnapshotAsync(destination, file.PreviousSize, file.PreviousSha256, token);
                var staged = MigrationIO.SafePath(directory, "next/" + index);
                if (file.Group == "language")
                {
                    var bytes = MigrationIO.MergeLanguage(await File.ReadAllBytesAsync(from, token),
                        file.PreviousSha256 is null ? [] : await File.ReadAllBytesAsync(destination, token));
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    await File.WriteAllBytesAsync(staged, bytes, token);
                    await RequireSnapshotAsync(staged, file.Size, file.Sha256, token);
                }
                else await CopyVerifiedAsync(from, staged, file.Size, file.Sha256, token);
                if (file.PreviousSha256 is { } hash)
                    await CopyVerifiedAsync(destination, MigrationIO.SafePath(directory, "before/" + index), file.PreviousSize!.Value, hash, token);
                var relative = Path.GetRelativePath(target, destination).Replace('\\', '/');
                var extraMod = GtnhExtraModPolicy.IsGroup(file.Group);
                entries.Add(new(relative, file.Size, file.Sha256, file.PreviousSize, file.PreviousSha256)
                { Rule = extraMod ? GtnhExtraModPolicy.Rule : null });
                migrated[relative] = new(relative, file.Size, file.Sha256, journal.Id,
                    extraMod ? GtnhExtraModPolicy.Rule : "gtnh-player-v1")
                {
                    SupersededLocalSha256 = priorSelections.TryGetValue(relative, out var selected) &&
                        selected.Origin == InstanceFileOrigin.UserLocalImport && selected.Hashes.TryGetValue("sha256", out var selectedHash)
                        ? selectedHash : null
                };
            }
            // 用途记录也作为事务最后一个文件提交和恢复，避免数据成功但来源状态遗漏。
            var statePath = MigrationIO.SafePath(target, MigrationDataStore.RelativePath);
            var oldState = await MigrationIO.SnapshotAsync(statePath, token);
            var stateIndex = entries.Count;
            if (oldState is { } prior) await CopyVerifiedAsync(statePath, MigrationIO.SafePath(directory, "before/" + stateIndex), prior.Size, prior.Hash, token);
            var nextState = MigrationIO.SafePath(directory, "next/" + stateIndex);
            MigrationDataStore.Validate(manifest, migrated.Values.ToArray());
            MigrationIO.WriteJson(nextState, new MigrationDataStore.State(1, journal.ManifestFingerprint,
                migrated.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray()));
            var stateHash = (await MigrationIO.SnapshotAsync(nextState, token))!.Value;
            entries.Add(new(MigrationDataStore.RelativePath, stateHash.Size, stateHash.Hash, oldState?.Size, oldState?.Hash));
            // 再次核对完整预览，捕获复制期间新增或更改的源文件以及同名世界竞争。
            var current = await _planner.BuildAsync(plan.Request, token);
            if (current.Fingerprint != plan.Fingerprint) throw new IOException("暂存期间源或目标发生变化，请重新预览。");
            var gamePrefix = manifest.GameDirectory == "." ? "" : manifest.GameDirectory + "/";
            journal.NewWorldDirectories = pending.Where(file => file.Group.StartsWith("world:", StringComparison.Ordinal))
                .Select(file => gamePrefix + string.Join('/', file.TargetPath.Split('/').Take(2)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            journal.Files = entries; journal.Status = "Applying"; MigrationJournalStore.Write(target, journal);
            for (var index = 0; index < entries.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                EmbeddedFileBaseline.ValidateInstance(_root, target);
                var entry = entries[index];
                var destination = MigrationIO.SafePath(target, entry.Path);
                await RequireSnapshotAsync(destination, entry.PreviousSize, entry.PreviousSha256, token);
                progress?.Report(new("提交迁移", entry.Path, index, entries.Count));
                // 不信任回调期间的外部更改。
                await RequireSnapshotAsync(destination, entry.PreviousSize, entry.PreviousSha256, token);
                EmbeddedFileBaseline.ValidateInstance(_root, target);
                var staged = MigrationIO.SafePath(directory, "next/" + index);
                await RequireSnapshotAsync(staged, entry.Size, entry.Sha256, token);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(staged, destination, entry.PreviousSha256 is not null);
            }
            journal.Status = "Completed"; MigrationJournalStore.Write(target, journal);
            return new(journal.Id, journal.Status, pending.Length, plan.Files.Count - pending.Length,
                Path.Combine(directory, "journal.json"), ["迁移完成，旧实例保持不变。", "未迁移或安装完整汉化。", "请实际验证新版存档；恢复前不要继续修改迁入数据。",
                    .. (pending.Any(file => GtnhExtraModPolicy.IsGroup(file.Group)) ? new[] { GtnhExtraModPolicy.Warning } : Array.Empty<string>())]);
        }
        catch (Exception ex)
        {
            var restored = await RestoreCoreAsync(target, journal, null, CancellationToken.None);
            return restored with { Messages = [ex is OperationCanceledException ? "迁移已取消。" : "迁移未完成：" + ex.Message, .. restored.Messages] };
        }
    }

    /// <summary>优先显示未完成事务，否则只允许恢复最近一次尚未撤销的迁移。</summary>
    public Task<MigrationRecoveryInfo?> GetRecoveryAsync(string instancePath, CancellationToken token = default) => Task.Run(() =>
    {
        var root = EmbeddedFileBaseline.ValidateInstance(_root, instancePath);
        var all = MigrationJournalStore.List(root);
        var last = all.FirstOrDefault(j => j.Status is not ("Completed" or "RolledBack")) ?? all.FirstOrDefault(j => j.Status != "RolledBack");
        return last is null ? null : new MigrationRecoveryInfo(last.Id, last.Status, last.CreatedAt);
    }, token);

    /// <summary>仅恢复最新操作，并保留玩家在迁移后再次修改的文件。</summary>
    public Task<InstanceMigrationResult> RestoreAsync(string instancePath, string migrationId,
        IProgress<InstanceMigrationProgress>? progress = null, CancellationToken token = default) => Task.Run(async () =>
    {
        var target = EmbeddedFileBaseline.ValidateInstance(_root, instancePath);
        using var lease = InstanceOperationLease.Acquire(target);
        var latest = await GetRecoveryAsync(target, token);
        if (latest?.Id != migrationId) throw new InvalidOperationException("只能依次恢复最近一次迁移。");
        return await RestoreCoreAsync(target, MigrationJournalStore.Read(target, migrationId), progress, token);
    }, token);

    /// <summary>先检查全部恢复前提再写入；文件再次变化时保留现场，禁止静默覆盖新进度。</summary>
    private static async Task<InstanceMigrationResult> RestoreCoreAsync(string target, MigrationJournal journal,
        IProgress<InstanceMigrationProgress>? progress, CancellationToken token)
    {
        var directory = MigrationJournalStore.DirectoryPath(target, journal.Id);
        var messages = new List<string>();
        var originalStatus = journal.Status;
        try
        {
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException("实例目录已移动，不能在旧路径重建恢复数据。");
            var manifest = InstanceManifestStore.Read(Path.Combine(target, InstanceImportManifest.FileName));
            if (InstanceLocalReplacementService.ApplyWithSnapshot(target, manifest, manifest.Files).ReplacementFingerprint != journal.LocalReplacementFingerprint)
                throw new IOException("迁移后用户文件替换记录已变化，停止恢复以保留新的选择。");
            var expected = journal.Files.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var world in journal.NewWorldDirectories)
                foreach (var path in MigrationIO.Files(target, world, token))
                    if (!expected.Contains(path)) throw new IOException("迁入世界出现后续新增文件，未恢复：" + path);
            for (var i = 0; i < journal.Files.Count; i++)
            {
                var entry = journal.Files[i];
                var current = await MigrationIO.SnapshotAsync(MigrationIO.SafePath(target, entry.Path), token);
                if (!Matches(current, entry.PreviousSize, entry.PreviousSha256) && !Matches(current, entry.Size, entry.Sha256))
                    throw new IOException("迁移后文件再次变化，未覆盖：" + entry.Path);
                if (entry.PreviousSha256 is { } hash)
                    await RequireSnapshotAsync(MigrationIO.SafePath(directory, "before/" + i), entry.PreviousSize, hash, token);
            }
            token.ThrowIfCancellationRequested();
            journal.Status = "RollingBack"; MigrationJournalStore.Write(target, journal);
            // 回滚提交后不再接受取消，避免只恢复一半；故障仍可按日志再次恢复。
            for (var i = journal.Files.Count - 1; i >= 0; i--)
            {
                if (!Directory.Exists(target)) throw new DirectoryNotFoundException("实例目录已移动，停止恢复。");
                var entry = journal.Files[i]; var destination = MigrationIO.SafePath(target, entry.Path);
                var current = await MigrationIO.SnapshotAsync(destination, CancellationToken.None);
                if (Matches(current, entry.PreviousSize, entry.PreviousSha256)) continue;
                if (!Matches(current, entry.Size, entry.Sha256)) throw new IOException("恢复期间文件变化，已保留：" + entry.Path);
                progress?.Report(new("恢复迁移前数据", entry.Path, journal.Files.Count - i, journal.Files.Count));
                await RequireSnapshotAsync(destination, entry.Size, entry.Sha256, CancellationToken.None);
                if (entry.PreviousSha256 is { } previous)
                {
                    var temporary = MigrationIO.SafePath(directory, "restore/" + i + "-" + Guid.NewGuid().ToString("N"));
                    await CopyVerifiedAsync(MigrationIO.SafePath(directory, "before/" + i), temporary, entry.PreviousSize!.Value, previous, CancellationToken.None);
                    await RequireSnapshotAsync(destination, entry.Size, entry.Sha256, CancellationToken.None);
                    File.Move(temporary, destination, true);
                }
                else
                {
                    var removed = MigrationIO.SafePath(directory, "removed/" + i + "-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.GetDirectoryName(removed)!);
                    File.Move(destination, removed); // 本次新增内容转入恢复目录，不永久删除。
                }
            }
            // 只清理本次新建世界中已空的目录；任何新增内容都保留，不递归删除。
            foreach (var world in journal.NewWorldDirectories)
                foreach (var entry in journal.Files.Where(entry => entry.Path.StartsWith(world + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    var worldPath = MigrationIO.SafePath(target, world);
                    for (var parent = Path.GetDirectoryName(MigrationIO.SafePath(target, entry.Path));
                         parent is not null && (parent.Equals(worldPath, StringComparison.OrdinalIgnoreCase) || parent.StartsWith(worldPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                         parent = Path.GetDirectoryName(parent))
                    {
                        InstanceImportPaths.EnsureNoLinks(parent);
                        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
                    }
                }
            journal.Status = "RolledBack"; MigrationJournalStore.Write(target, journal);
            messages.Add("已恢复迁移前的文件和用途记录；备份及本次新增文件仍保留在事务目录。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && journal.Status != "RollingBack") { throw; }
        catch (Exception ex)
        {
            // 对已完成迁移的恢复预检失败，不把正常游玩后的实例强行锁为损坏。
            journal.Status = originalStatus == "Completed" && journal.Status == "Completed" ? "Completed" : "RecoveryRequired";
            // 实例被外部移动时保留移动后的原日志，不能重新创建旧实例目录。
            if (Directory.Exists(target) && File.Exists(Path.Combine(directory, "journal.json"))) MigrationJournalStore.Write(target, journal);
            messages.Add("未能自动恢复，请保留备份并处理冲突：" + ex.Message);
            return new(journal.Id, "RecoveryRequired", 0, 0, Path.Combine(directory, "journal.json"), messages);
        }
        return new(journal.Id, journal.Status, 0, 0, Path.Combine(directory, "journal.json"), messages);
    }

    /// <summary>流式复制，保留源文件且不覆盖已有暂存，写完核对复制摘要。</summary>
    private static async Task CopyVerifiedAsync(string source, string destination, long size, string hash, CancellationToken token)
    {
        InstanceImportPaths.EnsureNoLinks(source); InstanceImportPaths.EnsureNoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
        { if (input.Length != size) throw new IOException("复制源大小变化。"); await input.CopyToAsync(output, token); await output.FlushAsync(token); output.Flush(true); }
        await RequireSnapshotAsync(destination, size, hash, token);
    }
    /// <summary>存在性及摘要均须与预览一致，目录或访问错误不会当作缺失。</summary>
    private static async Task RequireSnapshotAsync(string path, long? size, string? hash, CancellationToken token)
    {
        if (!Matches(await MigrationIO.SnapshotAsync(path, token), size, hash)) throw new IOException("文件与预览不一致：" + path);
    }
    /// <summary>将不存在与原值区分，不能把缺失文件当成空文件。</summary>
    private static bool Matches((long Size, string Hash)? current, long? size, string? hash) =>
        current is null ? size is null && hash is null : size == current.Value.Size && hash == current.Value.Hash;
}
