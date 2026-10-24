using Launcher.Contracts;
using Launcher.Metadata;

namespace Launcher.Download;

/// <summary>
/// 从版本 JSON 推导「这个实例还缺哪些文件」。
/// 路径规则和 LaunchPlanner 保持一致：下好的文件必须正好是启动时要读的那一份。
/// </summary>
public static class DownloadPlanner
{
    private const string OfficialLibraries = "https://libraries.minecraft.net/";
    private const string OfficialAssets = "https://resources.download.minecraft.net/";
    private const string OfficialAssetIndex = "https://s3.amazonaws.com/Minecraft.Download/indexes/";

    public static IReadOnlyList<DownloadTask> Build(ResolvedVersion version, string gameDirectory, RuleContext platform)
    {
        var gameDir = Path.GetFullPath(gameDirectory);
        var librariesDir = Path.Combine(gameDir, "libraries");
        var assetsDir = Path.Combine(gameDir, "assets");
        var tasks = new List<DownloadTask>();

        var client = version.Downloads?.Client;
        if (client?.Url is { } clientUrl)
        {
            var jar = Path.Combine(gameDir, "versions", version.ClientJarVersionId, version.ClientJarVersionId + ".jar");
            tasks.Add(new DownloadTask(clientUrl, jar, client.Sha1, client.Size, "客户端 jar", "client"));
        }

        foreach (var library in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, platform)) continue;
            AddLibrary(library, librariesDir, tasks);
            AddNative(library, platform, librariesDir, tasks);
        }

        if (version.AssetIndex is { Id: { } indexId } index)
        {
            var url = index.Url ?? OfficialAssetIndex + indexId + ".json";
            var path = Path.Combine(assetsDir, "indexes", indexId + ".json");
            tasks.Add(new DownloadTask(url, path, index.Sha1, index.Size, "资源索引 " + indexId, "asset-index"));
        }

        if (version.Logging?.Client?.File is { Id: { } logId } log && log.Url is { } logUrl)
        {
            var path = Path.Combine(assetsDir, "log_configs", logId);
            tasks.Add(new DownloadTask(logUrl, path, log.Sha1, log.Size, "日志配置 " + logId, "log-config"));
        }

        return Dedupe(tasks);
    }

    /// <summary>索引本身下好之后，才能按对象表把几千个资源文件排出来。</summary>
    public static IReadOnlyList<DownloadTask> BuildAssets(AssetIndexJson index, string gameDirectory)
    {
        if (index.Objects is null) return Array.Empty<DownloadTask>();

        var root = Path.Combine(Path.GetFullPath(gameDirectory), "assets", "objects");
        return index.Objects
            .Where(kv => !string.IsNullOrEmpty(kv.Value.Hash))
            .Select(kv =>
            {
                var hash = kv.Value.Hash!;
                var prefix = hash[..2];
                return new DownloadTask(
                    $"{OfficialAssets}{prefix}/{hash}",
                    Path.Combine(root, prefix, hash),
                    hash,
                    kv.Value.Size,
                    kv.Key,
                    "asset");
            })
            .ToList();
    }

    /// <summary>已经存在、且大小对得上的文件不再下载（sha1 由下载器在真正拉取时校验）。</summary>
    public static IReadOnlyList<DownloadTask> FilterExisting(IReadOnlyList<DownloadTask> tasks) =>
        tasks.Where(t => !IsUpToDate(t)).ToList();

    public static bool IsUpToDate(DownloadTask task)
    {
        var info = new FileInfo(task.Destination);
        if (!info.Exists) return false;
        return task.Size is not { } size || info.Length == size;
    }

    private static void AddLibrary(LibraryJson library, string librariesDir, List<DownloadTask> tasks)
    {
        var artifact = library.Downloads?.Artifact;
        var relative = artifact?.Path
                       ?? (library.Downloads is null && library.Name is not null ? ArgumentBuilder.MavenPath(library.Name) : null);
        if (relative is null) return;

        var url = artifact?.Url ?? (library.Url ?? OfficialLibraries).TrimEnd('/') + "/" + relative;
        tasks.Add(new DownloadTask(url, ToLocal(librariesDir, relative), artifact?.Sha1, artifact?.Size, library.Name ?? relative, "library"));
    }

    private static void AddNative(LibraryJson library, RuleContext platform, string librariesDir, List<DownloadTask> tasks)
    {
        if (library.Natives is null || !library.Natives.TryGetValue(platform.OsName, out var template)) return;

        var classifier = ArgumentBuilder.Expand(template, new Dictionary<string, string> { ["arch"] = ArchBits(platform.Arch) });
        if (library.Downloads?.Classifiers is not { } classifiers || !classifiers.TryGetValue(classifier, out var file)) return;
        if (string.IsNullOrEmpty(file.Path)) return;

        var url = file.Url ?? (library.Url ?? OfficialLibraries).TrimEnd('/') + "/" + file.Path;
        var label = (library.Name ?? "natives") + " (" + classifier + ")";
        tasks.Add(new DownloadTask(url, ToLocal(librariesDir, file.Path), file.Sha1, file.Size, label, "natives"));
    }

    private static IReadOnlyList<DownloadTask> Dedupe(IEnumerable<DownloadTask> tasks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return tasks.Where(t => seen.Add(t.Destination)).ToList();
    }

    private static string ArchBits(string arch) => arch switch
    {
        "x86_64" => "64",
        "x86" => "32",
        _ => arch,
    };

    private static string ToLocal(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
}
