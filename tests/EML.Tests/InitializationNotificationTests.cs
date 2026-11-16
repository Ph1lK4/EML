using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Java;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Accounts;
using EML.Core.Minecraft.Launching;

namespace EML.Tests;

/// <summary>初始化仍更新页面和错误状态，但不入弹窗队列；手动命令保持原有反馈。</summary>
public sealed class InitializationNotificationTests
{
    /// <summary>成功、空结果、失败和取消均静默；之后手动检测仍报告相同结果。</summary>
    [Theory]
    [InlineData("success", OperationNoticeKind.Success)]
    [InlineData("empty", OperationNoticeKind.Warning)]
    [InlineData("failure", OperationNoticeKind.Failure)]
    [InlineData("cancel", OperationNoticeKind.Cancelled)]
    public async Task JavaInitializationPreservesStateWithoutDialogs(string outcome, OperationNoticeKind expected)
    {
        var recorder = new Recorder();
        var notifications = new ScopedOperationNotifications(recorder);
        var model = new JavaSettingsViewModel(new JavaLocator(outcome), notifications);
        await notifications.RunInitializationAsync(() => model.DetectJavaCommand.ExecuteAsync(null));
        Assert.Empty(recorder.Items);
        Assert.False(model.IsBusy);
        Assert.NotEqual("尚未检测 Java", model.StatusText);
        if (outcome == "success") Assert.Single(model.Installations);
        if (outcome == "failure") Assert.Contains("检测目录不可访问", model.StatusText);
        await model.DetectJavaCommand.ExecuteAsync(null);
        Assert.Equal(expected, Assert.Single(recorder.Items).Kind);
    }

    /// <summary>实例扫描仍填充列表或显示错误；手动刷新保留成功/失败弹窗。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstanceInitializationPreservesStateWithoutDialogs(bool failure)
    {
        var recorder = new Recorder();
        var notifications = new ScopedOperationNotifications(recorder);
        var model = CreateInstances(notifications, new InstanceLocator(failure));
        await notifications.RunInitializationAsync(() => model.RefreshCommand.ExecuteAsync(null));
        Assert.Empty(recorder.Items);
        Assert.False(model.IsBusy);
        if (failure) Assert.Contains("实例目录不可访问", model.StatusText);
        else Assert.Equal("Test pack", Assert.Single(model.Instances).Name);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(failure ? OperationNoticeKind.Failure : OperationNoticeKind.Success, Assert.Single(recorder.Items).Kind);
    }

    /// <summary>账号自动读取的全部结果仅更新页面，错误依旧脱敏，手动读取正常通知。</summary>
    [Theory]
    [InlineData("success", OperationNoticeKind.Success)]
    [InlineData("empty", OperationNoticeKind.Success)]
    [InlineData("failure", OperationNoticeKind.Failure)]
    [InlineData("cancel", OperationNoticeKind.Cancelled)]
    public async Task AccountInitializationPreservesStateWithoutDialogs(string outcome, OperationNoticeKind expected)
    {
        var recorder = new Recorder();
        var notifications = new ScopedOperationNotifications(recorder);
        var model = new MinecraftAccountViewModel(new AccountService(outcome), notifications: notifications);
        await notifications.RunInitializationAsync(() => model.LoadCommand.ExecuteAsync(null));
        Assert.Empty(recorder.Items);
        Assert.False(model.IsBusy);
        Assert.Equal(outcome == "success" ? "Steve" : "尚未登录", model.AccountName);
        Assert.DoesNotContain("private-token", model.StatusText);
        await model.LoadCommand.ExecuteAsync(null);
        Assert.Equal(expected, Assert.Single(recorder.Items).Kind);
    }

    /// <summary>初始化扫描结束后才失败的派生设置读取仍静默，主动重新读取会报告错误。</summary>
    [Fact]
    public async Task DelayedSettingsFailureRetainsInitializationOrigin()
    {
        var recorder = new Recorder();
        var notifications = new ScopedOperationNotifications(recorder);
        var store = new DelayedSettings();
        var model = CreateInstances(notifications, new InstanceLocator(false), store);
        await notifications.RunInitializationAsync(() => model.RefreshCommand.ExecuteAsync(null));
        Assert.False(model.IsBusy);
        Assert.True(model.IsLaunchSettingsLoading);
        store.Result.SetException(new IOException("启动设置不可访问"));
        await model.LaunchSettingsLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(recorder.Items);
        Assert.Contains("启动设置不可访问", model.LaunchSettingsStatus);
        Assert.False(model.IsLaunchSettingsLoaded);
        await model.ReloadLaunchSettingsCommand.ExecuteAsync(null);
        Assert.Equal(OperationNoticeKind.Failure, Assert.Single(recorder.Items).Kind);
    }

