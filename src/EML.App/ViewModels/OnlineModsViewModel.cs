using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods.Online;

namespace EML.App.ViewModels;

/// <summary>在线目录与安装预览；查询不占用实例写锁，确认安装才进入全局忙碌状态。</summary>
public partial class OnlineModsViewModel : ObservableObject
{
    private readonly IOnlineModCatalog _catalog;
    private readonly IOnlineModInstaller _installer;
    private readonly IModIconCache? _icons;
    private readonly IOnlineModLocalization? _localization;
    private readonly Func<bool> _canModify;
    private readonly Action<bool> _setBusy;
    private readonly Action _filesChanged;
    private readonly Action<Uri>? _openWebsite;
    private readonly IOperationNotifications _notifications;
    private readonly HashSet<Task> _pending = [];
    private CancellationTokenSource? _searchCancellation, _detailCancellation, _versionCancellation, _planCancellation;
    private int _contextRevision, _searchRevision, _detailRevision, _versionRevision, _planRevision, _installRevision;
    private int _searchPage, _versionPage;
    private bool _active, _closing, _settingContext;
    private string? _instancePath;

    /// <summary>查询接口可由测试替换，文件写入通过宿主页的忙碌状态与其他实例操作互斥。</summary>
    public OnlineModsViewModel(IOnlineModCatalog catalog, IOnlineModInstaller installer, IModIconCache? icons = null,
        Func<bool>? canModify = null, Action<bool>? setBusy = null, Action? filesChanged = null, Action<Uri>? openWebsite = null,
        IOnlineModLocalization? localization = null, IOperationNotifications? notifications = null)
    {
        _catalog = catalog; _installer = installer; _icons = icons; _localization = localization;
        _canModify = canModify ?? (() => true); _setBusy = setBusy ?? (_ => { });
        _filesChanged = filesChanged ?? (() => { }); _openWebsite = openWebsite;
        _notifications = notifications ?? NullOperationNotifications.Instance;
    }

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private int providerIndex;
    [ObservableProperty] private string gameVersion = "";
    [ObservableProperty] private string loader = "forge";
    [ObservableProperty] private int categoryIndex;
    [ObservableProperty] private int environmentIndex;
    [ObservableProperty] private int sortIndex;
    [ObservableProperty] private string targetText = "请先选择实例。";
    [ObservableProperty] private string status = "进入在线模组页后搜索。";
    [ObservableProperty] private string detailStatus = "选择项目可查看介绍和兼容版本。";
    [ObservableProperty] private string installStatus = "选择精确版本和文件后，先预览安装清单。";
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private double installPercent;
    [ObservableProperty] private bool isSearching;
    [ObservableProperty] private bool isLoadingDetails;
    [ObservableProperty] private bool isLoadingVersion;
    [ObservableProperty] private bool isPlanning;
    [ObservableProperty] private bool isInstalling;
    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private bool hasMoreVersions;
    [ObservableProperty] private bool includeRequired = true;
    [ObservableProperty] private OnlineModResultRow? selectedProject;
    [ObservableProperty] private OnlineModProject? projectDetails;
    [ObservableProperty] private OnlineModVersion? selectedVersion;
    [ObservableProperty] private OnlineModVersion? versionDetails;
    [ObservableProperty] private OnlineModFile? selectedFile;
    [ObservableProperty] private OnlineModInstallPlan? plan;

