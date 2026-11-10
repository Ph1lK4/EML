using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Mods;
using EML.Infrastructure.Minecraft;

namespace EML.Tests;

public sealed partial class LocalModManagementTests
{
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
    private InstancesViewModel CreateModViewModel(Instance instance, ILocalModManager manager, ModDialogs dialogs)
    {
        var locator = new LauncherRootMinecraftInstanceLocator(_root); var checker = new MinecraftInstanceChecker(_root);
        var vm = new InstancesViewModel(locator, new MinecraftInstanceManager(_root), new MinecraftInstanceImporter(_root),
            dialogs, locator.InstancesRoot, checker, new MinecraftInstanceDownloader(_root, checker, new NoDownload()),
            localMods: manager, modDialogs: dialogs);
        vm.SelectedInstance = new(instance.Path, instance.Path, instance.Game, ["1.7.10"]);
        return vm;
    }

    /// <summary>记录读写次数，可让旧读取延迟完成以验证异步界面生命周期。</summary>
    private sealed class ModManagerStub : ILocalModManager
    {
        public LocalModCatalog Catalog { get; init; } = new([], []);
        public Func<string, Task<LocalModCatalog>>? List { get; init; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        /// <summary>允许故意忽略取消的读取，以验证界面自身的过期结果保护。</summary>
        public Task<LocalModCatalog> ListAsync(string instancePath, CancellationToken token = default)
        { Reads++; return List?.Invoke(instancePath) ?? Task.FromResult(Catalog); }
        /// <summary>仅记录显式添加请求。</summary>
        public Task AddAsync(string instancePath, string sourcePath, CancellationToken token = default)
        { Writes++; return Task.CompletedTask; }
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
