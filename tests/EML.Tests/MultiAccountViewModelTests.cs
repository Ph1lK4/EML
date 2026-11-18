using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Minecraft.Accounts;

namespace EML.Tests;

/// <summary>多账号界面只使用公开档案和内存服务，不读取真实凭据、不打开网页。</summary>
public sealed class MultiAccountViewModelTests
{
    private static readonly MinecraftAccount First = new("11111111222233334444555555555555", "FirstPlayer");
    private static readonly MinecraftAccount Second = new("aaaaaaaa222233334444555555555555", "SecondPlayer");

    /// <summary>首次读取展示完整列表和默认账号，不请求任何会话。</summary>
    [Fact]
    public async Task LoadDisplaysAllSavedAccountsWithoutAccessingSessions()
    {
        var service = new MemoryManager([First, Second], First.Id);
        var view = new MinecraftAccountViewModel(service);
        var changes = 0;
        view.AccountsChanged += (_, _) => changes++;

        await view.LoadCommand.ExecuteAsync(null);

        Assert.True(view.SupportsMultipleAccounts);
        Assert.Equal(new[] { First, Second }, view.SavedAccounts.ToArray());
        Assert.Equal(First.Id, view.AccountId);
        Assert.Equal(First.Name, view.AccountName);
        Assert.Equal(First, view.SelectedAccount);
        Assert.Contains("2", view.AccountListStatus);
        Assert.Equal(1, service.LoadCalls);
        Assert.True(changes > 0);
        Assert.False(view.SelectAccountCommand.CanExecute(null));
        Assert.True(view.RemoveAccountCommand.CanExecute(null));
    }

    /// <summary>会话刷新只更新已有 UUID 的名字，不切换默认账号，不重新添加已移除身份。</summary>
    [Fact]
    public async Task RefreshedPublicProfilePreservesDefaultAndDoesNotAddUnknownAccounts()
    {
        var service = new MemoryManager([First, Second], Second.Id);
        var view = new MinecraftAccountViewModel(service);
        await view.LoadCommand.ExecuteAsync(null);
        view.SelectedAccount = First;

        var renamed = First with { Name = "RenamedPlayer" };
        view.UpdateKnownAccount(renamed);

        Assert.Equal(renamed, view.SavedAccounts[0]);
        Assert.Equal(renamed, view.SelectedAccount);
        Assert.Equal(Second.Id, view.AccountId);
        Assert.Equal(Second.Id, service.ActiveId);
        view.UpdateKnownAccount(new("bbbbbbbb222233334444555555555555", "Unknown"));
        Assert.Equal(2, view.SavedAccounts.Count);
        Assert.Empty(service.SelectedIds);
    }

    /// <summary>高亮列表项不改变默认身份，必须明确执行设为默认账号命令。</summary>
    [Fact]
    public async Task HighlightDoesNotSwitchDefaultUntilExplicitCommand()
    {
        var service = new MemoryManager([First, Second], First.Id);
        var view = new MinecraftAccountViewModel(service);
        await view.LoadCommand.ExecuteAsync(null);

        view.SelectedAccount = Second;

        Assert.Equal(First.Id, view.AccountId);
        Assert.Equal(First.Id, service.ActiveId);
        Assert.Empty(service.SelectedIds);
        Assert.True(view.SelectAccountCommand.CanExecute(null));
        await view.SelectAccountCommand.ExecuteAsync(null);
        Assert.Equal(new[] { Second.Id }, service.SelectedIds);
        Assert.Equal(Second.Id, view.AccountId);
        Assert.Equal(Second.Name, view.AccountName);
        Assert.Equal(2, view.SavedAccounts.Count);
        Assert.False(view.SelectAccountCommand.CanExecute(null));
    }

