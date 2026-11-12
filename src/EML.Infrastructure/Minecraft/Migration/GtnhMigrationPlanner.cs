using System.Security.Cryptography;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;
using EML.Core.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Gtnh;

namespace EML.Infrastructure.Minecraft.Migration;

/// <summary>按 GTNH 文档生成只读预览，语言项单独合并，不识别或复制旧版汉化包。</summary>
internal sealed class GtnhMigrationPlanner(string launcherRoot)
{
    private static readonly (string Id, string Name, string[] Paths, bool Recommended)[] Common =
    [
        ("maps", "地图、矿脉和神秘节点", ["journeymap", "visualprospecting", "TCNodeTracker"], true),
        ("language", "游戏语言（只修改 lang）", ["options.txt"], true),
        ("settings", "完整个人设置（按键、画面等，可能覆盖新版默认值）", ["options.txt", "optionsnf.txt", "localconfig.cfg"], false),
        ("servers", "服务器列表", ["servers.dat"], true),
        ("personal", "植物魔法记录与售货机收藏", ["BotaniaVars.dat", "config/vendingmachine/favourites"], true),
        ("schematics", "建筑投影", ["schematics"], true),
        ("screenshots", "截图", ["screenshots"], false),
        ("shaders", "光影包及设置", ["shaderpacks", "config/shaders.properties"], false),
        ("backups", "历史备份（保留旧实例时通常无需复制）", ["backups"], false)
    ];

