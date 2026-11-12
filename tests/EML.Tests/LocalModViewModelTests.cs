using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods;
using EML.Infrastructure.Minecraft;

namespace EML.Tests;

public sealed partial class LocalModManagementTests
{
    /// <summary>后台读列表不弹成功框，用户主动刷新只报告一次明确结果。</summary>
    [Fact]
    public async Task ModRefreshNotifiesOnlyExplicitSuccess()
    {
        var instance = await CreateAsync(); var notices = new ModNoticeRecorder();
        var vm = CreateModViewModel(instance, new ModManagerStub(), new ModDialogs(), notices);
        vm.SelectedDetailTab = 6; await vm.ModListLoadTask;
        Assert.Empty(notices.Items);
        await vm.RefreshModsCommand.ExecuteAsync(null);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(OperationNoticeKind.Success, notice.Kind);
        Assert.Contains(instance.Game, notice.Paths);
        await vm.StopModListLoadingAsync();
    }

    /// <summary>批量添加中的失败与成功合并成一次警告，失败源文件的完整路径可复制。</summary>
    [Fact]
    public async Task PartialModAddReportsOneWarningWithSourcePaths()
    {
        var instance = await CreateAsync(); var notices = new ModNoticeRecorder();
        var good = Path.Combine(_root, "中文模组.jar"); var bad = Path.Combine(_root, "中文失败.jar");
        var manager = new ModManagerStub
        {
            Add = path => path == bad ? Task.FromException(new IOException("文件被其他程序占用")) : Task.CompletedTask
        };
        var vm = CreateModViewModel(instance, manager, new ModDialogs { Files = [good, bad] }, notices);
        await vm.AddLocalModsCommand.ExecuteAsync(null);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(OperationNoticeKind.Warning, notice.Kind);
        Assert.Contains("1 / 2", notice.Message);
        Assert.Contains("文件被其他程序占用", notice.Message);
        Assert.Contains(bad, notice.Paths);
        Assert.Equal(2, manager.Writes);
        await vm.StopModListLoadingAsync();
    }

    /// <summary>写入成功但刷新失败仍为一次部分完成警告，不能提示全部失败或全部成功。</summary>
    [Fact]
    public async Task SuccessfulModWriteWithFailedRefreshReportsOneWarning()
    {
        var instance = await CreateAsync(); var notices = new ModNoticeRecorder();
        var manager = new ModManagerStub { List = _ => Task.FromException<LocalModCatalog>(new IOException("列表读取权限不足")) };
        var vm = CreateModViewModel(instance, manager, new ModDialogs { Files = ["custom.jar"] }, notices);
        await vm.AddLocalModsCommand.ExecuteAsync(null);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(OperationNoticeKind.Warning, notice.Kind);
        Assert.Contains("已添加 1 / 1", notice.Message);
        Assert.Contains("列表读取权限不足", notice.Message);
        await vm.StopModListLoadingAsync();
    }

    /// <summary>全部添加失败和用户取消使用不同结果类型，均提供可复制的目标目录。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledModAddUsesAccurateNotice(bool cancelled)
    {
        var instance = await CreateAsync(); var notices = new ModNoticeRecorder();
        var manager = new ModManagerStub
        {
            Add = _ => Task.FromException(cancelled ? new OperationCanceledException() : new IOException("不是有效模组文件"))
        };
        var vm = CreateModViewModel(instance, manager, new ModDialogs { Files = ["custom.jar"] }, notices);
        await vm.AddLocalModsCommand.ExecuteAsync(null);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(cancelled ? OperationNoticeKind.Cancelled : OperationNoticeKind.Failure, notice.Kind);
        Assert.Contains(instance.Game, notice.Paths);
        if (!cancelled) Assert.Contains("不是有效模组文件", notice.Message);
        await vm.StopModListLoadingAsync();
    }

    /// <summary>选中实例不会提前扫描模组；打开管理页才读取，并可按模组 ID 搜索。</summary>
    [Fact]
    public async Task ModPageLoadsOnDemandAndFiltersByIdentity()
    {
        var instance = await CreateAsync();
        var manager = new ModManagerStub
        {
            Catalog = new([UiMod("first.jar", "first-id"), UiMod("second.jar", "second-id")], [])
        };
        var vm = CreateModViewModel(instance, manager, new ModDialogs());
        Assert.Equal(0, manager.Reads);
        vm.SelectedDetailTab = 6;
        await vm.ModListLoadTask;
        Assert.Equal(1, manager.Reads); Assert.Equal(2, vm.ModFiles.Count);
        vm.ModSearchText = "second-id";
        Assert.Equal("second.jar", Assert.Single(vm.ModFiles).FileName);
        Assert.Equal(1, manager.Reads); // 筛选仅使用内存，不重新读取全部模组文件。
        await vm.StopModListLoadingAsync();
    }