    /// <summary>登录新账号保留旧账号；同 UUID 重新授权只更新原条目。</summary>
    [Fact]
    public async Task LoginAddsAccountsAndReauthorizationUpdatesRatherThanDuplicates()
    {
        var service = new MemoryManager([First], First.Id) { NextLogin = Second };
        var view = new MinecraftAccountViewModel(service);
        await view.LoadCommand.ExecuteAsync(null);

        await view.LoginCommand.ExecuteAsync(null);

        Assert.Equal(new[] { First, Second }, view.SavedAccounts.ToArray());
        Assert.Equal(Second.Id, view.AccountId);
        Assert.Equal(Second, view.SelectedAccount);
        service.NextLogin = Second with { Name = "RenamedPlayer" };
        await view.LoginCommand.ExecuteAsync(null);
        Assert.Equal(2, view.SavedAccounts.Count);
        Assert.Equal(First, view.SavedAccounts.Single(account => account.Id == First.Id));
        Assert.Equal("RenamedPlayer", view.SavedAccounts.Single(account => account.Id == Second.Id).Name);
        Assert.Equal("RenamedPlayer", view.AccountName);
    }

    /// <summary>登录或加密落盘失败保留已有列表，不泄露未知异常的私有正文。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLoginPreservesVisibleAccountsAndSanitizesErrors(bool safeAuthenticationError)
    {
        var service = new MemoryManager([First, Second], First.Id)
        {
            LoginError = safeAuthenticationError ? new MinecraftAuthenticationException("账号授权未完成。")
                : new IOException("private-refresh-token")
        };
        var notices = new Notices();
        var view = new MinecraftAccountViewModel(service, notifications: notices);
        await view.LoadCommand.ExecuteAsync(null);
        notices.Items.Clear();

        await view.LoginCommand.ExecuteAsync(null);

        Assert.Equal(new[] { First, Second }, view.SavedAccounts.ToArray());
        Assert.Equal(First.Id, view.AccountId);
        Assert.Equal(OperationNoticeKind.Failure, Assert.Single(notices.Items).Kind);
        Assert.DoesNotContain("private-refresh-token", view.StatusText);
        Assert.All(notices.Items, notice => Assert.DoesNotContain("private-refresh-token", notice.Message));
        Assert.False(view.IsBusy);
    }

    /// <summary>登录已经成功后列表刷新失败，只提示待刷新，不能反过来声称登录失败。</summary>
    [Fact]
    public async Task PostLoginRefreshFailureKeepsNewAccountAndReportsWarning()
    {
        var service = new MemoryManager([First], First.Id) { NextLogin = Second };
        service.AfterLogin = () => service.LoadError = new IOException("private-refresh-token");
        var notices = new Notices();
        var view = new MinecraftAccountViewModel(service, notifications: notices);
        await view.LoadCommand.ExecuteAsync(null);
        notices.Items.Clear();

        await view.LoginCommand.ExecuteAsync(null);

        Assert.Equal(new[] { First, Second }, view.SavedAccounts.ToArray());
        Assert.Equal(Second.Id, view.AccountId);
        Assert.Equal(new[] { OperationNoticeKind.Success, OperationNoticeKind.Warning }, notices.Items.Select(notice => notice.Kind));
        Assert.DoesNotContain(notices.Items, notice => notice.Kind == OperationNoticeKind.Failure);
        Assert.DoesNotContain("private-refresh-token", view.StatusText);
        Assert.Contains("操作已完成", view.StatusText);
    }

    /// <summary>没有确认提供者或用户拒绝确认时，不删除任何账号。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalRequiresExplicitConfirmation(bool provideDecliningConfirmation)
    {
        var service = new MemoryManager([First, Second], First.Id);
        Func<MinecraftAccount, bool>? confirmation = provideDecliningConfirmation ? _ => false : null;
        var view = new MinecraftAccountViewModel(service, confirmRemove: confirmation);
        await view.LoadCommand.ExecuteAsync(null);

        await view.RemoveAccountCommand.ExecuteAsync(null);

        Assert.Empty(service.RemovedIds);
        Assert.Equal(new[] { First, Second }, view.SavedAccounts.ToArray());
        Assert.Equal(First.Id, view.AccountId);
    }

