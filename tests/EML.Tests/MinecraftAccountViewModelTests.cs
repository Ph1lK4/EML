using EML.App.ViewModels;
using EML.App.Services;
using EML.Core.Minecraft.Accounts;

namespace EML.Tests;

/// <summary>账号界面使用受控服务，验证配置、取消、错误和命令可用状态。</summary>
public sealed class MinecraftAccountViewModelTests
{
    /// <summary>重新读取、登录与退出各报告一次真实成功，不输出登录凭证。</summary>
    [Fact]
    public async Task LoadLoginAndLogoutEachReportSuccess()
    {
        var notices = new Notices();
        var view = new MinecraftAccountViewModel(new FakeService(), notifications: notices);
        await view.LoadCommand.ExecuteAsync(null);
        Assert.Single(notices.Items);
        await view.LoginCommand.ExecuteAsync(null);
        await view.LogoutCommand.ExecuteAsync(null);
        Assert.Equal(3, notices.Items.Count);
        Assert.All(notices.Items, item => Assert.Equal(OperationNoticeKind.Success, item.Kind));
        Assert.Contains("读取", notices.Items[0].Title);
        Assert.Contains("登录", notices.Items[1].Title);
        Assert.Contains("退出", notices.Items[2].Title);
    }

    /// <summary>弹窗沿用安全诊断，未知异常不能通过详情或路径泄露令牌。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginFailureNotificationUsesOnlySanitizedText(bool safeDiagnostic)
    {
        var notices = new Notices();
        var service = new FakeService
        {
            Error = safeDiagnostic ? new MinecraftAuthenticationException("Minecraft 登录失败（HTTP 403）。应用尚未获批。")
                : new IOException("secret-token-value")
        };
        var view = new MinecraftAccountViewModel(service, notifications: notices);
        await view.LoginCommand.ExecuteAsync(null);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
        Assert.Equal(view.StatusText, notice.Message);
        Assert.DoesNotContain("secret-token-value", notice.Message);
        Assert.Empty(notice.Paths);
        if (safeDiagnostic) Assert.Contains("HTTP 403", notice.Message);
    }

    /// <summary>读取或退出账号遇到未知文件异常时，详情和路径均不得暴露异常携带的凭证。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalAccountFailureNotificationsDoNotLeakExceptionPaths(bool logout)
    {
        var notices = new Notices();
        var error = new FileNotFoundException("secret-token-value", "secret-token-value");
        var service = new FakeService { LoadError = logout ? null : error, LogoutError = logout ? error : null };
        var view = new MinecraftAccountViewModel(service, notifications: notices);

        if (logout) await view.LogoutCommand.ExecuteAsync(null);
        else await view.LoadCommand.ExecuteAsync(null);

        var notice = Assert.Single(notices.Items);
        Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
        Assert.DoesNotContain("secret-token-value", notice.Message);
        Assert.Empty(notice.Paths);
        Assert.False(view.IsBusy);
    }

    /// <summary>取消登录只报告取消，不把用户主动停止误报为失败。</summary>
    [Fact]
    public async Task CancelledLoginReportsCancellationOnly()
    {
        var notices = new Notices();
        var view = new MinecraftAccountViewModel(new FakeService { Wait = true }, notifications: notices);
        var pending = view.LoginCommand.ExecuteAsync(null);
        view.LoginCommand.Cancel();
        await pending;
        Assert.Equal(OperationNoticeKind.Cancelled, Assert.Single(notices.Items).Kind);
    }

    /// <summary>缺少注册 ID 时禁用登录，但仍能读取或删除旧的本机凭证。</summary>
    [Fact]
    public void MissingConfigurationDisablesLoginOnly()
    {
        var view = new MinecraftAccountViewModel(new FakeService { IsConfigured = false });
        Assert.False(view.LoginCommand.CanExecute(null));
        Assert.True(view.LogoutCommand.CanExecute(null));
        Assert.True(view.LoadCommand.CanExecute(null));
    }

