using EML.Core.Minecraft.Accounts;

namespace EML.Infrastructure.Minecraft.Accounts;

/// <summary>串行管理多个正版账号和各自会话；实例指定 UUID，不借用另一个账号或降级为离线。</summary>
public sealed class MinecraftAccountService : IMinecraftAccountManager, IDisposable
{
    private readonly MicrosoftLoginOptions _options;
    private readonly IMinecraftAccountStore _store;
    private readonly IMicrosoftMinecraftAuthClient _client;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient? _ownedHttp;
    private readonly Dictionary<string, MinecraftAccountSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private StoredMinecraftAccounts _stored = new();
    public bool IsConfigured => _options.IsConfigured;

    /// <summary>固定官方认证端点；沿用旧凭证位置，由存储层安全读取并逐步迁移单账号格式。</summary>
    public MinecraftAccountService(MicrosoftLoginOptions options)
    {
        _options = options;
        _clock = TimeProvider.System;
        _ownedHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        _client = new MicrosoftMinecraftAuthClient(_ownedHttp, options);
        _store = new WindowsMinecraftAccountStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EML", "accounts", "microsoft.dpapi"));
    }

    /// <summary>测试注入认证接口、加密集合和时钟，不需要真实账号或网络。</summary>
    internal MinecraftAccountService(MicrosoftLoginOptions options, IMicrosoftMinecraftAuthClient client,
        IMinecraftAccountStore store, TimeProvider? clock = null)
    { _options = options; _client = client; _store = store; _clock = clock ?? TimeProvider.System; }