    /// <summary>移除默认账号后保留其余账号，但不自动把另一个人设为默认。</summary>
    [Fact]
    public async Task RemovingDefaultLeavesOtherAccountsWithoutAutomaticFallback()
    {
        var service = new MemoryManager([First, Second], First.Id);
        var view = new MinecraftAccountViewModel(service, confirmRemove: _ => true);
        await view.LoadCommand.ExecuteAsync(null);

        await view.RemoveAccountCommand.ExecuteAsync(null);

        Assert.Equal(new[] { First.Id }, service.RemovedIds);
        Assert.Equal(Second, Assert.Single(view.SavedAccounts));
        Assert.Null(service.ActiveId);
        Assert.Equal("", view.AccountId);
        Assert.Equal("尚未选择默认账号", view.AccountName);
        Assert.Null(view.SelectedAccount);
        Assert.False(view.SelectAccountCommand.CanExecute(null));
        Assert.False(view.RemoveAccountCommand.CanExecute(null));
    }

    /// <summary>移除非默认账号只更新对应条目，默认身份及其他账号保持不变。</summary>
    [Fact]
    public async Task RemovingNonDefaultDoesNotChangeDefaultAccount()
    {
        var service = new MemoryManager([First, Second], First.Id);
        var view = new MinecraftAccountViewModel(service, confirmRemove: _ => true);
        await view.LoadCommand.ExecuteAsync(null);
        view.SelectedAccount = Second;

        await view.RemoveAccountCommand.ExecuteAsync(null);

        Assert.Equal(new[] { Second.Id }, service.RemovedIds);
        Assert.Equal(First, Assert.Single(view.SavedAccounts));
        Assert.Equal(First.Id, view.AccountId);
        Assert.Equal(First.Id, service.ActiveId);
    }

    /// <summary>确认期间即使列表选择变化，仍只移除用户最初确认的账号。</summary>
    [Fact]
    public async Task RemovalFreezesConfirmedAccountBeforeSelectionChanges()
    {
        var service = new MemoryManager([First, Second], First.Id);
        MinecraftAccountViewModel? view = null;
        MinecraftAccount? confirmed = null;
        view = new(service, confirmRemove: account =>
        {
            confirmed = account;
            view!.SelectedAccount = Second;
            return true;
        });
        await view.LoadCommand.ExecuteAsync(null);

        await view.RemoveAccountCommand.ExecuteAsync(null);

        Assert.Equal(First, confirmed);
        Assert.Equal(new[] { First.Id }, service.RemovedIds);
        Assert.Equal(Second, Assert.Single(view.SavedAccounts));
    }

    /// <summary>移除失败不能先从界面删除条目，也不能泄露凭证异常中的路径和正文。</summary>
    [Fact]
    public async Task FailedRemovalPreservesSelectionAndSanitizesFailure()
    {
        var service = new MemoryManager([First, Second], First.Id)
        { RemoveError = new FileNotFoundException("private-refresh-token", "private-credential-path") };
        var notices = new Notices();
        var view = new MinecraftAccountViewModel(service, notifications: notices, confirmRemove: _ => true);
        await view.LoadCommand.ExecuteAsync(null);
        notices.Items.Clear();

        await view.RemoveAccountCommand.ExecuteAsync(null);

        Assert.Equal(new[] { First, Second }, view.SavedAccounts.ToArray());
        Assert.Equal(First, view.SelectedAccount);
        Assert.Equal(First.Id, view.AccountId);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
        Assert.DoesNotContain("private-refresh-token", notice.Message);
        Assert.DoesNotContain("private-credential-path", notice.Message);
        Assert.Empty(notice.Paths);
        Assert.False(view.IsBusy);
    }

