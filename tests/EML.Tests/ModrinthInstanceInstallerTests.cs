using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Modrinth;

namespace EML.Tests;

/// <summary>用隔离实例和可控下载器验证 Modrinth 文件下载、覆盖优先级、取消以及运行阶段的调用边界。</summary>
public sealed class ModrinthInstanceInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-Modrinth-install-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>全部必需模组下载并通过哈希后才进入运行阶段，同时保持原导入清单不变。</summary>
    [Fact]
    public async Task DownloadsRequiredFilesBeforeRuntimeAndPreservesManifest()
    {
        var fixture = await CreateFixtureAsync(6);
        var downloader = new FixtureDownloader(fixture.Payloads);
        var runtime = new FixtureRuntimeInstaller
        {
            BeforeInstall = () => Assert.All(fixture.Manifest.Files,
                file => Assert.True(File.Exists(Path.Combine(fixture.Instance, file.Path!))))
        };

        var result = await Installer(downloader, runtime).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Equal("Modrinth", result.SourceFormat);
        Assert.Equal(6, downloader.Requests.Count);
        Assert.Equal(6, result.Files.Count);
        Assert.All(result.Files, file => Assert.Equal(InstanceDownloadState.Downloaded, file.State));
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(fixture.ManifestBytes, File.ReadAllBytes(fixture.ManifestPath));
        Assert.All(downloader.Requests, request =>
        {
            Assert.NotEmpty(request.Hashes);
            Assert.True(request.ExpectedSize > 0);
        });
    }

    /// <summary>默认并发限制为四，允许显式降至单路或升至另一合法值，不会串行等待整个文件列表。</summary>
    [Theory]
    [InlineData(null, 4)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    public async Task DownloadConcurrencyHonorsConfiguredBound(int? configuredConcurrency, int expectedPeak)
    {
        var fixture = await CreateFixtureAsync(12);
        var downloader = new FixtureDownloader(fixture.Payloads) { ConcurrencyBarrierTarget = expectedPeak };
        var options = configuredConcurrency is null ? null : new ModrinthInstallOptions { MaxConcurrentFiles = configuredConcurrency.Value };

        var result = await Installer(downloader, new FixtureRuntimeInstaller(), options).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Equal(expectedPeak, downloader.PeakConcurrent);
        Assert.Equal(12, downloader.Requests.Count);
    }

    /// <summary>哈希有效的已有模组可复用，重复执行不会重复下载或改写文件。</summary>
    [Fact]
    public async Task ValidExistingFilesAreReusedOnSecondRun()
    {
        var fixture = await CreateFixtureAsync(3);
        var downloader = new FixtureDownloader(fixture.Payloads);
        var runtime = new FixtureRuntimeInstaller();
        var installer = Installer(downloader, runtime);
        var first = await installer.InstallAsync(fixture.Instance);

        var second = await installer.InstallAsync(fixture.Instance);

        Assert.True(first.FilesReady);
        Assert.True(second.FilesReady);
        Assert.Equal(3, downloader.Requests.Count);
        Assert.Empty(second.Files);
        Assert.Equal(2, runtime.Calls);
        Assert.Equal(fixture.ManifestBytes, File.ReadAllBytes(fixture.ManifestPath));
    }

    /// <summary>导入器已将 overrides 同路径需求转成 Embedded 时，安装器保留包内内容，不恢复旧下载项。</summary>
    [Fact]
    public async Task EmbeddedOverrideWinsWithoutDownloadingOriginalIndexFile()
    {
        var embedded = Requirement(".minecraft/mods/override.jar", Encoding.UTF8.GetBytes("client override")) with
        {
            Source = InstanceFileSource.Embedded, Downloads = [], ArchiveEntry = "client-overrides/mods/override.jar"
        };
        var fixture = await CreateFixtureAsync(1, [embedded]);
        var originalBytes = Encoding.UTF8.GetBytes("client override");
        WriteFile(fixture.Instance, embedded.Path!, originalBytes);
        var downloader = new FixtureDownloader(fixture.Payloads);
        var runtime = new FixtureRuntimeInstaller();

        var result = await Installer(downloader, runtime).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Single(downloader.Requests);
        Assert.DoesNotContain(downloader.Requests, request => request.DestinationPath.EndsWith("override.jar"));
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(fixture.Instance, embedded.Path!)));
        Assert.Equal(fixture.ManifestBytes, File.ReadAllBytes(fixture.ManifestPath));
    }

    /// <summary>缺少必须的包内文件时明确要求原包恢复，不调用运行阶段或伪造下载来源。</summary>
    [Fact]
    public async Task MissingEmbeddedFileBlocksRuntimeAndExplainsRecovery()
    {
        var embedded = Requirement(".minecraft/config/required.cfg", Encoding.UTF8.GetBytes("settings")) with
        {
            Source = InstanceFileSource.Embedded, Downloads = [], ArchiveEntry = "overrides/config/required.cfg"
        };
        var fixture = await CreateFixtureAsync(0, [embedded]);
        var downloader = new FixtureDownloader(fixture.Payloads);
        var runtime = new FixtureRuntimeInstaller();

        var result = await Installer(downloader, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.Equal(0, runtime.Calls);
        Assert.Empty(downloader.Requests);
        Assert.Equal(InstanceDownloadState.Unavailable, Assert.Single(result.Files).State);
        Assert.Contains(result.Issues, issue => issue.Contains("原 mrpack"));
    }

    /// <summary>游戏会改写的包内配置保留玩家内容并写入说明，游戏目录为实例根目录时也正确匹配。</summary>
    [Theory]
    [InlineData(".minecraft", "config/user.cfg", false)]
    [InlineData(".minecraft", "defaultconfigs/mod.toml", true)]
    [InlineData(".minecraft", "options.txt", false)]
    [InlineData(".minecraft", "optionsof.txt", true)]
    [InlineData(".minecraft", "optionsshaders.txt", false)]
    [InlineData(".minecraft", "servers.dat", true)]
    [InlineData(".", "config/user.cfg", true)]
    public async Task ChangedEmbeddedMutableConfigurationIsPreservedWithNote(string gameDirectory, string relative, bool sameSize)
    {
        var original = Encoding.UTF8.GetBytes("pack settings");
        var path = gameDirectory == "." ? relative : gameDirectory + "/" + relative;
        var embedded = Requirement(path, original) with { Source = InstanceFileSource.Embedded, Downloads = [] };
        var fixture = await CreateFixtureAsync(0, [embedded]);
        await InstanceManifestStore.WriteAsync(fixture.ManifestPath,
            fixture.Manifest with { GameDirectory = gameDirectory }, CancellationToken.None);
        var changed = sameSize ? Enumerable.Repeat((byte)'x', original.Length).ToArray() : Encoding.UTF8.GetBytes("player changed these settings");
        WriteFile(fixture.Instance, path, changed);
        var downloader = new FixtureDownloader(fixture.Payloads);
        var runtime = new FixtureRuntimeInstaller();

        var result = await Installer(downloader, runtime).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady, string.Join("\n", result.Issues));
        Assert.Equal(1, runtime.Calls);
        Assert.Empty(downloader.Requests);
        Assert.Equal(changed, File.ReadAllBytes(Path.Combine(fixture.Instance, path)));
        Assert.Contains(result.Notes, note => note.Contains("已保留导入后修改的配置") && note.Contains(path));
    }

    /// <summary>配置白名单不会放宽其他包内文件，也不会允许清单下载项绕过哈希验证。</summary>
    [Theory]
    [InlineData(".minecraft/mods/embedded.jar", InstanceFileSource.Embedded)]
    [InlineData(".minecraft/options.ini", InstanceFileSource.Embedded)]
    [InlineData(".minecraft/subfolder/options.txt", InstanceFileSource.Embedded)]
    [InlineData("other/config/user.cfg", InstanceFileSource.Embedded)]
    [InlineData(".minecraft/config/downloaded.cfg", InstanceFileSource.DirectDownload)]
    public async Task ChangedFilesOutsideEmbeddedConfigurationWhitelistRemainBlocking(string path, InstanceFileSource source)
    {
        var file = Requirement(path, Encoding.UTF8.GetBytes("pack content")) with { Source = source };
        if (source == InstanceFileSource.Embedded) file = file with { Downloads = [] };
        var fixture = await CreateFixtureAsync(0, [file]);
        var changed = Encoding.UTF8.GetBytes("different player content");
        WriteFile(fixture.Instance, path, changed);
        var downloader = new FixtureDownloader(fixture.Payloads);
        var runtime = new FixtureRuntimeInstaller();

        var result = await Installer(downloader, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Equal(0, runtime.Calls);
        Assert.Empty(downloader.Requests);
        Assert.Equal(changed, File.ReadAllBytes(Path.Combine(fixture.Instance, path)));
        Assert.Equal(InstanceDownloadState.Failed, Assert.Single(result.Files).State);
        Assert.DoesNotContain(result.Notes, note => note.Contains("已保留导入后修改的配置"));
    }

    /// <summary>同大小但内容不同的已有文件保留原样，报告哈希冲突，不覆盖用户内容或继续发布就绪状态。</summary>
    [Fact]
    public async Task ExistingHashMismatchIsPreservedAndBlocksRuntime()
    {
        var fixture = await CreateFixtureAsync(1);
        var file = Assert.Single(fixture.Manifest.Files);
        var unexpected = Enumerable.Repeat((byte)'x', checked((int)file.Size!.Value)).ToArray();
        WriteFile(fixture.Instance, file.Path!, unexpected);
        var downloader = new FixtureDownloader(fixture.Payloads);
        var runtime = new FixtureRuntimeInstaller();

        var result = await Installer(downloader, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Equal(0, runtime.Calls);
        Assert.Empty(downloader.Requests);
        Assert.Equal(unexpected, File.ReadAllBytes(Path.Combine(fixture.Instance, file.Path!)));
        Assert.Contains(result.Issues, issue => issue.Contains("哈希不符"));
    }

    /// <summary>可选模组不自动安装，缺少可选文件不会阻止必需文件与运行依赖准备。</summary>
    [Fact]
    public async Task OptionalFilesAreSkippedAndReportedWithoutBlockingRuntime()
    {
        var optional = Requirement(".minecraft/mods/optional.jar", Encoding.UTF8.GetBytes("optional")) with { Required = false };
        var fixture = await CreateFixtureAsync(1, [optional]);
        var downloader = new FixtureDownloader(fixture.Payloads);

        var result = await Installer(downloader, new FixtureRuntimeInstaller()).InstallAsync(fixture.Instance);

        Assert.True(result.FilesReady);
        Assert.Single(downloader.Requests);
        Assert.False(File.Exists(Path.Combine(fixture.Instance, optional.Path!)));
        Assert.Contains(result.Notes, note => note.Contains("1 个可选文件"));
    }

    /// <summary>缺少来源哈希的模组需求不能降级为不验证下载，也不能进入运行安装阶段。</summary>
    [Fact]
    public async Task MissingHashBlocksDownloadAndRuntime()
    {
        var file = Requirement(".minecraft/mods/no-hash.jar", Encoding.UTF8.GetBytes("unverified")) with
        {
            Hashes = new Dictionary<string, string>()
        };
        var fixture = await CreateFixtureAsync(0, [file]);
        var downloader = new FixtureDownloader(fixture.Payloads);
        var runtime = new FixtureRuntimeInstaller();

        var result = await Installer(downloader, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Equal(0, runtime.Calls);
        Assert.Empty(downloader.Requests);
        Assert.Contains(result.Issues, issue => issue.Contains("缺少文件哈希"));
    }

    /// <summary>下载器未确认哈希时不可将文件标记为校验完成，即使下载器已写出内容。</summary>
    [Fact]
    public async Task DownloaderMustConfirmHashBeforeRuntimeCanRun()
    {
        var fixture = await CreateFixtureAsync(1);
        var downloader = new FixtureDownloader(fixture.Payloads) { ConfirmHash = false };
        var runtime = new FixtureRuntimeInstaller();

        var result = await Installer(downloader, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Equal(0, runtime.Calls);
        Assert.Equal(InstanceDownloadState.Failed, Assert.Single(result.Files).State);
        Assert.Contains(result.Issues, issue => issue.Contains("未确认"));
    }

    /// <summary>部分下载失败仍保留其他成功文件及记录，但不会生成一个缺失必需模组的就绪实例。</summary>
    [Fact]
    public async Task PartialDownloadFailureRetainsSuccessesAndBlocksRuntime()
    {
        var fixture = await CreateFixtureAsync(3);
        var downloader = new FixtureDownloader(fixture.Payloads)
        {
            BeforeDownload = request =>
            {
                if (request.DestinationPath.EndsWith("mod-1.jar")) throw new HttpRequestException("simulated network failure");
            }
        };
        var runtime = new FixtureRuntimeInstaller();

        var result = await Installer(downloader, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Equal(0, runtime.Calls);
        Assert.Equal(2, result.Files.Count(file => file.State == InstanceDownloadState.Downloaded));
        Assert.Equal(InstanceDownloadState.Failed, Assert.Single(result.Files, file => file.Requirement.Path!.EndsWith("mod-1.jar")).State);
        Assert.Contains(result.Issues, issue => issue.Contains("simulated network failure"));
    }

    /// <summary>模组文件提交后取消仍返回已完成记录，下次安装可以复用已下载内容。</summary>
    [Fact]
    public async Task CancellationAfterCommittedFileRetainsFileAndResult()
    {
        var fixture = await CreateFixtureAsync(3);
        using var cancellation = new CancellationTokenSource();
        var downloader = new FixtureDownloader(fixture.Payloads) { AfterDownload = _ => cancellation.Cancel() };
        var runtime = new FixtureRuntimeInstaller();

        var result = await Installer(downloader, runtime, new ModrinthInstallOptions { MaxConcurrentFiles = 1 })
            .InstallAsync(fixture.Instance, cancellationToken: cancellation.Token);

        Assert.True(result.IsCanceled);
        Assert.False(result.FilesReady);
        Assert.Equal(0, runtime.Calls);
        Assert.Equal(InstanceDownloadState.Downloaded, Assert.Single(result.Files).State);
        Assert.True(File.Exists(Assert.Single(downloader.Requests).DestinationPath));
    }

    /// <summary>运行阶段取消也保留此前的全部模组下载及运行阶段已经完成的文件记录。</summary>
    [Fact]
    public async Task RuntimeCancellationRetainsBothStagesResults()
    {
        var fixture = await CreateFixtureAsync(1);
        var runtimeFile = Requirement(".minecraft/libraries/test.jar", Encoding.UTF8.GetBytes("runtime"));
        var runtime = new FixtureRuntimeInstaller
        {
            Result = new PrismInstallResult
            {
                IsCanceled = true, Files = [new(runtimeFile, InstanceDownloadState.Downloaded, "runtime downloaded")]
            }
        };

        var result = await Installer(new FixtureDownloader(fixture.Payloads), runtime).InstallAsync(fixture.Instance);

        Assert.True(result.IsCanceled);
        Assert.False(result.FilesReady);
        Assert.Equal(2, result.Files.Count);
        Assert.Null(result.LaunchProfilePath);
    }

    /// <summary>运行阶段抛出可预期错误时，已经成功下载的模组记录不会丢失。</summary>
    [Fact]
    public async Task RuntimeFailureRetainsModResults()
    {
        var fixture = await CreateFixtureAsync(2);
        var runtime = new FixtureRuntimeInstaller { BeforeInstall = () => throw new IOException("runtime metadata failure") };

        var result = await Installer(new FixtureDownloader(fixture.Payloads), runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Equal(2, result.Files.Count);
        Assert.Contains(result.Issues, issue => issue.Contains("runtime metadata failure"));
    }

    /// <summary>下载期间修改清单时阻止继续运行准备，避免旧模组集合与新组件声明混用。</summary>
    [Fact]
    public async Task ChangedManifestBlocksRuntimeAfterPreservingCompletedDownloads()
    {
        var fixture = await CreateFixtureAsync(1);
        var downloader = new FixtureDownloader(fixture.Payloads)
        {
            AfterDownload = _ => File.WriteAllText(fixture.ManifestPath,
                File.ReadAllText(fixture.ManifestPath).Replace("Test Modrinth", "Changed Modrinth"))
        };
        var runtime = new FixtureRuntimeInstaller();

        var result = await Installer(downloader, runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Equal(0, runtime.Calls);
        Assert.Single(result.Files);
        Assert.Contains(result.Issues, issue => issue.Contains("清单发生变化"));
    }

    /// <summary>运行阶段改变清单时也不能把其配置与先前模组检查结果合并为就绪状态。</summary>
    [Fact]
    public async Task ChangedManifestDuringRuntimePreventsReadyResult()
    {
        var fixture = await CreateFixtureAsync(1);
        var runtime = new FixtureRuntimeInstaller
        {
            BeforeInstall = () => File.WriteAllText(fixture.ManifestPath,
                File.ReadAllText(fixture.ManifestPath).Replace("Test Modrinth", "Changed Modrinth"))
        };

        var result = await Installer(new FixtureDownloader(fixture.Payloads), runtime).InstallAsync(fixture.Instance);

        Assert.False(result.FilesReady);
        Assert.Equal(1, runtime.Calls);
        Assert.Null(result.LaunchProfilePath);
        Assert.Single(result.Files);
        Assert.Contains(result.Issues, issue => issue.Contains("清单发生变化"));
    }

    /// <summary>非直接子目录和其他来源格式不得进入 Modrinth 下载流程。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsOutOfScopePathOrWrongSource(bool outside)
    {
        var fixture = await CreateFixtureAsync(1);
        var instance = fixture.Instance;
        if (outside) instance = Path.Combine(fixture.Instance, "nested");
        else await InstanceManifestStore.WriteAsync(fixture.ManifestPath, fixture.Manifest with { SourceFormat = "Prism" }, CancellationToken.None);
        var downloader = new FixtureDownloader(fixture.Payloads);
        var runtime = new FixtureRuntimeInstaller();

        var result = await Installer(downloader, runtime).InstallAsync(instance);

        Assert.False(result.FilesReady);
        Assert.NotEmpty(result.Issues);
        Assert.Empty(downloader.Requests);
        Assert.Equal(0, runtime.Calls);
    }

    /// <summary>开始前已经取消也返回可处理的取消结果，不启动任何下载。</summary>
    [Fact]
    public async Task PreCanceledRequestReturnsCanceledWithoutNetwork()
    {
        var fixture = await CreateFixtureAsync(1);
        var downloader = new FixtureDownloader(fixture.Payloads);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await Installer(downloader, new FixtureRuntimeInstaller())
            .InstallAsync(fixture.Instance, cancellationToken: cancellation.Token);

        Assert.True(result.IsCanceled);
        Assert.Empty(downloader.Requests);
    }

    /// <summary>非法并发配置在创建服务时立即拒绝，不等下载开始后才发生不确定行为。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void InvalidConcurrencyIsRejected(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Installer(new FixtureDownloader(new Dictionary<string, byte[]>()),
            new FixtureRuntimeInstaller(), new ModrinthInstallOptions { MaxConcurrentFiles = value }));
    }

    /// <summary>集中创建被测服务，所有测试都显式注入运行替身，避免网络与真实实例操作。</summary>
    private ModrinthInstanceInstaller Installer(IFileDownloader downloader, IPrismInstanceInstaller runtime,
        ModrinthInstallOptions? options = null) => new(_root, downloader, options, runtime);

    /// <summary>创建含固定下载内容的 Modrinth 清单，可附加包内、可选或错误需求。</summary>
    private async Task<Fixture> CreateFixtureAsync(int downloadableCount, IReadOnlyList<InstanceFileRequirement>? additional = null)
    {
        var instance = Path.Combine(_root, "instances", "Test Modrinth");
        Directory.CreateDirectory(instance);
        var files = new List<InstanceFileRequirement>();
        var payloads = new Dictionary<string, byte[]>();
        for (var index = 0; index < downloadableCount; index++)
        {
            var bytes = Encoding.UTF8.GetBytes("fixed-mod-content-" + index);
            var file = Requirement($".minecraft/mods/mod-{index}.jar", bytes);
            files.Add(file);
            payloads.Add(file.Downloads[0], bytes);
        }
        if (additional is not null) files.AddRange(additional);
        var manifest = new InstanceImportManifest
        {
            SourceFormat = "Modrinth", Name = "Test Modrinth", MinecraftVersion = "1.21.1",
            Components = [new("minecraft", "1.21.1"), new("fabric-loader", "0.16.10")], Files = files
        };
        var manifestPath = Path.Combine(instance, InstanceImportManifest.FileName);
        await InstanceManifestStore.WriteAsync(manifestPath, manifest, CancellationToken.None);
        return new(instance, manifestPath, manifest, payloads, File.ReadAllBytes(manifestPath));
    }

    /// <summary>按固定测试字节生成 Modrinth 常用 SHA-512 校验及多个候选地址。</summary>
    private static InstanceFileRequirement Requirement(string path, byte[] bytes) => new()
    {
        Path = path, Source = InstanceFileSource.DirectDownload, Size = bytes.Length,
        Downloads = ["https://cdn.example.test/" + path, "https://mirror.example.test/" + path],
        Hashes = new Dictionary<string, string> { ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(bytes)) }
    };

    /// <summary>仅向随机测试目录写入固定相对路径的内容。</summary>
    private static void WriteFile(string instance, string relative, byte[] bytes)
    {
        var path = Path.Combine(instance, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>释放测试专用随机目录，不接触桌面实例。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>保存测试实例清单、固定网络内容以及用于检查未被改写的原始字节。</summary>
    private sealed record Fixture(string Instance, string ManifestPath, InstanceImportManifest Manifest,
        Dictionary<string, byte[]> Payloads, byte[] ManifestBytes);

    /// <summary>模拟运行安装阶段并记录是否在模组尚未准备好时被错误调用。</summary>
    private sealed class FixtureRuntimeInstaller : IPrismInstanceInstaller
    {
        public int Calls { get; private set; }
        public Action? BeforeInstall { get; init; }
        public PrismInstallResult Result { get; init; } = new() { SourceFormat = "Modrinth", LaunchProfilePath = "test-profile.json" };

        /// <summary>执行阶段断言并返回可控的就绪、失败或取消结果，不生成真实启动配置。</summary>
        public Task<PrismInstallResult> InstallAsync(string instancePath,
            IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            BeforeInstall?.Invoke();
            return Task.FromResult(Result);
        }
    }

    /// <summary>提供无覆盖的内存内容下载，记录并发峰值，并允许注入失败或提交后的取消。</summary>
    private sealed class FixtureDownloader : IFileDownloader
    {
        private readonly IReadOnlyDictionary<string, byte[]> _payloads;
        private readonly TaskCompletionSource _firstWaveReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _peak;
        public ConcurrentQueue<FileDownloadRequest> Requests { get; } = new();
        public int PeakConcurrent => _peak;
        public int ConcurrencyBarrierTarget { get; init; }
        public bool ConfirmHash { get; init; } = true;
        public Action<FileDownloadRequest>? BeforeDownload { get; init; }
        public Action<FileDownloadRequest>? AfterDownload { get; init; }

        /// <summary>保存预期地址到字节的映射，测试中不访问网络。</summary>
        public FixtureDownloader(IReadOnlyDictionary<string, byte[]> payloads) => _payloads = payloads;

        /// <summary>记录并发请求并无覆盖写入固定内容；提交后回调可验证取消时不会丢失结果。</summary>
        public async Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request,
            IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            var active = Interlocked.Increment(ref _active);
            int observed;
            do { observed = Volatile.Read(ref _peak); }
            while (active > observed && Interlocked.CompareExchange(ref _peak, active, observed) != observed);
            try
            {
                BeforeDownload?.Invoke(request);
                if (ConcurrencyBarrierTarget > 0)
                {
                    if (active >= ConcurrencyBarrierTarget) _firstWaveReady.TrySetResult();
                    await _firstWaveReady.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                }
                var bytes = _payloads[request.Urls[0]];
                Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
                await using (var output = new FileStream(request.DestinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await output.WriteAsync(bytes, cancellationToken);
                AfterDownload?.Invoke(request);
                return new(request.DestinationPath, bytes.Length, ConfirmHash);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
