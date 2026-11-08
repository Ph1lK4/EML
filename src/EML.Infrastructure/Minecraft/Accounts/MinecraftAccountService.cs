using EML.Core.Minecraft.Accounts;

namespace EML.Infrastructure.Minecraft.Accounts;

/// <summary>管理一个当前账号，串行化登录、刷新和退出；临时网络错误不删除已有账号。</summary>
public sealed class MinecraftAccountService : IMinecraftAccountService, IDisposable
{
    private readonly MicrosoftLoginOptions _options;
    private readonly IMinecraftAccountStore _store;
    private readonly IMicrosoftMinecraftAuthClient _client;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient? _ownedHttp;
    private bool _loaded;
    private StoredMinecraftAccount? _stored;
    private MinecraftAccountSession? _session;
    public bool IsConfigured => _options.IsConfigured;

    /// <summary>固定官方认证端点并禁用重定向，避免认证请求被转发到非预期主机。</summary>
    public MinecraftAccountService(MicrosoftLoginOptions options)
    {
        _options = options;
        _clock = TimeProvider.System;
        _ownedHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        _client = new MicrosoftMinecraftAuthClient(_ownedHttp, options);
        _store = new WindowsMinecraftAccountStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EML", "accounts", "microsoft.dpapi"));
    }

    /// <summary>测试注入官方接口、加密存储和时钟，不需要真实账号或网络。</summary>
    internal MinecraftAccountService(MicrosoftLoginOptions options, IMicrosoftMinecraftAuthClient client,
        IMinecraftAccountStore store, TimeProvider? clock = null)
    { _options = options; _client = client; _store = store; _clock = clock ?? TimeProvider.System; }

    /// <summary>启动时只读取本机公开档案，不主动联网刷新，避免拖慢窗口首次显示。</summary>
    public async Task<MinecraftAccount?> LoadAccountAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await LoadCoreAsync(cancellationToken).ConfigureAwait(false); return _stored?.Account; }
        finally { _gate.Release(); }
    }

    /// <summary>所有授权步骤和加密落盘完成后才替换当前账号；失败或取消保留原账号。</summary>
    public async Task<MinecraftAccount> SignInAsync(IProgress<MinecraftLoginPrompt>? progress = null, CancellationToken cancellationToken = default)
    {
        _options.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tokens = await _client.SignInAsync(progress, cancellationToken).ConfigureAwait(false);
            var session = await _client.ExchangeAsync(tokens, cancellationToken).ConfigureAwait(false);
            var stored = new StoredMinecraftAccount { ClientId = _options.ClientId, Account = session.Account, RefreshToken = tokens.RefreshToken };
            await _store.SaveAsync(stored, cancellationToken).ConfigureAwait(false);
            _stored = stored; _session = session; _loaded = true;
            return session.Account;
        }
        finally { _gate.Release(); }
    }

    /// <summary>会话临近过期时刷新全链；失败明确阻止正版启动，不返回过期或离线会话。</summary>
    public async Task<MinecraftAccountSession> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        _options.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (_stored is null) throw new MinecraftAuthenticationException("请先在“微软账号”页面登录，再使用正版身份启动。");
            if (!string.Equals(_stored.ClientId, _options.ClientId, StringComparison.OrdinalIgnoreCase))
                throw new MinecraftAuthenticationException("EML 的微软应用 ID 已改变，请重新登录。");
            if (_session is not null && _session.ExpiresAt > _clock.GetUtcNow().AddMinutes(2)) return _session;
            var tokens = await _client.RefreshAsync(_stored.RefreshToken, cancellationToken).ConfigureAwait(false);
            // 微软可能轮换 refresh_token；先保存，避免后续 Xbox 网络故障丢失刷新能力。
            var rotated = new StoredMinecraftAccount { ClientId = _stored.ClientId, Account = _stored.Account, RefreshToken = tokens.RefreshToken };
            await _store.SaveAsync(rotated, CancellationToken.None).ConfigureAwait(false);
            _stored = rotated;
            var session = await _client.ExchangeAsync(tokens, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(session.Account.Id, _stored.Account.Id, StringComparison.OrdinalIgnoreCase))
                throw new MinecraftAuthenticationException("刷新后的游戏身份不一致，请重新登录账号。");
            var updated = new StoredMinecraftAccount { ClientId = _stored.ClientId, Account = session.Account, RefreshToken = tokens.RefreshToken };
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _stored = updated; _session = session;
            return session;
        }
        finally { _gate.Release(); }
    }

    /// <summary>先删除磁盘凭证再清空内存，删除失败时不误报已退出；不会撤销浏览器授权。</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
            _stored = null; _session = null; _loaded = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>只在成功读取后标记已加载，读盘错误允许用户重试或退出清除。</summary>
    private async Task LoadCoreAsync(CancellationToken token)
    {
        if (_loaded) return;
        _stored = await _store.LoadAsync(token).ConfigureAwait(false);
        _loaded = true;
    }

    /// <summary>窗口结束所有活动任务后释放 HTTP 和串行锁。</summary>
    public void Dispose() { _ownedHttp?.Dispose(); _gate.Dispose(); _session = null; _stored = null; }
}