    /// <summary>从旧实例列出实际存在的组；每个世界独立选择，服务器模式只推荐所需客户端记录。</summary>
    internal IReadOnlyList<MigrationGroup> Groups(string source, GtnhMigrationMode mode, CancellationToken token = default)
    {
        var info = Require(source);
        var groups = Common.Where(group => group.Paths.Any(path => Exists(info.GameDirectory, path)))
            .Select(group => new MigrationGroup(group.Id, group.Name,
                group.Recommended && (mode == GtnhMigrationMode.FullClient || group.Id is "maps" or "language"))).ToList();
        var saves = MigrationIO.SafePath(info.GameDirectory, "saves");
        if (Directory.Exists(saves))
            foreach (var directory in Directory.EnumerateDirectories(saves).Order(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (groups.Count > 4096) throw new IOException("世界目录数量过多，请先整理旧实例。");
                var name = InstanceImportPaths.InstanceName(Path.GetFileName(directory));
                MigrationIO.SafePath(info.GameDirectory, "saves/" + name);
                if (name.Equals("NEI", StringComparison.OrdinalIgnoreCase)) continue;
                groups.Add(new("world:" + name, "世界：" + name, mode == GtnhMigrationMode.FullClient));
            }
        if (Exists(info.GameDirectory, mode == GtnhMigrationMode.ServerClient ? "saves/NEI/global" : "saves/NEI"))
            groups.Add(new("nei", mode == GtnhMigrationMode.ServerClient ? "NEI 全局记录" : "NEI 记录", true));
        groups.AddRange(GtnhExtraModCatalog.Groups(info, token));
        return groups;
    }

    /// <summary>以源文件和目标原内容的摘要固定预览；执行时必须重新生成相同计划。</summary>
    internal async Task<InstanceMigrationPlan> BuildAsync(InstanceMigrationRequest request, CancellationToken token)
    {
        if (!Enum.IsDefined(request.Mode) || !request.TargetTested || !request.GamesClosed)
            throw new InvalidOperationException("请确认新版已成功运行，且两个实例的游戏均已关闭。");
        var source = Require(request.SourceInstance); var target = Require(request.TargetInstance);
        if (source.InstancePath.Equals(target.InstancePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("必须选择两个不同实例。");
        var sourceVersion = Version.Parse(string.Join('.', source.Version.Split('.', '-').Take(3)));
        var targetVersion = Version.Parse(string.Join('.', target.Version.Split('.', '-').Take(3)));
        if (targetVersion < sourceVersion) throw new InvalidOperationException("接收实例的 GTNH 基础版本低于旧实例；不支持存档降级迁移，请检查方向。");
        var known = Groups(source.InstancePath, request.Mode, token).Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        if (request.Groups.Count == 0 || request.Groups.Distinct().Count() != request.Groups.Count || request.Groups.Any(group => !known.Contains(group)))
            throw new InvalidDataException("请选择有效且不重复的数据组。");
        var includesMods = request.Groups.Any(GtnhExtraModPolicy.IsGroup);
        if (includesMods && !request.ExtraModsRiskAccepted)
            throw new InvalidOperationException("请先确认所选模组为自己额外添加，并接受与新版不兼容的风险。");
        var notes = new List<string> { "仅迁移所选玩家数据、语言偏好及明确勾选的额外模组，不复制配方脚本、旧版汉化覆盖文件或运行配置。",
            "文件迁移不代表存档兼容；GTNH 大跨度更新请逐个大版本进入存档、退出后再继续。" };
        var sourceManifest = InstanceManifestStore.Read(Path.Combine(source.InstancePath, InstanceImportManifest.FileName));
        var targetManifest = InstanceManifestStore.Read(Path.Combine(target.InstancePath, InstanceImportManifest.FileName));
        var sourceContent = AddedContentStore.Read(source.InstancePath, sourceManifest);
        var targetContent = AddedContentStore.Read(target.InstancePath, targetManifest);
        var sourceRequirements = includesMods ? InstanceEffectiveRequirements.Build(source.InstancePath, sourceManifest).Requirements : [];
        var sourceModState = ModManagementStore.Read(source.InstancePath, sourceManifest);
        var sourceMigratedMods = includesMods ? MigrationDataStore.Read(source.InstancePath, sourceManifest).Files
            .Where(file => file.Rule == GtnhExtraModPolicy.Rule).ToArray() : [];
        var targetMods = includesMods ? await GtnhExtraModCatalog.SnapshotAsync(target.GameDirectory, token) : [];
        var selectedMods = new List<GtnhExtraModCatalog.Mod>();
        var declaredMods = includesMods ? GtnhExtraModCatalog.DeclaredPaths(target.InstancePath, targetManifest) : [];
        if (includesMods) notes.Add(GtnhExtraModPolicy.Warning + " 同名、相同内容或已识别为相同 modid 的模组始终跳过，不受覆盖开关影响。未提供可读 modid 时无法排除不同文件名的版本冲突。");
        var files = new List<MigrationFile>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var worldDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in request.Groups.Order(StringComparer.Ordinal))
        {
            if (GtnhExtraModPolicy.IsGroup(group))
            {
                if (files.Count >= MigrationIO.MaximumFiles) throw new IOException("迁移文件数量过多。");
                var path = group[GtnhExtraModPolicy.GroupPrefix.Length..];
                var mod = await GtnhExtraModCatalog.ReadAsync(source.GameDirectory, path, token);
                // 已管理内容不能通过迁移重新获得信任；只接受原基线或此前明确确认的替换。
                var sourcePrefix = sourceManifest.GameDirectory == "." ? "" : sourceManifest.GameDirectory + "/";
                var sourceIdentity = LocalModPathPolicy.EnabledPath(sourcePrefix + path);
                var managed = sourceModState.Files.SingleOrDefault(file => LocalModPathPolicy.EnabledPath(file.Baseline.Path!)
                    .Equals(sourceIdentity, StringComparison.OrdinalIgnoreCase));
                var reservedPath = managed is null ? sourceMigratedMods.SingleOrDefault(file => LocalModPathPolicy.EnabledPath(file.Path)
                    .Equals(sourceIdentity, StringComparison.OrdinalIgnoreCase))?.Path : ModManagementStore.CurrentPath(managed);
                if (managed?.State == LocalModState.Removed || reservedPath is not null &&
                    !reservedPath.Equals(sourcePrefix + path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("该文件是已管理模组的未登记副本，不能通过迁移接受其内容：" + path);
                var trusted = sourceRequirements.SingleOrDefault(file =>
                    string.Equals(file.Path, sourcePrefix + path, StringComparison.OrdinalIgnoreCase));
                if (trusted?.Origin == InstanceFileOrigin.UserLocalImport &&
                    (trusted.Size != mod.Size || !trusted.Hashes.TryGetValue("sha256", out var trustedHash) ||
                     !trustedHash.Equals(mod.Sha256, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("额外模组与已确认内容不一致，请先处理本地修改：" + path);
                var trackedOnline = managed is not null || sourceMigratedMods.Any(file => file.OnlineModBaseline is not null &&
                    file.Path.Equals(sourcePrefix + path, StringComparison.OrdinalIgnoreCase));
                var online = trackedOnline && trusted?.Origin is InstanceFileOrigin.CurseForgeDownload or InstanceFileOrigin.ModrinthDownload or InstanceFileOrigin.GithubReleaseDownload
                    ? trusted : null;
                if (online is not null && !await Runtime.InstanceRuntimeInstaller.ExistingFileMatchesAsync(
                    MigrationIO.SafePath(source.GameDirectory, path), online, token).ConfigureAwait(false))
                    throw new IOException("在线模组与官方内容不一致，不能通过迁移接受修改：" + path);
                var previous = await MigrationIO.SnapshotAsync(MigrationIO.SafePath(target.GameDirectory, path), token);
                var collision = GtnhExtraModCatalog.Collision(mod, targetMods.Concat(selectedMods), declaredMods);
                var action = collision is null ? MigrationFileAction.Copy : MigrationFileAction.Skip;
                if (!destinations.Add(path)) throw new InvalidDataException("迁移目标重复。");
                files.Add(new(group, path, path, mod.Size, mod.Sha256, mod.Size, mod.Sha256,
                    previous?.Size, previous?.Hash, action)
                { OnlineModBaseline = online,
                    Detail = collision ?? (mod.Ids.Count == 0 ? "未提供可读 modid，请自行确认没有不同文件名的重复模组。" : "需自行确认版本兼容性；只复制所选文件。") });
                if (action == MigrationFileAction.Copy) selectedMods.Add(mod);
                continue;
            }
            string[] paths;
            string? worldTarget = null;
            if (group.StartsWith("world:", StringComparison.Ordinal))
            {
                var name = InstanceImportPaths.InstanceName(group[6..]); paths = ["saves/" + name];
                worldTarget = paths[0];
                for (var suffix = 1; Exists(target.GameDirectory, worldTarget) || worldDestinations.Contains(worldTarget); suffix++)
                {
                    if (suffix > 1000) throw new IOException("没有可用的世界副本名称。");
                    worldTarget = "saves/" + name + " (迁入 " + suffix + ")";
                }
                worldDestinations.Add(worldTarget);
                if (worldTarget != paths[0]) notes.Add(paths[0] + " → " + worldTarget + "，保留新版已有世界。");
            }
            else if (group == "nei") paths = [request.Mode == GtnhMigrationMode.ServerClient ? "saves/NEI/global" : "saves/NEI"];
            else paths = Common.Single(value => value.Id == group).Paths;
            // 完整 options 已由用户选中时，不再叠加一次语言项复制。
            if (group == "language" && request.Groups.Contains("settings")) continue;
            foreach (var prefix in paths)
                foreach (var sourcePath in MigrationIO.Files(source.GameDirectory, prefix, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (!GtnhPlayerDataPolicy.Allows(sourcePath)) throw new InvalidDataException("文件不在玩家数据范围：" + sourcePath);
                    if (files.Count >= MigrationIO.MaximumFiles) throw new IOException("迁移文件数量过多。");
                    var destination = worldTarget is null ? sourcePath : worldTarget + sourcePath[prefix.Length..];
                    if (!destinations.Add(destination)) throw new InvalidDataException("迁移目标重复。");
                    var from = MigrationIO.SafePath(source.GameDirectory, sourcePath);
                    var original = await MigrationIO.SnapshotAsync(from, token) ?? throw new IOException("源文件已消失。");
                    // 官方资源仍保留平台基线，不借玩家数据迁移把当前字节重新认可为可变内容。
                    // 在读取目标前排除受保护文件和父子路径冲突，避免把新版 ZIP 当成目录继续访问。
                    var protectedTarget = ContentOwnsPath(targetContent, targetManifest, destination);
                    var protectedSource = ContentOwnsPath(sourceContent, sourceManifest, sourcePath);
                    if (protectedTarget || protectedSource)
                    {
                        var reason = protectedTarget ? "新版此路径已登记官方资源，保留现有来源；覆盖冲突开关不会替换它。" :
                            "旧版此文件是已登记的官方资源，请在新实例重新选择适配版本下载；不转为玩家数据或重新建立摘要。";
                        files.Add(new(group, sourcePath, destination, original.Size, original.Hash,
                            original.Size, original.Hash, null, null, MigrationFileAction.Skip) { Detail = reason });
                        notes.Add(sourcePath + "：" + reason);
                        continue;
                    }
                    var to = MigrationIO.SafePath(target.GameDirectory, destination);
                    var previous = await MigrationIO.SnapshotAsync(to, token);
                    var expected = original;
                    if (group == "language")
                    {
                        if (original.Size > 1024 * 1024 || previous?.Size > 1024 * 1024) throw new InvalidDataException("options.txt 过大。");
                        var merged = MigrationIO.MergeLanguage(await File.ReadAllBytesAsync(from, token), previous is null ? [] : await File.ReadAllBytesAsync(to, token));
                        expected = (merged.LongLength, Convert.ToHexStringLower(SHA256.HashData(merged)));
                    }
                    var action = previous is null ? MigrationFileAction.Copy : previous == expected ? MigrationFileAction.Identical : MigrationFileAction.Conflict;
                    // 修改语言是单独明确选择的键级合并，不需要把整个 options 当成覆盖冲突。
                    if (group == "language" && action == MigrationFileAction.Conflict) action = MigrationFileAction.Copy;
                    files.Add(new(group, sourcePath, destination, expected.Size, expected.Hash,
                        original.Size, original.Hash, previous?.Size, previous?.Hash, action));
                }
        }
        if (files.Count == 0) throw new InvalidOperationException("选中的项目没有可迁移文件。");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request, source, target, files, TargetMods = targetMods, DeclaredMods = declaredMods, Source = EmbeddedFileBaseline.ManifestFingerprint(sourceManifest),
            Target = EmbeddedFileBaseline.ManifestFingerprint(targetManifest), Data = MigrationDataStore.Read(target.InstancePath, targetManifest),
            SourceModState = sourceModState,
            TargetModState = ModManagementStore.Read(target.InstancePath, targetManifest),
            SourceAddedContent = sourceContent, TargetAddedContent = targetContent,
            Replacement = InstanceLocalReplacementService.ApplyWithSnapshot(target.InstancePath, targetManifest, targetManifest.Files).ReplacementFingerprint
        })));
        return new(request, source, target, files, notes, fingerprint);
    }

    /// <summary>把游戏相对位置映射到来源清单，保护官方资源及其父子路径，不按文件名猜测来源。</summary>
    private static bool ContentOwnsPath(AddedContentStore.Snapshot snapshot, InstanceImportManifest manifest, string gamePath)
    {
        var relative = (manifest.GameDirectory == "." ? "" : manifest.GameDirectory + "/") + gamePath;
        return snapshot.Files.Any(record => record.Baseline.Path is { } path &&
            (path.Equals(relative, StringComparison.OrdinalIgnoreCase) || path.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase) ||
             relative.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>未识别实例不得凭名称进入 GTNH 文件迁移管线。</summary>
    private GtnhInstanceInfo Require(string path)
    {
        var root = EmbeddedFileBaseline.ValidateInstance(launcherRoot, path);
        ModOperationJournal.EnsureSettled(root);
        return GtnhIdentity.Read(launcherRoot, root) ??
            throw new InvalidOperationException("未确认此实例为受支持的 GTNH：需要 1.7.10、GTNH 包内版本标识及配置特征。");
    }
    /// <summary>只检查明确路径，链接或异常父目录仍拒绝。</summary>
    private static bool Exists(string game, string relative) { var path = MigrationIO.SafePath(game, relative); return File.Exists(path) || Directory.Exists(path); }
}