    public ObservableCollection<OnlineModResultRow> Projects { get; } = [];
    public ObservableCollection<OnlineModVersion> Versions { get; } = [];
    public ObservableCollection<OptionalModChoice> OptionalDependencies { get; } = [];
    public IReadOnlyList<string> Providers { get; } = ["全部平台", "Modrinth", "CurseForge"];
    public IReadOnlyList<string> Loaders { get; } = ["", "forge", "fabric", "neoforge", "quilt", "liteloader"];
    public IReadOnlyList<string> Categories { get; } = ["全部分类", "科技", "魔法", "冒险", "世界生成", "存储", "前置 / 库", "实用工具", "性能优化"];
    public IReadOnlyList<string> Environments { get; } = ["不限环境", "客户端", "服务端", "双端"];
    public IReadOnlyList<string> Sorts { get; } = ["相关度", "下载量", "最近更新", "最新发布"];
    private static readonly string[] CategoryKeys = ["", "technology", "magic", "adventure", "worldgen", "storage", "library", "utility", "optimization"];
    private static readonly string[] EnvironmentKeys = ["", "client", "server", "both"];
    /// <summary>自动查询任务供关闭窗口与测试等待；构造页面不会发出网络请求。</summary>
    public Task SearchTask { get; private set; } = Task.CompletedTask;
    public Task DetailTask { get; private set; } = Task.CompletedTask;
    public Task VersionTask { get; private set; } = Task.CompletedTask;
    public bool CanEdit => !_closing && !IsInstalling && _canModify();
    public string ProjectDisplayName => ProjectDetails is not { } project ? "" : OnlineModDisplayText.Name(project.Id, project.Name, _localization);
    public string ProjectDisplaySummary => ProjectDetails is not { } project ? "" : OnlineModDisplayText.Summary(project.Id, project.Summary, _localization);
    public string ProjectSummaryNote => ProjectDetails is not { } project ? "" : OnlineModDisplayText.SummaryNote(project.Id, _localization);
    public string ProjectInformation => ProjectDetails is not { } project ? "" :
        $"{project.Id.Provider} · {project.Author}\n下载 {project.Downloads:N0} 次 · 环境：{project.Environment}\n许可证：{project.License}\n" +
        (project.Updated == default ? "" : $"最近更新：{project.Updated:yyyy-MM-dd}\n") +
        $"游戏版本：{string.Join("、", project.GameVersions)}\n加载器：{string.Join("、", project.Loaders.Select(OnlineModDisplayText.Loader))}";
    public string FileInformation => SelectedFile is not { } file ? "" :
        $"{file.FileName}\n大小：{(file.Requirement.Size ?? 0) / 1048576d:F2} MiB\n" +
        string.Join("\n", file.Requirement.Hashes.Select(hash => hash.Key + ": " + hash.Value)) +
        (file.Problem is null ? "" : "\n" + file.Problem);
    public string DependencyInformation => VersionDetails is not { } version ? "" :
        string.Join("\n", version.Dependencies.Where(item => item.Kind != OnlineDependencyKind.Optional)
            .Select(item => $"{DependencyLabel(item.Kind)}：{OnlineModDisplayText.Name(item.Project, item.Name, _localization)}"));
    public string PlanSummary => Plan is not { } plan ? "尚未生成清单。" :
        $"下载 {plan.Items.Count(item => item.Status == OnlineModPlanStatus.Download)} 个文件，约 {plan.TotalBytes / 1048576d:F2} MiB；" +
        $"已安装 {plan.Items.Count(item => item.Status == OnlineModPlanStatus.AlreadyInstalled)} 个。\n" + string.Join("\n", plan.Warnings);
    public string PlanDetails => Plan is not { } plan ? "" : string.Join("\n\n", plan.Items.Select(item =>
        $"{PlanLabel(item.Status)} · {item.File.FileName}\n{item.Version.Project.Provider} · {item.Version.VersionNumber} · {item.Reason}" +
        $"\n文件来源：{DownloadOriginLabel(item.File.Requirement)} · {(item.File.Requirement.Size ?? 0) / 1048576d:F2} MiB" +
        (item.Message is null ? "" : "\n" + item.Message) +
        (item.File.ManualDownloadUrl is null ? "" : "\n手动下载：" + item.File.ManualDownloadUrl)));

    /// <summary>切换实例立即取消旧查询、清空计划，并使用扫描所得的版本与加载器作为搜索默认值。</summary>
    public void SetInstance(MinecraftInstance? instance)
    {
        if (IsInstalling && !string.Equals(_instancePath, instance?.InstancePath, StringComparison.OrdinalIgnoreCase))
            InstallCommand.Cancel();
        ++_contextRevision; CancelReads(); _settingContext = true;
        try
        {
            _instancePath = instance?.InstancePath;
            TargetText = instance is null ? "请先选择实例。" : "安装目标：" + instance.Name;
            GameVersion = instance?.Versions.FirstOrDefault()?.MinecraftVersion ?? "";
            if (GameVersion == "未知") GameVersion = "";
            Loader = DetectLoader(instance?.Versions.FirstOrDefault()?.Loader);
            Projects.Clear(); SelectedProject = null; HasMore = false;
            Status = instance is null ? "请先选择实例。" : "输入关键词或点击搜索。";
            InstallStatus = "选择精确版本和文件后，先预览安装清单。";
            InvalidatePlan();
        }
        finally { _settingContext = false; }
        NotifyHostStateChanged();
        if (_active) QueueSearch();
    }

    /// <summary>返回页面时恢复未完成的详情读取；保留已加载选择，安装仍由独立取消按钮控制。</summary>
    public void SetActive(bool active)
    {
        if (_closing) return;
        var entering = active && !_active;
        _active = active;
        if (!active) { CancelReads(); return; }
        if (!entering || IsInstalling || _instancePath is null) return;

        // 取消请求不会清空选择；返回时按缺失层级恢复，避免重复搜索或改选用户的固定版本。
        if (Projects.Count == 0) QueueSearch();
        else if (SelectedProject is { } project && ProjectDetails is null)
            DetailTask = Track(LoadProjectAsync(project.Project));
        else if (SelectedVersion is { } version && VersionDetails is null)
            VersionTask = Track(LoadVersionAsync(version));
    }

