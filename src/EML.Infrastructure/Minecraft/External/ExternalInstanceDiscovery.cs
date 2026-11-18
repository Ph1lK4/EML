using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.External;

/// <summary>在常见启动器位置或用户选择目录内有界发现实例；不全盘搜索、不联网、不改源文件。</summary>
public sealed class ExternalInstanceDiscovery : IExternalInstanceDiscovery
{
    private static readonly string[] InstanceContainers = ["instances", "Instances", "profiles", "Profiles"];
    private static readonly string[] Containers = ["instances", "Instances", "profiles", "Profiles", "minecraft", "Minecraft", ".minecraft", "data", "Data"];
    private readonly IReadOnlyList<string> _locations;

    /// <summary>默认使用当前用户常见位置；测试可传入明确的隔离目录。</summary>
    public ExternalInstanceDiscovery(IEnumerable<string>? locations = null) =>
        _locations = (locations ?? DefaultLocations()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>后台扫描并逐个报告损坏或不支持的目录，单个失败不阻止其他实例。</summary>
    public Task<ExternalInstanceDiscoveryResult> DiscoverAsync(string? directory = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Discover(directory, cancellationToken), cancellationToken);

    /// <summary>仅检查已知容器最多三层及其直接子实例，限制总目录数量。</summary>
    private ExternalInstanceDiscoveryResult Discover(string? directory, CancellationToken token)
    {
        var result = new List<ExternalMinecraftInstance>();
        var warnings = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth, bool Children)>();
        foreach (var path in directory is null ? _locations : [directory]) queue.Enqueue((path, 0, true));
        while (queue.TryDequeue(out var entry))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Path));
                if (!visited.Add(path)) continue;
                if (visited.Count > 4096) { warnings.Add("候选目录超过 4096 个，已停止扫描；请直接选择目标实例文件夹。"); break; }
                if (!Directory.Exists(path)) continue;
                InstanceImportPaths.EnsureNoLinks(path);
                // 启动器根目录可能同时有共享 versions 或损坏的旧标记；先保留明确的实例容器。
                // 具体子实例不再深入，避免把它的 .minecraft/versions 当成第二个候选。
                if (entry.Children && entry.Depth < 3)
                    foreach (var container in InstanceContainers)
                    {
                        var nested = Path.Combine(path, container);
                        if (Directory.Exists(nested)) queue.Enqueue((nested, entry.Depth + 1, true));
                    }
                var game = Directory.Exists(Path.Combine(path, "versions")) ? path : Path.Combine(path, ".minecraft");
                // EML 清单说明需要保留来源，不说明实例属于当前启动器；由读取器验证并生成保留计划。
                var hasMarker = new[] { InstanceImportManifest.FileName, "mmc-pack.json", "profile.json", "minecraftinstance.json", "instance.json" }
                    .Any(name => File.Exists(Path.Combine(path, name)));
                // config.json 是常见游戏文件，只有同时存在 mods 才作为 GDLauncher 候选，仍由适配器严格确认。
                hasMarker |= File.Exists(Path.Combine(path, "config.json")) && Directory.Exists(Path.Combine(path, "mods"));
                var parent = Path.GetDirectoryName(path);
                hasMarker |= parent is not null && new[] { "profiles", "instances" }.Contains(Path.GetFileName(parent), StringComparer.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(Path.GetDirectoryName(parent)!, "app.db"));
                if (hasMarker || Directory.Exists(Path.Combine(game, "versions")))
                {
                    var definitions = hasMarker
                        ? new[] { ExternalInstanceReader.Read(new(Path.GetFileName(path), "", path, path, null, ""), token) }
                        : ExternalInstanceReader.StandardCandidates(path, game, warnings, token);
                    foreach (var definition in definitions)
                    {
                        var key = definition.Instance.SourcePath + "\n" + definition.Instance.SelectionId;
                        if (instances.Add(key)) result.Add(definition.Instance);
                    }
                    continue;
                }
                if (entry.Depth >= 3) continue;
                foreach (var container in Containers)
                {
                    var nested = Path.Combine(path, container);
                    if (Directory.Exists(nested)) queue.Enqueue((nested, entry.Depth + 1, true));
                }
                if (!entry.Children) continue;
                var count = 0;
                foreach (var child in Directory.EnumerateDirectories(path))
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > 1024) { warnings.Add("目录子项过多，仅扫描前 1024 项：" + path); break; }
                    if (Path.GetFileName(child).StartsWith('.')) continue;
                    queue.Enqueue((child, entry.Depth + 1, false));
                }
            }
            catch (Exception ex) when (Expected(ex)) { warnings.Add(entry.Path + "：" + ex.Message); }
        }
        if (directory is not null && result.Count == 0 && warnings.Count == 0)
            warnings.Add("所选位置未发现可识别的实例；请选择包含 eml-instance.json、mmc-pack.json、profile.json、minecraftinstance.json 或 versions 的目录。");
        return new(result.OrderBy(instance => instance.Launcher).ThenBy(instance => instance.Name).ToArray(), warnings.Distinct().Take(100).ToArray());
    }

    /// <summary>覆盖 Windows 常见安装位置；便携版和更改过的数据目录通过手动选择补充。</summary>
    internal static IEnumerable<string> DefaultLocations()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var name in new[] { ".minecraft", "PrismLauncher", "MultiMC", "PolyMC", "ModrinthApp", "com.modrinth.theseus", "ATLauncher", "gdlauncher", "GDLauncher", "gdlauncher_carbon", "BakaXL" })
            if (!string.IsNullOrWhiteSpace(roaming)) yield return Path.Combine(roaming, name);
        foreach (var name in new[] { "ModrinthApp", "com.modrinth.theseus", "PrismLauncher", "GDLauncher", "gdlauncher_carbon" })
            if (!string.IsNullOrWhiteSpace(local)) yield return Path.Combine(local, name);
        if (!string.IsNullOrWhiteSpace(home)) yield return Path.Combine(home, "curseforge", "minecraft", "Instances");
        if (!string.IsNullOrWhiteSpace(documents)) yield return Path.Combine(documents, "Curse", "Minecraft", "Instances");
        if (!string.IsNullOrWhiteSpace(documents)) yield return Path.Combine(documents, "CurseForge", "Minecraft", "Instances");
    }

    /// <summary>取消不归类为目录错误；可预期的文件或元数据错误转换为可见警告。</summary>
    private static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidDataException or
        ArgumentException or InvalidOperationException or System.Text.Json.JsonException;
}
