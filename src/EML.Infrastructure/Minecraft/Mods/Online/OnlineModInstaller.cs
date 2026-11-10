using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods;
using EML.Core.Minecraft.Mods.Online;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;

namespace EML.Infrastructure.Minecraft.Mods.Online;

/// <summary>先解析可审阅的固定文件计划，再校验下载并通过本地模组事务登记来源。</summary>
public sealed class OnlineModInstaller : IOnlineModInstaller
{
    private const int MaximumProjects = 128;
    private readonly string _launcherRoot;
    private readonly IOnlineModCatalog _catalog;
    private readonly IFileDownloader _downloader;
    private readonly IOnlineModRegistrar _registrar;
    private readonly ConcurrentDictionary<string, IssuedPlan> _plans = new();
    private sealed record Context(string Root, InstanceImportManifest Manifest, InstanceEffectiveRequirements.Snapshot Effective, string Loader);
    private sealed record IssuedPlan(string Fingerprint, Context Context, DateTimeOffset Created);

    /// <summary>复用通用下载器和本地管理事务，不在搜索 API 中写入实例。</summary>
    public OnlineModInstaller(string launcherRoot, IOnlineModCatalog catalog, IFileDownloader downloader, IOnlineModRegistrar registrar)
    {
        _launcherRoot = Path.GetFullPath(launcherRoot);
        _catalog = catalog;
        _downloader = downloader;
        _registrar = registrar;
    }

    /// <summary>只读取实例与远程元数据；可选项和必要依赖开关共同决定计划中的固定文件。</summary>
    public async Task<OnlineModInstallPlan> PlanAsync(OnlineModPlanRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OptionalDependencyKeys is null || request.OptionalDependencyKeys.Count > MaximumProjects ||
            string.IsNullOrWhiteSpace(request.VersionId) || string.IsNullOrWhiteSpace(request.FileName))
            throw new ArgumentException("请选择具体模组版本与文件。");
        // 清单读取和候选文件摘要放在线程池，避免大型实例阻塞 WPF。
        var context = await Task.Run(() => ReadContext(request.InstancePath), token).ConfigureAwait(false);
        var items = new List<OnlineModPlanItem>();
        var optional = new Dictionary<string, OnlineModDependency>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var visited = new Dictionary<OnlineProjectId, string>();
        var selectedOptional = request.OptionalDependencyKeys.ToHashSet(StringComparer.Ordinal);
        var rootVersion = await _catalog.GetVersionAsync(request.Project, request.VersionId, token).ConfigureAwait(false);
        await VisitAsync(rootVersion, request.FileName, "主动选择", 0).ConfigureAwait(false);
        foreach (var stale in selectedOptional.Except(optional.Keys, StringComparer.Ordinal))
            warnings.Add("先前选择的可选依赖已不在当前依赖关系中，本次不会安装：" + stale);
        InstanceEffectiveRequirements.EnsureUnchanged(context.Root, context.Manifest, context.Effective);
        var plan = new OnlineModInstallPlan
        {
            Id = Guid.NewGuid().ToString("N"), Request = request with { InstancePath = context.Root, OptionalDependencyKeys = selectedOptional.ToArray() },
            Items = items.ToArray(), OptionalDependencies = optional.Values.ToArray(), Warnings = warnings.Distinct().ToArray()
        };
        foreach (var stale in _plans.Where(pair => DateTimeOffset.UtcNow - pair.Value.Created > TimeSpan.FromMinutes(30)).Select(pair => pair.Key))
            _plans.TryRemove(stale, out _);
        if (_plans.Count >= 32)
            foreach (var old in _plans.OrderBy(pair => pair.Value.Created).Take(_plans.Count - 31).Select(pair => pair.Key)) _plans.TryRemove(old, out _);
        _plans[plan.Id] = new(Fingerprint(plan), context, DateTimeOffset.UtcNow);
        return plan;