    /// <summary>兼容原入口，仅返回默认账号；读取公开档案不会联网刷新。</summary>
    public async Task<MinecraftAccount?> LoadAccountAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadAccountsAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Accounts.FirstOrDefault(account => SameId(account.Id, snapshot.ActiveAccountId));
    }

    /// <summary>一次读取完整公开列表与默认选择，不把刷新令牌或缓存会话交给界面。</summary>
    public async Task<MinecraftAccountCollection> LoadAccountsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return new(_stored.Accounts.Select(account => account.Account).ToArray(), _stored.ActiveAccountId);
        }
        finally { _gate.Release(); }
    }

    /// <summary>授权和落盘全部成功后才添加或更新该 UUID；重复登录只更新这一项，不清除其他账号。</summary>
    public async Task<MinecraftAccount> SignInAsync(IProgress<MinecraftLoginPrompt>? progress = null, CancellationToken cancellationToken = default)
    {
        _options.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var tokens = await _client.SignInAsync(progress, cancellationToken).ConfigureAwait(false);
            var session = await _client.ExchangeAsync(tokens, cancellationToken).ConfigureAwait(false);
            var account = new StoredMinecraftAccount { ClientId = _options.ClientId, Account = session.Account, RefreshToken = tokens.RefreshToken };
            var updated = Upsert(account, session.Account.Id);
            if (updated.Accounts.Count > WindowsMinecraftAccountStore.MaximumAccounts)
                throw new MinecraftAuthenticationException($"最多保存 {WindowsMinecraftAccountStore.MaximumAccounts} 个正版账号，请先移除不再使用的本机账号。");
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            _sessions[account.Account.Id] = session;
            return session.Account;
        }
        finally { _gate.Release(); }
    }

    /// <summary>更改未指定账号时的默认选择；不刷新令牌，也不修改任何实例记住的账号。</summary>
    public async Task<MinecraftAccount> SelectAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var id = NormalizeId(accountId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var account = FindAccount(id);
            if (!SameId(_stored.ActiveAccountId, account.Account.Id))
                await SaveCoreAsync(new() { Accounts = _stored.Accounts, ActiveAccountId = account.Account.Id }, cancellationToken).ConfigureAwait(false);
            return account.Account;
        }
        finally { _gate.Release(); }
    }

    /// <summary>兼容默认账号的启动入口；未设置默认身份时明确要求选择，不取列表第一项。</summary>
    public async Task<MinecraftAccountSession> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        _options.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (_stored.ActiveAccountId is not { } id)
                throw new MinecraftAuthenticationException("请先在“微软账号”页面登录并选择默认账号，或在实例中选择正版账号。");
            return await GetSessionCoreAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>按实例明确选择的 UUID 获取会话，既不更改默认账号，也不借用其他账号凭证。</summary>
    public async Task<MinecraftAccountSession> GetSessionForAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        _options.Validate();
        var id = NormalizeId(accountId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return await GetSessionCoreAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>仅刷新选中账号；先保存微软轮换后的刷新令牌，后续 Xbox 失败仍能重试。</summary>
    private async Task<MinecraftAccountSession> GetSessionCoreAsync(string id, CancellationToken token)
    {
        var account = FindAccount(id);
        if (!string.Equals(account.ClientId, _options.ClientId, StringComparison.OrdinalIgnoreCase))
            throw new MinecraftAuthenticationException("EML 的微软应用 ID 已改变，请重新登录此账号。");
        if (_sessions.TryGetValue(account.Account.Id, out var cached) && cached.ExpiresAt > _clock.GetUtcNow().AddMinutes(2)) return cached;
        var tokens = await _client.RefreshAsync(account.RefreshToken, token).ConfigureAwait(false);
        var rotated = new StoredMinecraftAccount { ClientId = account.ClientId, Account = account.Account, RefreshToken = tokens.RefreshToken };
        await SaveCoreAsync(Upsert(rotated, _stored.ActiveAccountId), CancellationToken.None).ConfigureAwait(false);
        var session = await _client.ExchangeAsync(tokens, token).ConfigureAwait(false);
        if (!SameId(session.Account.Id, account.Account.Id))
            throw new MinecraftAuthenticationException("刷新后的游戏身份不一致，请重新登录此账号。");
        var updated = new StoredMinecraftAccount { ClientId = account.ClientId, Account = session.Account, RefreshToken = tokens.RefreshToken };
        await SaveCoreAsync(Upsert(updated, _stored.ActiveAccountId), token).ConfigureAwait(false);
        _sessions[updated.Account.Id] = session;
        return session;
    }

    /// <summary>移除指定账号的本机凭证和缓存；删除默认账号时保持未选择，避免自动换成别人。</summary>
    public async Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var id = NormalizeId(accountId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            await RemoveCoreAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>旧退出入口只移除当前默认账号；凭证集合损坏时保留原文件，绝不借此清空其他账号。</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (_stored.ActiveAccountId is { } id) await RemoveCoreAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>先提交磁盘变化再清理内存；移除失败时所有账号仍保持原来的可见状态。</summary>
    private async Task RemoveCoreAsync(string id, CancellationToken token)
    {
        var account = FindAccount(id);
        var remaining = _stored.Accounts.Where(value => !SameId(value.Account.Id, account.Account.Id)).ToArray();
        var updated = new StoredMinecraftAccounts
        {
            Accounts = remaining,
            ActiveAccountId = SameId(_stored.ActiveAccountId, account.Account.Id) ? null : _stored.ActiveAccountId
        };
        if (remaining.Length == 0)
        {
            await _store.DeleteAsync(token).ConfigureAwait(false);
            _stored = updated;
        }
        else await SaveCoreAsync(updated, token).ConfigureAwait(false);
        _sessions.Remove(account.Account.Id);
    }

    /// <summary>保持列表顺序，以 UUID 原位更新或添加；调用方明确提供新的默认账号。</summary>
    private StoredMinecraftAccounts Upsert(StoredMinecraftAccount account, string? activeId)
    {
        var values = _stored.Accounts.ToList();
        var index = values.FindIndex(value => SameId(value.Account.Id, account.Account.Id));
        if (index >= 0) values[index] = account;
        else values.Add(account);
        return new() { Accounts = values.ToArray(), ActiveAccountId = activeId };
    }

    /// <summary>对磁盘及测试存储使用同一集合验证，保存失败不能发布新的内存状态。</summary>
    private async Task SaveCoreAsync(StoredMinecraftAccounts updated, CancellationToken token)
    {
        WindowsMinecraftAccountStore.Validate(updated);
        await _store.SaveAsync(updated, token).ConfigureAwait(false);
        _stored = updated;
    }

    /// <summary>只在成功读取和验证后标记已加载；损坏文件可重试，不能被一次新登录覆盖。</summary>
    private async Task LoadCoreAsync(CancellationToken token)
    {
        if (_loaded) return;
        var stored = await _store.LoadAsync(token).ConfigureAwait(false) ?? new();
        WindowsMinecraftAccountStore.Validate(stored);
        _stored = stored;
        _loaded = true;
    }

    /// <summary>已移除或不存在的身份明确失败，不回退为默认账号或列表中的另一项。</summary>
    private StoredMinecraftAccount FindAccount(string id) => _stored.Accounts.FirstOrDefault(account => SameId(account.Account.Id, id))
        ?? throw new MinecraftAuthenticationException("所选正版账号已移除或尚未登录，请在“微软账号”页面重新添加此账号，或明确选择其他账号。");

    /// <summary>外部选择只接受常见 UUID 格式，避免任意字符串成为凭证索引。</summary>
    private static string NormalizeId(string id)
    {
        if (id is not null && (id.Length == 32 && Guid.TryParseExact(id, "N", out var value) ||
            id.Length == 36 && Guid.TryParseExact(id, "D", out value)) && value != Guid.Empty) return value.ToString("N");
        throw new MinecraftAuthenticationException("所选正版账号 UUID 无效，请重新选择。");
    }

    /// <summary>已验证集合中 UUID 均为 N 格式，比较时忽略十六进制大小写。</summary>
    private static bool SameId(string? first, string? second) => first is not null && second is not null &&
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    /// <summary>窗口结束所有活动任务后释放网络与串行锁，清除内存中的会话引用。</summary>
    public void Dispose() { _ownedHttp?.Dispose(); _gate.Dispose(); _sessions.Clear(); _stored = new(); }
}
