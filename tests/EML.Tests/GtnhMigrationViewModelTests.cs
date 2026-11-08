using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Migration;
using EML.Infrastructure.Minecraft;

namespace EML.Tests;

public sealed partial class GtnhMigrationTests
{
    /// <summary>界面选择新版后识别资格，执行真实迁移并显示日志位置及恢复入口。</summary>
    [Fact]
    public async Task ViewModelMigratesToSelectedTargetAndEnablesRestore()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "servers.dat", "old");
        var dialogs = new MigrationDialogs();
        var vm = CreateViewModel(old, target, dialogs);
        await vm.MigrationStateLoadTask;
        Assert.True(vm.MigratePlayerDataCommand.CanExecute(null));
        Assert.False(vm.RestoreMigrationCommand.CanExecute(null));
        await vm.MigratePlayerDataCommand.ExecuteAsync(null);
        Assert.Equal("old", Read(target.Game, "servers.dat"));
        Assert.False(vm.IsBusy); Assert.True(vm.RestoreMigrationCommand.CanExecute(null));
        Assert.Contains("迁移完成", vm.MigrationStatus); Assert.True(File.Exists(vm.MigrationJournalPath));
        Assert.Equal(target.Path, dialogs.Target?.InstancePath);
        await vm.RestoreMigrationCommand.ExecuteAsync(null);
        Assert.False(File.Exists(Path.Combine(target.Game, "servers.dat")));
        Assert.False(vm.RestoreMigrationCommand.CanExecute(null));
    }

    /// <summary>用户在最终预览取消时不建立事务，也不修改目标。</summary>
    [Fact]
    public async Task ViewModelFinalPreviewCancelDoesNotWriteFiles()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new"); Write(old.Game, "servers.dat", "old");
        var vm = CreateViewModel(old, target, new MigrationDialogs { Confirmation = null });
        await vm.MigrationStateLoadTask;
        await vm.MigratePlayerDataCommand.ExecuteAsync(null);
        Assert.False(File.Exists(Path.Combine(target.Game, "servers.dat")));
        Assert.False(Directory.Exists(Path.Combine(target.Path, ".eml-runtime")));
        Assert.False(vm.IsBusy); Assert.Contains("取消", vm.MigrationStatus);
    }

    /// <summary>未识别包不开放迁移，忙碌期间所有迁移入口都禁用。</summary>
    [Fact]
    public async Task ViewModelBlocksUnknownPackAndBusyOperations()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        var vm = CreateViewModel(old, target, new MigrationDialogs());
        await vm.MigrationStateLoadTask;
        vm.IsBusy = true; Assert.False(vm.MigratePlayerDataCommand.CanExecute(null));
        await vm.MigratePlayerDataCommand.ExecuteAsync(null); // 方法本身也有许可判断。
        vm.IsBusy = false;
        File.Delete(Path.Combine(target.Game, "config/txloader/load/mainmenu/version.txt"));
        vm.SelectedInstance = null; vm.SelectedInstance = vm.Instances[1];
        await vm.MigrationStateLoadTask;
        Assert.False(vm.MigratePlayerDataCommand.CanExecute(null));
        Assert.Contains("未被识别", vm.MigrationStatus);
    }

    /// <summary>对话框后发现文件改变时，界面报告服务拒绝并恢复可操作状态。</summary>
    [Fact]
    public async Task ViewModelReportsStalePreviewAndReleasesBusyState()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new"); Write(old.Game, "servers.dat", "old");
        var vm = CreateViewModel(old, target, new MigrationDialogs { BeforeConfirmation = () => Write(old.Game, "servers.dat", "changed") });
        await vm.MigrationStateLoadTask; await vm.MigratePlayerDataCommand.ExecuteAsync(null);
        Assert.Contains("预览后发生变化", vm.MigrationStatus); Assert.False(vm.IsBusy);
        Assert.False(File.Exists(Path.Combine(target.Game, "servers.dat")));
    }

    /// <summary>界面必须把单独的模组风险确认传给服务，缺少确认时保持实例不变。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewModelPassesExtraModRiskAcceptance(bool accepted)
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "mods/custom.jar", "custom");
        var dialogs = new MigrationDialogs { Selection = new(["mod:mods/custom.jar"], true, true) { ExtraModsRiskAccepted = accepted } };
        var vm = CreateViewModel(old, target, dialogs);
        await vm.MigrationStateLoadTask;
        await vm.MigratePlayerDataCommand.ExecuteAsync(null);
        Assert.Equal(accepted, File.Exists(Path.Combine(target.Game, "mods/custom.jar")));
        Assert.False(vm.IsBusy);
        Assert.Contains(accepted ? "迁移完成" : "请先确认", vm.MigrationStatus);
        if (accepted) Assert.Contains("不兼容", vm.MigrationStatus);
    }

    /// <summary>使用生产 ViewModel 和迁移服务，仅把用户交互替换为固定选择。</summary>
    private InstancesViewModel CreateViewModel(Instance old, Instance target, MigrationDialogs dialogs)
    {
        var locator = new LauncherRootMinecraftInstanceLocator(_root); var checker = new MinecraftInstanceChecker(_root);
        var vm = new InstancesViewModel(locator, new MinecraftInstanceManager(_root), new MinecraftInstanceImporter(_root),
            dialogs, locator.InstancesRoot, checker, new MinecraftInstanceDownloader(_root, checker, new NoDownload()),
            migration: Service, migrationDialogs: dialogs);
        vm.Instances.Add(new("old", old.Path, old.Game, ["1.7.10"]));
        vm.Instances.Add(new("new", target.Path, target.Game, ["1.7.10"]));
        vm.SelectedInstance = vm.Instances[1]; return vm;
    }

    /// <summary>明确模拟选择与确认，不弹出 WPF 对话框。</summary>
    private sealed class MigrationDialogs : IInstanceDialogs, IInstanceMigrationDialogs
    {
        public bool? Confirmation { get; init; } = false;
        public Action? BeforeConfirmation { get; init; }
        public GtnhInstanceInfo? Target { get; private set; }
        public MigrationSelection Selection { get; init; } = new(["servers"], true, true);
        /// <summary>选择唯一旧实例，记录固定接收方。</summary>
        public MigrationSourceChoice? ChooseSource(GtnhInstanceInfo target, IReadOnlyList<GtnhInstanceInfo> sources)
        { Target = target; return new(sources.Single(), GtnhMigrationMode.FullClient); }
        /// <summary>返回测试指定的范围和风险确认，默认仅迁移服务器列表。</summary>
        public MigrationSelection? ChooseGroups(GtnhInstanceInfo source, GtnhInstanceInfo target, IReadOnlyList<MigrationGroup> groups) => Selection;
        /// <summary>可在确认时模拟外部更改或取消。</summary>
        public bool? ConfirmMigration(InstanceMigrationPlan plan) { BeforeConfirmation?.Invoke(); return Confirmation; }
        /// <summary>测试明确请求恢复。</summary>
        public bool ConfirmRestore(string instanceName, MigrationRecoveryInfo recovery) => true;
        /// <summary>迁移流程不应调用其他管理对话框。</summary>
        public bool ConfirmDelete(MinecraftInstance instance) => throw new NotSupportedException();
        public string? ChooseExportPath(MinecraftInstance instance) => throw new NotSupportedException();
        public string? ChooseImportPath() => throw new NotSupportedException();
        public string? ConfirmImport(InstanceImportPreview preview) => throw new NotSupportedException();
    }
}