        // 已安装项也展开固定版本的依赖；文件本身沿用经过校验的本地基线，不重新解析下载。
        async Task VisitAsync(OnlineModVersion version, string? chosenFile, string reason, int depth,
            InstanceFileRequirement? installedFile = null)
        {
            token.ThrowIfCancellationRequested();
            if (visited.TryGetValue(version.Project, out var seen))
            {
                if (seen != version.Id) throw new InvalidOperationException("同一项目要求了不同固定版本，无法生成单一安装计划：" + version.Project.ProjectId);
                return;
            }
            if (depth > 32 || visited.Count >= MaximumProjects) throw new InvalidDataException("依赖数量或深度过大，请分批选择模组。");
            visited.Add(version.Project, version.Id);
            OnlineModPlanItem item;
            if (installedFile is not null)
            {
                var file = new OnlineModFile { FileName = Path.GetFileName(installedFile.Path!), Requirement = installedFile, Primary = true };
                item = new(version, file, "已满足的依赖", OnlineModPlanStatus.AlreadyInstalled,
                    "已经存在并通过来源校验，安装时将再次确认。");
            }
            else
            {
                RequireCompatible(version, context);
                var file = ChooseFile(version, chosenFile);
                file = await _catalog.ResolveDownloadAsync(version, file, token).ConfigureAwait(false);
                var relative = LocalModPaths.NewPath(context.Manifest, file.FileName);
                if (relative.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("在线下载文件不能伪装成禁用状态。");
                file = file with { Requirement = file.Requirement with { Path = relative, Required = true, Role = InstanceFileRole.Content } };
                item = await InspectTargetAsync(context, version, file, reason, token).ConfigureAwait(false);
            }
            foreach (var dependency in version.Dependencies)
            {
                token.ThrowIfCancellationRequested();
                if (dependency.Kind == OnlineDependencyKind.Optional) optional.TryAdd(dependency.Key, dependency);
                if (dependency.Kind is OnlineDependencyKind.Incompatible or OnlineDependencyKind.Embedded) continue;
                var required = dependency.Kind == OnlineDependencyKind.Required;
                var install = required ? request.IncludeRequired : selectedOptional.Contains(dependency.Key);
                if (!install && !required) continue;
                if (await FindDependencyAsync(context, dependency, token).ConfigureAwait(false) is { } existing)
                {
                    var identity = dependency.Project!;
                    var fixedId = identity.Provider == ModProvider.CurseForge
                        ? (existing.FileId ?? existing.CurseForgeOrigin?.FileId)!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : existing.ModrinthOrigin!.VersionId;
                    if (visited.TryGetValue(identity, out var existingId))
                    {
                        if (existingId != fixedId)
                            throw new InvalidOperationException("计划选择的版本与现有依赖版本不同，请先处理本地模组：" + DependencyName(dependency));
                        continue;
                    }
                    // 只读取本地记录的固定版本依赖，不以最新版本或新的摘要替换已有文件。
                    var existingVersion = await _catalog.GetVersionAsync(identity, fixedId, token).ConfigureAwait(false);
                    if (existingVersion.Project != identity || existingVersion.Id != fixedId)
                        throw new InvalidDataException("已有依赖返回的项目或固定版本身份不一致：" + DependencyName(dependency));
                    await VisitAsync(existingVersion, null, "已满足的依赖", depth + 1, existing).ConfigureAwait(false);
                    continue;
                }
                if (!install)
                {
                    warnings.Add("未自动加入必要依赖：" + DependencyName(dependency) + "。需要自行安装，否则游戏可能无法启动。");
                    continue;
                }
                if (dependency.Project is null)
                    throw new InvalidOperationException("依赖缺少可验证的平台项目编号：" + DependencyName(dependency));
                var dependencyVersion = await ResolveDependencyAsync(context, dependency, token).ConfigureAwait(false);
                await VisitAsync(dependencyVersion, null, required ? "必要依赖：" + version.Name : "用户选择的可选依赖", depth + 1).ConfigureAwait(false);
            }
            // 必要依赖先于依赖它的主文件安装，失败时不报告整组完成。
            if (items.Any(existing => existing.File.Requirement.Path!.Equals(item.File.Requirement.Path, StringComparison.OrdinalIgnoreCase) && existing.Version.Project != version.Project))
                item = item with { Status = OnlineModPlanStatus.Blocked, Message = "不同项目使用相同目标文件名，不能覆盖。" };
            items.Add(item);
        }
    }