    /// <summary>宿主的运行、补全和文件管理状态变化时，刷新安装及编辑权限。</summary>
    public void NotifyHostStateChanged()
    {
        if (!_canModify() && !IsInstalling && Plan is not null) InvalidatePlan();
        OnPropertyChanged(nameof(CanEdit)); NotifyCommands();
    }

    /// <summary>窗口关闭时取消网络和安装任务，并等待所有已经安排的任务退出。</summary>
    public async Task StopAsync()
    {
        _closing = true; _active = false; CancelReads(); InstallCommand.Cancel(); NotifyHostStateChanged();
        var tasks = _pending.Concat(new Task?[] { InstallCommand.ExecutionTask, SearchCommand.ExecutionTask,
            LoadMoreCommand.ExecutionTask, LoadMoreVersionsCommand.ExecutionTask, PreviewCommand.ExecutionTask }
            .OfType<Task>()).ToArray();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
    }

    /// <summary>绑定输入变化触发防抖查询，不在启动或未打开页面时访问平台。</summary>
    partial void OnSearchTextChanged(string value) => FiltersChanged();
    partial void OnProviderIndexChanged(int value) => FiltersChanged();
    partial void OnGameVersionChanged(string value) => FiltersChanged();
    partial void OnLoaderChanged(string value) => FiltersChanged();
    partial void OnCategoryIndexChanged(int value) => FiltersChanged();
    partial void OnEnvironmentIndexChanged(int value) => FiltersChanged();
    partial void OnSortIndexChanged(int value) => FiltersChanged();
    partial void OnIncludeRequiredChanged(bool value) => InvalidatePlan();
    partial void OnSelectedProjectChanged(OnlineModResultRow? value)
    {
        ResetDetails();
        if (value is not null && !_closing) DetailTask = Track(LoadProjectAsync(value.Project));
    }
    partial void OnSelectedVersionChanged(OnlineModVersion? value)
    {
        _versionCancellation?.Cancel(); ++_versionRevision;
        VersionDetails = null; SelectedFile = null; ClearOptional(); InvalidatePlan();
        if (value is not null && !_closing) VersionTask = Track(LoadVersionAsync(value));
    }
    partial void OnSelectedFileChanged(OnlineModFile? value) { OnPropertyChanged(nameof(FileInformation)); InvalidatePlan(); }
    partial void OnProjectDetailsChanged(OnlineModProject? value)
    {
        OnPropertyChanged(nameof(ProjectInformation)); OnPropertyChanged(nameof(ProjectDisplayName));
        OnPropertyChanged(nameof(ProjectDisplaySummary)); OnPropertyChanged(nameof(ProjectSummaryNote));
        OpenProjectCommand.NotifyCanExecuteChanged();
    }
    partial void OnVersionDetailsChanged(OnlineModVersion? value) => OnPropertyChanged(nameof(DependencyInformation));
    partial void OnPlanChanged(OnlineModInstallPlan? value)
    {
        OnPropertyChanged(nameof(PlanSummary)); OnPropertyChanged(nameof(PlanDetails)); NotifyCommands();
    }
    partial void OnIsSearchingChanged(bool value) => NotifyCommands();
    partial void OnIsLoadingDetailsChanged(bool value) => NotifyCommands();
    partial void OnIsLoadingVersionChanged(bool value) => NotifyCommands();
    partial void OnIsPlanningChanged(bool value) => NotifyCommands();
    partial void OnIsInstallingChanged(bool value) => NotifyHostStateChanged();
    partial void OnHasMoreChanged(bool value) => LoadMoreCommand.NotifyCanExecuteChanged();
    partial void OnHasMoreVersionsChanged(bool value) => LoadMoreVersionsCommand.NotifyCanExecuteChanged();

    /// <summary>清除与旧筛选相关的详情和清单，避免下载与当前显示条件不一致的结果。</summary>
    private void FiltersChanged()
    {
        if (_settingContext) return;
        SelectedProject = null; HasMore = false; InvalidatePlan();
        QueueSearch();
    }

    /// <summary>立即使旧响应失效，再安排 350 ms 防抖任务。</summary>
    private void QueueSearch()
    {
        _searchCancellation?.Cancel(); ++_searchRevision;
        if (!_active || _closing || _instancePath is null || IsInstalling) return;
        SearchTask = Track(SearchCoreAsync(false, true, CancellationToken.None));
    }

