using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.Core.Downloads;
using EML.Core.Minecraft.Gtnh;
using EML.Core.Minecraft.Migration;

namespace EML.App.ViewModels;

/// <summary>GTNH 专区共用明确的实例选择；识别只读，迁移与安装沿用全局实例操作互斥。</summary>
public sealed partial class GtnhHubViewModel : ObservableObject, IDisposable
{
    private readonly IMinecraftInstanceMigrationService _migration;
    private readonly IGtnhAddonService _addons;
    private readonly Action _filesChanged;
    private readonly Action<Uri>? _openWebsite;
    private readonly HashSet<Task> _pending = [];
    private readonly HashSet<string> _recoveryOnly = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _targetCancellation, _releaseCancellation;
    private int _targetRevision, _releaseRevision, _installRevision;
    private bool _active, _closing, _disposed, _syncingSelection, _targetsDirty = true;

    /// <summary>接收已有实例模型和服务；构造时不联网或扫描玩家文件。</summary>
    public GtnhHubViewModel(InstancesViewModel instances, IMinecraftInstanceMigrationService migration,
        IGtnhAddonService addons, Action filesChanged, Action<Uri>? openWebsite = null)
    {
        Instances = instances; _migration = migration; _addons = addons;
        _filesChanged = filesChanged; _openWebsite = openWebsite;
        foreach (var addon in addons.Addons) Addons.Add(addon);
        instances.PropertyChanged += OnInstancesChanged;
        instances.Instances.CollectionChanged += OnInstanceCollectionChanged;
    }