    /// <summary>读取档案不刷新令牌，成功登录更新名称，退出清空显示。</summary>
    [Fact]
    public async Task LoadLoginAndLogoutShowPublicProfile()
    {
        var service = new FakeService();
        var view = new MinecraftAccountViewModel(service);
        await view.LoadCommand.ExecuteAsync(null);
        Assert.Equal("OldName", view.AccountName);
        await view.LoginCommand.ExecuteAsync(null);
        Assert.Equal("NewName", view.AccountName);
        await view.LogoutCommand.ExecuteAsync(null);
        Assert.Equal("尚未登录", view.AccountName);
        Assert.Equal("", view.UserCode);
    }

    /// <summary>认证失败保留原账号显示，未知异常不能把敏感原文展示给用户。</summary>
    [Fact]
    public async Task UnexpectedFailureIsSanitizedAndPreservesProfile()
    {
        var service = new FakeService { Error = new IOException("secret-token-value") };
        var view = new MinecraftAccountViewModel(service);
        await view.LoadCommand.ExecuteAsync(null);
        await view.LoginCommand.ExecuteAsync(null);
        Assert.Equal("OldName", view.AccountName);
        Assert.DoesNotContain("secret-token-value", view.StatusText);
        Assert.False(view.IsBusy);
        Assert.True(view.LoginCommand.CanExecute(null));
    }

    /// <summary>安全诊断完整显示阶段与状态；授权失败不会清除旧账号或留下有效验证码。</summary>
    [Fact]
    public async Task SafeAuthenticationFailureShowsDiagnosticAndPreservesAccount()
    {
        const string diagnostic = "Minecraft 登录失败（HTTP 403；api.minecraftservices.com/launcher/login）。请检查应用访问权限。";
        var service = new FakeService { Error = new MinecraftAuthenticationException(diagnostic) };
        var view = new MinecraftAccountViewModel(service);
        await view.LoadCommand.ExecuteAsync(null);
        await view.LoginCommand.ExecuteAsync(null);

        Assert.Equal(diagnostic, view.StatusText);
        Assert.Equal("OldName", view.AccountName);
        Assert.Equal("id", view.AccountId);
        Assert.Equal("", view.UserCode);
        Assert.Equal("", view.VerificationAddress);
        Assert.False(view.OpenLoginPageCommand.CanExecute(null));
        Assert.False(view.IsBusy);
        Assert.True(view.LoginCommand.CanExecute(null));
    }

    /// <summary>取消异步登录会退出等待并恢复命令，不更改当前账号。</summary>
    [Fact]
    public async Task CancelPendingLoginPreservesAccountAndUnlocksCommands()
    {
        var service = new FakeService { Wait = true };
        var view = new MinecraftAccountViewModel(service);
        await view.LoadCommand.ExecuteAsync(null);
        var pending = view.LoginCommand.ExecuteAsync(null);
        Assert.True(view.IsBusy);
        Assert.False(view.LogoutCommand.CanExecute(null));
        view.LoginCommand.Cancel();
        await pending;
        Assert.Equal("OldName", view.AccountName);
        Assert.Contains("取消", view.StatusText);
        Assert.False(view.IsBusy);
    }

    /// <summary>记录通知而不显示窗口，验证业务结果与安全文案。</summary>
    private sealed class Notices : IOperationNotifications
    {
        public List<OperationNotice> Items { get; } = [];
        /// <summary>保留收到的完整通知，供测试断言。</summary>
        public void Report(OperationNotice notice) => Items.Add(notice);
    }

    private sealed class FakeService : IMinecraftAccountService
    {
        public bool IsConfigured { get; init; } = true;
        internal Exception? Error;
        internal Exception? LoadError;
        internal Exception? LogoutError;
        internal bool Wait;
        public Task<MinecraftAccount?> LoadAccountAsync(CancellationToken cancellationToken = default) =>
            LoadError is null ? Task.FromResult<MinecraftAccount?>(new("id", "OldName")) : Task.FromException<MinecraftAccount?>(LoadError);
        public async Task<MinecraftAccount> SignInAsync(IProgress<MinecraftLoginPrompt>? progress = null, CancellationToken cancellationToken = default)
        {
            if (Wait) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Error is not null) throw Error;
            return new("id", "NewName");
        }
        public Task<MinecraftAccountSession> GetSessionAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("UI must not access tokens");
        public Task SignOutAsync(CancellationToken cancellationToken = default) =>
            LogoutError is null ? Task.CompletedTask : Task.FromException(LogoutError);
    }
}