    /// <summary>只接受本服务签发且未被改写的计划；部分完成时保留成功文件并明确报告未完成项。</summary>
    public async Task<OnlineModInstallResult> InstallAsync(OnlineModInstallPlan plan,
        IProgress<OnlineModInstallProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!_plans.TryRemove(plan.Id, out var issued) || issued.Fingerprint != Fingerprint(plan) ||
            DateTimeOffset.UtcNow - issued.Created > TimeSpan.FromMinutes(30))
            throw new InvalidOperationException("安装计划已过期、被修改或已经使用，请重新生成清单。");
        if (!plan.CanInstall) throw new InvalidOperationException("安装清单仍有阻断项，请先处理后重新生成。");
        var installed = 0;
        var skipped = 0;
        var issues = new List<string>();
        string? staging = null;
        var created = false;
        try
        {
            token.ThrowIfCancellationRequested();
            using (InstanceOperationLease.Acquire(issued.Context.Root))
            {
                InstanceEffectiveRequirements.EnsureUnchanged(issued.Context.Root, issued.Context.Manifest, issued.Context.Effective);
                // 预览到确认之间磁盘可能被外部程序改写，先核实所有已满足依赖再安装任何新文件。
                foreach (var existing in plan.Items.Where(item => item.Status == OnlineModPlanStatus.AlreadyInstalled))
                    if (!await MatchesAsync(MigrationIO.SafePath(issued.Context.Root, existing.File.Requirement.Path!), existing.File.Requirement, token).ConfigureAwait(false))
                        throw new IOException("预览后已有模组发生变化，请重新生成计划：" + existing.File.FileName);
            }
            var stagingRelative = ".eml-runtime/online-mods/" + plan.Id;
            staging = MigrationIO.SafePath(_launcherRoot, stagingRelative);
            if (Directory.Exists(staging) || File.Exists(staging)) throw new IOException("本次下载暂存位置已被占用。");
            Directory.CreateDirectory(staging);
            created = true;
            var completed = 0;
            foreach (var item in plan.Items)
            {
                token.ThrowIfCancellationRequested();
                EmbeddedFileBaseline.EnsureManifestUnchanged(issued.Context.Root, issued.Context.Manifest);
                if (item.Status == OnlineModPlanStatus.AlreadyInstalled)
                {
                    if (!await MatchesAsync(MigrationIO.SafePath(issued.Context.Root, item.File.Requirement.Path!), item.File.Requirement, token).ConfigureAwait(false))
                        throw new IOException("预览后已有模组发生变化，请重新生成计划：" + item.File.FileName);
                    skipped++; completed++;
                    continue;
                }
                var target = MigrationIO.SafePath(_launcherRoot, stagingRelative + "/" + completed.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jar");
                var file = item.File.Requirement;
                var index = completed;
                var active = 1;
                try
                {
                    Report("正在下载", 0);
                    await _downloader.DownloadAsync(new FileDownloadRequest
                    {
                        DestinationPath = target, Urls = file.Downloads, ExpectedSize = file.Size,
                        Hashes = InstanceFileIntegrity.HashesFor(file), MaximumBytes = SafeInstanceArchive.MaxUncompressedBytes,
                        DisplayName = item.File.FileName, GroupName = Path.GetFileName(issued.Context.Root) + " · 在线模组"
                    }, new InlineProgress<FileDownloadProgress>(value =>
                    {
                        if (Volatile.Read(ref active) == 1) Report("正在下载", value.BytesReceived);
                    }), token).ConfigureAwait(false);
                    Report("正在校验", file.Size ?? 0);
                    if (!await MatchesAsync(target, file, token).ConfigureAwait(false))
                        throw new InvalidDataException("下载内容没有通过平台大小与哈希校验：" + item.File.FileName);
                    Report("正在安装", file.Size ?? 0);
                    await _registrar.RegisterDownloadedAsync(issued.Context.Root, target, file, token).ConfigureAwait(false);
                    installed++; completed++;
                    Report("已安装", file.Size ?? 0);
                }
                finally
                {
                    Interlocked.Exchange(ref active, 0);
                    InstanceImportPaths.EnsureNoLinks(target);
                    if (File.Exists(target)) File.Delete(target);
                }

                void Report(string stage, long received) => progress?.Report(new(stage, item.File.FileName,
                    index, plan.Items.Count, received, file.Size));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(installed, skipped, true, ["已取消；已经安装的文件保留，其余文件未安装。"]); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or HttpRequestException or TimeoutException)
        { issues.Add(ex.Message); }
        finally
        {
            if (created && staging is not null)
            {
                try
                {
                    InstanceImportPaths.EnsureNoLinks(staging);
                    // 只移除本次独占创建且已清空的目录，不递归删除任何实例或缓存。
                    if (Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.TraceWarning("在线模组暂存目录清理未完成：{0}", ex.Message); }
            }
        }
        return new(installed, skipped, false, issues);
    }

    /// <summary>安装兼容条件读取可信导入清单，不能由搜索框里临时输入的版本绕过。</summary>
    private Context ReadContext(string instancePath)
    {
        var root = EmbeddedFileBaseline.ValidateInstance(_launcherRoot, instancePath);
        using var lease = InstanceOperationLease.Acquire(root);
        var manifest = InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName));
        if (!LocalModPaths.CanWrite(manifest) || string.IsNullOrWhiteSpace(manifest.MinecraftVersion))
            throw new InvalidOperationException("请先选择已识别 Minecraft 版本及模组加载器的实例。");
        var loaders = manifest.Components.Select(component => component.Id.ToLowerInvariant() switch
        {
            "forge" or "net.minecraftforge" => "forge", "neoforge" or "net.neoforged" => "neoforge",
            "fabric" or "fabric-loader" or "net.fabricmc.fabric-loader" => "fabric",
            "quilt" or "quilt-loader" or "org.quiltmc.quilt-loader" => "quilt", _ => ""
        }).Where(value => value.Length > 0).Distinct().ToArray();
        if (loaders.Length != 1) throw new InvalidOperationException("实例没有唯一明确的模组加载器，暂时无法自动安装在线模组。");
        var effective = InstanceEffectiveRequirements.Build(root, manifest);
        if (effective.Requirements.Any(file => file.Path is null))
            throw new InvalidOperationException("请先补全实例中尚未解析的原包文件，再添加在线模组。");
        return new(root, manifest, effective, loaders[0]);
    }