    /// <summary>并发初始化之间互不干扰，也不会吞掉另一个用户操作的通知。</summary>
    [Fact]
    public async Task ConcurrentManualNotificationsAreNotSuppressed()
    {
        var recorder = new Recorder();
        var notifications = new ScopedOperationNotifications(recorder);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingFirst = notifications.RunInitializationAsync(async () =>
        { await first.Task; notifications.Success("初始化一", "完成"); });
        var pendingSecond = notifications.RunInitializationAsync(async () =>
        { await second.Task; notifications.Failure("初始化二", "失败"); });
        notifications.Success("手动操作", "完成");
        first.SetResult();
        await pendingFirst.WaitAsync(TimeSpan.FromSeconds(5));
        notifications.Failure("手动操作二", "失败");
        second.SetResult();
        await pendingSecond.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "手动操作", "手动操作二" }, recorder.Items.Select(item => item.Title));
    }

    /// <summary>嵌套初始化退出后仍处于外层静默范围，抛错不会残留禁用状态。</summary>
    [Fact]
    public async Task NestedAndFailedInitializationRestoresNotificationState()
    {
        var recorder = new Recorder();
        var notifications = new ScopedOperationNotifications(recorder);
        await Assert.ThrowsAsync<IOException>(() => notifications.RunInitializationAsync(async () =>
        {
            await notifications.RunInitializationAsync(() =>
            { notifications.Success("内层", "完成"); return Task.CompletedTask; });
            notifications.Failure("外层", "失败");
            throw new IOException("初始化故障");
        }));
        Assert.Empty(recorder.Items);
        Assert.Throws<IOException>(() => notifications.RunInitialization<int>(() => throw new IOException("同步故障")));
        notifications.Success("手动操作", "完成");
        Assert.Single(recorder.Items);
    }

    /// <summary>只注入扫描和可选设置读取，其他写入服务不会被初始化调用。</summary>
    private static InstancesViewModel CreateInstances(IOperationNotifications notifications, IMinecraftInstanceLocator locator,
        IMinecraftLaunchSettingsStore? settings = null) => new(locator, null!, null!, null!, @"C:\test\instances", null!, null!,
            launchSettingsStore: settings, notifications: notifications);

    /// <summary>记录最终通知，不创建真实窗口。</summary>
    private sealed class Recorder : IOperationNotifications
    {
        public List<OperationNotice> Items { get; } = [];
        public void Report(OperationNotice notice) => Items.Add(notice);
    }

    /// <summary>异步返回检测结果，确保静默标记跨越 await。</summary>
    private sealed class JavaLocator(string outcome) : IJavaLocator
    {
        public async Task<IReadOnlyList<JavaInstallation>> FindAllAsync(CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (outcome == "failure") throw new IOException("检测目录不可访问");
            if (outcome == "cancel") throw new OperationCanceledException();
            return outcome == "empty" ? [] : [new(@"C:\Java\bin\java.exe", "25", "x64")];
        }
    }

    /// <summary>提供内存实例，不读取用户磁盘。</summary>
    private sealed class InstanceLocator(bool failure) : IMinecraftInstanceLocator
    {
        public async Task<IReadOnlyList<MinecraftInstance>> FindAllAsync(CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (failure) throw new IOException("实例目录不可访问");
            return [new("Test pack", @"C:\test\instances\pack", @"C:\test\instances\pack\.minecraft", ["1.7.10"])];
        }
    }

    /// <summary>手动控制读取完成时间，不创建或修改任何设置文件。</summary>
    private sealed class DelayedSettings : IMinecraftLaunchSettingsStore
    {
        public TaskCompletionSource<MinecraftLaunchSettings> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<MinecraftLaunchSettings> LoadAsync(string instancePath, CancellationToken cancellationToken = default) => Result.Task;
        public Task SaveAsync(string instancePath, MinecraftLaunchSettings settings, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("初始化不应写入设置。");
    }

    /// <summary>模拟本机公开账号读取，不使用真实凭据或访问网络。</summary>
    private sealed class AccountService(string outcome) : IMinecraftAccountService
    {
        public bool IsConfigured => true;
        public async Task<MinecraftAccount?> LoadAccountAsync(CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (outcome == "failure") throw new IOException("private-token");
            if (outcome == "cancel") throw new OperationCanceledException();
            return outcome == "empty" ? null : new("id", "Steve");
        }
        public Task<MinecraftAccount> SignInAsync(IProgress<MinecraftLoginPrompt>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("初始化不应登录。");
        public Task<MinecraftAccountSession> GetSessionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("初始化不应刷新会话。");
        public Task SignOutAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("初始化不应退出账号。");
    }
}