    /// <summary>默认账号切换写入失败时仍展示原默认值，选中项可保留供重试。</summary>
    [Fact]
    public async Task FailedDefaultSwitchDoesNotCommitVisibleDefault()
    {
        var service = new MemoryManager([First, Second], First.Id) { SelectError = new IOException("private-refresh-token") };
        var notices = new Notices();
        var view = new MinecraftAccountViewModel(service, notifications: notices);
        await view.LoadCommand.ExecuteAsync(null);
        notices.Items.Clear();
        view.SelectedAccount = Second;

        await view.SelectAccountCommand.ExecuteAsync(null);

        Assert.Equal(First.Id, view.AccountId);
        Assert.Equal(First.Id, service.ActiveId);
        Assert.Equal(Second, view.SelectedAccount);
        Assert.Equal(OperationNoticeKind.Failure, Assert.Single(notices.Items).Kind);
        Assert.DoesNotContain("private-refresh-token", view.StatusText);
        Assert.True(view.SelectAccountCommand.CanExecute(null));
    }

    /// <summary>正在授权时禁止切换、移除和重复读取，取消后恢复命令且原列表不变。</summary>
    [Fact]
    public async Task PendingLoginDisablesManagementUntilCancellationCompletes()
    {
        var service = new MemoryManager([First, Second], First.Id) { WaitForLoginCancellation = true };
        var view = new MinecraftAccountViewModel(service, confirmRemove: _ => true);
        await view.LoadCommand.ExecuteAsync(null);
        view.SelectedAccount = Second;

        var pending = view.LoginCommand.ExecuteAsync(null);

        Assert.True(view.IsBusy);
        Assert.False(view.LoginCommand.CanExecute(null));
        Assert.False(view.LoadCommand.CanExecute(null));
        Assert.False(view.LogoutCommand.CanExecute(null));
        Assert.False(view.SelectAccountCommand.CanExecute(null));
        Assert.False(view.RemoveAccountCommand.CanExecute(null));
        view.LoginCommand.Cancel();
        await pending;
        Assert.False(view.IsBusy);
        Assert.Equal(new[] { First, Second }, view.SavedAccounts.ToArray());
        Assert.Equal(First.Id, view.AccountId);
        Assert.True(view.SelectAccountCommand.CanExecute(null));
        Assert.True(view.RemoveAccountCommand.CanExecute(null));
    }

    /// <summary>列表读取失败保留已显示账号，不把暂时不可访问的存储解释成全部退出。</summary>
    [Fact]
    public async Task FailedReloadKeepsCurrentPublicSnapshot()
    {
        var service = new MemoryManager([First, Second], First.Id);
        var view = new MinecraftAccountViewModel(service);
        await view.LoadCommand.ExecuteAsync(null);
        view.SelectedAccount = Second;
        service.LoadError = new IOException("private-refresh-token");

        await view.LoadCommand.ExecuteAsync(null);

        Assert.Equal(new[] { First, Second }, view.SavedAccounts.ToArray());
        Assert.Equal(First.Id, view.AccountId);
        Assert.Equal(Second, view.SelectedAccount);
        Assert.DoesNotContain("private-refresh-token", view.StatusText);
    }

    /// <summary>兼容退出命令只移除默认账号，不按当前高亮项或批量退出其他身份。</summary>
    [Fact]
    public async Task LegacyLogoutRemovesOnlyDefaultEvenIfAnotherAccountIsHighlighted()
    {
        var service = new MemoryManager([First, Second], First.Id);
        var view = new MinecraftAccountViewModel(service, confirmRemove: _ => true);
        await view.LoadCommand.ExecuteAsync(null);
        view.SelectedAccount = Second;

        await view.LogoutCommand.ExecuteAsync(null);

        Assert.Equal(new[] { First.Id }, service.RemovedIds);
        Assert.Equal(Second, Assert.Single(view.SavedAccounts));
        Assert.Equal("", view.AccountId);
    }

