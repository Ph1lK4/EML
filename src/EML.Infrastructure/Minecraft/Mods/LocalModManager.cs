using System.Diagnostics;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;
using EML.Core.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Infrastructure.Minecraft.Mods;

/// <summary>管理实例中的玩家额外模组；原包文件只读，启禁与回收不改变已确认的内容来源。</summary>
public sealed class LocalModManager : ILocalModManager, IOnlineModRegistrar
{
    private readonly string _launcherRoot;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly LocalModFileCatalog _catalog = new();
    private sealed record Context(string Root, InstanceImportManifest Manifest, InstanceEffectiveRequirements.Snapshot Effective,
        ModManagementStore.Snapshot Mods, MigrationDataStore.Snapshot Migration);

    /// <summary>与实例扫描和迁移服务一致，接收启动器根目录而不是 instances 子目录。</summary>
    public LocalModManager(string? launcherRoot = null) => _launcherRoot = Path.GetFullPath(launcherRoot ?? AppContext.BaseDirectory);

    /// <summary>只读描述、大小和管理状态；不会每次刷新都对整个 mods 目录计算摘要。</summary>
    public async Task<LocalModCatalog> ListAsync(string instancePath, CancellationToken token = default)
    {
        await _operationLock.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => List(Load(instancePath), token), token).ConfigureAwait(false); }
        finally { _operationLock.Release(); }
    }

    /// <summary>复制一个本地模组到独立暂存区，验证并排除冲突后才提交到 mods。</summary>
    public Task AddAsync(string instancePath, string sourcePath, CancellationToken token = default) =>
        AddCoreAsync(instancePath, sourcePath, null, token);

    /// <summary>在线下载仍经相同的冲突检查和事务提交，但保留官方哈希与固定平台身份。</summary>
    public Task RegisterDownloadedAsync(string instancePath, string sourcePath, InstanceFileRequirement requirement,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ModManagementStore.ValidateOnlineBaseline(requirement);
        // GitHub ZIP 仅供独立资源安装器使用；放开来源模型后不能把资源包送入模组管理目录。
        if (requirement.Origin == InstanceFileOrigin.GithubReleaseDownload &&
            !requirement.GithubReleaseOrigin!.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub 资源 ZIP 不能作为模组安装，请使用资源包或光影页面。");
        // 固定集合快照，调用者在等待实例锁时修改原集合也不会改变本次授权内容。
        return AddCoreAsync(instancePath, sourcePath, requirement with
        {
            Hashes = requirement.Hashes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            Downloads = requirement.Downloads.ToArray(),
            CurseForgeOrigin = requirement.CurseForgeOrigin is not { } origin ? null : origin with
            { Hashes = origin.Hashes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) }
        }, token);
    }

    /// <summary>复制到实例内暂存区，在保存来源记录前验证内容和目标；不修改输入文件。</summary>
    private Task AddCoreAsync(string instancePath, string sourcePath, InstanceFileRequirement? online, CancellationToken token) =>
        RunAsync(instancePath, async context =>
        {
            var source = Path.GetFullPath(sourcePath);
            InstanceImportPaths.EnsureNoLinks(source);
            var target = LocalModPaths.NewPath(context.Manifest, Path.GetFileName(online?.Path ?? source));
            if (online is not null && !target.Equals(InstanceImportPaths.Relative(online.Path!), StringComparison.Ordinal))
                throw new InvalidDataException("在线模组只能安装到当前实例 mods 根目录。");
            RequireResolved(context);
            var id = Guid.NewGuid().ToString("N");
            var stagedRelative = ModOperationJournal.StagingPath(id);
            var staged = MigrationIO.SafePath(context.Root, stagedRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            var created = false;
            try
            {
                // 原文件只读复制，暂存文件只有在事务正式提交后才进入游戏目录。
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
                {
                    if (input.Length > SafeInstanceArchive.MaxUncompressedBytes) throw new InvalidDataException("模组文件超过允许大小。");
                    MigrationIO.SafePath(context.Root, stagedRelative);
                    await using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
                    created = true;
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                var candidate = await ModArchiveInspector.ReadAsync(staged, target, token).ConfigureAwait(false);
                if (online is not null)
                    await RequireTrustedAsync(staged, candidate, online, token).ConfigureAwait(false);
                await EnsureNoCollisionAsync(context, candidate, null, token).ConfigureAwait(false);
                var record = new ManagedModRecord
                {
                    Id = Guid.NewGuid().ToString("N"), Added = true, Baseline = online ?? Baseline(target, candidate.Size, candidate.Sha256),
                    State = Disabled(target) ? LocalModState.Disabled : LocalModState.Enabled
                };
                await CommitAsync(context, record, id, "Add", stagedRelative, target, candidate.Size, candidate.Sha256, token).ConfigureAwait(false);
            }
            finally
            {
                if (created) CleanUncommittedStaging(context.Root, id);
            }
        }, token);

    /// <summary>用户确认后登记一个外部新增文件；原包文件和已有管理身份无法被接管。</summary>
    public Task AdoptAsync(string instancePath, string path, CancellationToken token = default) =>
        RunAsync(instancePath, async context =>
        {
            path = InstanceImportPaths.Relative(path);
            RequireModPath(context.Manifest, path);
            RequireResolved(context);
            if (ReservedPaths(context).Any(value => SameIdentity(value, path)))
                throw new InvalidOperationException("该文件属于原包或已经登记，不能作为未知来源接管。");
            var candidate = await ModArchiveInspector.ReadAsync(MigrationIO.SafePath(context.Root, path), path, token).ConfigureAwait(false);
            await EnsureNoCollisionAsync(context, candidate, path, token).ConfigureAwait(false);
            var record = new ManagedModRecord
            {
                Id = Guid.NewGuid().ToString("N"), Added = true, Baseline = Baseline(path, candidate.Size, candidate.Sha256),
                State = Disabled(path) ? LocalModState.Disabled : LocalModState.Enabled
            };
            await CommitAsync(context, record, Guid.NewGuid().ToString("N"), "Adopt", path, path,
                candidate.Size, candidate.Sha256, token).ConfigureAwait(false);
        }, token);

    /// <summary>通过 .disabled 后缀切换加载状态，先核对用户确认过的内容且不覆盖目标。</summary>
    public Task SetEnabledAsync(string instancePath, string path, bool enabled, CancellationToken token = default) =>
        ChangeAsync(instancePath, path, enabled ? "Enable" : "Disable", token);

    /// <summary>移入实例私有回收区并保存移除意图，之后补全不会重新安装该模组。</summary>
    public Task RemoveAsync(string instancePath, string path, CancellationToken token = default) => ChangeAsync(instancePath, path, "Remove", token);

    /// <summary>恢复回收前的启用状态；缺少回收副本或出现同名、同标识冲突时停止。</summary>
    public Task RestoreAsync(string instancePath, string path, CancellationToken token = default) => ChangeAsync(instancePath, path, "Restore", token);

    /// <summary>持有实例锁后完成中断事务，恢复不能绕过迁移事务或文件完整性要求。</summary>
    public async Task RecoverAsync(string instancePath, CancellationToken token = default)
    {
        await _operationLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(async () =>
            {
                var root = EmbeddedFileBaseline.ValidateInstance(_launcherRoot, instancePath);
                using var lease = InstanceOperationLease.Acquire(root);
                MigrationJournalStore.EnsureSettled(root);
                var manifest = InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName));
                await ModOperationJournal.RecoverAsync(root, manifest, token).ConfigureAwait(false);
                _catalog.Invalidate(root);
            }, token).ConfigureAwait(false);
        }
        finally { _operationLock.Release(); }
    }

    /// <summary>写操作统一在实例锁和线程池中执行，避免与启动、补全、导出和迁移互相覆盖。</summary>
    private async Task RunAsync(string instancePath, Func<Context, Task> action, CancellationToken token)
    {
        await _operationLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(async () =>
            {
                var root = EmbeddedFileBaseline.ValidateInstance(_launcherRoot, instancePath);
                using var lease = InstanceOperationLease.Acquire(root);
                var context = Load(root);
                if (!LocalModPaths.CanWrite(context.Manifest))
                    throw new InvalidOperationException("当前实例未识别到受支持的模组加载器，暂时只支持查看本地模组。");
                try { await action(context).ConfigureAwait(false); }
                finally { _catalog.Invalidate(root); }
            }, token).ConfigureAwait(false);
        }
        finally { _operationLock.Release(); }
    }

    /// <summary>沿用统一有效需求读取可信来源，不用当前磁盘文件覆盖原始完整性记录。</summary>
    private Context Load(string instancePath)
    {
        var root = EmbeddedFileBaseline.ValidateInstance(_launcherRoot, instancePath);
        MigrationJournalStore.EnsureSettled(root);
        ModOperationJournal.EnsureSettled(root);
        var manifestPath = Path.Combine(root, InstanceImportManifest.FileName);
        if (!File.Exists(manifestPath)) throw new InvalidOperationException("实例尚无 EML 导入记录，请先通过导入功能登记实例后管理模组。");
        var manifest = InstanceManifestStore.Read(manifestPath);
        var effective = InstanceEffectiveRequirements.Build(root, manifest);
        return new(root, manifest, effective, effective.Mods, effective.Migration);
    }

    /// <summary>合并磁盘列表、原包声明、迁入记录和用户选择，保留缺失与回收项供用户处理。</summary>
    private LocalModCatalog List(Context context, CancellationToken token)
    {
        var disk = _catalog.Scan(context.Root, context.Manifest, token).ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var result = new List<LocalModEntry>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var records = context.Mods.Files.ToDictionary(record => record.Baseline.Path!, StringComparer.OrdinalIgnoreCase);
        var migrated = ExtraMods(context).ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var writable = LocalModPaths.CanWrite(context.Manifest);
        var trusted = context.Mods.Files.Count == 0 ? new Dictionary<string, InstanceFileRequirement>(StringComparer.OrdinalIgnoreCase) :
            RequirementsBeforeMapping(context).Where(file => file.Path is not null)
                .ToDictionary(file => file.Path!, StringComparer.OrdinalIgnoreCase);
        foreach (var record in context.Mods.Files)
        {
            token.ThrowIfCancellationRequested();
            var current = ModManagementStore.CurrentPath(record);
            var physical = current ?? ModManagementStore.TrashPath(record);
            LocalModFileCatalog.Item? item = null;
            if (current is not null) { disk.TryGetValue(current, out item); consumed.Add(current); }
            else if (File.Exists(MigrationIO.SafePath(context.Root, physical))) item = _catalog.Read(context.Root, physical);
            var selected = trusted.GetValueOrDefault(record.Baseline.Path!)
                ?? throw new InvalidDataException("管理模组缺少有效的内容来源。");
            result.Add(Entry(record.Baseline.Path!, current, record.State, item, selected,
                record.Added ? SourceDescription(selected) : "GTNH 迁入的额外模组",
                writable && LocalModPaths.Allows(context.Manifest, record.Baseline.Path!), false,
                writable && record.State == LocalModState.Removed && item is not null));
        }
        foreach (var file in context.Effective.Requirements.Where(file => file.Path is { } path && LocalModPaths.Allows(context.Manifest, path)))
        {
            var path = file.Path!;
            if (consumed.Contains(path) || records.ContainsKey(path)) continue;
            disk.TryGetValue(path, out var item);
            consumed.Add(path);
            var extra = migrated.ContainsKey(path);
            result.Add(Entry(path, path, Disabled(path) ? LocalModState.Disabled : LocalModState.Enabled, item, file,
                extra ? "GTNH 迁入的额外模组" : "导入清单声明（受保护）", extra && writable, false, false));
        }
        var reserved = ReservedPaths(context).ToArray();
        var unresolved = context.Effective.Requirements.Any(file => file.Path is null);
        foreach (var item in disk.Values.Where(item => !consumed.Contains(item.Path)))
        {
            var occupied = reserved.Any(path => SameIdentity(path, item.Path));
            result.Add(Entry(item.Path, item.Path, Disabled(item.Path) ? LocalModState.Disabled : LocalModState.Enabled,
                item, null, occupied ? "原包或管理文件的未登记副本" : "来源未确认", false, writable && !occupied && !unresolved, false)
                with { Problem = occupied ? "此启用/禁用路径已有来源记录，不能重复接管。" : item.Metadata.Warning });
        }
        // 元数据冲突只作提示；列表不为此额外计算所有文件摘要。
        var duplicatePaths = DuplicatePaths(result);
        result = result.Select(entry => duplicatePaths.Contains(entry.Path)
            ? entry with { Problem = Join(entry.Problem, "存在同名或相同 modid 的其他文件，需确认是否重复安装。") } : entry).ToList();
        InstanceEffectiveRequirements.EnsureUnchanged(context.Root, context.Manifest, context.Effective);
        var notes = new List<string>
        {
            "首版保护导入清单声明的模组；可以管理玩家添加、明确接管和 GTNH 迁入的额外模组。",
            "列表只读取描述与文件状态，不代表依赖或版本兼容性已经验证。请关闭游戏后修改模组，并保留存档备份。"
        };
        if (unresolved) notes.Add("实例仍有尚未解析的文件来源，请先补全实例，再接管外部文件或添加模组。");
        if (!writable) notes.Add("未识别到受支持的模组加载器，目前仅显示本地文件，暂不开放添加、接管、启禁和回收操作。");
        if (context.Mods.Files.Any(record => record.State == LocalModState.Removed))
            notes.Add("已移除模组保留在本机实例回收区；实例导出保留移除选择，但不携带回收副本。");
        return new(result.OrderBy(entry => entry.State == LocalModState.Removed).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray(), notes);
    }

    /// <summary>将轻量观察结果变为界面条目，缺失和大小改变均明确提示而不重建可信基线。</summary>
    private static LocalModEntry Entry(string path, string? current, LocalModState state, LocalModFileCatalog.Item? item,
        InstanceFileRequirement? expected, string source, bool canManage, bool canAdopt, bool canRestore)
    {
        var metadata = item?.Metadata;
        var problem = item is null ? state == LocalModState.Removed ? "本机没有可恢复的回收副本。" : "文件缺失。" :
            expected?.Size is { } size && size != item.Size ? "文件大小与已确认的内容不符，请先检查或恢复。" : metadata?.Warning;
        return new()
        {
            Path = path, CurrentPath = current, FileName = Path.GetFileName(current ?? path),
            Name = metadata?.Name ?? ModArchiveInspector.Name(path), Version = metadata?.Version ?? "未知",
            Identities = metadata?.Identities ?? [],
            State = state, SourceDescription = source, Problem = problem, IsPresent = item is not null,
            CanManage = canManage && item is not null, CanAdopt = canAdopt, CanRestore = canRestore, Size = item?.Size ?? expected?.Size
        };
    }

    /// <summary>启禁、回收和恢复共用可信摘要校验与单文件事务。</summary>
    private Task ChangeAsync(string instancePath, string path, string kind, CancellationToken token) =>
        RunAsync(instancePath, async context =>
        {
            path = InstanceImportPaths.Relative(path);
            RequireModPath(context.Manifest, path);
            var record = FindManaged(context, path);
            var selected = TrustedRequirement(context, record);
            if (kind == "Restore" ? record.State != LocalModState.Removed : record.State == LocalModState.Removed)
                throw new InvalidOperationException(kind == "Restore" ? "该模组没有被移除。" : "模组已移除，请先恢复。" );
            var state = kind switch
            {
                "Enable" => LocalModState.Enabled,
                "Disable" => LocalModState.Disabled,
                "Remove" => LocalModState.Removed,
                "Restore" => record.RemovedWasDisabled ? LocalModState.Disabled : LocalModState.Enabled,
                _ => throw new ArgumentException("未知模组操作。", nameof(kind))
            };
            if (record.State == state) return;
            var source = ModManagementStore.CurrentPath(record) ?? ModManagementStore.TrashPath(record);
            var sourcePath = MigrationIO.SafePath(context.Root, source);
            var candidate = await ModArchiveInspector.ReadAsync(sourcePath, path, token).ConfigureAwait(false);
            await RequireTrustedAsync(sourcePath, candidate, selected, token).ConfigureAwait(false);
            var updated = record with { State = state, RemovedWasDisabled = kind == "Remove" ? record.State == LocalModState.Disabled : record.RemovedWasDisabled };
            var destination = ModManagementStore.CurrentPath(updated) ?? ModManagementStore.TrashPath(updated);
            if (kind is "Enable" or "Restore")
                await EnsureNoCollisionAsync(context, candidate with { Path = destination }, path, token).ConfigureAwait(false);
            await CommitAsync(context, updated, Guid.NewGuid().ToString("N"), kind, source, destination,
                candidate.Size, candidate.Sha256, token).ConfigureAwait(false);
        }, token);

    /// <summary>优先使用已有稳定记录；首次管理迁入模组时保留迁移原始基线。</summary>
    private static ManagedModRecord FindManaged(Context context, string path)
    {
        var record = context.Mods.Files.SingleOrDefault(item => item.Baseline.Path!.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (record is not null) return record;
        var migrated = ExtraMods(context).SingleOrDefault(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (migrated is null) throw new InvalidOperationException("原包声明的模组受到保护；未登记的外部文件需要先明确接管。");
        return new()
        {
            Id = Guid.NewGuid().ToString("N"), Baseline = MigrationDataStore.Requirement(migrated), Added = false,
            State = Disabled(path) ? LocalModState.Disabled : LocalModState.Enabled
        };
    }

    /// <summary>读取管理映射前的真实选择，使移除项也认可之前明确接受的本地替代内容。</summary>
    private static InstanceFileRequirement TrustedRequirement(Context context, ManagedModRecord record)
    {
        if (ModManagementStore.CurrentPath(record) is { } current)
        {
            var existing = context.Effective.Requirements.SingleOrDefault(file => string.Equals(file.Path, current, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
        }
        return RequirementsBeforeMapping(context).SingleOrDefault(file => string.Equals(file.Path, record.Baseline.Path, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("管理模组缺少有效的内容来源。");
    }

    /// <summary>一次读取管理映射前的选择；列表批量复用，避免为每个回收项重复解析状态。</summary>
    private static IReadOnlyList<InstanceFileRequirement> RequirementsBeforeMapping(Context context)
    {
        var replacements = InstanceLocalReplacementService.ApplyWithSnapshot(context.Root, context.Manifest, context.Manifest.Files);
        return MigrationDataStore.Apply(replacements.Requirements, context.Migration);
    }

    /// <summary>变更前仍须匹配原先可信基线，不能把外部修改顺手记成新的合法版本。</summary>
    private static async Task RequireTrustedAsync(string path, ModArchiveInspector.Snapshot file, InstanceFileRequirement expected,
        CancellationToken token)
    {
        _ = InstanceFileIntegrity.HashesFor(expected);
        if (expected.Size != file.Size || !await InstanceRuntimeInstaller.ExistingFileMatchesAsync(path, expected, token).ConfigureAwait(false))
            throw new InvalidDataException("模组内容与对应来源的大小或哈希不符，请先恢复可信副本；本次未移动文件。");
    }

    /// <summary>显示实际来源，备用下载同时说明原 CurseForge 文件身份。</summary>
    private static string SourceDescription(InstanceFileRequirement file) => file.Origin switch
    {
        InstanceFileOrigin.CurseForgeDownload => $"CurseForge 下载 · {file.ProjectId}/{file.FileId}",
        InstanceFileOrigin.GithubReleaseDownload => $"GitHub 发行版 · {file.GithubReleaseOrigin?.Repository} / {file.GithubReleaseOrigin?.Tag}",
        InstanceFileOrigin.ModrinthDownload => "Modrinth 下载" +
            (file.CurseForgeOrigin is null ? "" : "（CurseForge 同内容备用来源）"),
        _ => "玩家添加 / 已确认接管"
    };

    /// <summary>同时检查声明占位、文件名和 modid，只对相同大小候选补算摘要以识别重命名副本。</summary>
    private async Task EnsureNoCollisionAsync(Context context, ModArchiveInspector.Snapshot candidate, string? excluded,
        CancellationToken token)
    {
        var excludedRecord = excluded is null ? null : context.Mods.Files.SingleOrDefault(record =>
            record.Baseline.Path!.Equals(excluded, StringComparison.OrdinalIgnoreCase));
        var excludedCurrent = excludedRecord is null ? excluded : ModManagementStore.CurrentPath(excludedRecord);
        var declared = ReservedPaths(context).Where(path => excluded is null || !SameIdentity(path, excluded));
        var disk = _catalog.Scan(context.Root, context.Manifest, token).Where(item =>
            !item.Path.Equals(excludedCurrent, StringComparison.OrdinalIgnoreCase)).ToArray();
        var conflict = ModArchiveInspector.Collision(candidate.Path, null, candidate.Metadata.Ids,
            disk.Select(item => (item.Path, (string?)null, item.Metadata.Ids)), declared);
        if (conflict is not null) throw new InvalidOperationException(conflict);
        foreach (var other in disk.Where(item => item.Size == candidate.Size))
        {
            var actual = await MigrationIO.SnapshotAsync(MigrationIO.SafePath(context.Root, other.Path), token).ConfigureAwait(false);
            if (actual is not null && actual.Value.Hash.Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("已有相同内容的模组：" + other.Path);
        }
    }

    /// <summary>确认读取快照仍有效后，向独立事务日志提交一个文件的明确状态变化。</summary>
    private static async Task CommitAsync(Context context, ManagedModRecord record, string id, string kind, string source,
        string destination, long size, string hash, CancellationToken token)
    {
        InstanceEffectiveRequirements.EnsureUnchanged(context.Root, context.Manifest, context.Effective);
        var after = context.Mods.Files.Where(item => item.Id != record.Id).Append(record).ToArray();
        await ModOperationJournal.CommitAsync(context.Root, context.Manifest, context.Mods, after, id, kind, record.Id,
            source, destination, size, hash, token).ConfigureAwait(false);
    }

    /// <summary>所有原包、平台、迁入和管理身份均保留占位，缺失或移除不表示名字可被另一来源复用。</summary>
    private static IEnumerable<string> ReservedPaths(Context context) => context.Manifest.Files.Concat(context.Effective.Requirements)
        .Select(file => file.Path).Concat(context.Mods.Files.Select(record => record.Baseline.Path))
        .Concat(ExtraMods(context).Select(file => file.Path)).Where(path => path is not null && LocalModPaths.Allows(context.Manifest, path))
        .Select(path => path!).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>只把已有明确额外模组迁移记录视为可管理对象，不把原包内文件猜作玩家新增。</summary>
    private static IEnumerable<MigratedPlayerFile> ExtraMods(Context context) => context.Migration.Files.Where(file => file.Rule == GtnhExtraModPolicy.Rule);

    /// <summary>新确认文件使用独立用户来源，只保留其自身大小和 SHA-256。</summary>
    private static InstanceFileRequirement Baseline(string path, long size, string hash) => new()
    {
        Path = path, Source = InstanceFileSource.Embedded, Origin = InstanceFileOrigin.UserLocalImport,
        Role = InstanceFileRole.Content, Required = true, Size = size, Hashes = new Dictionary<string, string> { ["sha256"] = hash }
    };

    /// <summary>未解析的官方文件可能尚无文件名，应先补全来源再判断外部文件身份。</summary>
    private static void RequireResolved(Context context)
    {
        if (context.Effective.Requirements.Any(file => file.Path is null))
            throw new InvalidOperationException("实例仍有未解析的文件来源，请先补全实例依赖，再添加或接管模组。");
    }

    /// <summary>写操作只允许当前实例加载器实际使用的模组位置。</summary>
    private static void RequireModPath(InstanceImportManifest manifest, string path)
    {
        if (!LocalModPaths.Allows(manifest, path)) throw new InvalidOperationException("路径不属于当前实例可管理的模组目录。");
    }

    /// <summary>保留物理文件的禁用状态，不以其他文件名片段猜测是否启用。</summary>
    private static bool Disabled(string path) => path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>同一位置的启用和禁用变体属于一个稳定身份。</summary>
    private static bool SameIdentity(string left, string right) => LocalModPathPolicy.EnabledPath(left).Equals(
        LocalModPathPolicy.EnabledPath(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>只使用已读取的名称和标识找出明显重复，保留未知标识的不确定性。</summary>
    private static HashSet<string> DuplicatePaths(IReadOnlyList<LocalModEntry> entries)
    {
        var candidates = entries.Where(entry => entry.State != LocalModState.Removed && entry.IsPresent).ToArray();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in candidates)
            foreach (var key in new[] { "name:" + ModArchiveInspector.Name(entry.Path) }.Concat(entry.Identities.Select(identity => "id:" + identity.Id)))
                if (keys.TryGetValue(key, out var previous)) { result.Add(previous); result.Add(entry.Path); }
                else keys[key] = entry.Path;
        return result;
    }

    /// <summary>在已有问题后追加独立提示，避免隐藏来源或读取错误。</summary>
    private static string Join(string? first, string next) => string.IsNullOrWhiteSpace(first) ? next : first + " " + next;

    /// <summary>只清理本次 CreateNew 产生且未被待恢复事务引用的暂存文件，不删除原文件或回收副本。</summary>
    private static void CleanUncommittedStaging(string root, string id)
    {
        try
        {
            if (ModOperationJournal.ReferencesStaging(root, id)) return;
            var path = MigrationIO.SafePath(root, ModOperationJournal.StagingPath(id));
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Trace.TraceWarning("无法清理本次模组暂存文件，已保留现场：{0}", ex.Message);
        }
    }
}
