using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
using EML.Core.Minecraft.Accounts;

namespace EML.App.ViewModels;

/// <summary>显示当前 Java 账号和设备验证码；界面不接触刷新令牌或游戏 access token。</summary>
public partial class MinecraftAccountViewModel(IMinecraftAccountService service, Action<Uri>? openBrowser = null,
    IOperationNotifications? notifications = null, Func<MinecraftAccount, bool>? confirmRemove = null) : ObservableObject
{
    private readonly IOperationNotifications _notifications = notifications ?? NullOperationNotifications.Instance;
    private int _revision;
    private Uri? _verificationUri;
    /// <summary>只展示公开账号信息；刷新令牌与会话缓存始终留在认证服务中。</summary>
    public ObservableCollection<MinecraftAccount> SavedAccounts { get; } = [];
    public bool SupportsMultipleAccounts => service is IMinecraftAccountManager;
    public string AccountListStatus => $"已保存 {SavedAccounts.Count} 个正版账号；各实例可以选择不同账号。";
    /// <summary>列表或公开身份变化后通知启动入口，事件不携带任何凭证。</summary>
    public event EventHandler? AccountsChanged;
    public bool IsConfigured => service.IsConfigured;
    public string ConfigurationStatus => IsConfigured ? "已配置 EML 微软登录应用。" :
        "尚未配置微软 Client ID；请按 docs/microsoft-login.md 注册应用并配置后重启 EML。";

    [ObservableProperty] private string accountName = "尚未登录";
    [ObservableProperty] private string accountId = "";
    [ObservableProperty] private string statusText = "点击登录后，在微软网页完成账号授权。";
    [ObservableProperty] private string userCode = "";
    [ObservableProperty] private string verificationAddress = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectAccountCommand), nameof(RemoveAccountCommand))]
    private MinecraftAccount? selectedAccount;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand), nameof(LogoutCommand), nameof(LoadCommand), nameof(SelectAccountCommand), nameof(RemoveAccountCommand))]
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
            await ReadPublicAccountsAsync(token);
            StatusText = SavedAccounts.Count == 0 ? "尚未登录。" : "已读取本机账号列表；仅在启动所选账号时按需刷新会话。";
            _notifications.Success("账号信息读取完成", StatusText);
        }
        catch (OperationCanceledException) { StatusText = "已取消读取账号。"; _notifications.Cancelled("读取账号已取消", StatusText); }
        catch (Exception ex) { StatusText = SafeError(ex); _notifications.Failure("读取账号失败", StatusText); }
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
            var account = await service.SignInAsync(progress, token);
            UpsertPublicAccount(account);
            ShowAccount(account);
            SelectedAccount = account;
            StatusText = "登录成功。该账号已添加或更新，其他账号保留；可在实例启动界面选择正版账号。";
            _notifications.Success("微软账号登录成功", StatusText);
            await RefreshAfterMutationAsync();
        }
        catch (OperationCanceledException)
        { StatusText = "已取消登录，原有账号未更改。"; _notifications.Cancelled("登录已取消", StatusText); }
        catch (Exception ex)
        {
            // 弹窗与行内提示使用同一安全文案，绝不传入可能含令牌的原始异常。
            StatusText = SafeError(ex);
            _notifications.Failure("微软账号登录失败", StatusText);
        }
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
        catch
        {
            StatusText = "无法打开浏览器，请手动访问上方微软网址并输入验证码。";
            _notifications.Failure("无法打开登录网页", StatusText);
        }
    }

    /// <summary>旧入口退出默认账号；多账号服务只移除该账号，绝不批量清空其他身份。</summary>
    [RelayCommand(CanExecute = nameof(CanManage), IncludeCancelCommand = true)]
    private async Task LogoutAsync(CancellationToken token)
    {
        if (service is IMinecraftAccountManager)
        {
            var current = SavedAccounts.FirstOrDefault(account => SameId(account.Id, AccountId));
            if (current is not null) await RemovePublicAccountAsync(current, token);
            return;
        }
        IsBusy = true;
        try
        {
            await service.SignOutAsync(token); SavedAccounts.Clear(); SelectedAccount = null; ShowAccount(null);
            NotifyAccountsChanged(); StatusText = "已删除 EML 的本机登录凭证。";
            _notifications.Success("账号已退出", StatusText);
        }
        catch (OperationCanceledException)
        { StatusText = "已取消退出账号。"; _notifications.Cancelled("退出账号已取消", StatusText); }
        catch (Exception ex) { StatusText = SafeError(ex); _notifications.Failure("退出账号失败", StatusText); }
        finally { IsBusy = false; }
    }

    /// <summary>只展示公开档案，UUID 用于帮助玩家辨认离线和正版存档身份。</summary>
    private void ShowAccount(MinecraftAccount? account)
    {
        AccountName = account?.Name ?? (SavedAccounts.Count > 0 ? "尚未选择默认账号" : "尚未登录");
        AccountId = account?.Id ?? "";
        SelectAccountCommand.NotifyCanExecuteChanged();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>默认账号只影响未明确选择身份的实例，不覆盖已有实例记住的 UUID。</summary>
    private bool CanSelectAccount() => !IsBusy && SupportsMultipleAccounts && SelectedAccount is { } selected && !SameId(selected.Id, AccountId);
    /// <summary>移除只针对列表中选中的一个账号，忙碌期间禁用重复操作。</summary>
    private bool CanRemoveAccount() => !IsBusy && SupportsMultipleAccounts && SelectedAccount is not null;

    /// <summary>将选中的已登录身份设为默认，不触发新的网页授权或全账号刷新。</summary>
    [RelayCommand(CanExecute = nameof(CanSelectAccount), IncludeCancelCommand = true)]
    private async Task SelectAccountAsync(CancellationToken token)
    {
        if (!CanSelectAccount()) return;
        var account = SelectedAccount!;
        IsBusy = true;
        try
        {
            ShowAccount(await ((IMinecraftAccountManager)service).SelectAccountAsync(account.Id, token));
            StatusText = $"默认账号已设为 {AccountName}。各实例已记住的账号不会被改变。";
            _notifications.Success("默认账号已切换", StatusText);
        }
        catch (OperationCanceledException) { StatusText = "已取消切换默认账号。"; }
        catch (Exception ex) { StatusText = SafeError(ex); _notifications.Failure("切换账号失败", StatusText); }
        finally { IsBusy = false; }
    }

    /// <summary>必须先确认，再移除所选账号；不会删除世界、玩家数据或微软账号。</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveAccount), IncludeCancelCommand = true)]
    private Task RemoveAccountAsync(CancellationToken token) => !CanRemoveAccount() ? Task.CompletedTask : RemovePublicAccountAsync(SelectedAccount!, token);

    /// <summary>冻结确认时的目标，成功后只删除对应公开条目，不自动选择另一个默认账号。</summary>
    private async Task RemovePublicAccountAsync(MinecraftAccount account, CancellationToken token)
    {
        if (!(confirmRemove?.Invoke(account) ?? false)) return;
        IsBusy = true;
        try
        {
            await ((IMinecraftAccountManager)service).RemoveAccountAsync(account.Id, token);
            foreach (var entry in SavedAccounts.Where(item => SameId(item.Id, account.Id)).ToArray()) SavedAccounts.Remove(entry);
            SelectedAccount = null;
            if (SameId(AccountId, account.Id)) ShowAccount(null);
            NotifyAccountsChanged();
            StatusText = $"已移除 {account.Name} 的本机登录记录。游戏文件和其他账号未更改；原实例仍保留公开身份提示。";
            _notifications.Success("账号已移除", StatusText);
        }
        catch (OperationCanceledException) { StatusText = "已取消移除账号。"; }
        catch (Exception ex) { StatusText = SafeError(ex); _notifications.Failure("移除账号失败", StatusText); }
        finally { IsBusy = false; }
    }

    /// <summary>一次读取完整公开列表及默认身份；旧单账号服务仍可用于已有调用方与测试。</summary>
    private async Task ReadPublicAccountsAsync(CancellationToken token)
    {
        MinecraftAccountCollection snapshot;
        if (service is IMinecraftAccountManager manager) snapshot = await manager.LoadAccountsAsync(token);
        else
        {
            var account = await service.LoadAccountAsync(token);
            snapshot = new(account is null ? [] : [account], account?.Id);
        }
        var selectedId = SelectedAccount?.Id ?? snapshot.ActiveAccountId;
        SavedAccounts.Clear();
        foreach (var account in snapshot.Accounts) SavedAccounts.Add(account);
        SelectedAccount = SavedAccounts.FirstOrDefault(account => SameId(account.Id, selectedId));
        ShowAccount(SavedAccounts.FirstOrDefault(account => SameId(account.Id, snapshot.ActiveAccountId)));
        NotifyAccountsChanged();
    }

    /// <summary>新增或重新授权成功后刷新其余账号；刷新展示失败不能误报已经完成的登录失败。</summary>
    private async Task RefreshAfterMutationAsync()
    {
        if (!SupportsMultipleAccounts) return;
        try { await ReadPublicAccountsAsync(CancellationToken.None); }
        catch (Exception ex)
        {
            StatusText = "账号操作已完成，但列表刷新失败：" + SafeError(ex);
            _notifications.Warning("账号列表待刷新", StatusText);
        }
    }

    /// <summary>同一 UUID 重新授权更新原条目，不添加重复账号。</summary>
    private void UpsertPublicAccount(MinecraftAccount account)
    {
        foreach (var old in SavedAccounts.Where(item => SameId(item.Id, account.Id)).ToArray()) SavedAccounts.Remove(old);
        if (!SupportsMultipleAccounts) SavedAccounts.Clear();
        SavedAccounts.Add(account);
        NotifyAccountsChanged();
    }

    /// <summary>启动刷新得到新玩家名时只更新已有 UUID 的公开档案，不补回已移除账号或改变默认身份。</summary>
    public void UpdateKnownAccount(MinecraftAccount account)
    {
        var existing = SavedAccounts.FirstOrDefault(item => SameId(item.Id, account.Id));
        if (existing is null || existing == account) return;
        var selected = SelectedAccount is { } selection && SameId(selection.Id, account.Id);
        SavedAccounts[SavedAccounts.IndexOf(existing)] = account;
        if (selected) SelectedAccount = account;
        if (SameId(AccountId, account.Id)) ShowAccount(account);
        NotifyAccountsChanged();
    }

    /// <summary>统一刷新列表统计和两个启动入口的可用账号，不发送登录令牌。</summary>
    private void NotifyAccountsChanged()
    {
        OnPropertyChanged(nameof(AccountListStatus));
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>使用 UUID 比较账号，不把改名后的同一个人当成新账号。</summary>
    private static bool SameId(string? first, string? second) => !string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(second) &&
        (string.Equals(first, second, StringComparison.OrdinalIgnoreCase) || Guid.TryParse(first, out var left) && Guid.TryParse(second, out var right) && left == right);

    /// <summary>仅展示认证层已处理过的错误；未知异常不输出可能包含敏感信息的原文。</summary>
    private static string SafeError(Exception error) => error is MinecraftAuthenticationException
        ? error.Message : "账号操作失败，请检查网络或本机凭证目录的访问权限后重试。";
}
