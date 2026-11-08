using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.Core.Minecraft.Accounts;

namespace EML.App.ViewModels;

/// <summary>显示当前 Java 账号和设备验证码；界面不接触刷新令牌或游戏 access token。</summary>
public partial class MinecraftAccountViewModel(IMinecraftAccountService service, Action<Uri>? openBrowser = null) : ObservableObject
{
    private int _revision;
    private Uri? _verificationUri;
    public bool IsConfigured => service.IsConfigured;
    public string ConfigurationStatus => IsConfigured ? "已配置 EML 微软登录应用。" :
        "尚未配置微软 Client ID；请按 docs/microsoft-login.md 注册应用并配置后重启 EML。";

    [ObservableProperty] private string accountName = "尚未登录";
    [ObservableProperty] private string accountId = "";
    [ObservableProperty] private string statusText = "点击登录后，在微软网页完成账号授权。";
    [ObservableProperty] private string userCode = "";
    [ObservableProperty] private string verificationAddress = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand), nameof(LogoutCommand), nameof(LoadCommand))]
    private bool isBusy;

    /// <summary>本机读取和退出不依赖应用配置或网络，可清理过期或损坏凭证。</summary>
    private bool CanManage() => !IsBusy;
    private bool CanLogin() => IsConfigured && !IsBusy;
    private bool CanOpenPage() => _verificationUri is not null;

    /// <summary>首次显示只加载加密档案；实际刷新留到选择正版启动时执行。</summary>
    [RelayCommand(CanExecute = nameof(CanManage), IncludeCancelCommand = true)]
    private async Task LoadAsync(CancellationToken token)
    {
        IsBusy = true;
        try
        {
            var account = await service.LoadAccountAsync(token);
            ShowAccount(account);
            StatusText = account is null ? "尚未登录。" : "已读取本机账号，正版启动时会按需刷新会话。";
        }
        catch (OperationCanceledException) { StatusText = "已取消读取账号。"; }
        catch (Exception ex) { StatusText = SafeError(ex); }
        finally { IsBusy = false; }
    }

    /// <summary>登录期间显示验证码；迟到的进度在取消或完成后不能重新显示已失效验证码。</summary>
    [RelayCommand(CanExecute = nameof(CanLogin), IncludeCancelCommand = true)]
    private async Task LoginAsync(CancellationToken token)
    {
        IsBusy = true;
        var revision = ++_revision;
        StatusText = "正在向微软申请登录验证码……";
        try
        {
            var progress = new Progress<MinecraftLoginPrompt>(prompt =>
            {
                if (_revision != revision || token.IsCancellationRequested) return;
                UserCode = prompt.UserCode;
                _verificationUri = prompt.VerificationUri;
                VerificationAddress = prompt.VerificationUri.AbsoluteUri;
                OpenLoginPageCommand.NotifyCanExecuteChanged();
                StatusText = $"请在微软网页输入验证码，确认正在授权 EML。验证码到期时间：{prompt.ExpiresAt.ToLocalTime():HH:mm:ss}。";
                OpenLoginPage();
            });
            ShowAccount(await service.SignInAsync(progress, token));
            StatusText = "登录成功。请在实例的“启动设置”中勾选“使用已登录的正版账号”。";
        }
        catch (OperationCanceledException) { StatusText = "已取消登录，原有账号未更改。"; }
        catch (Exception ex) { StatusText = SafeError(ex); }
        finally
        {
            ++_revision;
            UserCode = ""; VerificationAddress = ""; _verificationUri = null;
            OpenLoginPageCommand.NotifyCanExecuteChanged();
            IsBusy = false;
        }
    }

    /// <summary>系统浏览器只打开认证层验证过的微软网址；失败时保留可手动访问的网址。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenPage))]
    private void OpenLoginPage()
    {
        if (_verificationUri is null) return;
        try { openBrowser?.Invoke(_verificationUri); }
        catch { StatusText = "无法打开浏览器，请手动访问上方微软网址并输入验证码。"; }
    }

    /// <summary>删除本机账号；不会关闭已运行的游戏，也不退出浏览器中的微软账号。</summary>
    [RelayCommand(CanExecute = nameof(CanManage), IncludeCancelCommand = true)]
    private async Task LogoutAsync(CancellationToken token)
    {
        IsBusy = true;
        try { await service.SignOutAsync(token); ShowAccount(null); StatusText = "已删除 EML 的本机登录凭证。"; }
        catch (OperationCanceledException) { StatusText = "已取消退出账号。"; }
        catch (Exception ex) { StatusText = SafeError(ex); }
        finally { IsBusy = false; }
    }

    /// <summary>只展示公开档案，UUID 用于帮助玩家辨认离线和正版存档身份。</summary>
    private void ShowAccount(MinecraftAccount? account)
    { AccountName = account?.Name ?? "尚未登录"; AccountId = account?.Id ?? ""; }

    /// <summary>仅展示认证层已处理过的错误；未知异常不输出可能包含敏感信息的原文。</summary>
    private static string SafeError(Exception error) => error is MinecraftAuthenticationException
        ? error.Message : "账号操作失败，请检查网络或本机凭证目录的访问权限后重试。";
}
