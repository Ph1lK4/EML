using EML.App.ViewModels;
using EML.Core.Minecraft.Accounts;

namespace EML.Tests;

/// <summary>账号界面使用受控服务，验证配置、取消、错误和命令可用状态。</summary>
public sealed class MinecraftAccountViewModelTests
{
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

    private sealed class FakeService : IMinecraftAccountService
    {
        public bool IsConfigured { get; init; } = true;
        internal Exception? Error;
        internal bool Wait;
        public Task<MinecraftAccount?> LoadAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult<MinecraftAccount?>(new("id", "OldName"));
        public async Task<MinecraftAccount> SignInAsync(IProgress<MinecraftLoginPrompt>? progress = null, CancellationToken cancellationToken = default)
        {
            if (Wait) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Error is not null) throw Error;
            return new("id", "NewName");
        }
        public Task<MinecraftAccountSession> GetSessionAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("UI must not access tokens");
        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