    /// <summary>显式搜索可以立即提交当前条件，不必等待输入防抖。</summary>
    [RelayCommand(CanExecute = nameof(CanSearch), AllowConcurrentExecutions = true)]
    private Task SearchAsync(CancellationToken token)
    {
        if (!CanSearch()) return Task.CompletedTask;
        return SearchTask = Track(SearchCoreAsync(false, false, token));
    }

    /// <summary>继续读取同一查询的下一页；失败后仍保留当前结果和页码。</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync(CancellationToken token) => Track(SearchCoreAsync(true, false, token));

    /// <summary>按查询代次发布结果；忽略已取消或来自旧实例的迟到响应。</summary>
    private async Task SearchCoreAsync(bool append, bool debounce, CancellationToken token)
    {
        _searchCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _searchCancellation = cancellation;
        var revision = ++_searchRevision; var context = _contextRevision;
        var page = append ? _searchPage + 1 : 0;
        var originalSearch = SearchText.Trim();
        var query = Query(page);
        IsSearching = true;
        if (!append) { Projects.Clear(); SelectedProject = null; HasMore = false; }
        try
        {
            if (debounce) await Task.Delay(350, cancellation.Token);
            if (!SearchCurrent(revision, context)) return;
            Status = "正在搜索模组……";
            var result = await _catalog.SearchAsync(query, cancellation.Token);
            if (!SearchCurrent(revision, context) || cancellation.IsCancellationRequested) return;
            var known = Projects.Select(row => row.Project.Id).ToHashSet();
            foreach (var project in result.Items.Where(project => known.Add(project.Id)))
            {
                var row = new OnlineModResultRow(project, _localization); Projects.Add(row);
                _ = Track(LoadIconAsync(row, revision, context, cancellation.Token));
            }
            _searchPage = page; HasMore = result.HasMore;
            Status = $"已显示 {Projects.Count} 个项目。" +
                (query.Text == originalSearch ? "" : $"\n已按“{query.Text}”搜索“{originalSearch}”。") +
                (result.Notes.Count == 0 ? "" : "\n" + string.Join("\n", result.Notes));
            if (!debounce)
            {
                if (result.Notes.Count > 0) _notifications.Warning("模组搜索完成，部分结果需注意", Status);
                else _notifications.Success("模组搜索完成", Status);
            }
        }
        catch (OperationCanceledException)
        {
            if (SearchCurrent(revision, context))
            {
                Status = "已取消搜索。";
                if (!debounce) _notifications.Cancelled("模组搜索已取消", Status);
            }
        }
        catch (Exception ex)
        {
            if (SearchCurrent(revision, context))
            {
                Status = "搜索失败：" + ex.Message;
                _notifications.Failure("模组搜索失败", ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation)) _searchCancellation = null;
            if (SearchCurrent(revision, context)) IsSearching = false;
        }
    }

    /// <summary>图标失败只保留占位符，旧查询的图标不能重新加入或改写当前结果。</summary>
    private async Task LoadIconAsync(OnlineModResultRow row, int revision, int context, CancellationToken token)
    {
        if (_icons is null) return;
        try
        {
            var path = await _icons.GetAsync(row.Project.IconUrl, token);
            if (SearchCurrent(revision, context) && !token.IsCancellationRequested && Projects.Contains(row)) row.IconPath = path;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 图标不影响项目和文件查询。 */ }
    }

    /// <summary>并行读取项目介绍和首屏兼容版本，只有当前选中项目可以发布结果。</summary>
    private async Task LoadProjectAsync(OnlineModProject project)
    {
        using var cancellation = new CancellationTokenSource(); _detailCancellation = cancellation;
        var revision = ++_detailRevision; var context = _contextRevision;
        IsLoadingDetails = true; DetailStatus = "正在读取项目介绍和兼容版本……";
        try
        {
            var detail = _catalog.GetProjectAsync(project.Id, cancellation.Token);
            var versions = _catalog.GetVersionsAsync(project.Id, GameVersion.Trim(), Loader, 0, cancellation.Token);
            await Task.WhenAll(detail, versions);
            if (!DetailCurrent(revision, context) || cancellation.IsCancellationRequested) return;
            var information = await detail;
            // Modrinth v2 详情未返回的作者和更新时间沿用同一固定项目的搜索信息。
            ProjectDetails = information with
            {
                Author = string.IsNullOrWhiteSpace(information.Author) ? project.Author : information.Author,
                Updated = information.Updated == default ? project.Updated : information.Updated
            };
            _versionPage = 0;
            foreach (var version in (await versions).Items) Versions.Add(version);
            HasMoreVersions = (await versions).HasMore;
            DetailStatus = Versions.Count == 0 ? "没有找到符合筛选条件的版本。" : "选择版本后核实具体文件和依赖。";
            SelectedVersion = Versions.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (DetailCurrent(revision, context) && !cancellation.IsCancellationRequested)
            {
                DetailStatus = "读取详情失败：" + ex.Message;
                _notifications.Failure("模组详情读取失败", ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_detailCancellation, cancellation)) _detailCancellation = null;
            if (DetailCurrent(revision, context)) IsLoadingDetails = false;
        }
    }