    /// <summary>取消添加风险确认不调用服务，也不改变当前实例内容。</summary>
    [Fact]
    public async Task CancelledAddConfirmationDoesNotCallManager()
    {
        var instance = await CreateAsync();
        var manager = new ModManagerStub();
        var vm = CreateModViewModel(instance, manager, new ModDialogs { Files = ["custom.jar"], Accept = false });
        await vm.AddLocalModsCommand.ExecuteAsync(null);
        Assert.Equal(0, manager.Writes); Assert.False(vm.IsBusy);
        await vm.StopModListLoadingAsync();
    }

    /// <summary>执行其他实例任务期间即使命令被程序调用，也不能进入模组写入服务。</summary>
    [Fact]
    public async Task BusyViewModelRejectsModMutation()
    {
        var instance = await CreateAsync(); var manager = new ModManagerStub();
        var vm = CreateModViewModel(instance, manager, new ModDialogs { Files = ["custom.jar"] });
        vm.IsBusy = true;
        Assert.False(vm.AddLocalModsCommand.CanExecute(null));
        await vm.AddLocalModsCommand.ExecuteAsync(null);
        Assert.Equal(0, manager.Writes);
        vm.IsBusy = false;
        await vm.StopModListLoadingAsync();
    }

    /// <summary>模组变更后旧检查快照必须清空，不能继续展示之前的文件准备结论。</summary>
    [Fact]
    public async Task SuccessfulModChangeInvalidatesVisibleCheckResults()
    {
        var instance = await CreateAsync(); var mod = UiMod("custom.jar", "custom");
        var manager = new ModManagerStub { Catalog = new([mod], []) };
        var vm = CreateModViewModel(instance, manager, new ModDialogs());
        await vm.RefreshModsCommand.ExecuteAsync(null);
        vm.SelectedMod = Assert.Single(vm.ModFiles);
        vm.CheckFiles = [new(new() { Path = ".minecraft/mods/custom.jar" }, InstanceFileState.Verified, "old")];
        await vm.DisableModCommand.ExecuteAsync(null);
        Assert.Equal(1, manager.Writes); Assert.Empty(vm.CheckFiles); Assert.False(vm.IsBusy);
        await vm.StopModListLoadingAsync();
    }

    /// <summary>切换实例后迟到的旧扫描不能覆盖新实例的模组列表。</summary>
    [Fact]
    public async Task LateOldCatalogCannotReplaceNewSelection()
    {
        var old = await CreateAsync("Old"); var current = await CreateAsync("New");
        var oldResult = new TaskCompletionSource<LocalModCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new ModManagerStub
        {
            List = path =>
            {
                if (path == old.Path) { oldStarted.SetResult(); return oldResult.Task; }
                return Task.FromResult(new LocalModCatalog([UiMod("new.jar", "new")], []));
            }
        };
        var vm = CreateModViewModel(old, manager, new ModDialogs());
        vm.SelectedDetailTab = 6;
        var previous = vm.ModListLoadTask;
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.SelectedInstance = new("New", current.Path, current.Game, ["1.7.10"]);
        oldResult.SetResult(new([UiMod("old.jar", "old")], []));
        await previous.WaitAsync(TimeSpan.FromSeconds(10));
        await vm.ModListLoadTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(vm.ModFiles, file => file.FileName == "old.jar");
        Assert.Equal(current.Path, vm.SelectedInstance.InstancePath);
        await vm.StopModListLoadingAsync();
    }

    /// <summary>只构造供界面展示的一个玩家模组，不访问任何本机文件。</summary>
    private static LocalModEntry UiMod(string filename, string id) => new()
    {
        Path = ".minecraft/mods/" + filename, CurrentPath = ".minecraft/mods/" + filename,
        FileName = filename, Name = filename, Identities = [new(id, filename, "1")],
        IsPresent = true, CanManage = true, State = LocalModState.Enabled
    };