    /// <summary>仅使用平台声明的相同游戏版本和加载器，不推测跨版本兼容。</summary>
    private static void RequireCompatible(OnlineModVersion version, Context context)
    {
        if (!version.GameVersions.Contains(context.Manifest.MinecraftVersion!, StringComparer.OrdinalIgnoreCase) ||
            !version.Loaders.Contains(context.Loader, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"所选文件未声明支持此实例的 Minecraft {context.Manifest.MinecraftVersion} / {context.Loader}：{version.Name}");
    }

    /// <summary>根项目使用明确选中的文件；依赖只使用唯一主文件或唯一可用文件。</summary>
    private static OnlineModFile ChooseFile(OnlineModVersion version, string? chosen)
    {
        var files = version.Files.Where(file => file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (chosen is not null)
            return files.SingleOrDefault(file => file.FileName == chosen) ?? throw new InvalidOperationException("所选文件已不在固定版本中，请刷新详情。");
        var primary = files.Where(file => file.Primary).ToArray();
        return primary.Length == 1 ? primary[0] : files.Length == 1 ? files[0] :
            throw new InvalidOperationException("依赖有多个文件且没有唯一主文件，请先单独选择安装：" + version.Name);
    }

    /// <summary>已有相同内容才标记跳过；禁用、主动移除、原包占位及文件名冲突不能自动覆盖。</summary>
    private static async Task<OnlineModPlanItem> InspectTargetAsync(Context context, OnlineModVersion version, OnlineModFile file,
        string reason, CancellationToken token)
    {
        var path = file.Requirement.Path!;
        var managed = context.Effective.Mods.Files.FirstOrDefault(record => SameProject(record.Baseline, version.Project) ||
            ModArchiveInspector.Name(record.Baseline.Path!).Equals(file.FileName, StringComparison.OrdinalIgnoreCase));
        if (managed is { State: not LocalModState.Enabled })
            return new(version, file, reason, OnlineModPlanStatus.Blocked, "此模组已被用户禁用或移除，请在本地模组页明确恢复后再安装。");
        foreach (var existing in context.Effective.Requirements.Where(existing => existing.Path is not null && LocalModPaths.Allows(context.Manifest, existing.Path)))
        {
            var sameName = ModArchiveInspector.Name(existing.Path!).Equals(file.FileName, StringComparison.OrdinalIgnoreCase);
            var sameHash = existing.Size == file.Requirement.Size && existing.Hashes.Any(pair =>
                file.Requirement.Hashes.TryGetValue(pair.Key, out var hash) && hash.Equals(pair.Value, StringComparison.OrdinalIgnoreCase));
            if (!sameName && !sameHash && !SameProject(existing, version.Project)) continue;
            if (!existing.Path!.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) &&
                await MatchesAsync(MigrationIO.SafePath(context.Root, existing.Path!), file.Requirement, token).ConfigureAwait(false))
                return new(version, file with { Requirement = file.Requirement with { Path = existing.Path } }, reason,
                    OnlineModPlanStatus.AlreadyInstalled, "已存在并通过官方哈希校验，将跳过下载。");
            return new(version, file, reason, OnlineModPlanStatus.Blocked, "实例已有同名、同项目或禁用模组，需在本地模组页处理，在线安装不会替换原文件。");
        }
        if (File.Exists(MigrationIO.SafePath(context.Root, path)) || File.Exists(MigrationIO.SafePath(context.Root, path + ".disabled")) ||
            Directory.Exists(MigrationIO.SafePath(context.Root, path)))
            return new(version, file, reason, OnlineModPlanStatus.Blocked, "目标位置已有未登记内容，请先在本地模组页确认来源。");
        if (file.Problem is not null || file.Requirement.Downloads.Count == 0)
            return new(version, file, reason, OnlineModPlanStatus.Blocked, file.Problem ?? "没有可验证的自动下载地址，请从官方页面获取文件。");
        _ = InstanceFileIntegrity.HashesFor(file.Requirement);
        if (file.Requirement.Size is null or < 0 || file.Requirement.Hashes.Count == 0)
            throw new InvalidDataException("在线模组缺少可信的大小或官方摘要。");
        return new(version, file, reason, OnlineModPlanStatus.Download);
    }

    /// <summary>仅在已登记的固定项目身份与可用文件均满足时跳过依赖，不以相似名称认定满足。</summary>
    private static async Task<InstanceFileRequirement?> FindDependencyAsync(Context context, OnlineModDependency dependency, CancellationToken token)
    {
        if (dependency.Project is null) return null;
        foreach (var existing in context.Effective.Requirements.Where(file => file.Path is not null &&
            LocalModPaths.Allows(context.Manifest, file.Path) &&
            file.Origin is InstanceFileOrigin.CurseForgeDownload or InstanceFileOrigin.ModrinthDownload &&
            !file.Path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) && SameProject(file, dependency.Project)))
        {
            if (dependency.VersionId is { } fixedId && fixedId != (dependency.Project.Provider == ModProvider.CurseForge
                    ? (existing.FileId ?? existing.CurseForgeOrigin?.FileId)?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : existing.ModrinthOrigin?.VersionId)) continue;
            if (await MatchesAsync(MigrationIO.SafePath(context.Root, existing.Path!), existing, token).ConfigureAwait(false)) return existing;
        }
        return null;
    }