    /// <summary>版本列表分页继续沿用当前项目与筛选，不用最新版本替代用户的精确选择。</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMoreVersions))]
    private async Task LoadMoreVersionsAsync(CancellationToken token)
    {
        if (!CanLoadMoreVersions() || SelectedProject is not { } selected) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); _detailCancellation = cancellation;
        var revision = ++_detailRevision; var context = _contextRevision; IsLoadingDetails = true;
        try
        {
            var result = await _catalog.GetVersionsAsync(selected.Project.Id, GameVersion.Trim(), Loader, _versionPage + 1, cancellation.Token);
            if (!DetailCurrent(revision, context) || cancellation.IsCancellationRequested) return;
            var known = Versions.Select(version => version.Id).ToHashSet();
            foreach (var version in result.Items.Where(version => known.Add(version.Id))) Versions.Add(version);
            ++_versionPage; HasMoreVersions = result.HasMore;
            _notifications.Success("模组版本列表已更新", $"已显示 {Versions.Count} 个版本。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (DetailCurrent(revision, context) && !cancellation.IsCancellationRequested)
            {
                DetailStatus = "读取更多版本失败：" + ex.Message;
                _notifications.Failure("模组版本列表读取失败", ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_detailCancellation, cancellation)) _detailCancellation = null;
            if (DetailCurrent(revision, context)) IsLoadingDetails = false;
        }
    }

    /// <summary>固定版本详情独立查询，切换版本使旧文件和依赖列表立即失效。</summary>
    private async Task LoadVersionAsync(OnlineModVersion version)
    {
        using var cancellation = new CancellationTokenSource(); _versionCancellation = cancellation;
        var revision = ++_versionRevision; var context = _contextRevision;
        IsLoadingVersion = true;
        try
        {
            var result = await _catalog.GetVersionAsync(version.Project, version.Id, cancellation.Token);
            if (revision != _versionRevision || context != _contextRevision || cancellation.IsCancellationRequested || _closing) return;
            VersionDetails = result;
            SelectedFile = result.Files.FirstOrDefault(file => file.Primary) ?? result.Files.FirstOrDefault();
            MergeOptional(result.Dependencies.Where(item => item.Kind == OnlineDependencyKind.Optional));
            DetailStatus = $"{result.VersionNumber} · {result.Channel} · {result.Published:yyyy-MM-dd}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (revision == _versionRevision && context == _contextRevision && !_closing && !cancellation.IsCancellationRequested)
            {
                DetailStatus = "版本读取失败：" + ex.Message;
                _notifications.Failure("模组版本读取失败", ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_versionCancellation, cancellation)) _versionCancellation = null;
            if (revision == _versionRevision && context == _contextRevision) IsLoadingVersion = false;
        }
    }

