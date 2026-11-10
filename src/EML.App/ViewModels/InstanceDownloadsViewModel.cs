using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
using EML.Core.Downloads;
using EML.Core.Minecraft.Catalog;

namespace EML.App.ViewModels;

/// <summary>新实例目录、精确版本和下载入口；网页查询与现有实例写入状态分开管理。</summary>
public sealed partial class InstanceDownloadsViewModel : ObservableObject
{
    private readonly IInstanceCatalog _catalog;
    private readonly Func<InstanceCatalogRelease, IProgress<FileDownloadProgress>, CancellationToken, Task<string>> _install;
    private readonly Func<bool> _canModify;
    private readonly IModIconCache? _icons;
    private readonly Action<Uri>? _openWebsite;
    private readonly HashSet<Task> _pending = [];
    private CancellationTokenSource? _searchCancellation, _versionCancellation, _detailsCancellation;
    private int _searchRevision, _versionRevision, _detailsRevision, _installRevision, _nextSearch, _nextVersion;
    private bool _active, _closing;

    /// <summary>接收可替换的查询与安装入口；构造时不联网、不创建实例。</summary>
    public InstanceDownloadsViewModel(IInstanceCatalog catalog,
        Func<InstanceCatalogRelease, IProgress<FileDownloadProgress>, CancellationToken, Task<string>> install,
        Func<bool> canModify, IModIconCache? icons = null, Action<Uri>? openWebsite = null)
    {
        _catalog = catalog; _install = install; _canModify = canModify; _icons = icons; _openWebsite = openWebsite;
    }

    [ObservableProperty] private int providerIndex;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string gameVersion = "";
    [ObservableProperty] private string loader = "";
    [ObservableProperty] private int sortIndex;
    [ObservableProperty] private bool includeSnapshots;
    [ObservableProperty] private bool isSearching;
    [ObservableProperty] private bool isLoadingVersions;
    [ObservableProperty] private bool isLoadingDetails;
    [ObservableProperty] private bool isInstalling;
    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private bool hasMoreVersions;
    [ObservableProperty] private string status = "选择来源后搜索整合包或原版版本。";
    [ObservableProperty] private string versionStatus = "选择左侧项目查看版本。";
    [ObservableProperty] private string descriptionStatus = "选择整合包查看平台介绍。";
    [ObservableProperty] private string installStatus = "下载后确认实例名称，随后自动安装缺失依赖。";
    [ObservableProperty] private string archiveProgress = "";
    [ObservableProperty] private double archivePercent;
    [ObservableProperty] private InstanceDownloadRow? selectedProject;
    [ObservableProperty] private InstanceCatalogRelease? selectedRelease;

    public ObservableCollection<InstanceDownloadRow> Projects { get; } = [];
    public ObservableCollection<InstanceCatalogRelease> Releases { get; } = [];
    public IReadOnlyList<string> Providers { get; } = ["Modrinth 整合包", "CurseForge 整合包", "Minecraft 官方原版"];
    public IReadOnlyList<string> Loaders { get; } = ["", "forge", "fabric", "neoforge", "quilt"];
    public IReadOnlyList<string> Sorts { get; } = ["相关度 / 热门", "下载量", "最近更新", "最新发布"];
    public bool IsMinecraft => ProviderIndex == 2;
    public bool IsModpack => !IsMinecraft;
    public bool CanBrowse => !_closing && !IsInstalling;
    public string ProjectInformation => SelectedProject is not { } row ? "" :
        row.DisplayName + "\n" + row.DisplaySummary + "\n" + row.Project.Author +
        (row.Project.Provider == InstanceCatalogProvider.Minecraft ? "" : $" · 下载 {row.Project.Downloads:N0} 次");
    public string ProjectDescription => SelectedProject?.Details?.Description ?? "";
    public bool HasProjectDescription => !string.IsNullOrWhiteSpace(ProjectDescription);
    public string ReleaseInformation => SelectedRelease is not { } release ? "请选择具体版本。" :
        $"{release.Name}\n游戏版本：{string.Join("、", release.GameVersions)}\n" +
        (release.Loaders.Count == 0 ? "" : $"加载器：{string.Join("、", release.Loaders)}\n") +
        (release.Published == default ? "" : $"发布：{release.Published:yyyy-MM-dd}\n") +
        (release.Provider == InstanceCatalogProvider.Minecraft ? "将从官方获取客户端、类库和资源。" :
            $"整合包文件：{release.FileName}\n原包大小：{release.Size / 1048576d:F1} MiB；额外依赖下载量另计。") +
        (release.Problem is null ? "" : "\n" + release.Problem);
    public Task SearchTask { get; private set; } = Task.CompletedTask;
    public Task VersionsTask { get; private set; } = Task.CompletedTask;
    public Task DetailsTask { get; private set; } = Task.CompletedTask;

