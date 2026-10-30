using System.Text.Json;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Prism;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Infrastructure.Minecraft.Modrinth;

/// <summary>按 Modrinth 导入清单补全必需模组和配置，再交给共享安装器准备本体、加载器与资源。</summary>
public sealed class ModrinthInstanceInstaller : IModrinthInstanceInstaller
{
    private readonly string _instancesRoot;
    private readonly IFileDownloader _downloader;
    private readonly IPrismInstanceInstaller _runtimeInstaller;
    private readonly ModrinthInstallOptions _options;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>复用通用下载器及共享运行安装器，允许测试替换运行阶段并单独调整文件并发数。</summary>
    public ModrinthInstanceInstaller(string launcherRoot, IFileDownloader downloader,
        ModrinthInstallOptions? options = null, IPrismInstanceInstaller? runtimeInstaller = null)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        _instancesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(launcherRoot, "instances")));
        _downloader = downloader;
        _options = options ?? new ModrinthInstallOptions();
        _options.Validate();
        _runtimeInstaller = runtimeInstaller ?? new InstanceRuntimeInstaller(launcherRoot, downloader,
            new ModrinthRuntimeResolver(), "Modrinth");
    }

    /// <summary>串行执行此服务的安装任务，避免同一实例重复下载；等待或工作中取消均返回明确状态。</summary>
    public async Task<PrismInstallResult> InstallAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var acquired = false;
        try
        {
            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            // 将本地清单解析和文件校验安排在后台，避免导入完成后阻塞 WPF 界面。
            return await Task.Run(() => InstallCoreAsync(instancePath, progress, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PrismInstallResult { SourceFormat = "Modrinth", IsCanceled = true };
        }
        finally
        {
            if (acquired) _operationLock.Release();
        }
    }

    /// <summary>验证导入文件并有界并发下载；只有全部必需文件通过后才生成运行依赖和启动配置。</summary>
    private async Task<PrismInstallResult> InstallCoreAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress, CancellationToken token)
    {
        var records = new List<InstanceDownloadFileResult>();
        var issues = new List<string>();
        var notes = new List<string>();
        string? launchPath = null;
        var canceled = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var root = ValidateInstance(instancePath);
            var manifestPath = Path.Combine(root, InstanceImportManifest.FileName);
            var manifest = InstanceManifestStore.Read(manifestPath);
            if (!manifest.SourceFormat.Equals("Modrinth", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("此安装器只处理 Modrinth 实例。");
            var manifestSnapshot = JsonSerializer.Serialize(manifest);
            var files = manifest.Files.Where(file => file.Required).ToArray();
            var optionalCount = manifest.Files.Count - files.Length;
            if (optionalCount > 0) notes.Add($"已跳过 {optionalCount} 个可选文件；可选模组选择功能尚未启用。");
            var completed = 0;
            var resultGate = new object();
            await Parallel.ForEachAsync(files, new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxConcurrentFiles, CancellationToken = token
            }, async (file, fileToken) =>
            {
                InstanceDownloadFileResult? result = null;
                try
                {
                    result = await EnsureManifestFileAsync(root, manifest.GameDirectory, file,
                        new InlineProgress<FileDownloadProgress>(value => progress?.Report(new(
                            "下载整合包文件", Volatile.Read(ref completed), files.Length, file.Path ?? "", value))),
                        note => { lock (resultGate) notes.Add(note); },
                        fileToken).ConfigureAwait(false);
                }
                finally
                {
                    // 取消会等待各个任务结束；已提交的文件记录必须先汇总，不能随取消一起丢失。
                    lock (resultGate)
                    {
                        if (result is not null)
                        {
                            records.Add(result);
                            if (result.State is InstanceDownloadState.Failed or InstanceDownloadState.Unavailable)
                                issues.Add(result.PathDisplay + "：" + result.Message);
                        }
                        completed++;
                        progress?.Report(new("检查整合包文件", completed, files.Length, file.Path ?? ""));
                    }
                }
            }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (issues.Count > 0) return Result();

            ValidateInstance(root);
            EnsureManifestUnchanged(manifestPath, manifestSnapshot);
            var runtime = await _runtimeInstaller.InstallAsync(root, progress, token).ConfigureAwait(false);
            records.AddRange(runtime.Files);
            issues.AddRange(runtime.Issues);
            notes.AddRange(runtime.Notes);
            canceled = runtime.IsCanceled;
            if (canceled) return Result();
            token.ThrowIfCancellationRequested();
            // 下载期间清单被修改时，旧模组检查结果不能和新运行配置拼成“已完成”。
            EnsureManifestUnchanged(manifestPath, manifestSnapshot);
            launchPath = runtime.LaunchProfilePath;
            if (launchPath is null && issues.Count == 0) issues.Add("运行依赖安装未生成启动配置，请查看组件解析结果。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; }
        catch (Exception ex) when (IsExpectedFailure(ex)) { issues.Add(ex.Message); }
        return Result();

        /// <summary>汇总文件、问题及取消状态；存在模组问题时绝不报告整个实例已准备完成。</summary>
        PrismInstallResult Result() => new()
        {
            SourceFormat = "Modrinth", Files = records.ToArray(), Issues = issues.Distinct().ToArray(),
            Notes = notes.Distinct().ToArray(), LaunchProfilePath = launchPath, IsCanceled = canceled
        };
    }

    /// <summary>校验并复用已有文件；仅下载有明确地址和哈希的缺失文件，保留所有不匹配的原文件。</summary>
    private async Task<InstanceDownloadFileResult?> EnsureManifestFileAsync(string root, string gameDirectory,
        InstanceFileRequirement file, IProgress<FileDownloadProgress>? progress, Action<string> addNote, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            ValidateInstance(root);
            var path = InstanceImportPaths.Within(root, file.Path ?? throw new InvalidDataException("文件缺少目标路径。"));
            InstanceImportPaths.EnsureNoLinks(path);
            try
            {
                if (await PrismInstanceInstaller.ExistingFileMatchesAsync(path, file, token).ConfigureAwait(false)) return null;
            }
            catch (InvalidDataException) when (IsMutableConfiguration(file, gameDirectory))
            {
                // 游戏会更新设置和服务器列表；仅对白名单内已成功打开的包内配置保留用户改动。
                addNote("已保留导入后修改的配置：" + file.Path);
                return null;
            }
            if (file.Source == InstanceFileSource.Embedded)
                return new(file, InstanceDownloadState.Unavailable, "缺少包内文件，请从原 mrpack 压缩包恢复。");
            if (file.Source != InstanceFileSource.DirectDownload || file.Downloads.Count == 0)
                return new(file, InstanceDownloadState.Unavailable, "文件没有可用于 Modrinth 安装的直接下载地址。");
            if (file.Hashes.Count == 0)
                return new(file, InstanceDownloadState.Unavailable, "下载清单缺少文件哈希，无法验证来源内容。");
            var download = await _downloader.DownloadAsync(new FileDownloadRequest
            {
                DestinationPath = path, Urls = file.Downloads, ExpectedSize = file.Size, Hashes = file.Hashes
            }, progress, token).ConfigureAwait(false);
            if (!download.HashVerified) throw new InvalidDataException("下载器未确认清单指定的文件哈希。");
            return new(file, InstanceDownloadState.Downloaded, "已下载并校验整合包文件。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsExpectedFailure(ex)) { return new(file, InstanceDownloadState.Failed, ex.Message); }
    }

    /// <summary>只允许明确可变的包内配置不同于导入快照，模组 JAR 与其他必需文件仍严格校验。</summary>
    private static bool IsMutableConfiguration(InstanceFileRequirement file, string gameDirectory)
    {
        if (file.Source != InstanceFileSource.Embedded || file.Path is null) return false;
        var prefix = gameDirectory == "." ? "" : gameDirectory + "/";
        if (!file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = file.Path[prefix.Length..];
        return relative.StartsWith("config/", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith("defaultconfigs/", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("options.txt", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("optionsof.txt", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("optionsshaders.txt", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("servers.dat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>限定为启动器 instances 的直接子实例，并拒绝目录联接和符号链接。</summary>
    private string ValidateInstance(string instancePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instancePath));
        if (!string.Equals(Path.GetDirectoryName(root), _instancesRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能补全 instances 的直接子实例。");
        InstanceImportPaths.EnsureNoLinks(root);
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) throw new IOException("实例路径不是目录。");
        return root;
    }

    /// <summary>比较已验证的清单内容，拒绝安装过程中来源文件、必需项或组件版本发生变化。</summary>
    private static void EnsureManifestUnchanged(string path, string snapshot)
    {
        if (JsonSerializer.Serialize(InstanceManifestStore.Read(path)) != snapshot)
            throw new IOException("准备过程中实例清单发生变化，请重新补全。");
    }

    /// <summary>只把可预期的文件、网络、格式和参数错误转为安装问题，取消另行处理。</summary>
    private static bool IsExpectedFailure(Exception ex) => ex is IOException or InvalidDataException or
        HttpRequestException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
        TimeoutException or JsonException;

    /// <summary>同步转发下载进度，由最外层界面进度对象切回 UI 线程。</summary>
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        /// <summary>转发单个下载更新，不在后台任务额外捕获同步上下文。</summary>
        public void Report(T value) => callback(value);
    }
}