    /// <summary>必要依赖开关和可选项选择只影响预览；这里不下载或改写实例。</summary>
    [RelayCommand(CanExecute = nameof(CanPlan), IncludeCancelCommand = true)]
    private async Task PreviewAsync(CancellationToken token)
    {
        if (!CanPlan()) return;
        InvalidatePlan();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); _planCancellation = cancellation;
        var revision = _planRevision; var context = _contextRevision;
        IsPlanning = true; InstallStatus = "正在解析依赖和下载来源……";
        try
        {
            var request = new OnlineModPlanRequest
            {
                InstancePath = _instancePath!, Project = VersionDetails!.Project, VersionId = VersionDetails.Id,
                FileName = SelectedFile!.FileName, IncludeRequired = IncludeRequired,
                OptionalDependencyKeys = OptionalDependencies.Where(item => item.IsSelected).Select(item => item.Dependency.Key).ToArray()
            };
            var result = await _installer.PlanAsync(request, cancellation.Token);
            if (revision != _planRevision || context != _contextRevision || cancellation.IsCancellationRequested || _closing) return;
            ReplaceOptional(result.OptionalDependencies);
            Plan = result;
            InstallStatus = result.CanInstall ? "请核对清单；点击“确认下载并安装”后才会修改实例。" : "安装清单中有待处理项目，请查看具体说明。";
            var details = InstallStatus + "\n" + PlanSummary + "\n" + PlanDetails;
            if (!result.CanInstall || result.Warnings.Count > 0)
                _notifications.Warning("模组安装清单需要确认", details, _instancePath);
            else _notifications.Success("模组安装清单已生成", details, _instancePath);
        }
        catch (OperationCanceledException)
        {
            if (revision == _planRevision && context == _contextRevision && !_closing)
            {
                InstallStatus = "已取消生成清单。";
                _notifications.Cancelled("模组安装清单已取消", InstallStatus, _instancePath);
            }
        }
        catch (Exception ex)
        {
            if (revision == _planRevision && context == _contextRevision && !_closing && !cancellation.IsCancellationRequested)
            {
                InstallStatus = "清单生成失败：" + ex.Message;
                _notifications.Failure("模组安装清单生成失败", ex, _instancePath);
            }
        }
        finally
        {
            if (ReferenceEquals(_planCancellation, cancellation)) _planCancellation = null;
            if (revision == _planRevision) IsPlanning = false;
        }
    }

    /// <summary>仅安装当前已确认的计划；进度、取消与部分成功均独立于实例启动就绪状态。</summary>
    [RelayCommand(CanExecute = nameof(CanInstall), IncludeCancelCommand = true)]
    private async Task InstallAsync(CancellationToken token)
    {
        if (!CanInstall() || Plan is not { } selected) return;
        IsInstalling = true; _setBusy(true); _filesChanged();
        var context = _contextRevision;
        var revision = ++_installRevision;
        var paths = new[] { selected.Request.InstancePath };
        InstallPercent = 0; ProgressText = "准备下载……";
        try
        {
            // 需求可能只有平台编号而尚无路径；路径格式错误也必须进入统一失败处理和忙碌状态清理。
            paths = paths.Concat(selected.Items.Select(item => item.File.Requirement.Path).OfType<string>()
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => System.IO.Path.Combine(selected.Request.InstancePath,
                    path.Replace('/', System.IO.Path.DirectorySeparatorChar)))).ToArray();
            var progress = new Progress<OnlineModInstallProgress>(value =>
            {
                if (!IsInstalling || context != _contextRevision || revision != _installRevision || _closing) return;
                var fraction = value.TotalBytes is > 0 ? Math.Clamp(value.ReceivedBytes / (double)value.TotalBytes.Value, 0, 1) : 0;
                InstallPercent = value.TotalFiles > 0 ? Math.Clamp((value.CompletedFiles + fraction) * 100d / value.TotalFiles, 0, 100) : 0;
                ProgressText = $"{value.Stage} · {value.FileName}（{value.CompletedFiles}/{value.TotalFiles}）";
            });
            var result = await _installer.InstallAsync(selected, progress, token);
            if (context != _contextRevision || _closing) return;
            InstallStatus = (result.Cancelled ? "已取消。" : result.Succeeded ? "所选文件安装完成。" : "安装未完成。") +
                $"已安装 {result.Installed} 个，跳过 {result.Skipped} 个。\n" + string.Join("\n", result.Issues) +
                "\n请在本地模组页查看；此结果不代表整合包兼容性或启动检查已通过。";
            if (result.Succeeded) InstallPercent = 100;
            if (result.Cancelled) _notifications.Cancelled("模组安装已取消", InstallStatus, paths);
            else if (result.Succeeded) _notifications.Success("模组安装完成", InstallStatus, paths);
            else if (result.Installed > 0) _notifications.Warning("模组安装部分完成", InstallStatus, paths);
            else _notifications.Failure("模组安装未完成", InstallStatus, paths);
        }
        catch (OperationCanceledException)
        {
            if (context == _contextRevision && !_closing)
            {
                InstallStatus = "已取消安装；已提交文件会保留，请刷新本地模组列表。";
                _notifications.Cancelled("模组安装已取消", InstallStatus, paths);
            }
        }
        catch (Exception ex)
        {
            if (context == _contextRevision && !_closing)
            {
                InstallStatus = "安装未完成：" + ex.Message;
                _notifications.Failure("模组安装未完成", ex, paths);
            }
        }
        finally
        {
            ++_installRevision;
            Plan = null; _filesChanged(); IsInstalling = false; _setBusy(false);
        }
    }

    /// <summary>用户主动打开平台项目页；只接受平台本身的 HTTPS 网页。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private void OpenProject()
    {
        if (!CanOpenProject() || !SafeWebsite(ProjectDetails?.WebsiteUrl, out var uri)) return;
        try { _openWebsite!(uri!); }
        catch (Exception ex)
        {
            DetailStatus = "无法打开项目网页：" + ex.Message;
            _notifications.Failure("无法打开模组网页", ex);
        }
    }

    /// <summary>更改依赖选择后必须重新预览，不能使用原清单进行安装。</summary>
    private void InvalidatePlan()
    {
        ++_planRevision; _planCancellation?.Cancel(); Plan = null; IsPlanning = false;
        if (!IsInstalling) InstallStatus = "选择已变化，请生成安装清单。";
        NotifyCommands();
    }

    /// <summary>合并递归发现的可选项，保留用户已选择项，新增项默认关闭。</summary>
    private void MergeOptional(IEnumerable<OnlineModDependency> dependencies)
    {
        var known = OptionalDependencies.Select(item => item.Dependency.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var dependency in dependencies.Where(item => known.Add(item.Key)))
            OptionalDependencies.Add(new(dependency, InvalidatePlan, localization: _localization));
    }

    /// <summary>重新预览后移除已不在依赖链中的选项，保留仍然有效的用户勾选。</summary>
    private void ReplaceOptional(IEnumerable<OnlineModDependency> dependencies)
    {
        var selected = OptionalDependencies.Where(item => item.IsSelected).Select(item => item.Dependency.Key).ToHashSet(StringComparer.Ordinal);
        var current = (VersionDetails?.Dependencies ?? []).Where(item => item.Kind == OnlineDependencyKind.Optional)
            .Concat(dependencies).DistinctBy(item => item.Key).ToArray();
        OptionalDependencies.Clear();
        foreach (var dependency in current) OptionalDependencies.Add(new(dependency, InvalidatePlan, selected.Contains(dependency.Key), _localization));
    }

    /// <summary>项目或版本切换时移除旧依赖选项。</summary>
    private void ClearOptional() => OptionalDependencies.Clear();

    /// <summary>清除详情也终止旧项目的版本与计划查询。</summary>
    private void ResetDetails()
    {
        ++_detailRevision; _detailCancellation?.Cancel(); ++_versionRevision; _versionCancellation?.Cancel();
        IsLoadingDetails = false; IsLoadingVersion = false;
        ProjectDetails = null; Versions.Clear(); SelectedVersion = null; VersionDetails = null; SelectedFile = null;
        HasMoreVersions = false; ClearOptional(); InvalidatePlan();
        DetailStatus = "选择项目可查看介绍和兼容版本。";
    }

    /// <summary>取消所有只读请求并递增代次，非协作式服务的迟到响应也会被丢弃。</summary>
    private void CancelReads()
    {
        ++_searchRevision; ++_detailRevision; ++_versionRevision; ++_planRevision;
        _searchCancellation?.Cancel(); _detailCancellation?.Cancel(); _versionCancellation?.Cancel(); _planCancellation?.Cancel();
        IsSearching = false; IsLoadingDetails = false; IsLoadingVersion = false; IsPlanning = false;
    }

    /// <summary>保存未完成的自动任务，关闭窗口时一起等待而不遗留后台更新。</summary>
    private Task Track(Task task)
    {
        _pending.RemoveWhere(item => item.IsCompleted); _pending.Add(task); return task;
    }

    /// <summary>制作不可变查询快照，让输入变化不影响正在运行的这一页。</summary>
    private OnlineModQuery Query(int page) => new()
    {
        Text = OnlineModDisplayText.SearchTerm(SearchText, _localization), Provider = ProviderIndex switch { 1 => ModProvider.Modrinth, 2 => ModProvider.CurseForge, _ => null },
        GameVersion = GameVersion.Trim(), Loader = Loader, Category = CategoryKeys[Math.Clamp(CategoryIndex, 0, CategoryKeys.Length - 1)],
        Environment = EnvironmentKeys[Math.Clamp(EnvironmentIndex, 0, EnvironmentKeys.Length - 1)],
        Sort = (OnlineModSort)Math.Clamp(SortIndex, 0, 3), Page = page
    };
    /// <summary>查询不依赖宿主的实例写入许可，但安装期间保持界面选择稳定。</summary>
    private bool CanSearch() => !_closing && !IsInstalling && _instancePath is not null;
    /// <summary>只有当前查询还有下一页且未在读取时才允许继续分页。</summary>
    private bool CanLoadMore() => CanSearch() && !IsSearching && HasMore;
    /// <summary>版本分页需要保持一个有效项目选择。</summary>
    private bool CanLoadMoreVersions() => CanSearch() && !IsLoadingDetails && HasMoreVersions && SelectedProject is not null;
    /// <summary>只有完整版本文件已经读到、宿主空闲时才生成计划。</summary>
    private bool CanPlan() => CanEdit && !IsPlanning && !IsLoadingVersion && _instancePath is not null && VersionDetails is not null && SelectedFile is not null;
    /// <summary>计划必须属于当前实例，且不能包含阻断条目。</summary>
    private bool CanInstall() => CanEdit && !IsPlanning && Plan is { CanInstall: true } && string.Equals(Plan.Request.InstancePath, _instancePath, StringComparison.OrdinalIgnoreCase);
    /// <summary>只有存在受支持的项目主页且宿主提供浏览器入口时才启用按钮。</summary>
    private bool CanOpenProject() => _openWebsite is not null && SafeWebsite(ProjectDetails?.WebsiteUrl, out _);
    /// <summary>检查搜索是否仍属于当前实例和最新查询。</summary>
    private bool SearchCurrent(int revision, int context) => !_closing && revision == _searchRevision && context == _contextRevision;
    /// <summary>检查项目介绍是否仍属于最新的选择。</summary>
    private bool DetailCurrent(int revision, int context) => !_closing && revision == _detailRevision && context == _contextRevision;
    /// <summary>集中刷新命令许可，避免异步任务完成后按钮停留在旧状态。</summary>
    private void NotifyCommands()
    {
        SearchCommand.NotifyCanExecuteChanged(); LoadMoreCommand.NotifyCanExecuteChanged();
        LoadMoreVersionsCommand.NotifyCanExecuteChanged(); PreviewCommand.NotifyCanExecuteChanged(); InstallCommand.NotifyCanExecuteChanged();
    }
    /// <summary>扫描描述仅用于搜索默认值，真正安装仍由服务核对实例清单。</summary>
    private static string DetectLoader(string? description)
    {
        foreach (var candidate in new[] { "neoforge", "fabric", "quilt", "liteloader", "forge" })
            if (description?.Contains(candidate, StringComparison.OrdinalIgnoreCase) == true) return candidate;
        return "";
    }
    /// <summary>保留平台声明的不兼容信息，但不声称首版完成了冲突分析。</summary>
    private static string DependencyLabel(OnlineDependencyKind kind) => kind switch
    { OnlineDependencyKind.Required => "必要依赖", OnlineDependencyKind.Incompatible => "声明不兼容（本阶段不做完整冲突分析）", _ => "内嵌依赖" };
    /// <summary>把计划内部状态转换为可读的逐文件提示。</summary>
    private static string PlanLabel(OnlineModPlanStatus state) => state switch
    { OnlineModPlanStatus.Download => "待下载", OnlineModPlanStatus.AlreadyInstalled => "已安装", _ => "待处理" };
    /// <summary>预览区显示实际采用的文件来源，使同内容备用下载对用户可见。</summary>
    private static string DownloadOriginLabel(InstanceFileRequirement file) => file.Origin switch
    {
        InstanceFileOrigin.ModrinthDownload when file.CurseForgeOrigin is not null => "Modrinth（与 CurseForge 原文件哈希一致）",
        InstanceFileOrigin.ModrinthDownload => "Modrinth",
        InstanceFileOrigin.CurseForgeDownload => "CurseForge",
        _ => file.Source.ToString()
    };
    /// <summary>平台链接不携带凭证，也不能由远程内容指定本机程序或其他 URI 协议。</summary>
    private static bool SafeWebsite(string? address, out Uri? uri) => Uri.TryCreate(address, UriKind.Absolute, out uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0 &&
        uri.Host is "modrinth.com" or "www.modrinth.com" or "curseforge.com" or "www.curseforge.com";
}

