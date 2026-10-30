using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Infrastructure.Downloads;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Modrinth;

namespace EML.Tests;

/// <summary>用真实清单检查器和可控下载替身验证补全流程；只访问测试临时目录，不访问网络。</summary>
public sealed class MinecraftInstanceDownloaderTests : IDisposable
{
    private const string DownloadContent = "verified jar bytes";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-download-workflow-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>生成带可信测试哈希的直接下载需求，目标始终相对实例根目录。</summary>
    private static InstanceFileRequirement Direct(string path, bool required = true, string content = DownloadContent) => new()
    {
        Path = path, Source = InstanceFileSource.DirectDownload, Required = required,
        Size = Encoding.UTF8.GetByteCount(content), Downloads = ["https://example.com/test.jar"],
        Hashes = new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))) }
    };

    /// <summary>创建一个独立实例和规范清单，避免测试依赖用户真实整合包。</summary>
    private async Task<string> CreateInstanceAsync(IReadOnlyList<InstanceFileRequirement> files,
        string gameDirectory = ".minecraft", IReadOnlyList<InstanceComponentRequirement>? components = null,
        string sourceFormat = "Zip")
    {
        var instance = Path.Combine(_root, "instances", "Test instance");
        Directory.CreateDirectory(instance);
        if (gameDirectory != ".") Directory.CreateDirectory(Path.Combine(instance, gameDirectory));
        await InstanceManifestStore.WriteAsync(Path.Combine(instance, InstanceImportManifest.FileName), new InstanceImportManifest
        {
            Name = "Original import name", GameDirectory = gameDirectory, Files = files, Components = components ?? [],
            SourceFormat = sourceFormat
        }, CancellationToken.None);
        return instance;
    }

    /// <summary>把指定测试内容写到实例相对路径，用于模拟已有文件及用户修改。</summary>
    private static string WriteExisting(string instance, string relative, string content)
    {
        var path = Path.Combine(instance, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>构造使用真实文件检查器的补全服务，下载行为由测试替身决定。</summary>
    private MinecraftInstanceDownloader CreateService(FakeDownloader downloader) =>
        new(_root, new MinecraftInstanceChecker(_root), downloader);

    /// <summary>串联真实 mrpack 导入、格式路由和 HTTP 下载；运行阶段使用替身，独立验证模组清单补全。</summary>
    [Fact]
    public async Task ImportsMrpackAndDownloadsMissingJarThroughRealServices()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "workflow.mrpack");
        var jar = Encoding.UTF8.GetBytes(DownloadContent);
        const string configuration = "user-setting=true";
        const string url = "https://example.com/integration.jar";
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var index = archive.CreateEntry("modrinth.index.json");
            using (var stream = index.Open())
                JsonSerializer.Serialize(stream, new
                {
                    formatVersion = 1, game = "minecraft", versionId = "1.0", name = "Integration pack",
                    files = new[]
                    {
                        new
                        {
                            path = "mods/integration.jar", fileSize = jar.Length, downloads = new[] { url },
                            hashes = new
                            {
                                sha1 = Convert.ToHexStringLower(SHA1.HashData(jar)),
                                sha512 = Convert.ToHexStringLower(SHA512.HashData(jar))
                            },
                            env = new { client = "required", server = "required" }
                        }
                    },
                    dependencies = new Dictionary<string, string> { ["minecraft"] = "1.20.1", ["fabric-loader"] = "0.16.10" }
                });
            using var config = new StreamWriter(archive.CreateEntry("overrides/config/test.cfg").Open(), new UTF8Encoding(false));
            config.Write(configuration);
        }

        var imported = await new MinecraftInstanceImporter(_root).ImportAsync(archivePath, "HTTP integration");
        var manifestPath = Path.Combine(imported.InstancePath, InstanceImportManifest.FileName);
        var baseline = File.ReadAllBytes(manifestPath);
        var checker = new MinecraftInstanceChecker(_root);
        var before = await checker.CheckAsync(imported.InstancePath);
        Assert.Contains(before.Files, file => file.Requirement.Path == ".minecraft/mods/integration.jar" && file.State == InstanceFileState.Missing);
        Assert.Contains(before.Files, file => file.Requirement.Path == ".minecraft/config/test.cfg" && file.State == InstanceFileState.Verified);
        using var handler = new JarResponseHandler(jar);
        using var downloader = new HttpFileDownloader(handler);
        var runtimeStub = new SuccessfulRuntimeInstaller(new PrismInstallResult
        {
            SourceFormat = "Modrinth", Issues = ["测试只覆盖清单下载，运行阶段由替身阻断。"]
        });
        var modrinth = new ModrinthInstanceInstaller(_root, downloader, runtimeInstaller: runtimeStub);
        var service = new MinecraftInstanceDownloader(_root, checker, downloader, modrinthInstaller: modrinth);

        var report = await service.DownloadMissingAsync(imported.InstancePath);

        Assert.Equal(url, Assert.Single(handler.Requests).AbsoluteUri);
        Assert.Equal(InstanceDownloadState.Downloaded, Assert.Single(report.Files).State);
        Assert.False(report.IsCanceled);
        Assert.Null(report.CheckError);
        Assert.Equal(imported.InstancePath, Assert.Single(runtimeStub.Instances));
        Assert.Equal("Modrinth", report.Runtime!.SourceFormat);
        Assert.False(report.Runtime.FilesReady);
        var after = Assert.IsType<InstanceCheckReport>(report.CheckReport);
        Assert.Equal(InstanceCheckMode.Full, after.Mode);
        Assert.All(after.Files, file => Assert.Equal(InstanceFileState.Verified, file.State));
        Assert.Equal(jar, File.ReadAllBytes(Path.Combine(imported.GameDirectory, "mods", "integration.jar")));
        Assert.Equal(configuration, File.ReadAllText(Path.Combine(imported.GameDirectory, "config", "test.cfg")));
        Assert.Equal(baseline, File.ReadAllBytes(manifestPath));
        Assert.Empty(Directory.EnumerateFiles(imported.InstancePath, "*.part", SearchOption.AllDirectories));
        Assert.Contains(after.Components, component => component.Id == "fabric-loader" && component.Version == "0.16.10");
    }

    /// <summary>只下载必需且缺失的远程文件，保留可选文件、已有模组、用户配置和额外文件。</summary>
    [Fact]
    public async Task DownloadsOnlyRequiredMissingVerifiableFilesAndPreservesExistingContents()
    {
        var requirements = new[]
        {
            Direct(".minecraft/mods/missing.jar"), Direct(".minecraft/mods/optional.jar", required: false),
            Direct(".minecraft/mods/present.jar"), Direct(".minecraft/mods/size-mismatch.jar"),
            Direct(".minecraft/mods/hash-mismatch.jar"),
            Direct(".minecraft/config/user.cfg", content: "original configuration") with
            { Source = InstanceFileSource.Embedded, Downloads = [] }
        };
        var instance = await CreateInstanceAsync(requirements);
        WriteExisting(instance, requirements[2].Path!, DownloadContent);
        var sizeMismatch = WriteExisting(instance, requirements[3].Path!, "changed");
        var hashMismatch = WriteExisting(instance, requirements[4].Path!, new string('x', DownloadContent.Length));
        var config = WriteExisting(instance, requirements[5].Path!, "user configuration has changed");
        var extra = WriteExisting(instance, ".minecraft/mods/extra.jar", "user installed extra");
        var manifestPath = Path.Combine(instance, InstanceImportManifest.FileName);
        var originalManifest = File.ReadAllBytes(manifestPath);
        var downloader = new FakeDownloader();

        var report = await CreateService(downloader).DownloadMissingAsync(instance);

        var request = Assert.Single(downloader.Requests);
        Assert.Equal(Path.Combine(instance, ".minecraft", "mods", "missing.jar"), request.DestinationPath);
        Assert.Equal(requirements[0].Size, request.ExpectedSize);
        Assert.Equal(requirements[0].Hashes["sha256"], request.Hashes["sha256"]);
        Assert.Single(report.Files, file => file.State == InstanceDownloadState.Downloaded);
        Assert.DoesNotContain(report.Files, file => file.Requirement.Path == requirements[1].Path);
        Assert.False(File.Exists(Path.Combine(instance, ".minecraft", "mods", "optional.jar")));
        Assert.Equal("changed", File.ReadAllText(sizeMismatch));
        Assert.Equal(new string('x', DownloadContent.Length), File.ReadAllText(hashMismatch));
        Assert.Equal("user configuration has changed", File.ReadAllText(config));
        Assert.Equal("user installed extra", File.ReadAllText(extra));
        Assert.Equal(originalManifest, File.ReadAllBytes(manifestPath));
        var check = Assert.IsType<InstanceCheckReport>(report.CheckReport);
        Assert.Equal(InstanceCheckMode.Full, check.Mode);
        Assert.Contains(check.Files, file => file.Requirement.Path == requirements[0].Path && file.State == InstanceFileState.Verified);
        Assert.Contains(check.Files, file => file.Requirement.Path == requirements[4].Path && file.State == InstanceFileState.HashMismatch);
        Assert.Contains(check.Files, file => file.Requirement.Path == requirements[5].Path && file.State == InstanceFileState.ChangedSinceImport);
    }

    /// <summary>清单路径直接相对实例根目录组合，兼容有点、无点以及实例根本身的游戏目录。</summary>
    [Theory]
    [InlineData(".minecraft", ".minecraft/mods/test.jar")]
    [InlineData("minecraft", "minecraft/mods/test.jar")]
    [InlineData(".", "mods/test.jar")]
    public async Task UsesInstanceRelativePathsForEveryGameDirectoryLayout(string gameDirectory, string relative)
    {
        var instance = await CreateInstanceAsync([Direct(relative)], gameDirectory);
        var downloader = new FakeDownloader();

        var report = await CreateService(downloader).DownloadMissingAsync(instance);

        var request = Assert.Single(downloader.Requests);
        Assert.Equal(Path.Combine(instance, relative.Replace('/', Path.DirectorySeparatorChar)), request.DestinationPath);
        Assert.Equal(DownloadContent, File.ReadAllText(request.DestinationPath));
        Assert.Equal(InstanceFileState.Verified, Assert.Single(report.CheckReport!.Files).State);
    }

    /// <summary>包内缺失文件、未解析平台引用和无校验值需求均报告真实原因，组件保持待解析声明。</summary>
    [Fact]
    public async Task ReportsUnavailableSourcesWithoutInventingDownloadUrls()
    {
        var embedded = Direct(".minecraft/mods/embedded.jar") with { Source = InstanceFileSource.Embedded, Downloads = [] };
        var unresolved = new InstanceFileRequirement { Source = InstanceFileSource.CurseForge, ProjectId = 12, FileId = 34 };
        var resolvedMissing = unresolved with { Path = ".minecraft/mods/curse.jar", ProjectId = 56, FileId = 78 };
        var noHash = Direct(".minecraft/mods/no-hash.jar") with { Hashes = new Dictionary<string, string>() };
        var component = new InstanceComponentRequirement("net.minecraftforge", "10.13.4.1614");
        var instance = await CreateInstanceAsync([embedded, unresolved, resolvedMissing, noHash], components: [component]);
        var downloader = new FakeDownloader();

        var report = await CreateService(downloader).DownloadMissingAsync(instance);

        Assert.Empty(downloader.Requests);
        Assert.Equal(4, report.Files.Count);
        Assert.All(report.Files, file => Assert.Equal(InstanceDownloadState.Unavailable, file.State));
        Assert.Contains("原压缩包", Assert.Single(report.Files, file => file.Requirement.Path == embedded.Path).Message);
        Assert.Contains("尚未解析", Assert.Single(report.Files, file => file.Requirement.Path is null).Message);
        Assert.Contains("CurseForge", Assert.Single(report.Files, file => file.Requirement.Path == resolvedMissing.Path).Message);
        Assert.Contains("校验值", Assert.Single(report.Files, file => file.Requirement.Path == noHash.Path).Message);
        Assert.Equal(component, Assert.Single(report.CheckReport!.Components));
        Assert.Contains(report.CheckReport.Files, file => file.Requirement.Path is null && file.State == InstanceFileState.Unresolved);
    }

    /// <summary>单个网络下载失败不阻断后续文件，复查仍显示失败项缺失以及成功项通过。</summary>
    [Fact]
    public async Task ContinuesAfterOneDownloadFailsAndRechecksBothResults()
    {
        var instance = await CreateInstanceAsync([Direct(".minecraft/mods/fails.jar"), Direct(".minecraft/mods/succeeds.jar")]);
        var downloader = new FakeDownloader
        {
            Handler = (request, token) => Path.GetFileName(request.DestinationPath) == "fails.jar"
                ? Task.FromException<FileDownloadResult>(new HttpRequestException("simulated unavailable server"))
                : SaveDownloadAsync(request, token)
        };

        var report = await CreateService(downloader).DownloadMissingAsync(instance);

        Assert.Equal(2, downloader.Requests.Count);
        Assert.Single(report.Files, file => file.State == InstanceDownloadState.Failed && file.Message.Contains("simulated"));
        Assert.Single(report.Files, file => file.State == InstanceDownloadState.Downloaded);
        Assert.Contains(report.CheckReport!.Files, file => file.Requirement.Path!.EndsWith("fails.jar") && file.State == InstanceFileState.Missing);
        Assert.Contains(report.CheckReport.Files, file => file.Requirement.Path!.EndsWith("succeeds.jar") && file.State == InstanceFileState.Verified);
        Assert.False(report.IsCanceled);
    }

    /// <summary>单文件超时或校验失败只记录当前失败，后续缺失文件仍继续下载并复查。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimeoutOrIntegrityFailureDoesNotStopFollowingDownloads(bool timeout)
    {
        var instance = await CreateInstanceAsync([Direct(".minecraft/mods/fails.jar"), Direct(".minecraft/mods/next.jar")]);
        Exception failure = timeout ? new TimeoutException("simulated timeout") : new InvalidDataException("simulated hash mismatch");
        var downloader = new FakeDownloader
        {
            Handler = (request, token) => Path.GetFileName(request.DestinationPath) == "fails.jar"
                ? Task.FromException<FileDownloadResult>(failure) : SaveDownloadAsync(request, token)
        };

        var report = await CreateService(downloader).DownloadMissingAsync(instance);

        Assert.Equal(2, downloader.Requests.Count);
        Assert.Equal(failure.Message, Assert.Single(report.Files, file => file.State == InstanceDownloadState.Failed).Message);
        Assert.Single(report.Files, file => file.State == InstanceDownloadState.Downloaded);
        Assert.Null(report.CheckError);
        Assert.False(report.IsCanceled);
        Assert.Contains(report.CheckReport!.Files, file => file.Requirement.Path!.EndsWith("fails.jar") && file.State == InstanceFileState.Missing);
        Assert.Contains(report.CheckReport.Files, file => file.Requirement.Path!.EndsWith("next.jar") && file.State == InstanceFileState.Verified);
    }

    /// <summary>下载器没有确认哈希时不声称下载校验成功，落盘内容是否正确交由最终完整检查确定。</summary>
    [Fact]
    public async Task UnverifiedDownloadResultIsFailureEvenWhenFinalCheckVerifiesTheFile()
    {
        var instance = await CreateInstanceAsync([Direct(".minecraft/mods/unverified.jar")]);
        var downloader = new FakeDownloader
        {
            Handler = async (request, token) => (await SaveDownloadAsync(request, token)) with { HashVerified = false }
        };

        var report = await CreateService(downloader).DownloadMissingAsync(instance);

        Assert.Equal(InstanceDownloadState.Failed, Assert.Single(report.Files).State);
        Assert.Contains("未确认", report.Files[0].Message);
        Assert.DoesNotContain(report.Files, file => file.State == InstanceDownloadState.Downloaded);
        Assert.Equal(InstanceFileState.Verified, Assert.Single(report.CheckReport!.Files).State);
        Assert.Equal(DownloadContent, File.ReadAllText(Assert.Single(downloader.Requests).DestinationPath));
    }

    /// <summary>成功提交第一个文件后取消只停止余下任务，保留成功记录并丢弃过期检查快照。</summary>
    [Fact]
    public async Task CancellationAfterFirstCommitPreservesCompletedFileAndClearsCheckSnapshot()
    {
        var instance = await CreateInstanceAsync([Direct(".minecraft/mods/first.jar"), Direct(".minecraft/mods/second.jar")]);
        using var cancellation = new CancellationTokenSource();
        var downloader = new FakeDownloader
        {
            Handler = async (request, token) =>
            {
                var result = await SaveDownloadAsync(request, token);
                cancellation.Cancel();
                return result;
            }
        };

        var report = await CreateService(downloader).DownloadMissingAsync(instance, cancellationToken: cancellation.Token);

        Assert.True(report.IsCanceled);
        Assert.Null(report.CheckReport);
        Assert.Null(report.CheckError);
        Assert.Equal(InstanceDownloadState.Downloaded, Assert.Single(report.Files).State);
        Assert.Single(downloader.Requests);
        Assert.Equal(DownloadContent, File.ReadAllText(Path.Combine(instance, ".minecraft", "mods", "first.jar")));
        Assert.False(File.Exists(Path.Combine(instance, ".minecraft", "mods", "second.jar")));
    }

    /// <summary>下载已完成但最终复查被取消时，成功文件与记录仍有效，不把旧快速检查当作最终结果。</summary>
    [Fact]
    public async Task CancellationDuringFinalCheckPreservesDownloadedFiles()
    {
        var instance = await CreateInstanceAsync([Direct(".minecraft/mods/finished.jar")]);
        using var cancellation = new CancellationTokenSource();
        var checker = new CancelFinalCheck(new MinecraftInstanceChecker(_root), cancellation);
        var downloader = new FakeDownloader();
        var service = new MinecraftInstanceDownloader(_root, checker, downloader);

        var report = await service.DownloadMissingAsync(instance, cancellationToken: cancellation.Token);

        Assert.Equal(new[] { InstanceCheckMode.Quick, InstanceCheckMode.Full }, checker.Modes);
        Assert.True(report.IsCanceled);
        Assert.Null(report.CheckReport);
        Assert.Equal(InstanceDownloadState.Downloaded, Assert.Single(report.Files).State);
        Assert.Equal(DownloadContent, File.ReadAllText(Assert.Single(downloader.Requests).DestinationPath));
    }

    /// <summary>各格式的依赖已落盘后，首次快速检查失败或取消仍保留运行准备结果及下载记录。</summary>
    [Theory]
    [InlineData("Prism", false)]
    [InlineData("Prism", true)]
    [InlineData("Modrinth", false)]
    [InlineData("Modrinth", true)]
    [InlineData("CurseForge", false)]
    [InlineData("CurseForge", true)]
    public async Task InitialQuickCheckFailureAfterFormatInstallPreservesCompletedRuntime(string sourceFormat, bool cancelCheck)
    {
        var instance = await CreateInstanceAsync([], sourceFormat: sourceFormat);
        var completed = new InstanceDownloadFileResult(Direct(".minecraft/libraries/prepared.jar"),
            InstanceDownloadState.Downloaded, "测试运行依赖已下载并校验。");
        var runtime = new PrismInstallResult
        {
            SourceFormat = sourceFormat,
            Files = [completed],
            LaunchProfilePath = ".minecraft/versions/prepared/prepared.json"
        };
        using var cancellation = new CancellationTokenSource();
        var installer = new SuccessfulRuntimeInstaller(runtime);
        var checker = new FailInitialCheck(cancelCheck ? cancellation : null);
        var downloader = new FakeDownloader();
        var service = new MinecraftInstanceDownloader(_root, checker, downloader,
            prismInstaller: sourceFormat == "Prism" ? installer : null,
            modrinthInstaller: sourceFormat == "Modrinth" ? installer : null,
            curseForgeInstaller: sourceFormat == "CurseForge" ? installer : null);

        var report = await service.DownloadMissingAsync(instance, cancellationToken: cancellation.Token);

        Assert.Equal(instance, Assert.Single(installer.Instances));
        Assert.Equal(InstanceCheckMode.Quick, Assert.Single(checker.Modes));
        Assert.Same(runtime, report.Runtime);
        Assert.Same(completed, Assert.Single(report.Files));
        Assert.Equal(DownloadContent, File.ReadAllText(Path.Combine(instance, ".minecraft", "libraries", "prepared.jar")));
        Assert.Empty(downloader.Requests);
        Assert.Null(report.CheckReport);
        Assert.Equal(cancelCheck, report.IsCanceled);
        if (cancelCheck) Assert.Null(report.CheckError);
        else Assert.Equal(FailInitialCheck.FailureMessage, report.CheckError);
    }

    /// <summary>平台已报告失败的清单文件不被通用阶段再次下载，避免重复请求及绕过分发限制。</summary>
    [Theory]
    [InlineData("Modrinth")]
    [InlineData("CurseForge")]
    public async Task PlatformFailedFileIsNotRetriedByGenericDownloadStage(string format)
    {
        var requirement = Direct(".minecraft/mods/failed.jar");
        var instance = await CreateInstanceAsync([requirement], sourceFormat: format,
            components: [new("minecraft", "1.20.1")]);
        var failed = new InstanceDownloadFileResult(requirement, InstanceDownloadState.Failed, "本次网络不可用。");
        var runtime = new PrismInstallResult { SourceFormat = format, Files = [failed], Issues = [failed.Message] };
        var installer = new SuccessfulRuntimeInstaller(runtime);
        var downloader = new FakeDownloader();
        var service = new MinecraftInstanceDownloader(_root, new MinecraftInstanceChecker(_root), downloader,
            modrinthInstaller: format == "Modrinth" ? installer : null,
            curseForgeInstaller: format == "CurseForge" ? installer : null);

        var report = await service.DownloadMissingAsync(instance);

        Assert.Same(failed, Assert.Single(report.Files));
        Assert.Same(runtime, report.Runtime);
        Assert.Empty(downloader.Requests);
        Assert.Equal(InstanceFileState.Missing, Assert.Single(report.CheckReport!.Files).State);
        Assert.Equal(format, report.CheckReport.RuntimeSourceFormat);
    }

    /// <summary>平台安装器取消时直接返回已完成记录，不继续检查或再次下载。</summary>
    [Theory]
    [InlineData("Modrinth")]
    [InlineData("CurseForge")]
    public async Task CanceledPlatformInstallerPreservesCompletedRecordsWithoutChecking(string format)
    {
        var instance = await CreateInstanceAsync([], sourceFormat: format);
        var completed = new InstanceDownloadFileResult(Direct(".minecraft/mods/completed.jar"),
            InstanceDownloadState.Downloaded, "已完成。");
        var runtime = new PrismInstallResult { SourceFormat = format, Files = [completed], IsCanceled = true };
        var installer = new SuccessfulRuntimeInstaller(runtime);
        var checker = new FailInitialCheck(null);
        var downloader = new FakeDownloader();
        var service = new MinecraftInstanceDownloader(_root, checker, downloader,
            modrinthInstaller: format == "Modrinth" ? installer : null,
            curseForgeInstaller: format == "CurseForge" ? installer : null);

        var report = await service.DownloadMissingAsync(instance);

        Assert.True(report.IsCanceled);
        Assert.Same(completed, Assert.Single(report.Files));
        Assert.Same(runtime, report.Runtime);
        Assert.Null(report.CheckReport);
        Assert.Empty(checker.Modes);
        Assert.Empty(downloader.Requests);
        Assert.Equal(DownloadContent, File.ReadAllText(Path.Combine(instance, ".minecraft", "mods", "completed.jar")));
    }

    /// <summary>检查后出现同名用户文件时拒绝覆盖，并继续处理其他缺失文件。</summary>
    [Fact]
    public async Task FileCreatedAfterCheckIsPreservedAndDoesNotStopOtherDownloads()
    {
        var instance = await CreateInstanceAsync([Direct(".minecraft/mods/race.jar"), Direct(".minecraft/mods/next.jar")]);
        var downloader = new FakeDownloader
        {
            Handler = (request, token) =>
            {
                if (Path.GetFileName(request.DestinationPath) == "race.jar")
                    WriteExisting(instance, ".minecraft/mods/race.jar", "user created this file after checking");
                return SaveDownloadAsync(request, token);
            }
        };

        var report = await CreateService(downloader).DownloadMissingAsync(instance);

        Assert.Equal("user created this file after checking", File.ReadAllText(Path.Combine(instance, ".minecraft", "mods", "race.jar")));
        Assert.Single(report.Files, file => file.State == InstanceDownloadState.Failed);
        Assert.Single(report.Files, file => file.State == InstanceDownloadState.Downloaded);
        Assert.Equal(DownloadContent, File.ReadAllText(Path.Combine(instance, ".minecraft", "mods", "next.jar")));
    }

    /// <summary>下载中实例被外部移动后不能依据旧选择重建目录，已完成文件仍保留在移动后的实例里。</summary>
    [Fact]
    public async Task InstanceMovedBetweenFilesIsNotRecreatedAtItsOldPath()
    {
        var instance = await CreateInstanceAsync([Direct(".minecraft/mods/first.jar"), Direct(".minecraft/mods/second.jar")]);
        var moved = Path.Combine(_root, "moved-instance");
        var downloader = new FakeDownloader
        {
            Handler = async (request, token) =>
            {
                var result = await SaveDownloadAsync(request, token);
                Directory.Move(instance, moved);
                return result;
            }
        };

        var report = await CreateService(downloader).DownloadMissingAsync(instance);

        Assert.Single(downloader.Requests);
        Assert.Single(report.Files, file => file.State == InstanceDownloadState.Downloaded);
        Assert.Single(report.Files, file => file.State == InstanceDownloadState.Failed);
        Assert.False(Directory.Exists(instance));
        Assert.Equal(DownloadContent, File.ReadAllText(Path.Combine(moved, ".minecraft", "mods", "first.jar")));
        Assert.False(File.Exists(Path.Combine(moved, ".minecraft", "mods", "second.jar")));
        Assert.Null(report.CheckReport);
        Assert.False(string.IsNullOrWhiteSpace(report.CheckError));
    }

    /// <summary>缺少清单时不猜测下载任务，并明确保留缺少检查依据的状态。</summary>
    [Fact]
    public async Task MissingManifestDoesNotTriggerDownloadsOrClaimVerification()
    {
        var instance = Path.Combine(_root, "instances", "No manifest");
        Directory.CreateDirectory(instance);
        var downloader = new FakeDownloader();

        var report = await CreateService(downloader).DownloadMissingAsync(instance);

        Assert.Empty(downloader.Requests);
        Assert.Empty(report.Files);
        Assert.False(report.CheckReport!.HasManifest);
        Assert.Empty(report.CheckReport.Files);
        Assert.Contains(report.CheckReport.Notes, note => note.Contains("eml-instance.json"));
    }

    /// <summary>最终复查期间发现清单损坏时保留已完成下载，并独立报告复查错误。</summary>
    [Fact]
    public async Task InvalidManifestAtFinalCheckPreservesCompletedDownloadsAndReportsCheckError()
    {
        var instance = await CreateInstanceAsync([Direct(".minecraft/mods/finished.jar")]);
        var downloader = new FakeDownloader
        {
            Handler = async (request, token) =>
            {
                var result = await SaveDownloadAsync(request, token);
                File.WriteAllText(Path.Combine(instance, InstanceImportManifest.FileName), "{ invalid json");
                return result;
            }
        };

        var report = await CreateService(downloader).DownloadMissingAsync(instance);

        Assert.False(report.IsCanceled);
        Assert.Null(report.CheckReport);
        Assert.False(string.IsNullOrWhiteSpace(report.CheckError));
        Assert.Equal(InstanceDownloadState.Downloaded, Assert.Single(report.Files).State);
        Assert.Equal(DownloadContent, File.ReadAllText(Assert.Single(downloader.Requests).DestinationPath));
    }

    /// <summary>根目录以外和嵌套子目录不能作为实例下载目标，即使对应文件夹确实存在。</summary>
    [Theory]
    [InlineData("outside")]
    [InlineData("instances/parent/nested")]
    public async Task RejectsTargetsOutsideDirectInstanceScope(string relative)
    {
        var destination = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(destination);
        var downloader = new FakeDownloader();

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(downloader).DownloadMissingAsync(destination));

        Assert.Empty(downloader.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    /// <summary>开始前就被取消时直接结束，不触发网络下载或生成目标文件。</summary>
    [Fact]
    public async Task CancellationBeforeStartingDoesNotDownloadAnything()
    {
        var instance = await CreateInstanceAsync([Direct(".minecraft/mods/test.jar")]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var downloader = new FakeDownloader();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(downloader).DownloadMissingAsync(instance, cancellationToken: cancellation.Token));

        Assert.Empty(downloader.Requests);
        Assert.False(File.Exists(Path.Combine(instance, ".minecraft", "mods", "test.jar")));
    }

    /// <summary>模拟成功下载的无覆盖提交，返回内容哈希已校验的结果；真实 HTTP 行为由下载器单元测试验证。</summary>
    private static async Task<FileDownloadResult> SaveDownloadAsync(FileDownloadRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
        var bytes = Encoding.UTF8.GetBytes(DownloadContent);
        await using var output = new FileStream(request.DestinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(bytes, token);
        return new(request.DestinationPath, bytes.Length, HashVerified: true);
    }

    /// <summary>只删除本测试类创建且带唯一标识的临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>记录下载请求并执行注入行为，供流程测试模拟成功、失败、竞争及取消。</summary>
    private sealed class FakeDownloader : IFileDownloader
    {
        public List<FileDownloadRequest> Requests { get; } = [];
        public Func<FileDownloadRequest, CancellationToken, Task<FileDownloadResult>>? Handler { get; init; }

        /// <summary>保存请求快照后调用指定处理方法；默认写入固定测试内容。</summary>
        public Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request,
            IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return (Handler ?? SaveDownloadAsync)(request, cancellationToken);
        }
    }

    /// <summary>提供内存中的 JAR 响应并记录 URL，使真实 HTTP 下载器完全离线运行。</summary>
    private sealed class JarResponseHandler : HttpMessageHandler
    {
        private readonly byte[] _jar;
        public List<Uri> Requests { get; } = [];

        /// <summary>保存测试内容，后续请求返回独立的响应及内容对象。</summary>
        public JarResponseHandler(byte[] jar) => _jar = jar;

        /// <summary>记录请求并返回固定成功内容；不建立任何真实网络连接。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_jar) });
        }
    }

    /// <summary>快速检查使用真实实现，仅在完整复查入口取消以验证两阶段流程的区别。</summary>
    private sealed class CancelFinalCheck : IMinecraftInstanceChecker
    {
        private readonly IMinecraftInstanceChecker _inner;
        private readonly CancellationTokenSource _cancellation;
        public List<InstanceCheckMode> Modes { get; } = [];

        /// <summary>保存实际检查器和取消源，避免伪造快速检查的文件状态。</summary>
        public CancelFinalCheck(IMinecraftInstanceChecker inner, CancellationTokenSource cancellation)
        {
            _inner = inner;
            _cancellation = cancellation;
        }

        /// <summary>记录两次检查的模式，进入最终复查时触发调用者取消。</summary>
        public Task<InstanceCheckReport> CheckAsync(string instancePath, InstanceCheckMode mode = InstanceCheckMode.Full,
            IProgress<InstanceCheckProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Modes.Add(mode);
            if (mode == InstanceCheckMode.Full)
            {
                _cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return _inner.CheckAsync(instancePath, mode, progress, cancellationToken);
        }
    }

    /// <summary>模拟格式安装器返回结果，用于隔离已下载文件与后续检查失败的行为。</summary>
    private sealed class SuccessfulRuntimeInstaller : IPrismInstanceInstaller, IModrinthInstanceInstaller, ICurseForgeInstanceInstaller
    {
        private readonly PrismInstallResult _result;
        public List<string> Instances { get; } = [];

        /// <summary>保存将要交还补全服务的结果对象，便于验证失败时没有丢失它。</summary>
        public SuccessfulRuntimeInstaller(PrismInstallResult result) => _result = result;

        /// <summary>写入已完成的测试依赖后返回成功结果，不访问网络或生成真实启动配置。</summary>
        public Task<PrismInstallResult> InstallAsync(string instancePath,
            IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Instances.Add(instancePath);
            foreach (var file in _result.Files.Where(file => file.State is InstanceDownloadState.Downloaded or InstanceDownloadState.DownloadedUnverified))
                WriteExisting(instancePath, file.Requirement.Path!, DownloadContent);
            return Task.FromResult(_result);
        }
    }

    /// <summary>在初始检查入口抛出磁盘错误或调用者取消，避免依赖真实文件竞争。</summary>
    private sealed class FailInitialCheck : IMinecraftInstanceChecker
    {
        public const string FailureMessage = "simulated initial quick check failure";
        private readonly CancellationTokenSource? _cancellation;
        public List<InstanceCheckMode> Modes { get; } = [];

        /// <summary>有取消源时触发取消，否则模拟读取清单期间的 IO 错误。</summary>
        public FailInitialCheck(CancellationTokenSource? cancellation) => _cancellation = cancellation;

        /// <summary>记录检查模式并立即失败，使测试能证明从未进入最终复查。</summary>
        public Task<InstanceCheckReport> CheckAsync(string instancePath, InstanceCheckMode mode = InstanceCheckMode.Full,
            IProgress<InstanceCheckProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Modes.Add(mode);
            if (_cancellation is not null)
            {
                _cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Task.FromException<InstanceCheckReport>(new IOException(FailureMessage));
        }
    }
}
