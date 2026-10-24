using Launcher.Contracts;
using Launcher.Metadata;

namespace Launcher.Download;

/// <summary>
/// 把一个实例补齐：先保证资源索引在，再按索引把 objects 全部排进任务表。
/// 上层（CLI / UI）只管调这一个方法，不用关心「先下索引再下资源」的顺序。
/// </summary>
public sealed class InstanceDownloader
{
    private readonly IDownloadService _downloader;

    public InstanceDownloader(IDownloadService? downloader = null) =>
        _downloader = downloader ?? new HttpFileDownloader();

    /// <summary>返回本次实际下载的文件数（已存在的不算）。</summary>
    public async Task<int> RunAsync(
        ResolvedVersion version,
        string gameDirectory,
        RuleContext platform,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var gameDir = Path.GetFullPath(gameDirectory);
        var pending = DownloadPlanner.FilterExisting(DownloadPlanner.Build(version, gameDir, platform)).ToList();

        if (version.AssetIndex is { Id: { } indexId } index)
        {
            var indexPath = Path.Combine(gameDir, "assets", "indexes", indexId + ".json");
            if (!File.Exists(indexPath))
            {
                var indexTask = pending.FirstOrDefault(t => t.Kind == "asset-index")
                                ?? throw new InvalidDataException($"版本 {version.Id} 缺少资源索引下载任务");
                await _downloader.DownloadAsync(new[] { indexTask }, progress: null, ct).ConfigureAwait(false);
                pending.Remove(indexTask);
            }

            var assetTasks = DownloadPlanner.FilterExisting(
                DownloadPlanner.BuildAssets(AssetIndexJson.Load(indexPath), gameDir));
            pending.AddRange(assetTasks);
        }

        if (pending.Count == 0) return 0;

        await _downloader.DownloadAsync(pending, progress, ct).ConfigureAwait(false);
        return pending.Count;
    }
}
