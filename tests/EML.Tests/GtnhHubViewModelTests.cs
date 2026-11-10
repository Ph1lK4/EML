using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Downloads;
using EML.Core.Java;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;
using EML.Core.Minecraft.Migration;

namespace EML.Tests;

/// <summary>专区仅操作明确识别的目标，测试使用可控服务，不访问玩家实例或网络。</summary>
public sealed class GtnhHubViewModelTests
{
    [Fact]
    public async Task OpeningHubListsOnlyRecognizedInstancesWithoutRequestingReleases()
    {
        await using var app = new Harness();
        Assert.Empty(app.Hub.Targets); Assert.Equal(0, app.Addons.Reads);
        app.Main.SelectedPage = 4;
        await app.Hub.TargetLoadTask;
        Assert.Equal(new[] { "old", "new" }, app.Hub.Targets.Select(item => item.Name));
        Assert.Equal(app.Instances.Instances[0].InstancePath, app.Hub.SelectedTarget!.InstancePath);
        Assert.True(app.Hub.HasTarget); Assert.Equal(0, app.Addons.Reads);
        app.Main.OpenGtnhMigrationCommand.Execute(null);
        Assert.True(app.Hub.IsMigration); Assert.False(app.Main.IsWorkspaceVisible);
    }

    [Fact]
    public async Task ExplicitReleaseAndCompatibilityConfirmationAreRequired()
    {
        await using var app = new Harness(); await app.SelectAddonAsync();
        Assert.Null(app.Hub.SelectedRelease);
        Assert.False(app.Hub.InstallCommand.CanExecute(null));
        app.Hub.SelectedRelease = Assert.Single(app.Hub.Releases);
        Assert.False(app.Hub.InstallCommand.CanExecute(null));
        app.Hub.CompatibilityConfirmed = true;
        Assert.True(app.Hub.InstallCommand.CanExecute(null));
        await app.Hub.InstallCommand.ExecuteAsync(null);
        Assert.Equal(app.Hub.SelectedTarget!.InstancePath, app.Addons.InstalledPath);
        Assert.Equal(1, app.Addons.Installs);
        Assert.Contains("完成", app.Hub.InstallStatus);
        Assert.False(app.Hub.CompatibilityConfirmed); Assert.False(app.Instances.IsBusy);
    }

    [Fact]
    public async Task MissingDigestOrOtherBusyOperationCannotInstall()
    {
        await using var app = new Harness(); await app.SelectAddonAsync();
        app.Hub.SelectedRelease = Release("unverified") with { Problem = "没有官方摘要", Sha256 = "" };
        app.Hub.CompatibilityConfirmed = true;
        await app.Hub.InstallCommand.ExecuteAsync(null);
        Assert.Equal(0, app.Addons.Installs);
        app.Hub.SelectedRelease = Assert.Single(app.Hub.Releases); app.Hub.CompatibilityConfirmed = true;
        app.Instances.IsBusy = true;
        Assert.False(app.Hub.InstallCommand.CanExecute(null));
        await app.Hub.InstallCommand.ExecuteAsync(null);
        Assert.Equal(0, app.Addons.Installs);
        app.Instances.IsBusy = false;
    }