    /// <summary>生产界面模型配合受控模组服务；其他服务只在被调用时才访问临时实例。</summary>
    private InstancesViewModel CreateModViewModel(Instance instance, ILocalModManager manager, ModDialogs dialogs,
        IOperationNotifications? notifications = null)
    {
        var locator = new LauncherRootMinecraftInstanceLocator(_root); var checker = new MinecraftInstanceChecker(_root);
        var vm = new InstancesViewModel(locator, new MinecraftInstanceManager(_root), new MinecraftInstanceImporter(_root),
            dialogs, locator.InstancesRoot, checker, new MinecraftInstanceDownloader(_root, checker, new NoDownload()),
            localMods: manager, modDialogs: dialogs, notifications: notifications);
        vm.SelectedInstance = new(instance.Path, instance.Path, instance.Game, ["1.7.10"]);
        return vm;
    }

    /// <summary>记录读写次数，可让旧读取延迟完成以验证异步界面生命周期。</summary>
    private sealed class ModManagerStub : ILocalModManager
    {
        public LocalModCatalog Catalog { get; init; } = new([], []);
        public Func<string, Task<LocalModCatalog>>? List { get; init; }
        public Func<string, Task>? Add { get; init; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        /// <summary>允许故意忽略取消的读取，以验证界面自身的过期结果保护。</summary>
        public Task<LocalModCatalog> ListAsync(string instancePath, CancellationToken token = default)
        { Reads++; return List?.Invoke(instancePath) ?? Task.FromResult(Catalog); }
        /// <summary>仅记录显式添加请求。</summary>
        public Task AddAsync(string instancePath, string sourcePath, CancellationToken token = default)
        { Writes++; return Add?.Invoke(sourcePath) ?? Task.CompletedTask; }
        /// <summary>仅记录明确接管请求。</summary>
        public Task AdoptAsync(string instancePath, string path, CancellationToken token = default)
        { Writes++; return Task.CompletedTask; }
        /// <summary>仅记录加载状态改变。</summary>
        public Task SetEnabledAsync(string instancePath, string path, bool enabled, CancellationToken token = default)
        { Writes++; return Task.CompletedTask; }
        /// <summary>仅记录回收请求。</summary>
        public Task RemoveAsync(string instancePath, string path, CancellationToken token = default)
        { Writes++; return Task.CompletedTask; }
        /// <summary>仅记录恢复请求。</summary>
        public Task RestoreAsync(string instancePath, string path, CancellationToken token = default)
        { Writes++; return Task.CompletedTask; }
        /// <summary>仅记录事务恢复请求。</summary>
        public Task RecoverAsync(string instancePath, CancellationToken token = default)
        { Writes++; return Task.CompletedTask; }
    }

    /// <summary>记录界面结果，不创建弹窗或访问系统剪贴板。</summary>
    private sealed class ModNoticeRecorder : IOperationNotifications
    {
        public List<OperationNotice> Items { get; } = [];
        /// <summary>保存一次完整操作的通知，供测试验证数量、级别和路径。</summary>
        public void Report(OperationNotice notice) => Items.Add(notice);
    }

    /// <summary>替代文件窗口与风险确认，不启动 WPF 或外部应用。</summary>
    private sealed class ModDialogs : IInstanceDialogs, IModManagementDialogs
    {
        public IReadOnlyList<string> Files { get; init; } = [];
        public bool Accept { get; init; } = true;
        /// <summary>返回固定的选择结果。</summary>
        public IReadOnlyList<string> ChooseModFiles() => Files;
        /// <summary>返回固定风险确认结果。</summary>
        public bool ConfirmAdd(MinecraftInstance instance, IReadOnlyList<string> paths) => Accept;
        /// <summary>返回固定接管确认结果。</summary>
        public bool ConfirmAdopt(MinecraftInstance instance, LocalModEntry mod) => Accept;
        /// <summary>返回固定回收确认结果。</summary>
        public bool ConfirmRemove(MinecraftInstance instance, LocalModEntry mod) => Accept;
        /// <summary>测试不打开真实系统目录。</summary>
        public void OpenModsFolder(MinecraftInstance instance) { }
        /// <summary>此测试不使用实例删除。</summary>
        public bool ConfirmDelete(MinecraftInstance instance) => throw new NotSupportedException();
        /// <summary>此测试不使用导出对话框。</summary>
        public string? ChooseExportPath(MinecraftInstance instance) => throw new NotSupportedException();
        /// <summary>此测试不使用导入对话框。</summary>
        public string? ChooseImportPath() => throw new NotSupportedException();
        /// <summary>此测试不使用导入确认。</summary>
        public string? ConfirmImport(InstanceImportPreview preview) => throw new NotSupportedException();
    }
}
