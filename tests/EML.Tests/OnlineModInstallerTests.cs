using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods;
using EML.Core.Minecraft.Mods.Online;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Mods;
using EML.Infrastructure.Minecraft.Mods.Online;

namespace EML.Tests;

/// <summary>使用临时实例和可控网络替身验证真实计划、下载校验及本地管理提交链。</summary>
public sealed class OnlineModInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eml-online-install-" + Guid.NewGuid().ToString("N"));
    private readonly Catalog _catalog = new();
    private readonly Downloader _downloader = new();
    private string Instance => Path.Combine(_root, "instances", "Test");
    private LocalModManager Manager => new(_root);
    private OnlineModInstaller Service => new(_root, _catalog, _downloader, Manager);

    /// <summary>每个测试独立拥有临时目录，清理不会涉及真实玩家实例。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    /// <summary>最小导入清单足以验证模组安装，测试不创建或运行真实 Minecraft。</summary>
    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Instance, ".minecraft", "mods"));
        await InstanceManifestStore.WriteAsync(Path.Combine(Instance, InstanceImportManifest.FileName), new()
        {
            SourceFormat = "Zip", Name = "Test", MinecraftVersion = "1.20.1", GameDirectory = ".minecraft",
            Components = [new("fabric", "0.16.0")]
        }, default);
    }

    /// <summary>固定项目、版本和官方摘要；替身下载的是实际包含模组元数据的归档。</summary>
    private OnlineModVersion Version(string id, params OnlineModDependency[] dependencies)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(false)))
            writer.Write("{\"schemaVersion\":1,\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"version\":\"1\"}");
        var bytes = buffer.ToArray();
        var url = "https://cdn.modrinth.com/data/" + id + "/versions/one/" + id + ".jar";
        _downloader.Files[url] = bytes;
        var version = new OnlineModVersion
        {
            Project = new(ModProvider.Modrinth, id), Id = id + "-v1", Name = id, VersionNumber = "1", Channel = "release",
            GameVersions = ["1.20.1"], Loaders = ["fabric"], Dependencies = dependencies,
            Files = [new() { FileName = id + ".jar", Primary = true, Requirement = new()
            {
                Source = InstanceFileSource.DirectDownload, Origin = InstanceFileOrigin.ModrinthDownload, Size = bytes.Length,
                Downloads = [url], Hashes = new Dictionary<string, string> { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(bytes)), ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(bytes)) },
                ModrinthOrigin = new() { ProjectId = id, VersionId = id + "-v1", FileName = id + ".jar" }
            } }]
        };
        _catalog.Versions[(version.Project, version.Id)] = version;
        return version;
    }

    /// <summary>构造具有稳定平台身份的依赖；名称相同不会合并其他项目。</summary>
    private static OnlineModDependency Dependency(OnlineModVersion version, OnlineDependencyKind kind = OnlineDependencyKind.Required) => new()
    {
        Key = version.Project.Provider + ":" + version.Project.ProjectId + ":" + kind,
        Project = version.Project, VersionId = version.Id, Name = version.Name, Kind = kind
    };

    /// <summary>模拟用户从详情页确认精确文件。</summary>
    private OnlineModPlanRequest Request(OnlineModVersion version) => new()
    { InstancePath = Instance, Project = version.Project, VersionId = version.Id, FileName = version.Files[0].FileName };

    /// <summary>默认递归加入必要依赖且先安装依赖，可选依赖不会被偷偷加入。</summary>
    [Fact]
    public async Task RequiredDefaultOnOptionalDefaultOff()
    {
        await InitializeAsync(); var library = Version("library"); var optional = Version("optional");
        var root = Version("root", Dependency(library), Dependency(optional, OnlineDependencyKind.Optional));
        var plan = await Service.PlanAsync(Request(root));
        Assert.Equal(new[] { "library", "root" }, plan.Items.Select(item => item.Version.Name));
        Assert.Single(plan.OptionalDependencies); Assert.True(plan.CanInstall);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Instance, ".minecraft", "mods")));
    }

    /// <summary>关闭必要依赖开关后主文件仍可安装，并清晰报告未满足依赖。</summary>
    [Fact]
    public async Task RequiredCanBeExcludedWithWarning()
    {
        await InitializeAsync(); var library = Version("library"); var root = Version("root", Dependency(library));
        var plan = await Service.PlanAsync(Request(root) with { IncludeRequired = false });
        Assert.Single(plan.Items); Assert.True(plan.CanInstall); Assert.Contains(plan.Warnings, value => value.Contains("library"));
    }

    /// <summary>选中的可选模组自己的必要依赖同样遵循总开关。</summary>
    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 2)]
    public async Task OptionalSelectionUsesSameRequiredSetting(bool required, int count)
    {
        await InitializeAsync(); var library = Version("library"); var optional = Version("optional", Dependency(library));
        var dependency = Dependency(optional, OnlineDependencyKind.Optional); var root = Version("root", dependency);
        var plan = await Service.PlanAsync(Request(root) with { IncludeRequired = required, OptionalDependencyKeys = [dependency.Key] });
        Assert.Equal(count, plan.Items.Count); Assert.Contains(plan.Items, item => item.Version.Name == "optional");
        Assert.Equal(!required, plan.Warnings.Count > 0);
    }

    /// <summary>过期的嵌套可选选择不能被当作已经安装。</summary>
    [Fact]
    public async Task StaleOptionalSelectionIsReported()
    {
        await InitializeAsync(); var root = Version("root");
        var plan = await Service.PlanAsync(Request(root) with { OptionalDependencyKeys = ["stale-child"] });
        Assert.Single(plan.Items); Assert.Contains(plan.Warnings, value => value.Contains("stale-child"));
    }

    /// <summary>依赖环按固定项目版本去重，避免无限递归与重复下载。</summary>
    [Fact]
    public async Task CyclicDependenciesAreVisitedOnce()
    {
        await InitializeAsync(); var root = Version("root"); var library = Version("library", Dependency(root));
        root = root with { Dependencies = [Dependency(library)] }; _catalog.Versions[(root.Project, root.Id)] = root;
        Assert.Equal(2, (await Service.PlanAsync(Request(root))).Items.Count);
    }

    /// <summary>搜索框条件不能覆盖真实实例的 Minecraft 与加载器要求。</summary>
    [Theory]
    [InlineData("1.21", "fabric")]
    [InlineData("1.20.1", "forge")]
    public async Task IncompatibleFileCannotBePlanned(string game, string loader)
    {
        await InitializeAsync(); var root = Version("root");
        _catalog.Versions[(root.Project, root.Id)] = root with { GameVersions = [game], Loaders = [loader] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.PlanAsync(Request(root)));
    }

    /// <summary>下载经二次官方哈希验证后登记平台来源，而不是伪装本地导入。</summary>
    [Fact]
    public async Task DownloadInstallsAndRetainsOfficialSource()
    {
        await InitializeAsync(); var root = Version("root"); var service = Service;
        var result = await service.InstallAsync(await service.PlanAsync(Request(root)));
        Assert.True(result.Succeeded); Assert.Equal(1, result.Installed);
        var manifest = InstanceManifestStore.Read(Path.Combine(Instance, InstanceImportManifest.FileName));
        var expected = Assert.Single(InstanceEffectiveRequirements.Build(Instance, manifest).Requirements);
        Assert.Equal(InstanceFileOrigin.ModrinthDownload, expected.Origin);
        Assert.Equal("root", expected.ModrinthOrigin!.ProjectId); Assert.Equal(2, expected.Hashes.Count);
        Assert.True(Assert.Single((await Manager.ListAsync(Instance)).Files).CanManage);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, ".eml-runtime", "online-mods"), "*", SearchOption.AllDirectories));
    }

    /// <summary>下载器替身故意不做校验，安装层仍拒绝摘要错误而不写入模组。</summary>
    [Fact]
    public async Task CorruptDownloadNeverBecomesManaged()
    {
        await InitializeAsync(); var root = Version("root"); var service = Service;
        var plan = await service.PlanAsync(Request(root));
        _downloader.Files[root.Files[0].Requirement.Downloads[0]][^1] ^= 1;
        var result = await service.InstallAsync(plan);
        Assert.False(result.Succeeded); Assert.Equal(0, result.Installed); Assert.NotEmpty(result.Issues);
        Assert.Empty((await Manager.ListAsync(Instance)).Files);
    }

    /// <summary>改写已确认的下载地址或用户选择会使计划失效。</summary>
    [Fact]
    public async Task MutatedPlanIsRejectedBeforeDownloading()
    {
        await InitializeAsync(); var root = Version("root"); var service = Service;
        var plan = await service.PlanAsync(Request(root));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(plan with { Request = plan.Request with { IncludeRequired = false } }));
        Assert.Equal(0, _downloader.Calls);
    }

    /// <summary>下载计划只能消费一次，防止双击重复安装。</summary>
    [Fact]
    public async Task CompletedPlanCannotBeReplayed()
    {
        await InitializeAsync(); var root = Version("root"); var service = Service;
        var plan = await service.PlanAsync(Request(root)); Assert.True((await service.InstallAsync(plan)).Succeeded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(plan));
        Assert.Equal(1, _downloader.Calls);
    }

    /// <summary>预览后清单改变时不沿用旧目标或旧来源。</summary>
    [Fact]
    public async Task ManifestChangeInvalidatesPlan()
    {
        await InitializeAsync(); var root = Version("root"); var service = Service;
        var plan = await service.PlanAsync(Request(root)); var path = Path.Combine(Instance, InstanceImportManifest.FileName);
        await InstanceManifestStore.WriteAsync(path, InstanceManifestStore.Read(path) with { Name = "Changed" }, default);
        var result = await service.InstallAsync(plan);
        Assert.False(result.Succeeded); Assert.Equal(0, _downloader.Calls);
    }

    /// <summary>已经存在的同内容文件通过官方摘要后跳过，避免重复流量。</summary>
    [Fact]
    public async Task ExistingExactFileIsSkipped()
    {
        await InitializeAsync(); var root = Version("root"); var service = Service;
        Assert.True((await service.InstallAsync(await service.PlanAsync(Request(root)))).Succeeded);
        var plan = await service.PlanAsync(Request(root));
        Assert.Equal(OnlineModPlanStatus.AlreadyInstalled, Assert.Single(plan.Items).Status);
        var result = await service.InstallAsync(plan);
        Assert.True(result.Succeeded); Assert.Equal(1, result.Skipped); Assert.Equal(1, _downloader.Calls);
    }

    /// <summary>已有依赖曾跳过自己的必要依赖时，开启自动依赖的新计划仍须递归补齐遗漏文件。</summary>
    [Fact]
    public async Task ExistingDependencyStillResolvesMissingTransitiveRequiredDependency()
    {
        await InitializeAsync();
        var leaf = Version("leaf");
        var library = Version("library", Dependency(leaf));
        var root = Version("root", Dependency(library));
        var service = Service;

        var initial = await service.PlanAsync(Request(library) with { IncludeRequired = false });
        Assert.Single(initial.Items);
        Assert.Contains(initial.Warnings, value => value.Contains("leaf"));
        var installed = await service.InstallAsync(initial);
        Assert.True(installed.Succeeded);
        Assert.Equal(1, installed.Installed);
        Assert.False(File.Exists(Path.Combine(Instance, ".minecraft", "mods", "leaf.jar")));

        var plan = await service.PlanAsync(Request(root) with { IncludeRequired = true });
        Assert.Contains(plan.Items, item => item.Version.Project == library.Project && item.Status == OnlineModPlanStatus.AlreadyInstalled);
        Assert.Contains(plan.Items, item => item.Version.Project == leaf.Project && item.Status == OnlineModPlanStatus.Download);
        Assert.True(plan.CanInstall);
        Assert.Equal(new[] { "leaf", "library", "root" }, plan.Items.Select(item => item.Version.Name));
        var completed = await service.InstallAsync(plan);
        Assert.True(completed.Succeeded); Assert.Equal(2, completed.Installed); Assert.Equal(1, completed.Skipped);
        Assert.Equal(3, _downloader.Calls);
        Assert.True(File.Exists(Path.Combine(Instance, ".minecraft", "mods", "leaf.jar")));
    }

    /// <summary>关闭自动依赖不会偷偷补下级文件，但仍明确提示已安装前置的必要依赖缺失。</summary>
    [Fact]
    public async Task ExistingDependencyReportsMissingTransitiveDependencyWhenAutomaticDependenciesAreOff()
    {
        await InitializeAsync(); var leaf = Version("leaf"); var library = Version("library", Dependency(leaf));
        var root = Version("root", Dependency(library)); var service = Service;
        Assert.True((await service.InstallAsync(await service.PlanAsync(Request(library) with { IncludeRequired = false }))).Succeeded);

        var plan = await service.PlanAsync(Request(root) with { IncludeRequired = false });

        Assert.Equal(2, plan.Items.Count);
        Assert.DoesNotContain(plan.Items, item => item.Version.Project == leaf.Project);
        Assert.Contains(plan.Warnings, message => message.Contains("leaf"));
        Assert.True(plan.CanInstall);
        Assert.Equal(1, _downloader.Calls);
    }

    /// <summary>现有依赖使用登记的固定版本及原摘要，即使当前平台不给下载地址也不会替换或重下。</summary>
    [Fact]
    public async Task ExistingDependencyKeepsFixedVersionAndBaselineWithoutResolvingAnotherDownload()
    {
        await InitializeAsync(); var leaf = Version("leaf"); var library = Version("library", Dependency(leaf));
        var root = Version("root", Dependency(library) with { VersionId = null }); var service = Service;
        Assert.True((await service.InstallAsync(await service.PlanAsync(Request(library) with { IncludeRequired = false }))).Succeeded);
        var path = Path.Combine(Instance, ".minecraft", "mods", "library.jar");
        var before = await File.ReadAllBytesAsync(path);
        _catalog.Versions[(library.Project, "library-v2")] = library with { Id = "library-v2", Dependencies = [] };
        _catalog.Versions[(library.Project, library.Id)] = library with
        {
            // 旧文件可能缺少加载器标签；这里只读取其依赖，不重新判断或替换已登记文件。
            Loaders = [],
            Files = [library.Files[0] with { Problem = "不再提供自动下载", Requirement = library.Files[0].Requirement with
            { Downloads = [], Size = 1, Hashes = new Dictionary<string, string> { ["sha1"] = new('0', 40) } } }]
        };
        _catalog.UnresolvableDownloads.Add(library.Id);
        _catalog.VersionReads.Clear();

        var plan = await service.PlanAsync(Request(root));

        var existing = Assert.Single(plan.Items, item => item.Version.Project == library.Project);
        Assert.Equal(OnlineModPlanStatus.AlreadyInstalled, existing.Status);
        Assert.Equal(library.Id, existing.Version.Id);
        Assert.Equal(".minecraft/mods/library.jar", existing.File.Requirement.Path);
        Assert.Equal(library.Files[0].Requirement.Size, existing.File.Requirement.Size);
        Assert.Equal(library.Files[0].Requirement.Origin, existing.File.Requirement.Origin);
        Assert.Equal(library.Files[0].Requirement.ModrinthOrigin, existing.File.Requirement.ModrinthOrigin);
        Assert.Equal(library.Files[0].Requirement.Hashes["sha1"], existing.File.Requirement.Hashes["sha1"]);
        Assert.Equal(library.Files[0].Requirement.Hashes["sha512"], existing.File.Requirement.Hashes["sha512"]);
        Assert.Contains((library.Project, library.Id), _catalog.VersionReads);
        Assert.DoesNotContain((library.Project, "library-v2"), _catalog.VersionReads);
        Assert.Equal(0, _catalog.ListCalls);
        Assert.True((await service.InstallAsync(plan)).Succeeded);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    /// <summary>已有依赖的可选项也可发现，但仅在用户选中后进入安装清单。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDependencyOptionalChildrenRequireExplicitSelection(bool selected)
    {
        await InitializeAsync(); var extra = Version("extra"); var optional = Dependency(extra, OnlineDependencyKind.Optional);
        var library = Version("library", optional); var root = Version("root", Dependency(library)); var service = Service;
        Assert.True((await service.InstallAsync(await service.PlanAsync(Request(library)))).Succeeded);

        var plan = await service.PlanAsync(Request(root) with { OptionalDependencyKeys = selected ? [optional.Key] : [] });

        Assert.Equal(optional.Key, Assert.Single(plan.OptionalDependencies).Key);
        Assert.Equal(selected, plan.Items.Any(item => item.Version.Project == extra.Project));
        Assert.True(plan.CanInstall);
    }

    /// <summary>递归穿过已安装依赖形成环时，固定项目只访问一次，不重复下载或无限查询。</summary>
    [Fact]
    public async Task ExistingDependencyCyclesAreVisitedOnce()
    {
        await InitializeAsync(); var root = Version("root"); var library = Version("library", Dependency(root));
        root = root with { Dependencies = [Dependency(library)] }; _catalog.Versions[(root.Project, root.Id)] = root;
        var service = Service;
        Assert.True((await service.InstallAsync(await service.PlanAsync(Request(library) with { IncludeRequired = false }))).Succeeded);
        _catalog.VersionReads.Clear();

        var plan = await service.PlanAsync(Request(root));

        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(1, _catalog.VersionReads.Count(read => read == (library.Project, library.Id)));
        Assert.True(plan.CanInstall);
    }

    /// <summary>固定版本依赖元数据读取失败时不签发可安装清单，也不改写已有模组。</summary>
    [Fact]
    public async Task ExistingDependencyMetadataFailureDoesNotPretendThePlanIsComplete()
    {
        await InitializeAsync(); var library = Version("library"); var root = Version("root", Dependency(library)); var service = Service;
        Assert.True((await service.InstallAsync(await service.PlanAsync(Request(library)))).Succeeded);
        _catalog.UnavailableVersion = library.Id;

        await Assert.ThrowsAsync<HttpRequestException>(() => service.PlanAsync(Request(root)));

        Assert.Equal(1, _downloader.Calls);
        Assert.Single((await Manager.ListAsync(Instance)).Files);
        Assert.False(File.Exists(Path.Combine(Instance, ".minecraft", "mods", "root.jar")));
    }

    /// <summary>已有文件的来源编号不能被返回错误项目或错误版本的元数据覆盖。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDependencyRejectsMismatchedFixedVersionMetadata(bool wrongProject)
    {
        await InitializeAsync(); var library = Version("library"); var root = Version("root", Dependency(library)); var service = Service;
        Assert.True((await service.InstallAsync(await service.PlanAsync(Request(library)))).Succeeded);
        _catalog.Versions[(library.Project, library.Id)] = wrongProject
            ? library with { Project = new(ModProvider.Modrinth, "other") } : library with { Id = "another-version" };

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PlanAsync(Request(root)));

        Assert.Equal(1, _downloader.Calls);
    }

    /// <summary>CurseForge 已安装依赖同样按本地 fileId 展开，不重新选择平台的最新文件。</summary>
    [Fact]
    public async Task ExistingCurseForgeDependencyResolvesChildrenFromItsRecordedFileId()
    {
        await InitializeAsync(); var leaf = Version("leaf"); var original = Version("library", Dependency(leaf));
        var source = original.Files[0].Requirement;
        var library = original with
        {
            Project = new(ModProvider.CurseForge, "42"), Id = "123",
            Files = [original.Files[0] with { Requirement = source with
            {
                Source = InstanceFileSource.CurseForge, Origin = InstanceFileOrigin.CurseForgeDownload,
                ProjectId = 42, FileId = 123, ModrinthOrigin = null,
                Hashes = new Dictionary<string, string> { ["sha1"] = source.Hashes["sha1"] }
            } }]
        };
        _catalog.Versions[(library.Project, library.Id)] = library;
        var root = Version("root", Dependency(library) with { VersionId = null }); var service = Service;
        Assert.True((await service.InstallAsync(await service.PlanAsync(Request(library) with { IncludeRequired = false }))).Succeeded);
        _catalog.UnresolvableDownloads.Add(library.Id);

        var plan = await service.PlanAsync(Request(root));

        var existing = Assert.Single(plan.Items, item => item.Version.Project == library.Project);
        Assert.Equal("123", existing.Version.Id);
        Assert.Equal(123L, existing.File.Requirement.FileId);
        Assert.Equal(InstanceFileOrigin.CurseForgeDownload, existing.File.Requirement.Origin);
        Assert.Contains(plan.Items, item => item.Version.Project == leaf.Project && item.Status == OnlineModPlanStatus.Download);
        Assert.Equal(0, _catalog.ListCalls);
        Assert.True((await service.InstallAsync(plan)).Succeeded);
    }

    /// <summary>已经满足的依赖也进入清单，确认前遭外部删除时不能安装主文件并宣称完成。</summary>
    [Fact]
    public async Task ExistingDependencyIsRecheckedBeforeAnyNewDownload()
    {
        await InitializeAsync(); var library = Version("library"); var service = Service;
        Assert.True((await service.InstallAsync(await service.PlanAsync(Request(library)))).Succeeded);
        var root = Version("root", Dependency(library)); var plan = await service.PlanAsync(Request(root));
        Assert.Contains(plan.Items, item => item.Version.Project == library.Project && item.Status == OnlineModPlanStatus.AlreadyInstalled);
        File.Delete(Path.Combine(Instance, ".minecraft", "mods", "library.jar"));
        var result = await service.InstallAsync(plan);
        Assert.False(result.Succeeded); Assert.Equal(0, result.Installed); Assert.Equal(1, _downloader.Calls);
    }

    /// <summary>明确禁用或移除的模组不得被在线下载自动重新启用。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrRemovedModsAreNotReinstalled(bool removed)
    {
        await InitializeAsync(); var root = Version("root"); var service = Service;
        Assert.True((await service.InstallAsync(await service.PlanAsync(Request(root)))).Succeeded);
        if (removed) await Manager.RemoveAsync(Instance, ".minecraft/mods/root.jar");
        else await Manager.SetEnabledAsync(Instance, ".minecraft/mods/root.jar", false);
        var plan = await service.PlanAsync(Request(root));
        Assert.False(plan.CanInstall); Assert.Equal(OnlineModPlanStatus.Blocked, Assert.Single(plan.Items).Status);
        Assert.False(File.Exists(Path.Combine(Instance, ".minecraft", "mods", "root.jar")));
    }

    /// <summary>预览之后出现的同名玩家文件保留，安装拒绝覆盖。</summary>
    [Fact]
    public async Task ConcurrentFileCreationDoesNotGetOverwritten()
    {
        await InitializeAsync(); var root = Version("root"); var service = Service;
        var plan = await service.PlanAsync(Request(root)); var path = Path.Combine(Instance, ".minecraft", "mods", "root.jar");
        await File.WriteAllTextAsync(path, "player file");
        Assert.False((await service.InstallAsync(plan)).Succeeded);
        Assert.Equal("player file", await File.ReadAllTextAsync(path));
    }

    /// <summary>预先取消不下载、不登记来源，结果明确标记取消。</summary>
    [Fact]
    public async Task CancellationBeforeDownloadHasNoInstalledFiles()
    {
        await InitializeAsync(); var root = Version("root"); var service = Service;
        var plan = await service.PlanAsync(Request(root)); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await service.InstallAsync(plan, token: cancel.Token);
        Assert.True(result.Cancelled); Assert.Equal(0, _downloader.Calls); Assert.Empty((await Manager.ListAsync(Instance)).Files);
    }

    /// <summary>主文件失败时保留已完成的依赖，并报告部分完成而非成功。</summary>
    [Fact]
    public async Task PartialFailureReportsAlreadyInstalledDependency()
    {
        await InitializeAsync(); var library = Version("library"); var root = Version("root", Dependency(library)); var service = Service;
        _downloader.FailUrl = root.Files[0].Requirement.Downloads[0];
        var result = await service.InstallAsync(await service.PlanAsync(Request(root)));
        Assert.False(result.Succeeded); Assert.Equal(1, result.Installed);
        Assert.Equal("library.jar", Assert.Single((await Manager.ListAsync(Instance)).Files).FileName);
    }

    /// <summary>主文件哈希失败仍返回已安装依赖的数量，不因校验异常丢失部分完成状态。</summary>
    [Fact]
    public async Task PartialHashFailureReportsAlreadyInstalledDependency()
    {
        await InitializeAsync(); var library = Version("library"); var root = Version("root", Dependency(library)); var service = Service;
        var plan = await service.PlanAsync(Request(root));
        _downloader.Files[root.Files[0].Requirement.Downloads[0]][^1] ^= 1;
        var result = await service.InstallAsync(plan);
        Assert.False(result.Succeeded); Assert.Equal(1, result.Installed); Assert.NotEmpty(result.Issues);
        Assert.Equal("library.jar", Assert.Single((await Manager.ListAsync(Instance)).Files).FileName);
    }

    /// <summary>缺少官方下载地址且无同内容备用来源时，清单保留手动处理项。</summary>
    [Fact]
    public async Task UnavailableDownloadProducesBlockedPlan()
    {
        await InitializeAsync(); var root = Version("root"); var file = root.Files[0];
        _catalog.Versions[(root.Project, root.Id)] = root with { Files = [file with { Problem = "需要手动下载", Requirement = file.Requirement with { Downloads = [] } }] };
        var plan = await Service.PlanAsync(Request(root));
        Assert.False(plan.CanInstall); Assert.Contains("手动", Assert.Single(plan.Items).Message);
    }

    /// <summary>带目录穿越的远程文件名不会逃出实例模组目录。</summary>
    [Fact]
    public async Task UnsafeRemoteFilenameIsRejected()
    {
        await InitializeAsync(); var root = Version("root");
        root = root with { Files = [root.Files[0] with { FileName = "../evil.jar" }] };
        _catalog.Versions[(root.Project, root.Id)] = root;
        await Assert.ThrowsAnyAsync<Exception>(() => Service.PlanAsync(Request(root)));
    }

    /// <summary>依赖首个查询页为空但仍有下一页时，继续查找兼容固定版本。</summary>
    [Fact]
    public async Task DependencySearchContinuesPastEmptyPage()
    {
        await InitializeAsync(); var library = Version("library");
        var root = Version("root", Dependency(library) with { VersionId = null });
        _catalog.EmptyFirstPage = true;
        Assert.Equal(2, (await Service.PlanAsync(Request(root))).Items.Count);
        Assert.Equal(2, _catalog.ListCalls);
    }

    /// <summary>测试目录只返回事先登记的固定元数据，不模拟网络下载或磁盘写入。</summary>
    private sealed class Catalog : IOnlineModCatalog
    {
        internal Dictionary<(OnlineProjectId, string), OnlineModVersion> Versions { get; } = [];
        internal List<(OnlineProjectId, string)> VersionReads { get; } = [];
        internal HashSet<string> UnresolvableDownloads { get; } = [];
        internal string? UnavailableVersion;
        internal bool EmptyFirstPage;
        internal int ListCalls;
        public Task<OnlineModSearchPage> SearchAsync(OnlineModQuery query, CancellationToken token = default) => throw new NotSupportedException();
        public Task<OnlineModProject> GetProjectAsync(OnlineProjectId project, CancellationToken token = default) => Task.FromResult(new OnlineModProject { Id = project, Name = project.ProjectId });
        public Task<OnlineModVersionPage> GetVersionsAsync(OnlineProjectId project, string gameVersion, string loader, int page = 0, CancellationToken token = default)
        {
            ListCalls++;
            return Task.FromResult(EmptyFirstPage && page == 0 ? new OnlineModVersionPage([], true) :
                new OnlineModVersionPage(Versions.Values.Where(value => value.Project == project).ToArray(), false));
        }
        public Task<OnlineModVersion> GetVersionAsync(OnlineProjectId project, string versionId, CancellationToken token = default)
        {
            VersionReads.Add((project, versionId));
            if (versionId == UnavailableVersion) throw new HttpRequestException("模拟固定版本元数据不可用");
            return Task.FromResult(Versions[(project, versionId)]);
        }
        public Task<OnlineModFile> ResolveDownloadAsync(OnlineModVersion version, OnlineModFile file, CancellationToken token = default)
        {
            if (UnresolvableDownloads.Contains(version.Id)) throw new InvalidOperationException("不应为已有文件重新解析下载");
            return Task.FromResult(file);
        }
    }

    /// <summary>可控的下载成功/失败替身；安装层必须自行防止不可信内容提交。</summary>
    private sealed class Downloader : IFileDownloader
    {
        internal Dictionary<string, byte[]> Files { get; } = [];
        internal string? FailUrl;
        internal int Calls;
        public async Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++; var url = request.Urls[0];
            if (url == FailUrl) throw new IOException("模拟网络失败");
            var bytes = Files[url]; await File.WriteAllBytesAsync(request.DestinationPath, bytes, cancellationToken);
            progress?.Report(new(bytes.Length, bytes.Length, 1)); return new(request.DestinationPath, bytes.Length, false);
        }
    }
}