    [Fact]
    public async Task SwitchingTargetDiscardsOldReleaseResponseAndResetsConfirmation()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<GtnhAddonRelease>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = new Harness();
        app.Addons.Read = path => path.EndsWith("old") ? pending.Task : Task.FromResult<IReadOnlyList<GtnhAddonRelease>>([Release("new")]);
        try
        {
            app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
            app.Hub.SelectedAddon = Assert.Single(app.Hub.Addons);
            var interrupted = app.Hub.ReleasesLoadTask;
            app.Hub.SelectedTarget = app.Hub.Targets[1];
            await app.Hub.ReleasesLoadTask;
            pending.SetResult([Release("old")]); await interrupted;
            Assert.Equal("new.jar", Assert.Single(app.Hub.Releases).FileName);
            Assert.Equal(app.Instances.Instances[1], app.Instances.SelectedInstance);
            Assert.Null(app.Hub.SelectedRelease); Assert.False(app.Hub.CompatibilityConfirmed);
        }
        finally { pending.TrySetResult([]); }
    }

    [Fact]
    public async Task ReenteringHubRestartsInterruptedReleaseReadWithoutLosingTarget()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<GtnhAddonRelease>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = new Harness(); var reads = 0;
        app.Addons.Read = _ => ++reads == 1 ? pending.Task : Task.FromResult<IReadOnlyList<GtnhAddonRelease>>([Release("current")]);
        try
        {
            app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
            app.Hub.SelectedAddon = Assert.Single(app.Hub.Addons); var interrupted = app.Hub.ReleasesLoadTask;
            app.Main.SelectedPage = 3;
            app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask; await app.Hub.ReleasesLoadTask;
            Assert.Equal(2, reads);
            pending.SetResult([Release("old")]); await interrupted;
            Assert.Equal("current.jar", Assert.Single(app.Hub.Releases).FileName);
            Assert.True(app.Hub.HasTarget);
        }
        finally { pending.TrySetResult([]); }
    }

    [Fact]
    public async Task LeavingHubDoesNotCancelInstallationButClosingDoes()
    {
        var pending = new TaskCompletionSource<GtnhAddonInstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = new Harness();
        app.Addons.Install = token => pending.Task.WaitAsync(token);
        await app.SelectAddonAsync();
        app.Hub.SelectedRelease = Assert.Single(app.Hub.Releases); app.Hub.CompatibilityConfirmed = true;
        var installation = app.Hub.InstallCommand.ExecuteAsync(null);
        Assert.True(app.Instances.IsBusy); Assert.False(app.Hub.CanInteract);
        app.Main.SelectedPage = 3;
        Assert.False(app.Addons.InstallToken.IsCancellationRequested); Assert.False(installation.IsCompleted);
        await app.Hub.StopAsync(); await installation;
        Assert.True(app.Addons.InstallToken.IsCancellationRequested);
        Assert.False(app.Instances.IsBusy);
    }

    [Fact]
    public async Task SelectingNonGtnhElsewhereCannotRetainAnInstallTarget()
    {
        await using var app = new Harness(); await app.SelectAddonAsync();
        app.Hub.SelectedRelease = Assert.Single(app.Hub.Releases); app.Hub.CompatibilityConfirmed = true;
        app.Instances.SelectedInstance = app.Instances.Instances[2];
        Assert.False(app.Hub.HasTarget); Assert.Null(app.Hub.SelectedTarget);
        Assert.Null(app.Hub.SelectedRelease); Assert.False(app.Hub.InstallCommand.CanExecute(null));
        Assert.Equal(0, app.Addons.Installs);
    }

    /// <summary>真实 WPF 会响应集合移除并清空选择，未变化的刷新应保留对象与用户的发行选择。</summary>
    [Fact]
    public async Task RefreshAndMatchingSearchKeepExistingSelectionObjects()
    {
        await using var app = new Harness(); await app.SelectAddonAsync();
        app.Hub.SelectedRelease = Assert.Single(app.Hub.Releases); app.Hub.CompatibilityConfirmed = true;
        var selected = app.Hub.SelectedRelease; var target = app.Hub.SelectedTarget; var changes = 0;
        app.Hub.Targets.CollectionChanged += (_, _) => changes++;
        app.Hub.Addons.CollectionChanged += (_, _) => changes++;
        app.Hub.SearchText = "测试";
        await app.Hub.RefreshTargetsCommand.ExecuteAsync(null);
        app.Main.SelectedPage = 0; app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
        Assert.Equal(0, changes); Assert.Same(target, app.Hub.SelectedTarget);
        Assert.Same(selected, app.Hub.SelectedRelease); Assert.True(app.Hub.CompatibilityConfirmed);
        Assert.Equal(1, app.Addons.Reads);
    }

    /// <summary>迁移中损坏 GTNH 标识时仍能选中实例恢复，但不能把它当正常目标下载模组。</summary>
    [Fact]
    public async Task UnrecognizedInstanceWithRecoveryRemainsSelectableWithoutAddonDownloads()
    {
        await using var app = new Harness(); app.Services.HasRecovery = true;
        app.Main.SelectedPage = 4; await app.Hub.TargetLoadTask;
        Assert.Equal(3, app.Hub.Targets.Count);
        app.Hub.SelectedTarget = app.Hub.Targets[2];
        app.Hub.SelectedAddon = Assert.Single(app.Hub.Addons);
        Assert.True(app.Hub.HasTarget); Assert.Equal("待恢复", app.Hub.SelectedTarget.Version);
        Assert.False(app.Hub.RefreshReleasesCommand.CanExecute(null));
        Assert.False(app.Hub.InstallCommand.CanExecute(null)); Assert.Equal(0, app.Addons.Reads);
    }

    /// <summary>构造可安装的固定发行文件，测试不会实际使用其中的网络地址。</summary>
    private static GtnhAddonRelease Release(string name) => new()
    {
        AddonId = "test", Repository = "GTNewHorizons/Test", ReleaseId = 1, AssetId = 2, Tag = "1.0",
        FileName = name + ".jar", Size = 5, Sha256 = new('a', 64),
        DownloadUrl = "https://github.com/GTNewHorizons/Test/releases/download/1.0/test.jar",
        ReleaseUrl = "https://github.com/GTNewHorizons/Test/releases/tag/1.0"
    };

    /// <summary>组合真实主导航与专区，测试结束等待全部异步任务。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public NoIo Services { get; } = new();
        public AddonService Addons { get; } = new();
        public InstancesViewModel Instances { get; }
        public MainViewModel Main { get; }
        public GtnhHubViewModel Hub => Instances.Gtnh!;
        public Harness()
        {
            Instances = new(Services, Services, Services, Services, "C:\\hub-test", Services, Services,
                migration: Services, gtnhAddons: Addons);
            foreach (var name in new[] { "old", "new", "GTNH-looking-vanilla" })
                Instances.Instances.Add(new(name, "C:\\hub-test\\" + name, "C:\\hub-test\\" + name + "\\.minecraft", ["1.7.10"]));
            Instances.SelectedInstance = Instances.Instances[0];
            Main = new(new JavaSettingsViewModel(Services), Instances);
        }
        public async Task SelectAddonAsync()
        {
            Main.SelectedPage = 4; await Hub.TargetLoadTask;
            Hub.SelectedAddon = Assert.Single(Hub.Addons); await Hub.ReleasesLoadTask;
        }
        public async ValueTask DisposeAsync() { await Hub.StopAsync(); Main.Dispose(); }
    }

    /// <summary>发行列表和下载均可被测试暂停，验证取消、乱序与固定目标。</summary>
    private sealed class AddonService : IGtnhAddonService
    {
        public IReadOnlyList<GtnhAddon> Addons { get; } = [new("test", "测试推荐", "用途", "工具", "GTNewHorizons/Test", "核对发行说明")];
        public Func<string, Task<IReadOnlyList<GtnhAddonRelease>>>? Read { get; set; }
        public Func<CancellationToken, Task<GtnhAddonInstallResult>>? Install { get; set; }
        public int Reads { get; private set; }
        public int Installs { get; private set; }
        public string? InstalledPath { get; private set; }
        public CancellationToken InstallToken { get; private set; }
        public Task<IReadOnlyList<GtnhAddonRelease>> GetReleasesAsync(string instancePath, string addonId, CancellationToken token = default)
        { Reads++; return Read?.Invoke(instancePath) ?? Task.FromResult<IReadOnlyList<GtnhAddonRelease>>([Release("test")]); }
        public Task<GtnhAddonInstallResult> InstallAsync(string instancePath, GtnhAddonRelease release, bool compatibilityConfirmed,
            IProgress<FileDownloadProgress>? progress = null, CancellationToken token = default)
        {
            Installs++; InstalledPath = instancePath; InstallToken = token;
            return Install?.Invoke(token) ?? Task.FromResult(new GtnhAddonInstallResult(true, false, "安装完成"));
        }
    }

    /// <summary>仅模拟识别，不允许本专区测试触发其他实例写入操作。</summary>
    private sealed class NoIo : IJavaLocator, IMinecraftInstanceLocator, IMinecraftInstanceManager, IMinecraftInstanceImporter,
        IInstanceDialogs, IMinecraftInstanceChecker, IMinecraftInstanceDownloader, IMinecraftInstanceMigrationService
    {
        public bool HasRecovery { get; set; }
        Task<IReadOnlyList<JavaInstallation>> IJavaLocator.FindAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JavaInstallation>>([]);
        Task<IReadOnlyList<MinecraftInstance>> IMinecraftInstanceLocator.FindAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MinecraftInstance>>([]);
        Task<GtnhInstanceInfo?> IMinecraftInstanceMigrationService.InspectAsync(string path, CancellationToken token) =>
            Task.FromResult<GtnhInstanceInfo?>(path.EndsWith("vanilla") ? null : new(path, Path.GetFileName(path), path + "\\.minecraft", "2.8.4"));
        public Task<MigrationRecoveryInfo?> GetRecoveryAsync(string instancePath, CancellationToken token = default) =>
            Task.FromResult<MigrationRecoveryInfo?>(HasRecovery && instancePath.EndsWith("vanilla") ? new("test", "Pending", DateTimeOffset.UtcNow) : null);
        public Task<IReadOnlyList<MigrationGroup>> GetGroupsAsync(string sourceInstance, GtnhMigrationMode mode, CancellationToken token = default) => throw new NotSupportedException();
        public Task<InstanceMigrationPlan> PreviewAsync(InstanceMigrationRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<InstanceMigrationResult> MigrateAsync(InstanceMigrationPlan plan, bool overwriteConflicts, IProgress<InstanceMigrationProgress>? progress = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<InstanceMigrationResult> RestoreAsync(string instancePath, string migrationId, IProgress<InstanceMigrationProgress>? progress = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task DeleteAsync(string instancePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RenameAsync(string instancePath, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportAsync(string instancePath, string destinationZip, IProgress<InstanceExportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstanceImportPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstanceImportResult> ImportAsync(string archivePath, string instanceName, IProgress<InstanceImportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public bool ConfirmDelete(MinecraftInstance instance) => throw new NotSupportedException();
        public string? ChooseExportPath(MinecraftInstance instance) => throw new NotSupportedException();
        public string? ChooseImportPath() => throw new NotSupportedException();
        public string? ConfirmImport(InstanceImportPreview preview) => throw new NotSupportedException();
        public Task<InstanceCheckReport> CheckAsync(string instancePath, InstanceCheckMode mode = InstanceCheckMode.Full, IProgress<InstanceCheckProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstanceDownloadReport> DownloadMissingAsync(string instancePath, IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
