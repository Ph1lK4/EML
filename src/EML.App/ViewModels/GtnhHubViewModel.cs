using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
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
    private readonly IOperationNotifications _notifications;
    private readonly HashSet<Task> _pending = [];
    private readonly HashSet<string> _recoveryOnly = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _targetCancellation, _releaseCancellation;
    private string? _preservedTargetPath;
    private int _targetRevision, _releaseRevision, _installRevision;
    private bool _active, _closing, _disposed, _syncingSelection, _requiresTargetSelection, _targetsDirty = true;

    /// <summary>接收已有实例模型和服务；构造时不联网或扫描玩家文件。</summary>
    public GtnhHubViewModel(InstancesViewModel instances, IMinecraftInstanceMigrationService migration,
        IGtnhAddonService addons, Action filesChanged, Action<Uri>? openWebsite = null,
        IOperationNotifications? notifications = null, IGtnhPackService? packs = null)
    {
        Instances = instances; _migration = migration; _addons = addons;
        _filesChanged = filesChanged; _openWebsite = openWebsite;
        _notifications = notifications ?? NullOperationNotifications.Instance;
        if (packs is not null)
            Packs = new GtnhPackDownloadViewModel(packs,
                (release, progress, token) => instances.DownloadInstanceAsync(
                    (downloadProgress, cancellation) => packs.PrepareAsync(release.Id, downloadProgress, cancellation), progress, token),
                () => !instances.IsBusy, openWebsite, _notifications);
        ApplyCatalogFilter();
        instances.PropertyChanged += OnInstancesChanged;
        instances.Instances.CollectionChanged += OnInstanceCollectionChanged;
    }

    public InstancesViewModel Instances { get; }
    /// <summary>下载本体创建新实例，不依赖已有 GTNH 目标或迁移选择。</summary>
    public GtnhPackDownloadViewModel? Packs { get; }
    public ObservableCollection<GtnhInstanceInfo> Targets { get; } = [];
    public ObservableCollection<GtnhAddon> Addons { get; } = [];
    public ObservableCollection<GtnhAddonRelease> Releases { get; } = [];
    public Task TargetLoadTask { get; private set; } = Task.CompletedTask;
    public Task ReleasesLoadTask { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private GtnhInstanceInfo? selectedTarget;
    [ObservableProperty] private GtnhAddon? selectedAddon;
    [ObservableProperty] private GtnhAddonRelease? selectedRelease;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string selectedCategory = "全部分类";
    [ObservableProperty] private int ruleFilterIndex;
    [ObservableProperty] private int selectedSection;
    [ObservableProperty] private bool isLoadingTargets;
    [ObservableProperty] private bool isLoadingReleases;
    [ObservableProperty] private bool isInstalling;
    [ObservableProperty] private bool isImportingOldInstance;
    [ObservableProperty] private bool compatibilityConfirmed;
    [ObservableProperty] private string targetStatus = "进入专区后识别本地 GTNH 实例。";
    [ObservableProperty] private string releaseStatus = "选择推荐模组，再选择与实例适配的发行版本。";
    [ObservableProperty] private string installStatus = "确认目标实例和发行说明后再安装。";
    [ObservableProperty] private string downloadText = "";
    [ObservableProperty] private double downloadPercent;
    public bool CanInteract => !_closing && !Instances.IsBusy && !IsInstalling && !IsImportingOldInstance && !IsLoadingTargets;
    public bool HasTarget => SelectedTarget is not null && SamePath(SelectedTarget.InstancePath, Instances.SelectedInstance?.InstancePath);
    public bool IsRecommendations => SelectedSection == 0;
    public bool IsMigration => SelectedSection == 1;
    public bool IsResourcePacks => SelectedSection == 2;
    public bool IsShaders => SelectedSection == 3;
    public bool IsPackDownloads => SelectedSection == 4;
    public bool IsCatalogPage => SelectedSection is 0 or 2 or 3;
    public IReadOnlyList<string> RuleFilters { get; } = ["全部规则分组", "常规分类", "非星门规则"];
    public IReadOnlyList<string> Categories => ["全部分类", .. _addons.Addons
        .Where(InCurrentSection)
        .Select(addon => addon.Category).Distinct()];
    public string CatalogSummary => $"当前显示 {Addons.Count} 个" + (IsResourcePacks ? "资源包" : IsShaders ? "光影相关" : "模组") + "项目";
    public string CatalogEmptyMessage => "没有匹配的项目，请调整分类、规则分组或搜索词。";
    public bool IsCatalogEmpty => Addons.Count == 0;
    public bool HasSelectedAddon => SelectedAddon is not null;
    public bool IsNonStargateAddon => SelectedAddon?.IsNonStargate == true;
    /// <summary>旧实例入口只准备迁移来源，不从普通安装包推断玩家记录。</summary>
    public string MigrationImportHint => "导入后会自动补全依赖，再选择新版作为接收方；不会自动开始迁移。";
    public string InstallationHint => SelectedAddon?.Kind switch
    {
        GtnhAddonKind.ResourcePack => "安装到 resourcepacks 后，请在游戏中自行启用并调整加载顺序。不会覆盖已有文件或自动更改 options.txt。",
        GtnhAddonKind.Shader => "安装到 shaderpacks 后，请在游戏中自行选择光影。请先核对加载器支持范围；不会自动替换 Angelica 或 OptiFine。",
        _ => "额外模组可在实例的“模组管理”中启用、禁用或移除。原包模组不会被覆盖；请核对前置依赖及服务端要求。"
    };

    /// <summary>光影包和加载器共享展示页，但保留不同文件种类与安装目录。</summary>
    private bool InCurrentSection(GtnhAddon addon) => SelectedSection switch
    {
        2 => addon.Kind == GtnhAddonKind.ResourcePack,
        3 => addon.Kind is GtnhAddonKind.Shader or GtnhAddonKind.ShaderLoader,
        _ => addon.Kind == GtnhAddonKind.Mod
    };

    /// <summary>打开时识别目标，离开只取消只读请求，用户已确认的下载继续执行。</summary>
    public void SetActive(bool active)
    {
        if (_closing || _active == active) return;
        _active = active;
        Packs?.SetActive(active && IsPackDownloads);
        if (!active) { CancelReads(); return; }
        if (!Instances.IsBusy && !IsImportingOldInstance) TargetLoadTask = Track(RefreshTargetsCoreAsync(CancellationToken.None));
        else _targetsDirty = true;
    }

    /// <summary>打开推荐模组目录，不改变当前安装目标。</summary>
    [RelayCommand] private void ShowRecommendations() => SelectedSection = 0;
    /// <summary>打开数据迁移页，继续使用上方选择的新实例。</summary>
    [RelayCommand] private void ShowMigration() => SelectedSection = 1;
    /// <summary>打开独立资源包目录，不把资源包作为模组安装。</summary>
    [RelayCommand] private void ShowResourcePacks() => SelectedSection = 2;
    /// <summary>打开光影及加载器目录，安装时仍按各自种类分流。</summary>
    [RelayCommand] private void ShowShaders() => SelectedSection = 3;
    /// <summary>打开国内镜像客户端下载页，空实例库也可以使用。</summary>
    [RelayCommand] private void ShowPackDownloads() => SelectedSection = 4;

    /// <summary>复用普通导入及自动补全，结束后按路径恢复新版接收方，不自动发起迁移。</summary>
    [RelayCommand(CanExecute = nameof(CanImportOldInstance), IncludeCancelCommand = true)]
    private async Task ImportOldInstanceAsync(CancellationToken token)
    {
        if (!CanImportOldInstance()) return;
        var targetPath = HasTarget ? SelectedTarget!.InstancePath : Instances.SelectedInstance?.InstancePath;
        IsImportingOldInstance = true;
        CancelReads(); ResetReleases(); _targetsDirty = true;
        try
        {
            token.ThrowIfCancellationRequested();
            var import = Instances.ImportCommand.ExecuteAsync(null);
            using var registration = token.Register(Instances.ImportCommand.Cancel);
            await import;
        }
        catch (OperationCanceledException) { }
        finally
        {
            // 导入会重建实例对象并选中旧版，不能沿用旧引用或让旧版成为迁移接收方。
            _requiresTargetSelection = true;
            _preservedTargetPath = targetPath;
            if (!_closing)
            {
                _syncingSelection = true;
                try
                {
                    var restored = Instances.Instances.FirstOrDefault(item => SamePath(item.InstancePath, targetPath));
                    Instances.SelectedInstance = restored;
                    SelectedTarget = restored is null ? null : Targets.FirstOrDefault(item => SamePath(item.InstancePath, targetPath));
                }
                finally { _syncingSelection = false; }
            }
            IsImportingOldInstance = false;
            if (!_closing && _active)
                await (TargetLoadTask = Track(RefreshTargetsCoreAsync(CancellationToken.None)));
        }
    }
    /// <summary>导入来源不要求已有目标；同一时间只允许一项实例写入操作。</summary>
    private bool CanImportOldInstance() => !_closing && !Instances.IsBusy && !IsInstalling && !IsImportingOldInstance &&
        Instances.ImportCommand.CanExecute(null);
    /// <summary>目录种类切换后重置分类，保持迁移页索引及目标实例不变。</summary>
    partial void OnSelectedSectionChanged(int value)
    {
        OnPropertyChanged(nameof(IsRecommendations)); OnPropertyChanged(nameof(IsMigration));
        OnPropertyChanged(nameof(IsResourcePacks)); OnPropertyChanged(nameof(IsShaders)); OnPropertyChanged(nameof(IsCatalogPage));
        OnPropertyChanged(nameof(IsPackDownloads));
        Packs?.SetActive(_active && IsPackDownloads);
        OnPropertyChanged(nameof(Categories)); OnPropertyChanged(nameof(InstallationHint));
        if (value is 0 or 2 or 3) { SelectedCategory = "全部分类"; RuleFilterIndex = 0; ApplyCatalogFilter(); }
    }

    /// <summary>搜索只过滤人工目录，不改变平台仓库和下载身份。</summary>
    partial void OnSearchTextChanged(string value) => ApplyCatalogFilter();
    /// <summary>分类筛选只更新当前列表，不访问网络或修改实例。</summary>
    partial void OnSelectedCategoryChanged(string value) => ApplyCatalogFilter();
    /// <summary>规则分组独立于功能分类；不会把分类标签当作发行版兼容证明。</summary>
    partial void OnRuleFilterIndexChanged(int value) => ApplyCatalogFilter();
    /// <summary>按资源种类、分类、规则和关键词取交集，保留仍然可见的项目选择。</summary>
    private void ApplyCatalogFilter()
    {
        var filter = SearchText.Trim();
        var matches = _addons.Addons.Where(addon => InCurrentSection(addon) &&
            (SelectedCategory == "全部分类" || addon.Category == SelectedCategory) &&
            (RuleFilterIndex == 0 || addon.IsNonStargate == (RuleFilterIndex == 2)) &&
            (filter.Length == 0 || (addon.Name + " " + addon.Summary + " " + addon.Repository + " " + addon.Category)
                .Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
        UpdateItems(Addons, matches);
        if (SelectedAddon is not null && !matches.Contains(SelectedAddon)) SelectedAddon = null;
        OnPropertyChanged(nameof(CatalogSummary)); OnPropertyChanged(nameof(IsCatalogEmpty)); OnPropertyChanged(nameof(CatalogEmptyMessage));
    }

    /// <summary>专区选择与实例管理共享同一目标，迁移按钮不会沿用另一实例。</summary>
    partial void OnSelectedTargetChanged(GtnhInstanceInfo? value)
    {
        if (IsInstalling) InstallCommand.Cancel();
        if (!_syncingSelection && !IsLoadingTargets && !IsImportingOldInstance && value is not null)
        { _requiresTargetSelection = false; _preservedTargetPath = null; }
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
    partial void OnSelectedAddonChanged(GtnhAddon? value)
    {
        ResetReleases();
        OnPropertyChanged(nameof(HasSelectedAddon)); OnPropertyChanged(nameof(IsNonStargateAddon)); OnPropertyChanged(nameof(InstallationHint));
        QueueReleases(); NotifyState();
    }
    partial void OnSelectedReleaseChanged(GtnhAddonRelease? value) { CompatibilityConfirmed = false; NotifyState(); }
    partial void OnCompatibilityConfirmedChanged(bool value) => NotifyState();
    partial void OnIsLoadingTargetsChanged(bool value) => NotifyState();
    partial void OnIsLoadingReleasesChanged(bool value) => NotifyState();
    partial void OnIsInstallingChanged(bool value) => NotifyState();
    partial void OnIsImportingOldInstanceChanged(bool value) => NotifyState();

    /// <summary>仅对启动器已发现的实例读取 GTNH 标识，不扫描整个电脑。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshTargets))]
    private Task RefreshTargetsAsync(CancellationToken token) => TargetLoadTask = Track(RefreshTargetsCoreAsync(token, notifySuccess: true));
    private bool CanRefreshTargets() => CanInteract;
    private async Task RefreshTargetsCoreAsync(CancellationToken token, bool notifySuccess = false)
    {
        if (_closing || Instances.IsBusy || IsImportingOldInstance) { _targetsDirty = true; return; }
        _targetCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _targetCancellation = cancellation;
        var revision = ++_targetRevision;
        IsLoadingTargets = true; TargetStatus = "正在识别本地 GTNH 实例……";
        var found = new List<GtnhInstanceInfo>(); var recoverable = new List<string>();
        var failures = new List<(string Path, string Reason)>();
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
                catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add((instance.InstancePath, ex.Message)); }
            }
            if (_closing || !_active || revision != _targetRevision || cancellation.IsCancellationRequested) return;
            if (Instances.IsBusy) { _targetsDirty = true; return; }
            _targetsDirty = false;
            _recoveryOnly.Clear(); foreach (var path in recoverable) _recoveryOnly.Add(path);
            var selectedPath = _requiresTargetSelection ? _preservedTargetPath : Instances.SelectedInstance?.InstancePath;
            UpdateItems(Targets, found);
            SelectedTarget = found.FirstOrDefault(item => SamePath(item.InstancePath, selectedPath)) ??
                (_requiresTargetSelection ? null : found.FirstOrDefault());
            if (_requiresTargetSelection && SelectedTarget is null) Instances.SelectedInstance = null;
            TargetStatus = found.Count == 0 ? "尚未发现可识别的 GTNH。请先在本地实例页导入并补全整合包，再刷新这里。"
                : $"已识别 {found.Count} 个 GTNH 实例。推荐模组安装与数据迁移都使用上方选择的目标。";
            if (failures.Count > 0) TargetStatus += $" {failures.Count} 个实例的标识未能读取，可检查后刷新。";
            if (recoverable.Count > 0) TargetStatus += " 标有“待恢复”的实例仅保留数据迁移恢复入口，不能安装推荐模组。";
            if (failures.Count > 0)
            {
                var details = TargetStatus + "\n" + string.Join("\n", failures.Select(item => item.Path + "：" + item.Reason));
                var paths = failures.Select(item => item.Path).ToArray();
                if (found.Count == 0) _notifications.Failure("GTNH 实例识别未完成", details, paths);
                else _notifications.Warning("GTNH 实例识别部分未完成", details, paths);
            }
            else if (notifySuccess) _notifications.Success("GTNH 实例列表已刷新", TargetStatus, Instances.InstancesRoot);
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
    private Task RefreshReleasesAsync(CancellationToken token) => ReleasesLoadTask = Track(LoadReleasesAsync(token, notifySuccess: true));
    private bool CanReadReleases() => CanInteract && HasTarget && !_recoveryOnly.Contains(SelectedTarget!.InstancePath) &&
        SelectedAddon is { CanQueryReleases: true };

    /// <summary>按目标和项目读取固定发行文件；不自动选择最新版本，迟到结果不能写入新目标。</summary>
    private async Task LoadReleasesAsync(CancellationToken token, bool notifySuccess = false)
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
            var installable = releases.Count(release => release.CanInstall);
            var extension = addon.Kind is GtnhAddonKind.ResourcePack or GtnhAddonKind.Shader ? "ZIP" : "JAR";
            ReleaseStatus = $"可安装 {installable} 个，不能自动安装 {releases.Count - installable} 个。" +
                (releases.Count == 0 ? $"本次未找到可供此入口安装的 {extension} 文件，请查看项目主页。"
                    : installable == 0 ? $"当前没有可自动安装的 {extension} 文件，请查看各文件的具体原因或项目主页。"
                    : "请选择发行版本并核对适配说明。不会自动采用最新版本；未提供完整校验信息的文件仅供查看。");
            if (notifySuccess) _notifications.Success("GTNH 发行版本列表已刷新", ReleaseStatus);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (revision == _releaseRevision && !_closing && _active && !cancellation.IsCancellationRequested)
            {
                ReleaseStatus = "读取失败：" + ex.Message;
                _notifications.Failure("GTNH 发行版本读取失败", ex, target.InstancePath);
            }
        }
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
        var folder = SelectedAddon?.Kind switch { GtnhAddonKind.ResourcePack => "resourcepacks", GtnhAddonKind.Shader => "shaderpacks", _ => "mods" };
        var destination = System.IO.Path.Combine(target.GameDirectory, folder, release.FileName);
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
            // 服务可能为同名资源分配本地别名，通知必须使用实际落盘路径。
            destination = result.DestinationPath ?? destination;
            if (_closing || revision != _installRevision || !SamePath(target.InstancePath, SelectedTarget?.InstancePath)) return;
            InstallStatus = result.Message;
            if (result.Succeeded) DownloadPercent = 100;
            if (result.Cancelled) _notifications.Cancelled("GTNH 项目安装已取消", InstallStatus, destination);
            else if (result.Succeeded) _notifications.Success("GTNH 项目安装完成", InstallStatus, destination);
            else _notifications.Failure("GTNH 项目安装未完成", InstallStatus, destination);
        }
        catch (OperationCanceledException)
        {
            if (!_closing && SamePath(target.InstancePath, SelectedTarget?.InstancePath))
            {
                InstallStatus = "安装已取消。";
                _notifications.Cancelled("GTNH 项目安装已取消", InstallStatus, destination);
            }
        }
        catch (Exception ex)
        {
            if (!_closing && SamePath(target.InstancePath, SelectedTarget?.InstancePath))
            {
                InstallStatus = "安装未完成：" + ex.Message;
                _notifications.Failure("GTNH 项目安装未完成", ex, destination);
            }
        }
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
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
            uri.Host is not ("github.com" or "gtnh.huijiwiki.com" or "www.curseforge.com" or "www.minecraftforum.net" or
                "modrinth.com" or "optifine.net" or "www.optifine.net" or "gitlab.com" or "discord.com" or "web.archive.org")) return;
        try { _openWebsite(uri); }
        catch (Exception ex)
        {
            ReleaseStatus = "无法打开网页：" + ex.Message;
            _notifications.Failure("无法打开 GTNH 网页", ex);
        }
    }

    /// <summary>列表变动延迟到空闲时识别，避免导入逐条发布时重复扫描。</summary>
    private void OnInstanceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    { _targetsDirty = true; if (_active && CanInteract) TargetLoadTask = Track(RefreshTargetsCoreAsync(CancellationToken.None)); }
    private void OnInstancesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closing) return;
        if (e.PropertyName == nameof(InstancesViewModel.SelectedInstance) && !_syncingSelection && !IsImportingOldInstance &&
            !(_requiresTargetSelection && Instances.IsBusy))
        {
            _syncingSelection = true;
            try
            {
                SelectedTarget = Targets.FirstOrDefault(item => SamePath(item.InstancePath, Instances.SelectedInstance?.InstancePath));
                if (SelectedTarget is not null) { _requiresTargetSelection = false; _preservedTargetPath = null; }
            }
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
        ReleaseStatus = SelectedAddon?.ManualReason ?? (SelectedAddon is { Provider: GtnhAddonProvider.External }
            ? "此项目需前往作者页面查看下载及安装说明。" : "选择推荐模组，再选择与实例适配的发行版本。");
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
        Packs?.NotifyHostStateChanged();
        RefreshTargetsCommand.NotifyCanExecuteChanged(); RefreshReleasesCommand.NotifyCanExecuteChanged(); InstallCommand.NotifyCanExecuteChanged();
        ImportOldInstanceCommand.NotifyCanExecuteChanged();
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
        _closing = true; _active = false; CancelReads(); InstallCommand.Cancel(); ImportOldInstanceCommand.Cancel();
        if (IsImportingOldInstance) Instances.ImportCommand.Cancel();
        NotifyState();
        try { await Task.WhenAll(_pending.Append(InstallCommand.ExecutionTask ?? Task.CompletedTask)
            .Append(ImportOldInstanceCommand.ExecutionTask ?? Task.CompletedTask)
            .Append(Packs?.StopAsync() ?? Task.CompletedTask)); }
        catch (OperationCanceledException) { }
    }
    /// <summary>释放事件订阅；异步任务应先由窗口调用 StopAsync 等待结束。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _closing = true; _active = false; CancelReads(); InstallCommand.Cancel(); ImportOldInstanceCommand.Cancel();
        if (IsImportingOldInstance) Instances.ImportCommand.Cancel();
        Packs?.Dispose();
        Instances.PropertyChanged -= OnInstancesChanged; Instances.Instances.CollectionChanged -= OnInstanceCollectionChanged;
    }
}