    /// <summary>固定版本依赖不升级；项目依赖优先稳定兼容版本，预览后锁定该版本。</summary>
    private async Task<OnlineModVersion> ResolveDependencyAsync(Context context, OnlineModDependency dependency, CancellationToken token)
    {
        if (dependency.VersionId is { } fixedId)
            return await _catalog.GetVersionAsync(dependency.Project!, fixedId, token).ConfigureAwait(false);
        OnlineModVersion? selected = null;
        // 有界翻页避免首页过滤为空误判，也避免递归依赖产生无限远程请求。
        for (var page = 0; page < 10; page++)
        {
            var result = await _catalog.GetVersionsAsync(dependency.Project!, context.Manifest.MinecraftVersion!, context.Loader, page, token).ConfigureAwait(false);
            var versions = result.Items.Where(version => version.GameVersions.Contains(context.Manifest.MinecraftVersion!, StringComparer.OrdinalIgnoreCase) &&
                version.Loaders.Contains(context.Loader, StringComparer.OrdinalIgnoreCase)).OrderByDescending(version => version.Published).ToArray();
            var release = versions.FirstOrDefault(version => version.Channel.Equals("release", StringComparison.OrdinalIgnoreCase));
            if (release is not null) { selected = release; break; }
            selected ??= versions.FirstOrDefault();
            if (!result.HasMore) break;
        }
        if (selected is null) throw new InvalidOperationException("在前 10 页中没有找到兼容版本的依赖，请手动选择：" + DependencyName(dependency));
        return await _catalog.GetVersionAsync(selected.Project, selected.Id, token).ConfigureAwait(false);
    }