    /// <summary>已有账号但尚未指定默认时不偷偷选列表首项，用户可以显式选择。</summary>
    [Fact]
    public async Task LoadingWithoutDefaultDoesNotChooseFirstAccount()
    {
        var service = new MemoryManager([First, Second], null);
        var view = new MinecraftAccountViewModel(service);

        await view.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, view.SavedAccounts.Count);
        Assert.Equal("", view.AccountId);
        Assert.Equal("尚未选择默认账号", view.AccountName);
        Assert.Null(view.SelectedAccount);
        Assert.Empty(service.SelectedIds);
    }

    /// <summary>收集界面通知供断言，不显示真实对话框。</summary>
    private sealed class Notices : IOperationNotifications
    {
        public List<OperationNotice> Items { get; } = [];
        /// <summary>保存安全的用户可见通知。</summary>
        public void Report(OperationNotice notice) => Items.Add(notice);
    }

    /// <summary>模拟公开账号管理；任何索取令牌或误用单账号接口都会直接使测试失败。</summary>
    private sealed class MemoryManager(IEnumerable<MinecraftAccount> accounts, string? activeId) : IMinecraftAccountManager
    {
        private readonly List<MinecraftAccount> _accounts = [.. accounts];
        public string? ActiveId { get; private set; } = activeId;
        public bool IsConfigured => true;
        public MinecraftAccount NextLogin { get; set; } = Second;
        public Exception? LoginError { get; set; }
        public Exception? LoadError { get; set; }
        public Exception? RemoveError { get; set; }
        public Exception? SelectError { get; set; }
        public Action? AfterLogin { get; set; }
        public bool WaitForLoginCancellation { get; set; }
        public int LoadCalls { get; private set; }
        public List<string> SelectedIds { get; } = [];
        public List<string> RemovedIds { get; } = [];

        /// <summary>返回独立的公开列表快照，允许模拟读取失败。</summary>
        public Task<MinecraftAccountCollection> LoadAccountsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCalls++;
            if (LoadError is not null) return Task.FromException<MinecraftAccountCollection>(LoadError);
            return Task.FromResult(new MinecraftAccountCollection(_accounts.ToArray(), ActiveId));
        }

        /// <summary>仅在成功授权后更新内存中的同 UUID 条目及默认身份。</summary>
        public async Task<MinecraftAccount> SignInAsync(IProgress<MinecraftLoginPrompt>? progress = null, CancellationToken cancellationToken = default)
        {
            if (WaitForLoginCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (LoginError is not null) throw LoginError;
            _accounts.RemoveAll(account => account.Id == NextLogin.Id);
            _accounts.Add(NextLogin);
            ActiveId = NextLogin.Id;
            AfterLogin?.Invoke();
            return NextLogin;
        }

        /// <summary>记录明确的默认账号切换；失败时不提前提交。</summary>
        public Task<MinecraftAccount> SelectAccountAsync(string accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SelectError is not null) return Task.FromException<MinecraftAccount>(SelectError);
            var account = _accounts.Single(value => value.Id == accountId);
            SelectedIds.Add(accountId);
            ActiveId = accountId;
            return Task.FromResult(account);
        }

        /// <summary>只移除一个指定账号；移除默认账号后保持没有默认选择。</summary>
        public Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RemoveError is not null) return Task.FromException(RemoveError);
            _accounts.Remove(_accounts.Single(value => value.Id == accountId));
            RemovedIds.Add(accountId);
            if (ActiveId == accountId) ActiveId = null;
            return Task.CompletedTask;
        }

        /// <summary>多账号页面应请求完整公开列表，不能退回单账号读取。</summary>
        public Task<MinecraftAccount?> LoadAccountAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Use the public account collection.");
        /// <summary>界面不能请求任何正版会话令牌。</summary>
        public Task<MinecraftAccountSession> GetSessionAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("UI must not access tokens.");
        /// <summary>界面不能针对账号读取令牌。</summary>
        public Task<MinecraftAccountSession> GetSessionForAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("UI must not access tokens.");
        /// <summary>多账号页面必须调用有明确目标的移除接口。</summary>
        public Task SignOutAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Remove an explicit account only.");
    }
}