    public InstancesViewModel Instances { get; }
    public ObservableCollection<GtnhInstanceInfo> Targets { get; } = [];
    public ObservableCollection<GtnhAddon> Addons { get; } = [];
    public ObservableCollection<GtnhAddonRelease> Releases { get; } = [];
    public Task TargetLoadTask { get; private set; } = Task.CompletedTask;
    public Task ReleasesLoadTask { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private GtnhInstanceInfo? selectedTarget;
    [ObservableProperty] private GtnhAddon? selectedAddon;
    [ObservableProperty] private GtnhAddonRelease? selectedRelease;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private int selectedSection;
    [ObservableProperty] private bool isLoadingTargets;
    [ObservableProperty] private bool isLoadingReleases;
    [ObservableProperty] private bool isInstalling;
    [ObservableProperty] private bool compatibilityConfirmed;
    [ObservableProperty] private string targetStatus = "进入专区后识别本地 GTNH 实例。";
    [ObservableProperty] private string releaseStatus = "选择推荐模组，再选择与实例适配的发行版本。";
    [ObservableProperty] private string installStatus = "确认目标实例和发行说明后再安装。";
    [ObservableProperty] private string downloadText = "";
    [ObservableProperty] private double downloadPercent;
    public bool CanInteract => !_closing && !Instances.IsBusy && !IsInstalling && !IsLoadingTargets;
    public bool HasTarget => SelectedTarget is not null && SamePath(SelectedTarget.InstancePath, Instances.SelectedInstance?.InstancePath);
    public bool IsRecommendations => SelectedSection == 0;
    public bool IsMigration => SelectedSection == 1;

    /// <summary>打开时识别目标，离开只取消只读请求，用户已确认的下载继续执行。</summary>
    public void SetActive(bool active)
    {
        if (_closing || _active == active) return;
        _active = active;
        if (!active) { CancelReads(); return; }
        if (!Instances.IsBusy) TargetLoadTask = Track(RefreshTargetsCoreAsync(CancellationToken.None));
        else _targetsDirty = true;
    }

    /// <summary>推荐与迁移是同一专区的两个页面，不改变当前安装目标。</summary>
    [RelayCommand] private void ShowRecommendations() => SelectedSection = 0;
    [RelayCommand] private void ShowMigration() => SelectedSection = 1;
    partial void OnSelectedSectionChanged(int value)
    { OnPropertyChanged(nameof(IsRecommendations)); OnPropertyChanged(nameof(IsMigration)); }

    /// <summary>搜索只过滤人工目录，不改变平台仓库和下载身份。</summary>
    partial void OnSearchTextChanged(string value)
    {
        var filter = value.Trim();
        var matches = _addons.Addons.Where(addon => filter.Length == 0 ||
            (addon.Name + " " + addon.Summary + " " + addon.Repository).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        UpdateItems(Addons, matches);
        if (SelectedAddon is not null && !matches.Contains(SelectedAddon)) SelectedAddon = null;
    }

    /// <summary>专区选择与实例管理共享同一目标，迁移按钮不会沿用另一实例。</summary>
    partial void OnSelectedTargetChanged(GtnhInstanceInfo? value)
    {
        if (IsInstalling) InstallCommand.Cancel();
        ResetReleases();
        if (!_syncingSelection && value is not null && !Instances.IsBusy)
        {
            var instance = Instances.Instances.FirstOrDefault(item => SamePath(item.InstancePath, value.InstancePath));
            if (instance is not null)
            {
                _syncingSelection = true;
                try { Instances.SelectedInstance = instance; }
                finally { _syncingSelection = false; }
            }
        }
        NotifyState();
        QueueReleases();
    }

    /// <summary>更换项目或文件后重新确认版本兼容性，旧下载选择不能沿用。</summary>
    partial void OnSelectedAddonChanged(GtnhAddon? value) { ResetReleases(); QueueReleases(); NotifyState(); }
    partial void OnSelectedReleaseChanged(GtnhAddonRelease? value) { CompatibilityConfirmed = false; NotifyState(); }
    partial void OnCompatibilityConfirmedChanged(bool value) => NotifyState();
    partial void OnIsLoadingTargetsChanged(bool value) => NotifyState();
    partial void OnIsLoadingReleasesChanged(bool value) => NotifyState();
    partial void OnIsInstallingChanged(bool value) => NotifyState();

    /// <summary>仅对启动器已发现的实例读取 GTNH 标识，不扫描整个电脑。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshTargets))]
    private Task RefreshTargetsAsync(CancellationToken token) => TargetLoadTask = Track(RefreshTargetsCoreAsync(token));
    private bool CanRefreshTargets() => CanInteract;
    private async Task RefreshTargetsCoreAsync(CancellationToken token)
    {
        if (_closing || Instances.IsBusy) { _targetsDirty = true; return; }
        _targetCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _targetCancellation = cancellation;
        var revision = ++_targetRevision;
        IsLoadingTargets = true; TargetStatus = "正在识别本地 GTNH 实例……";
        var found = new List<GtnhInstanceInfo>(); var recoverable = new List<string>(); var failed = 0;
        try
        {
            foreach (var instance in Instances.Instances.ToArray())
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    if (await _migration.InspectAsync(instance.InstancePath, cancellation.Token) is { } target)
                        found.Add(target with { Name = instance.Name });
                    else if (await _migration.GetRecoveryAsync(instance.InstancePath, cancellation.Token) is not null)
                    {
                        // 原来的迁移恢复允许标识损坏；迁到专区后仍需保留这条修复入口。
                        found.Add(new(instance.InstancePath, instance.Name, instance.GameDirectory, "待恢复"));
                        recoverable.Add(instance.InstancePath);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { failed++; }
            }
            if (_closing || !_active || revision != _targetRevision || cancellation.IsCancellationRequested) return;
            if (Instances.IsBusy) { _targetsDirty = true; return; }
            _targetsDirty = false;
            _recoveryOnly.Clear(); foreach (var path in recoverable) _recoveryOnly.Add(path);
            UpdateItems(Targets, found);
            SelectedTarget = found.FirstOrDefault(item => SamePath(item.InstancePath, Instances.SelectedInstance?.InstancePath)) ?? found.FirstOrDefault();
            TargetStatus = found.Count == 0 ? "尚未发现可识别的 GTNH。请先在本地实例页导入并补全整合包，再刷新这里。"
                : $"已识别 {found.Count} 个 GTNH 实例。推荐模组安装与数据迁移都使用上方选择的目标。";
            if (failed > 0) TargetStatus += $" {failed} 个实例的标识未能读取，可检查后刷新。";
            if (recoverable.Count > 0) TargetStatus += " 标有“待恢复”的实例仅保留数据迁移恢复入口，不能安装推荐模组。";
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_targetCancellation, cancellation)) _targetCancellation = null;
            if (revision == _targetRevision) { IsLoadingTargets = false; QueueReleases(); }
        }
    }