    /// <summary>按平台固定编号匹配，备用下载仍承认原 CurseForge 身份。</summary>
    private static bool SameProject(InstanceFileRequirement file, OnlineProjectId project) => project.Provider switch
    {
        ModProvider.CurseForge => long.TryParse(project.ProjectId, out var id) && (file.ProjectId == id || file.CurseForgeOrigin?.ProjectId == id),
        ModProvider.Modrinth => file.ModrinthOrigin?.ProjectId == project.ProjectId,
        _ => false
    };

    /// <summary>对本次候选文件执行全部自身摘要校验，不读取其他来源的元数据摘要。</summary>
    internal static async Task<bool> MatchesAsync(string path, InstanceFileRequirement expected, CancellationToken token)
    {
        InstanceImportPaths.EnsureNoLinks(path);
        if (!File.Exists(path) || expected.Size is null || expected.Hashes.Count == 0) return false;
        var hashes = InstanceFileIntegrity.HashesFor(expected);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        if (input.Length != expected.Size) return false;
        foreach (var pair in hashes)
        {
            input.Position = 0;
            var hash = pair.Key switch
            {
                "sha1" => await SHA1.HashDataAsync(input, token).ConfigureAwait(false),
                "sha256" => await SHA256.HashDataAsync(input, token).ConfigureAwait(false),
                "sha512" => await SHA512.HashDataAsync(input, token).ConfigureAwait(false),
                _ => throw new InvalidDataException("不支持的文件校验算法。")
            };
            if (!Convert.ToHexStringLower(hash).Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>未知名称时显示稳定编号，不能用空文本隐藏未满足依赖。</summary>
    private static string DependencyName(OnlineModDependency dependency) => string.IsNullOrWhiteSpace(dependency.Name)
        ? dependency.Project?.ProjectId ?? dependency.VersionId ?? dependency.Key : dependency.Name;

    /// <summary>绑定计划中的所有文件和用户选择，防止确认后替换 URL、摘要或目标实例。</summary>
    private static string Fingerprint(OnlineModInstallPlan plan) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan)));

    /// <summary>下载进度直接转交调用者，由界面的 Progress 负责线程切换。</summary>
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
