using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Launcher.Contracts;

namespace Launcher.Download;

/// <summary>
/// 带并发、重试、镜像回退、sha1 校验的下载器。
/// 每个文件先写 .part，校验通过才改名——中途断了不会留下「看起来存在其实是半个」的文件。
/// </summary>
public sealed class HttpFileDownloader : IDownloadService
{
    private readonly HttpClient _client;
    private readonly int _maxParallel;

    public HttpFileDownloader(HttpClient? client = null, int maxParallel = 8)
    {
        _client = client ?? CreateDefaultClient();
        _maxParallel = Math.Max(1, maxParallel);
    }

    public async Task DownloadAsync(
        IReadOnlyList<DownloadTask> tasks,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var pending = DownloadPlanner.FilterExisting(tasks);
        var total = pending.Count;
        if (total == 0) return;

        var totalBytes = pending.Sum(t => t.Size ?? 0);
        var bytesDone = 0L;
        var completed = 0;
        var gate = new object();
        var errors = new ConcurrentBag<string>();

        using var limiter = new SemaphoreSlim(_maxParallel);

        var jobs = pending.Select(async task =>
        {
            await limiter.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var written = await DownloadOneAsync(task, ct).ConfigureAwait(false);
                int done;
                long bytes;
                lock (gate)
                {
                    done = ++completed;
                    bytes = bytesDone += written;
                }

                progress?.Report(new DownloadProgress(done, total, bytes, totalBytes, task.Label));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{task.Label}：{ex.Message}");
            }
            finally
            {
                limiter.Release();
            }
        });

        await Task.WhenAll(jobs).ConfigureAwait(false);

        if (!errors.IsEmpty)
            throw new IOException($"有 {errors.Count} 个文件没下载成功，例如：{errors.First()}");
    }

    private async Task<long> DownloadOneAsync(DownloadTask task, CancellationToken ct)
    {
        if (DownloadPlanner.IsUpToDate(task)) return 0;

        var directory = Path.GetDirectoryName(task.Destination);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var urls = new List<string>();
        if (Mirror.ToMirror(task.Url) is { } mirrored) urls.Add(mirrored);
        if (!urls.Contains(task.Url)) urls.Add(task.Url);

        Exception? last = null;
        foreach (var url in urls)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await FetchAsync(url, task, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                    await Task.Delay(300 * (attempt + 1), ct).ConfigureAwait(false);
                }
            }
        }

        throw new IOException($"下载失败：{task.Label}（{last?.Message}）", last);
    }

    private async Task<long> FetchAsync(string url, DownloadTask task, CancellationToken ct)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var temp = task.Destination + ".part-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = File.Create(temp))
            {
                await source.CopyToAsync(target, 81920, ct).ConfigureAwait(false);
            }

            var length = new FileInfo(temp).Length;
            if (task.Size is { } expectedSize && length != expectedSize)
                throw new InvalidDataException($"大小不对：期望 {expectedSize}，实际 {length}");

            if (task.Sha1 is { } expectedSha1)
            {
                var actual = await Sha1Async(temp, ct).ConfigureAwait(false);
                if (!actual.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"sha1 不匹配：期望 {expectedSha1}，实际 {actual}");
            }

            File.Move(temp, task.Destination, overwrite: true);
            return length;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static async Task<string> Sha1Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }
}