    /// <summary>已有可用列表不重复加载；被切页打断的列表在返回时继续读取。</summary>
    private void QueueReleases()
    {
        if (_active && CanReadReleases() && !IsLoadingReleases && Releases.Count == 0)
            ReleasesLoadTask = Track(LoadReleasesAsync(CancellationToken.None));
    }
    [RelayCommand(CanExecute = nameof(CanReadReleases))]
    private Task RefreshReleasesAsync(CancellationToken token) => ReleasesLoadTask = Track(LoadReleasesAsync(token));
    private bool CanReadReleases() => CanInteract && HasTarget && !_recoveryOnly.Contains(SelectedTarget!.InstancePath) && SelectedAddon is not null;

    /// <summary>按目标和项目读取固定发行文件；不自动选择最新版本，迟到结果不能写入新目标。</summary>
    private async Task LoadReleasesAsync(CancellationToken token)
    {
        if (!CanReadReleases()) return;
        var target = SelectedTarget!; var addon = SelectedAddon!;
        ResetReleases();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _releaseCancellation = cancellation;
        var revision = ++_releaseRevision;
        IsLoadingReleases = true; ReleaseStatus = "正在读取适配项目的发行文件……";
        try
        {
            var releases = await _addons.GetReleasesAsync(target.InstancePath, addon.Id, cancellation.Token);
            if (_closing || !_active || revision != _releaseRevision || cancellation.IsCancellationRequested) return;
            foreach (var release in releases) Releases.Add(release);
            ReleaseStatus = releases.Count == 0 ? "没有找到可用的发行文件，请查看项目主页。"
                : "请选择发行版本并核对适配说明。不会自动采用最新版本；未提供完整校验信息的文件仅供查看。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (revision == _releaseRevision) ReleaseStatus = "读取失败：" + ex.Message; }
        finally
        {
            if (ReferenceEquals(_releaseCancellation, cancellation)) _releaseCancellation = null;
            if (revision == _releaseRevision) IsLoadingReleases = false;
        }
    }

    /// <summary>下载与迁移、启动等共用忙碌状态；服务再次确认目标和文件来源后提交。</summary>
    [RelayCommand(CanExecute = nameof(CanInstall), IncludeCancelCommand = true)]
    private async Task InstallAsync(CancellationToken token)
    {
        if (!CanInstall()) return;
        var target = SelectedTarget!; var release = SelectedRelease!;
        var revision = ++_installRevision;
        IsInstalling = true; Instances.IsBusy = true; _filesChanged();
        DownloadPercent = 0; DownloadText = release.FileName; InstallStatus = "正在下载并校验所选文件……";
        try
        {
            var progress = new Progress<FileDownloadProgress>(value =>
            {
                if (_closing || revision != _installRevision || !IsInstalling || !SamePath(target.InstancePath, SelectedTarget?.InstancePath)) return;
                DownloadPercent = value.Percent ?? 0;
                DownloadText = $"{release.FileName} · {value.BytesReceived / 1048576d:F1} MiB";
            });
            var result = await _addons.InstallAsync(target.InstancePath, release, CompatibilityConfirmed, progress, token);
            if (revision != _installRevision || !SamePath(target.InstancePath, SelectedTarget?.InstancePath)) return;
            InstallStatus = result.Message;
            if (result.Succeeded) DownloadPercent = 100;
        }
        catch (OperationCanceledException)
        { if (SamePath(target.InstancePath, SelectedTarget?.InstancePath)) InstallStatus = "安装已取消。"; }
        catch (Exception ex)
        { if (SamePath(target.InstancePath, SelectedTarget?.InstancePath)) InstallStatus = "安装未完成：" + ex.Message; }
        finally
        {
            ++_installRevision; CompatibilityConfirmed = false; _filesChanged();
            IsInstalling = false; Instances.IsBusy = false;
        }
    }
    private bool CanInstall() => CanReadReleases() && !IsLoadingReleases && CompatibilityConfirmed && SelectedRelease is { CanInstall: true };

    /// <summary>网页只用于查看作者说明；下载由固定来源服务处理。</summary>
    [RelayCommand] private void OpenAddonWebsite() => OpenWebsite(SelectedAddon?.ProjectUrl);
    [RelayCommand] private void OpenReleaseWebsite() => OpenWebsite(SelectedRelease?.ReleaseUrl);
    [RelayCommand] private void OpenGuide() => OpenWebsite("https://gtnh.huijiwiki.com/wiki/%E5%8F%AF%E6%B7%BB%E5%8A%A0MOD");
    private void OpenWebsite(string? address)
    {
        if (_openWebsite is null || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Host is not ("github.com" or "gtnh.huijiwiki.com")) return;
        try { _openWebsite(uri); } catch (Exception ex) { ReleaseStatus = "无法打开网页：" + ex.Message; }
    }

    /// <summary>列表变动延迟到空闲时识别，避免导入逐条发布时重复扫描。</summary>
    private void OnInstanceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    { _targetsDirty = true; if (_active && CanInteract) TargetLoadTask = Track(RefreshTargetsCoreAsync(CancellationToken.None)); }
    private void OnInstancesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closing) return;
        if (e.PropertyName == nameof(InstancesViewModel.SelectedInstance) && !_syncingSelection)
        {
            _syncingSelection = true;
            try { SelectedTarget = Targets.FirstOrDefault(item => SamePath(item.InstancePath, Instances.SelectedInstance?.InstancePath)); }
            finally { _syncingSelection = false; }
        }
        if (e.PropertyName is nameof(InstancesViewModel.IsBusy) or nameof(InstancesViewModel.SelectedInstance))
        {
            NotifyState();
            if (_active && _targetsDirty && CanInteract) TargetLoadTask = Track(RefreshTargetsCoreAsync(CancellationToken.None));
        }
    }

    /// <summary>切换上下文时清空旧发行选择；取消旧请求后由代次屏蔽迟到返回。</summary>
    private void ResetReleases()
    {
        ++_releaseRevision; _releaseCancellation?.Cancel(); IsLoadingReleases = false;
        Releases.Clear(); SelectedRelease = null; CompatibilityConfirmed = false;
        ReleaseStatus = "选择推荐模组，再选择与实例适配的发行版本。";
        if (!IsInstalling) InstallStatus = "确认目标实例和发行说明后再安装。";
    }
    private void CancelReads()
    {
        ++_targetRevision; ++_releaseRevision; _targetCancellation?.Cancel(); _releaseCancellation?.Cancel();
        IsLoadingTargets = false; IsLoadingReleases = false;
    }
    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanInteract)); OnPropertyChanged(nameof(HasTarget));
        RefreshTargetsCommand.NotifyCanExecuteChanged(); RefreshReleasesCommand.NotifyCanExecuteChanged(); InstallCommand.NotifyCanExecuteChanged();
    }
    private Task Track(Task task) { _pending.RemoveWhere(item => item.IsCompleted); _pending.Add(task); return task; }
    private static bool SamePath(string? left, string? right) => left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>保留仍有效的选中对象；清空再填充会使 WPF 双向选择暂时变成空并丢失发行选择。</summary>
    private static void UpdateItems<T>(ObservableCollection<T> current, IReadOnlyList<T> next)
    {
        for (var index = current.Count - 1; index >= 0; index--)
            if (!next.Contains(current[index])) current.RemoveAt(index);
        for (var index = 0; index < next.Count; index++)
        {
            var existing = current.IndexOf(next[index]);
            if (existing < 0) current.Insert(index, next[index]);
            else if (existing != index) current.Move(existing, index);
        }
    }

    /// <summary>窗口关闭时取消并等待本专区任务，避免下载器先于后台工作释放。</summary>
    public async Task StopAsync()
    {
        _closing = true; _active = false; CancelReads(); InstallCommand.Cancel(); NotifyState();
        try { await Task.WhenAll(_pending.Append(InstallCommand.ExecutionTask ?? Task.CompletedTask)); }
        catch (OperationCanceledException) { }
    }
    /// <summary>释放事件订阅；异步任务应先由窗口调用 StopAsync 等待结束。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _closing = true; _active = false; CancelReads(); InstallCommand.Cancel();
        Instances.PropertyChanged -= OnInstancesChanged; Instances.Instances.CollectionChanged -= OnInstanceCollectionChanged;
    }
}