    /// <summary>只在页面可见时查询目录；离开页面不会停止已经确认的安装。</summary>
    public void SetActive(bool active)
    {
        if (_closing) return;
        var entering = active && !_active;
        _active = active;
        NotifyCommands();
        if (!active) { CancelReads(); return; }
        if (!entering || IsInstalling) return;
        if (Projects.Count == 0) SearchTask = Track(SearchCoreAsync(false, CancellationToken.None));
        else if (SelectedProject is not null && Releases.Count == 0) QueueVersions();
        if (SelectedProject is { Details: null } && IsModpack) QueueDetails();
    }

    /// <summary>其他页面开始或结束实例任务时更新安装按钮，查询仍可独立进行。</summary>
    public void NotifyHostStateChanged() => NotifyCommands();

    /// <summary>关闭窗口时取消目录和安装，并等待所有已安排任务退出。</summary>
    public async Task StopAsync()
    {
        _closing = true; _active = false; CancelReads(); InstallCommand.Cancel(); NotifyCommands();
        await Task.WhenAll(_pending.Concat(new[] { InstallCommand.ExecutionTask }.OfType<Task>()).ToArray());
    }

    /// <summary>按当前筛选重新搜索，旧响应不能覆盖新来源或关键词。</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchAsync() => SearchTask = Track(SearchCoreAsync(false, CancellationToken.None));
    /// <summary>目录查询只在页面激活且未安装时允许执行。</summary>
    private bool CanSearch() => CanBrowse && _active;

    /// <summary>仅请求平台提供的下一页偏移，重复结果按固定项目身份去重。</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync() => SearchTask = Track(SearchCoreAsync(true, CancellationToken.None));
    /// <summary>上一页请求结束并且平台声明还有结果时才允许翻页。</summary>
    private bool CanLoadMore() => CanSearch() && HasMore && !IsSearching;

    /// <summary>读取固定项目的后续版本，不用 latestFiles 摘要代替版本列表。</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMoreVersions))]
    private Task LoadMoreVersionsAsync() => VersionsTask = Track(LoadVersionsAsync(true));
    /// <summary>只给当前项目继续加载版本，禁止同时重复请求下一页。</summary>
    private bool CanLoadMoreVersions() => CanBrowse && _active && SelectedProject is not null && HasMoreVersions && !IsLoadingVersions;

    /// <summary>按需重新读取作者介绍，失败后可独立重试，不重新查询或下载版本。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshDescription))]
    private Task RefreshDescriptionAsync() => DetailsTask = Track(LoadDetailsAsync());
    /// <summary>只对选中的整合包开放介绍刷新，正在安装或关闭时不发起新请求。</summary>
    private bool CanRefreshDescription() => CanBrowse && _active && IsModpack && SelectedProject is not null && !IsLoadingDetails;

