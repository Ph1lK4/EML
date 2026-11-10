using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Downloads;
using EML.Infrastructure.Minecraft.CurseForge.Api;

namespace EML.Infrastructure.Downloads;

/// <summary>有可信摘要时复用已验证对象，并将同一来源的可续传片段持久保存。</summary>
public sealed partial class HttpFileDownloader
{
    // 固定分片锁限制长期内存占用；海量资源摘要不会各自永久留下一个锁。
    private static readonly SemaphoreSlim[] CacheLocks = Enumerable.Range(0, 1024).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private const int MaximumResumeMetadataBytes = 8192;

    /// <summary>元数据不保存 URL、请求头或凭据；仅来源地址指纹和服务器强实体标签用于恢复请求。</summary>
    private sealed record ResumeMetadata(int SchemaVersion, string SourceFingerprint, string EntityTag, long? TotalLength);
    private sealed record CachePaths(string Blob, string Part, string Metadata, string Lock);

    /// <summary>同一摘要在进程内和跨进程串行填充缓存，命中仍按本次全部校验要求重新验证。</summary>
    private async Task<FileDownloadResult> DownloadCachedAsync(string destination, IReadOnlyList<Uri> urls,
        long? expectedSize, long maximumBytes, IReadOnlyList<HashExpectation> hashes,
        IProgress<FileDownloadProgress>? progress, CancellationToken token)
    {
        var paths = GetCachePaths(hashes);
        var gate = CacheLocks[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(paths.Blob) % (uint)CacheLocks.Length];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureDestinationAvailable(destination);
            EnsureNoLinks(_options.CacheDirectory!);
            Directory.CreateDirectory(_options.CacheDirectory!);
            await using var cacheLock = await AcquireCacheFileLockAsync(paths.Lock, token).ConfigureAwait(false);
            EnsureDestinationAvailable(destination);
            EnsureNoLinks(paths.Blob);
            if (File.Exists(paths.Blob))
            {
                try
                {
                    return await PublishCachedAsync(paths.Blob, destination, expectedSize, maximumBytes, hashes,
                        progress, 0, true, 0, token).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    // 缓存不满足当前全部要求时保留诊断副本，不会碰触请求的最终目标。
                    EnsureNoLinks(paths.Blob);
                    File.Move(paths.Blob, paths.Blob + ".invalid-" + Guid.NewGuid().ToString("N"), overwrite: false);
                }
            }
            return await FillCacheAsync(paths, destination, urls, expectedSize, maximumBytes, hashes, progress, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>按最强声明摘要构造固定缓存身份，文件名与远端路径完全无关。</summary>
    private CachePaths GetCachePaths(IReadOnlyList<HashExpectation> hashes)
    {
        var strongest = hashes.OrderByDescending(item => item.Expected.Length).First();
        var name = strongest.Algorithm.Name!.ToLowerInvariant() + "-" + Convert.ToHexStringLower(strongest.Expected);
        var root = _options.CacheDirectory!;
        var blob = Path.GetFullPath(Path.Combine(root, name + ".blob"));
        if (!blob.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("缓存路径超出指定目录。");
        return new(blob, Path.Combine(root, name + ".part"), Path.Combine(root, name + ".resume.json"), Path.Combine(root, name + ".lock"));
    }

    /// <summary>文件锁让不同启动器进程也不会同时写入同一片段；等待受取消和有限期限控制。</summary>
    private static async Task<FileStream> AcquireCacheFileLockAsync(string path, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            EnsureNoLinks(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
            { await Task.Delay(100, token).ConfigureAwait(false); }
        }
    }

    /// <summary>按原有镜像和重试规则填充缓存；切换来源时不拼接来自另一个服务器的片段。</summary>
    private async Task<FileDownloadResult> FillCacheAsync(CachePaths paths, string destination, IReadOnlyList<Uri> urls,
        long? expectedSize, long maximumBytes, IReadOnlyList<HashExpectation> hashes,
        IProgress<FileDownloadProgress>? progress, CancellationToken token)
    {
        Exception? lastFailure = null;
        var attemptNumber = 0;
        foreach (var url in urls)
        {
            for (var attempt = 1; attempt <= _options.MaxAttemptsPerUrl; attempt++)
            {
                token.ThrowIfCancellationRequested();
                EnsureDestinationAvailable(destination);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(_options.AttemptTimeout);
                long? completedResume = null;
                try
                {
                    completedResume = await FillCacheAttemptAsync(paths, url, expectedSize, maximumBytes, hashes,
                        progress, ++attemptNumber, deadline.Token, deadline).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                { lastFailure = new TimeoutException("本次下载超时。", ex); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TimeoutException or CurseForgeCredentialException)
                {
                    token.ThrowIfCancellationRequested();
                    EnsureDestinationAvailable(destination);
                    lastFailure = ex;
                }
                // 本地复制失败不重新联网：已验证对象仍在缓存，下次请求可直接重试发布。
                if (completedResume is { } resumed)
                    return await PublishCachedAsync(paths.Blob, destination, expectedSize, maximumBytes, hashes,
                        progress, attemptNumber, false, resumed, token).ConfigureAwait(false);
                if (!IsTransient(lastFailure!) || attempt == _options.MaxAttemptsPerUrl) break;
                await Task.Delay(GetRetryDelay(lastFailure!, attempt), token).ConfigureAwait(false);
            }
        }
        throw lastFailure ?? new IOException("所有下载地址均未成功。");
    }

    /// <summary>强 ETag 与来源指纹一致才发送 Range；200、416 或实体变更都从头重建片段。</summary>
    private async Task<long> FillCacheAttemptAsync(CachePaths paths, Uri url, long? expectedSize, long maximumBytes,
        IReadOnlyList<HashExpectation> hashes, IProgress<FileDownloadProgress>? progress, int attempt, CancellationToken token,
        CancellationTokenSource deadline)
    {
        var authentication = new DownloadAuthenticationState();
        try
        {
            var metadata = ReadResumeMetadata(paths, url, expectedSize, maximumBytes);
            var offset = metadata is null ? 0 : new FileInfo(paths.Part).Length;
            var total = expectedSize ?? metadata?.TotalLength;
            if (offset > 0 && total == offset)
            {
                try
                {
                    await VerifyCacheFileAsync(paths.Part, expectedSize, maximumBytes, hashes, progress, attempt, offset, token).ConfigureAwait(false);
                    PromoteCache(paths);
                    return offset;
                }
                catch (InvalidDataException) { ResetPartial(paths); metadata = null; offset = 0; }
            }
            // 只允许一次协议恢复，避免返回错误 Range 的服务器造成无限循环。
            for (var reset = 0; reset < 2; reset++)
            {
                token.ThrowIfCancellationRequested();
                using var response = await SendAsync(url, authentication, token, offset > 0 ? offset : null,
                    metadata?.EntityTag, allowRange: true).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    ResetPartial(paths); metadata = null; offset = 0;
                    if (reset == 0) continue;
                    throw new InvalidDataException("下载服务器重复拒绝有效的文件范围。");
                }
                var entityTag = StrongEntityTag(response.Headers.ETag);
                var ranged = response.StatusCode == HttpStatusCode.PartialContent;
                if (ranged && (offset <= 0 || metadata is null))
                    throw new InvalidDataException("未请求续传却收到文件片段。");
                if (ranged && entityTag != metadata!.EntityTag)
                {
                    ResetPartial(paths); metadata = null; offset = 0;
                    if (reset == 0) continue;
                    throw new InvalidDataException("续传期间服务器文件标识发生变化。");
                }
                if (response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("续传响应使用了不支持的内容编码。");
                var advertised = response.Content.Headers.ContentLength;
                if (ranged)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range is null || range.Unit != "bytes" || !range.HasRange || !range.HasLength ||
                        range.From != offset || range.To < range.From || range.Length <= range.To ||
                        (advertised.HasValue && advertised != range.To - range.From + 1) ||
                        (expectedSize.HasValue && range.Length != expectedSize) ||
                        (metadata!.TotalLength.HasValue && metadata.TotalLength != range.Length))
                        throw new InvalidDataException("服务器续传范围与已有片段不一致。");
                    total = range.Length;
                }
                else
                {
                    if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange is not null)
                        throw new InvalidDataException("服务器没有返回可下载的完整内容。");
                    ResetPartial(paths); offset = 0;
                    total = expectedSize ?? advertised;
                    if (expectedSize.HasValue && advertised.HasValue && expectedSize != advertised)
                        throw new InvalidDataException("服务器返回的文件大小与要求不符。");
                }
                if (total > maximumBytes || advertised > maximumBytes || offset > maximumBytes)
                    throw new InvalidDataException("下载内容超过允许的文件大小。");
                if (entityTag is not null)
                    await SaveResumeMetadataAsync(paths, new(1, SourceFingerprint(url), entityTag, total), token).ConfigureAwait(false);
                else DeleteCacheFile(paths.Metadata);
                var resumedBytes = offset;
                await ReceiveCachedBodyAsync(paths.Part, response, offset, total, maximumBytes, advertised,
                    progress, attempt, resumedBytes, token, deadline).ConfigureAwait(false);
                await VerifyCacheFileAsync(paths.Part, expectedSize ?? total, maximumBytes, hashes,
                    progress, attempt, resumedBytes, token).ConfigureAwait(false);
                PromoteCache(paths);
                return resumedBytes;
            }
            throw new InvalidDataException("无法恢复下载范围。");
        }
        catch (Exception ex)
        {
            if (ex is InvalidDataException) ResetPartial(paths);
            else if (!File.Exists(paths.Metadata)) DeleteCacheFile(paths.Part);
            var failure = authentication.IsProtected ? SanitizeCurseForgeFailure(ex) : ex;
            if (ReferenceEquals(failure, ex)) throw;
            throw failure;
        }
    }

    /// <summary>顺序追加网络内容并应用共享带宽限制，取消时已落盘字节可供下一次请求续传。</summary>
    private async Task ReceiveCachedBodyAsync(string path, HttpResponseMessage response, long offset, long? total,
        long maximumBytes, long? advertised, IProgress<FileDownloadProgress>? progress, int attempt, long resumedBytes,
        CancellationToken token, CancellationTokenSource deadline)
    {
        EnsureNoLinks(path);
        await using var output = new FileStream(path, offset == 0 ? FileMode.CreateNew : FileMode.Open,
            FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (output.Length != offset) throw new InvalidDataException("续传片段在请求期间发生变化。");
        output.Position = offset;
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long received = offset;
        try
        {
            progress?.Report(new(received, total, attempt) { ResumedBytes = resumedBytes });
            var lastProgress = Stopwatch.GetTimestamp();
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == 0) break;
                await ThrottleAsync(count, deadline, token).ConfigureAwait(false);
                received = checked(received + count);
                if (received > maximumBytes || total.HasValue && received > total ||
                    advertised.HasValue && received - offset > advertised)
                    throw new InvalidDataException("下载内容超过允许的文件大小。");
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                if (Stopwatch.GetElapsedTime(lastProgress) >= TimeSpan.FromMilliseconds(100))
                {
                    progress?.Report(new(received, total, attempt) { ResumedBytes = resumedBytes });
                    lastProgress = Stopwatch.GetTimestamp();
                }
            }
            progress?.Report(new(received, total, attempt) { ResumedBytes = resumedBytes });
            await output.FlushAsync(token).ConfigureAwait(false);
            if (total.HasValue && received != total || advertised.HasValue && received - offset != advertised)
                throw new HttpRequestException("下载连接结束时文件尚未完整，稍后可继续下载。");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>复制时同时校验缓存全部字节，最终以禁止覆盖的重命名发布目标文件。</summary>
    private static async Task<FileDownloadResult> PublishCachedAsync(string blob, string destination, long? expectedSize,
        long maximumBytes, IReadOnlyList<HashExpectation> hashes, IProgress<FileDownloadProgress>? progress,
        int attempt, bool cacheHit, long resumedBytes, CancellationToken token)
    {
        EnsureDestinationAvailable(destination);
        EnsureNoLinks(blob);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".eml-download-" + Guid.NewGuid().ToString("N") + ".part");
        var created = false;
        try
        {
            await using var source = new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length > maximumBytes || expectedSize.HasValue && source.Length != expectedSize)
                throw new InvalidDataException("缓存文件大小与当前下载要求不一致。");
            using var checksum = new HashCollection(hashes);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long copied = 0;
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    created = true;
                    progress?.Report(new(0, source.Length, attempt) { Phase = FileDownloadPhase.CopyingFromCache, FromCache = cacheHit, ResumedBytes = resumedBytes });
                    int count;
                    while ((count = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                    {
                        checksum.Append(buffer.AsSpan(0, count));
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        copied += count;
                    }
                    progress?.Report(new(copied, source.Length, attempt) { Phase = FileDownloadPhase.Verifying, FromCache = cacheHit, ResumedBytes = resumedBytes });
                    checksum.Verify();
                    await output.FlushAsync(token).ConfigureAwait(false);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            token.ThrowIfCancellationRequested();
            EnsureDestinationAvailable(destination);
            EnsureNoLinks(temporary);
            File.Move(temporary, destination, overwrite: false);
            created = false;
            ReportCompleted(progress, copied, attempt, cacheHit, resumedBytes);
            return new(destination, copied, true) { CacheHit = cacheHit, ResumedBytes = resumedBytes };
        }
        finally { if (created) DeleteCacheFile(temporary); }
    }

    /// <summary>完整片段发布为缓存对象前重算当前请求的全部摘要，不信任元数据中的状态。</summary>
    private static async Task VerifyCacheFileAsync(string path, long? expectedSize, long maximumBytes,
        IReadOnlyList<HashExpectation> hashes, IProgress<FileDownloadProgress>? progress, int attempt, long resumedBytes, CancellationToken token)
    {
        EnsureNoLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > maximumBytes || expectedSize.HasValue && input.Length != expectedSize)
            throw new InvalidDataException("下载文件大小与当前要求不符。");
        progress?.Report(new(input.Length, expectedSize ?? input.Length, attempt) { Phase = FileDownloadPhase.Verifying, ResumedBytes = resumedBytes });
        using var checksum = new HashCollection(hashes);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                checksum.Append(buffer.AsSpan(0, count));
            checksum.Verify();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>读取小型断点记录；来源、强标签、长度或路径不符合时仅丢弃自有缓存片段。</summary>
    private static ResumeMetadata? ReadResumeMetadata(CachePaths paths, Uri url, long? expectedSize, long maximumBytes)
    {
        EnsureNoLinks(paths.Part); EnsureNoLinks(paths.Metadata);
        if (!File.Exists(paths.Part) || !File.Exists(paths.Metadata)) { ResetPartial(paths); return null; }
        try
        {
            using var input = new FileStream(paths.Metadata, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumResumeMetadataBytes) throw new InvalidDataException("断点记录过大。");
            var metadata = JsonSerializer.Deserialize<ResumeMetadata>(input, new JsonSerializerOptions { MaxDepth = 4 });
            var length = new FileInfo(paths.Part).Length;
            if (metadata is null || metadata.SchemaVersion != 1 || metadata.SourceFingerprint != SourceFingerprint(url) ||
                !EntityTagHeaderValue.TryParse(metadata.EntityTag, out var tag) || StrongEntityTag(tag) != metadata.EntityTag ||
                metadata.TotalLength is <= 0 || metadata.TotalLength > maximumBytes || length <= 0 || length > maximumBytes ||
                (expectedSize.HasValue && length > expectedSize) || (metadata.TotalLength.HasValue && length > metadata.TotalLength) ||
                (expectedSize.HasValue && metadata.TotalLength.HasValue && expectedSize != metadata.TotalLength))
                throw new InvalidDataException("断点记录与当前下载来源不一致。");
            return metadata;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { ResetPartial(paths); return null; }
    }

    /// <summary>原子更新断点描述；强标签之外不记录响应头或任何请求凭据。</summary>
    private static async Task SaveResumeMetadataAsync(CachePaths paths, ResumeMetadata metadata, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
        if (bytes.Length > MaximumResumeMetadataBytes) throw new InvalidDataException("断点记录过大。");
        EnsureNoLinks(paths.Metadata);
        var temporary = paths.Metadata + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var created = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { created = true; await output.WriteAsync(bytes, token).ConfigureAwait(false); }
            token.ThrowIfCancellationRequested(); EnsureNoLinks(paths.Metadata);
            File.Move(temporary, paths.Metadata, overwrite: true); created = false;
        }
        finally { if (created) DeleteCacheFile(temporary); }
    }

    /// <summary>完成校验后原子发布缓存对象；断点信息不再需要，但对象可跨实例共享。</summary>
    private static void PromoteCache(CachePaths paths)
    {
        EnsureNoLinks(paths.Part); EnsureNoLinks(paths.Blob);
        File.Move(paths.Part, paths.Blob, overwrite: false);
        DeleteCacheFile(paths.Metadata);
    }

    /// <summary>只移除固定摘要名下的片段和记录，绝不操作请求的目标文件。</summary>
    private static void ResetPartial(CachePaths paths)
    { DeleteCacheFile(paths.Part); DeleteCacheFile(paths.Metadata); }

    /// <summary>删除自有缓存文件前逐层拒绝链接，不沿链接清理其他目录内容。</summary>
    private static void DeleteCacheFile(string path)
    { EnsureNoLinks(path); if (File.Exists(path)) File.Delete(path); }

    /// <summary>强 ETag 作为 If-Range 条件；弱标签、通配符和过长标签不用于恢复。</summary>
    private static string? StrongEntityTag(EntityTagHeaderValue? tag) =>
        tag is { IsWeak: false } && tag.Tag != "*" && tag.ToString().Length <= 512 ? tag.ToString() : null;

    /// <summary>完整来源地址仅参与不可逆摘要，缓存中不出现签名 URL 或访问令牌。</summary>
    private static string SourceFingerprint(Uri url) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)));

    /// <summary>文件提交后进度回调不能把成功改写为失败，也不能触发第二次下载。</summary>
    private static void ReportCompleted(IProgress<FileDownloadProgress>? progress, long bytes, int attempt, bool fromCache, long resumed)
    {
        try { progress?.Report(new(bytes, bytes, attempt) { Phase = FileDownloadPhase.Completed, FromCache = fromCache, ResumedBytes = resumed }); }
        catch (Exception) { Trace.TraceWarning("下载已完成，但进度接收方未能处理完成通知。"); }
    }
}