/// <summary>每个搜索条目单独更新图标，不因为远程图像到达重建整个列表。</summary>
public partial class OnlineModResultRow(OnlineModProject project, IOnlineModLocalization? localization = null) : ObservableObject
{
    public OnlineModProject Project { get; } = project;
    [ObservableProperty] private string? iconPath;
    public string DisplayName => OnlineModDisplayText.Name(Project.Id, Project.Name, localization);
    public string DisplaySummary => OnlineModDisplayText.Summary(Project.Id, Project.Summary, localization);
    public string SummaryNote => OnlineModDisplayText.SummaryNote(Project.Id, localization);
    public string Placeholder => string.IsNullOrWhiteSpace(Project.Name) ? "M" : Project.Name[..1].ToUpperInvariant();
    public string Caption => $"{Project.Id.Provider} · {Project.Author} · {Project.Downloads:N0} 次下载";
}

/// <summary>可选依赖默认关闭，任何勾选变化都会使已生成计划失效。</summary>
public partial class OptionalModChoice(OnlineModDependency dependency, Action changed, bool selected = false,
    IOnlineModLocalization? localization = null) : ObservableObject
{
    public OnlineModDependency Dependency { get; } = dependency;
    public string DisplayName => OnlineModDisplayText.Name(Dependency.Project, Dependency.Name, localization);
    public string DisplaySummary => OnlineModDisplayText.Summary(Dependency.Project, Dependency.Description, localization);
    public string SummaryNote => OnlineModDisplayText.SummaryNote(Dependency.Project, localization);
    [ObservableProperty] private bool isSelected = selected;
    partial void OnIsSelectedChanged(bool value) => changed();
}