    /// <summary>下载确认选择的版本；从开始到导入和补全结束禁止再次安装。</summary>
    [RelayCommand(CanExecute = nameof(CanInstall), IncludeCancelCommand = true)]
    private async Task InstallAsync(CancellationToken token)
    {
        if (!CanInstall()) return;
        var release = SelectedRelease!;
        IsInstalling = true;
        CancelReads();
        var revision = ++_installRevision;
        ArchivePercent = 0; ArchiveProgress = "正在核对固定版本与下载地址……";
        InstallStatus = "正在下载，随后会显示导入预览。";
        try
        {
            var progress = new Progress<FileDownloadProgress>(p =>
            {
                if (revision != _installRevision || !IsInstalling) return;
                ArchivePercent = p.Percent ?? 0;
                ArchiveProgress = $"原包下载：{p.BytesReceived / 1048576d:F1} MiB" +
                    (p.Percent is { } percent ? $" · {percent:F1}%" : "");
            });
            InstallStatus = await _install(release, progress, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { InstallStatus = "操作已取消；已导入的实例与已完成文件会保留。"; }
        catch (Exception ex) { InstallStatus = "安装未完成：" + ex.Message; }
        finally { ++_installRevision; IsInstalling = false; ArchiveProgress = "本次任务已结束，结果见下方。"; }
    }
    /// <summary>可信下载地址、可校验版本和宿主空闲状态必须同时具备。</summary>
    private bool CanInstall() => !_closing && _canModify() && !IsInstalling && SelectedRelease?.CanDownload == true;

    /// <summary>受限文件只能打开已校验的平台项目网页，不构造下载地址。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenWebsite))]
    private void OpenWebsite()
    {
        if (Website() is { } uri) _openWebsite?.Invoke(uri);
    }
    /// <summary>仅平台网页地址可供用户打开。</summary>
    private bool CanOpenWebsite() => !_closing && _openWebsite is not null && Website() is not null;
    /// <summary>校验链接协议和平台域名，远端文本不能成为任意系统操作。</summary>
    private Uri? Website()
    {
        var address = SelectedRelease?.WebsiteUrl ?? SelectedProject?.Project.WebsiteUrl;
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            uri.Host is "modrinth.com" or "www.modrinth.com" or "curseforge.com" or "www.curseforge.com" or "minecraft.net" or "www.minecraft.net"
            ? uri : null;
    }

    /// <summary>更换来源立即清除旧版本和筛选，防止把上一平台的下载目标沿用到新页面。</summary>
    partial void OnProviderIndexChanged(int value)
    {
        GameVersion = ""; Loader = ""; SearchText = "";
        OnPropertyChanged(nameof(IsMinecraft)); OnPropertyChanged(nameof(IsModpack));
        InvalidateSearch();
        if (_active && !IsInstalling) SearchTask = Track(SearchCoreAsync(false, CancellationToken.None));
    }
    /// <summary>关键词改变后清除旧结果，等待显式搜索。</summary>
    partial void OnSearchTextChanged(string value) => InvalidateSearch();
    /// <summary>版本筛选改变时清除旧安装选择。</summary>
    partial void OnGameVersionChanged(string value) => InvalidateSearch();
    /// <summary>加载器改变时取消旧查询结果。</summary>
    partial void OnLoaderChanged(string value) => InvalidateSearch();
    /// <summary>排序变化要求重新请求第一页。</summary>
    partial void OnSortIndexChanged(int value) => InvalidateSearch();
    /// <summary>正式版与快照范围改变后重新搜索。</summary>
    partial void OnIncludeSnapshotsChanged(bool value) => InvalidateSearch();
    /// <summary>项目切换清空版本列表，并为当前项目安排独立请求。</summary>
    partial void OnSelectedProjectChanged(InstanceDownloadRow? value)
    {
        _versionCancellation?.Cancel(); ++_versionRevision; Releases.Clear(); SelectedRelease = null; HasMoreVersions = false;
        _versionCancellation = null; IsLoadingVersions = false;
        _detailsCancellation?.Cancel(); ++_detailsRevision; _detailsCancellation = null; IsLoadingDetails = false;
        VersionStatus = "选择左侧项目查看版本。";
        DescriptionStatus = value?.Details is null ? "选择整合包查看平台介绍。" : DescriptionResultNote(value);
        NotifyProjectDetails();
        if (_active && value is not null && !IsInstalling)
        {
            QueueVersions();
            if (IsModpack && value.Details is null) QueueDetails();
        }
        NotifyCommands();
    }
    /// <summary>发布版本切换同步更新说明、安装权限和项目链接。</summary>
    partial void OnSelectedReleaseChanged(InstanceCatalogRelease? value)
    { OnPropertyChanged(nameof(ReleaseInformation)); NotifyCommands(); }
    /// <summary>查询状态改变后刷新翻页按钮。</summary>
    partial void OnIsSearchingChanged(bool value) => NotifyCommands();
    /// <summary>版本请求状态改变后刷新版本翻页按钮。</summary>
    partial void OnIsLoadingVersionsChanged(bool value) => NotifyCommands();
    /// <summary>介绍读取期间禁用重复刷新，版本与安装按钮仍按自身状态判断。</summary>
    partial void OnIsLoadingDetailsChanged(bool value) => NotifyCommands();
    /// <summary>安装期间锁定筛选和目标选择，取消入口独立保留。</summary>
    partial void OnIsInstallingChanged(bool value) { OnPropertyChanged(nameof(CanBrowse)); NotifyCommands(); }
    /// <summary>搜索分页标记更新时刷新继续加载权限。</summary>
    partial void OnHasMoreChanged(bool value) => NotifyCommands();
    /// <summary>版本分页标记更新时刷新继续加载权限。</summary>
    partial void OnHasMoreVersionsChanged(bool value) => NotifyCommands();

    /// <summary>输入更改后要求重新搜索，清除旧下载选择并取消迟到请求。</summary>
    private void InvalidateSearch()
    {
        if (IsInstalling) return;
        CancelReads(); Projects.Clear(); SelectedProject = null; HasMore = false;
        Status = "筛选已改变，点击搜索查看结果。";
    }

    /// <summary>查询一页，发布时检查请求代次；图标失败只保留占位图。</summary>
    private async Task SearchCoreAsync(bool append, CancellationToken token)
    {
        if (!CanSearch()) return;
        _searchCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _searchCancellation = cancellation;
        var revision = ++_searchRevision;
        IsSearching = true;
        if (!append) { Projects.Clear(); SelectedProject = null; HasMore = false; }
        Status = "正在查询目录……";
        try
        {
            var result = await _catalog.SearchAsync(new()
            {
                Provider = (InstanceCatalogProvider)ProviderIndex,
                Query = InstanceCatalogDisplayText.SearchTerm(SearchText),
                GameVersion = GameVersion.Trim(),
                Loader = Loader, Sort = SortIndex, IncludeSnapshots = IncludeSnapshots, Offset = append ? _nextSearch : 0
            }, cancellation.Token);
            if (revision != _searchRevision || cancellation.IsCancellationRequested) return;
            var rows = result.Items.Where(project => !Projects.Any(row => row.Project.Provider == project.Provider && row.Project.Id == project.Id))
                .Select(project => new InstanceDownloadRow(project)).ToArray();
            foreach (var row in rows) Projects.Add(row);
            _nextSearch = result.NextOffset; HasMore = result.HasMore;
            Status = Projects.Count == 0 ? "没有找到结果，请调整关键词或筛选。" : $"已显示 {Projects.Count} 项，选择项目查看具体版本。";
            if (_icons is not null) await Task.WhenAll(rows.Select(row => LoadIconAsync(row, revision, cancellation.Token)));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (revision == _searchRevision) Status = "查询失败：" + ex.Message; }
        finally { if (revision == _searchRevision) { IsSearching = false; _searchCancellation = null; } }
    }

    /// <summary>复用有大小与格式限制的图标缓存，关闭页面后不更新图标。</summary>
    private async Task LoadIconAsync(InstanceDownloadRow row, int revision, CancellationToken token)
    {
        try
        {
            var path = await _icons!.GetAsync(row.Project.IconUrl, token);
            if (!token.IsCancellationRequested && revision == _searchRevision) row.IconPath = path;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { /* 图标不可用不影响查询和安装。 */ }
    }

    /// <summary>登记自动版本查询，便于关闭窗口等待而不留下后台回调。</summary>
    private void QueueVersions() => VersionsTask = Track(LoadVersionsAsync(false));

    /// <summary>仅为选中项目安排介绍读取；列表分页不会批量预取正文。</summary>
    private void QueueDetails() => DetailsTask = Track(LoadDetailsAsync());

    /// <summary>独立加载平台正文并丢弃迟到响应；介绍不可用不会阻止版本安装。</summary>
    private async Task LoadDetailsAsync()
    {
        if (SelectedProject is not { } selected || !CanRefreshDescription()) return;
        _detailsCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); _detailsCancellation = cancellation;
        var revision = ++_detailsRevision; IsLoadingDetails = true;
        DescriptionStatus = "正在读取平台介绍……";
        try
        {
            var details = await _catalog.GetProjectAsync(selected.Project, cancellation.Token);
            if (revision != _detailsRevision || cancellation.IsCancellationRequested || SelectedProject != selected) return;
            selected.SetDetails(details);
            DescriptionStatus = DescriptionResultNote(selected);
            NotifyProjectDetails();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (revision == _detailsRevision)
                DescriptionStatus = "介绍加载失败，可点击刷新或查看项目网页。" + ex.Message;
        }
        finally { if (revision == _detailsRevision) { IsLoadingDetails = false; _detailsCancellation = null; } }
    }

    /// <summary>说明正文来自所选平台；没有正文时不虚构中文说明。</summary>
    private static string DescriptionResultNote(InstanceDownloadRow row) =>
        string.IsNullOrWhiteSpace(row.Details?.Description) ? "平台暂未提供完整介绍，可以查看项目网页。" :
        $"来自 {row.Project.Provider} 的项目介绍，按作者提供的语言显示。";

    /// <summary>详情载入或切换项目后，一并刷新名称、简介及正文的页面绑定。</summary>
    private void NotifyProjectDetails()
    {
        OnPropertyChanged(nameof(ProjectInformation));
        OnPropertyChanged(nameof(ProjectDescription));
        OnPropertyChanged(nameof(HasProjectDescription));
    }

    /// <summary>版本查询绑定项目和当前筛选，旧项目响应不能重新填充列表。</summary>
    private async Task LoadVersionsAsync(bool append)
    {
        if (SelectedProject is not { } selected || !CanBrowse || !_active) return;
        _versionCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); _versionCancellation = cancellation;
        var revision = ++_versionRevision; IsLoadingVersions = true;
        if (!append) { Releases.Clear(); SelectedRelease = null; HasMoreVersions = false; }
        VersionStatus = "正在读取发布版本……";
        try
        {
            var result = await _catalog.GetVersionsAsync(selected.Project, GameVersion.Trim(), Loader,
                append ? _nextVersion : 0, cancellationToken: cancellation.Token);
            if (revision != _versionRevision || cancellation.IsCancellationRequested) return;
            foreach (var release in result.Items)
                if (!Releases.Any(existing => existing.Id == release.Id)) Releases.Add(release);
            _nextVersion = result.NextOffset; HasMoreVersions = result.HasMore;
            VersionStatus = Releases.Count == 0 ? "此筛选下没有可用版本。" : $"已显示 {Releases.Count} 个版本，请选择需要安装的版本。";
            if (SelectedRelease is null && Releases.Count > 0) SelectedRelease = Releases[0];
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (revision == _versionRevision) VersionStatus = "读取版本失败：" + ex.Message; }
        finally { if (revision == _versionRevision) { IsLoadingVersions = false; _versionCancellation = null; } }
    }

    /// <summary>使所有目录响应失效；已确认的安装使用独立令牌。</summary>
    private void CancelReads()
    {
        ++_searchRevision; ++_versionRevision; ++_detailsRevision;
        _searchCancellation?.Cancel(); _versionCancellation?.Cancel(); _detailsCancellation?.Cancel();
        _searchCancellation = null; _versionCancellation = null; _detailsCancellation = null;
        if (IsLoadingDetails) DescriptionStatus = "介绍读取已暂停，可返回页面或点击刷新重试。";
        IsLoadingDetails = false;
        IsSearching = false; IsLoadingVersions = false;
    }

    /// <summary>跟踪已启动的异步任务，完成后释放引用。</summary>
    private Task Track(Task task)
    {
        _pending.Add(task);
        _ = RemoveWhenFinishedAsync(task);
        return task;
    }
    /// <summary>完成任务从关闭等待集合移除，避免长期持有查询结果。</summary>
    private async Task RemoveWhenFinishedAsync(Task task)
    {
        try { await task; }
        finally { _pending.Remove(task); }
    }

    /// <summary>集中刷新按钮执行条件，兼顾请求状态和宿主实例锁。</summary>
    private void NotifyCommands()
    {
        SearchCommand.NotifyCanExecuteChanged(); LoadMoreCommand.NotifyCanExecuteChanged();
        LoadMoreVersionsCommand.NotifyCanExecuteChanged(); InstallCommand.NotifyCanExecuteChanged(); OpenWebsiteCommand.NotifyCanExecuteChanged();
        RefreshDescriptionCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>搜索卡片的图标异步载入，不修改平台返回的固定项目身份。</summary>
public sealed partial class InstanceDownloadRow(InstanceCatalogProject project) : ObservableObject
{
    public InstanceCatalogProject Project { get; } = project;
    public InstanceCatalogProject? Details { get; private set; }
    public string DisplayName => InstanceCatalogDisplayText.Name(Details ?? Project);
    public string DisplaySummary => InstanceCatalogDisplayText.Summary(Details ?? Project);
    [ObservableProperty] private string? iconPath;

    /// <summary>只接收相同平台和项目的详情，保留搜索对象供版本与安装流程使用。</summary>
    public void SetDetails(InstanceCatalogProject details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (details.Provider != Project.Provider || details.Id != Project.Id)
            throw new InvalidDataException("平台返回了其他整合包的介绍。");
        Details = details;
        OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(DisplaySummary));
    }
}
