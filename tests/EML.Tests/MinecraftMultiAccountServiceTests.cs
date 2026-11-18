using EML.Core.Minecraft.Accounts;
using EML.Infrastructure.Minecraft.Accounts;
using static EML.Tests.MicrosoftMinecraftAuthTests;

namespace EML.Tests;

/// <summary>多账号事务与会话隔离使用内存凭据，不访问用户账号或认证网络。</summary>
public sealed class MinecraftMultiAccountServiceTests
{
    private const string FirstId = "11111111222233334444555555555555";
    private const string SecondId = "aaaaaaaa222233334444555555555555";
    private const string MissingId = "bbbbbbbb222233334444555555555555";
    private static readonly MicrosoftLoginOptions Options = new() { ClientId = ClientId };

    /// <summary>账号列表只读取公开档案，初始化不能刷新或登录任何账号。</summary>
    [Fact]
    public async Task AccountListLoadsLocallyWithoutAuthenticating()
    {
        var store = new MemoryStore { Value = Both(SecondId) };
        var client = new RoutedClient(new());
        using var service = new MinecraftAccountService(Options, client, store);

        var list = await service.LoadAccountsAsync();

        Assert.Equal(SecondId, list.ActiveAccountId);
        Assert.Equal(new[] { FirstId, SecondId }.Order(), list.Accounts.Select(account => account.Id).Order());
        Assert.Equal("SecondPlayer", (await service.LoadAccountAsync())!.Name);
        Assert.Empty(client.Refreshed);
        Assert.Equal(0, client.SignIns);
        Assert.Equal(0, store.Saves);
    }

    /// <summary>新增第二个正版账号保留第一个，重复授权相同 UUID 仅更新那一项。</summary>
    [Fact]
    public async Task AddingAndReauthorizingAccountDoesNotReplaceOthers()
    {
        var store = new MemoryStore { Value = Collection(FirstId, Entry(FirstId, "FirstPlayer")) };
        var client = new RoutedClient(new()) { SignInId = SecondId, SignInName = "SecondPlayer" };
        using var service = new MinecraftAccountService(Options, client, store);

        await service.SignInAsync();
        client.SignInName = "SecondRenamed";
        await service.SignInAsync();
        var list = await service.LoadAccountsAsync();

        Assert.Equal(2, list.Accounts.Count);
        Assert.Equal(SecondId, list.ActiveAccountId);
        Assert.Equal("SecondRenamed", Assert.Single(list.Accounts, account => account.Id == SecondId).Name);
        Assert.Equal("refresh-" + FirstId, Assert.Single(store.Value!.Accounts, account => account.Account.Id == FirstId).RefreshToken);
        Assert.Equal("FirstPlayer", Assert.Single(list.Accounts, account => account.Id == FirstId).Name);
    }

    /// <summary>新账号授权失败或取消时，原账号集合及默认选择保持不变。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledAdditionPreservesCollection(bool cancel)
    {
        var original = Both(FirstId);
        var store = new MemoryStore { Value = original };
        var client = new RoutedClient(new())
        {
            SignInId = MissingId,
            ExchangeFailure = cancel ? new OperationCanceledException() : new MinecraftAuthenticationException("模拟许可失败")
        };
        using var service = new MinecraftAccountService(Options, client, store);

        await Assert.ThrowsAnyAsync<Exception>(() => service.SignInAsync());

        Assert.Same(original, store.Value);
        Assert.Equal(FirstId, (await service.LoadAccountsAsync()).ActiveAccountId);
        Assert.Equal(0, store.Saves);
    }

    /// <summary>新增账号加密保存失败时，不发布未经持久化的新账号或会话。</summary>
    [Fact]
    public async Task FailedAdditionSaveKeepsOriginalAccountsAndDefault()
    {
        var original = Collection(FirstId, Entry(FirstId, "FirstPlayer"));
        var store = new MemoryStore { Value = original, FailSave = true };
        var client = new RoutedClient(new()) { SignInId = SecondId };
        using var service = new MinecraftAccountService(Options, client, store);

        await Assert.ThrowsAsync<IOException>(() => service.SignInAsync());

        Assert.Same(original, store.Value);
        Assert.Equal(FirstId, (await service.LoadAccountsAsync()).ActiveAccountId);
        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => service.GetSessionForAccountAsync(SecondId));
    }

    /// <summary>显式默认账号切换只改选择，随后启动才按需要刷新所选身份。</summary>
    [Fact]
    public async Task SelectingDefaultDoesNotPerformAuthentication()
    {
        var store = new MemoryStore { Value = Both(FirstId) };
        var client = new RoutedClient(new());
        using var service = new MinecraftAccountService(Options, client, store);

        var selected = await service.SelectAccountAsync(SecondId);

        Assert.Equal(SecondId, selected.Id);
        Assert.Equal(SecondId, store.Value!.ActiveAccountId);
        Assert.Equal(SecondId, (await service.LoadAccountAsync())!.Id);
        Assert.Empty(client.Refreshed);
        Assert.Equal(0, client.SignIns);
        Assert.Equal(2, store.Value.Accounts.Count);
    }

    /// <summary>选择不存在的账号必须明确拒绝，不能把任意输入变成默认身份。</summary>
    [Fact]
    public async Task MissingAccountCannotBecomeDefault()
    {
        var original = Both(FirstId);
        var store = new MemoryStore { Value = original };
        using var service = new MinecraftAccountService(Options, new RoutedClient(new()), store);

        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => service.SelectAccountAsync(MissingId));

        Assert.Same(original, store.Value);
        Assert.Equal(FirstId, (await service.LoadAccountAsync())!.Id);
    }

    /// <summary>默认选择保存失败不修改内存中的当前账号。</summary>
    [Fact]
    public async Task FailedSelectionSaveKeepsCurrentAccount()
    {
        var store = new MemoryStore { Value = Both(FirstId), FailSave = true };
        using var service = new MinecraftAccountService(Options, new RoutedClient(new()), store);

        await Assert.ThrowsAsync<IOException>(() => service.SelectAccountAsync(SecondId));

        Assert.Equal(FirstId, (await service.LoadAccountAsync())!.Id);
        Assert.Equal(FirstId, store.Value!.ActiveAccountId);
    }

    /// <summary>实例指定 A 时直接取得 A 的会话，账号页默认 B 不会被偷偷更改。</summary>
    [Fact]
    public async Task TargetedSessionUsesRememberedAccountWithoutChangingDefault()
    {
        var store = new MemoryStore { Value = Both(SecondId) };
        var client = new RoutedClient(new());
        using var service = new MinecraftAccountService(Options, client, store);

        var session = await service.GetSessionForAccountAsync(FirstId);

        Assert.Equal(FirstId, session.Account.Id);
        Assert.Equal([FirstId], client.Refreshed);
        Assert.Equal(SecondId, (await service.LoadAccountsAsync()).ActiveAccountId);
        Assert.Equal(SecondId, store.Value!.ActiveAccountId);
        Assert.Equal("refresh-" + SecondId, Assert.Single(store.Value.Accounts, entry => entry.Account.Id == SecondId).RefreshToken);
    }

    /// <summary>多个账号分别缓存会话，不会把 B 的访问令牌用于 A。</summary>
    [Fact]
    public async Task CachedSessionsAreIsolatedByUuid()
    {
        var store = new MemoryStore { Value = Both(FirstId) };
        var client = new RoutedClient(new());
        using var service = new MinecraftAccountService(Options, client, store);

        var first = await service.GetSessionForAccountAsync(FirstId);
        var second = await service.GetSessionForAccountAsync(SecondId);

        Assert.NotSame(first, second);
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        Assert.Same(first, await service.GetSessionForAccountAsync(FirstId));
        Assert.Same(second, await service.GetSessionForAccountAsync(SecondId));
        Assert.Equal(2, client.Refreshed.Count);
    }

    /// <summary>一个账号微软刷新已轮换但后续交换失败时，只保存该账号的新刷新凭据。</summary>
    [Fact]
    public async Task RotatedTokenSurvivesExchangeFailureWithoutChangingOtherAccount()
    {
        var store = new MemoryStore { Value = Both(SecondId) };
        var client = new RoutedClient(new()) { ExchangeFailure = new MinecraftAuthenticationException("模拟 Xbox 断网") };
        using var service = new MinecraftAccountService(Options, client, store);

        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => service.GetSessionForAccountAsync(FirstId));

        Assert.Equal("rotated-" + FirstId, Assert.Single(store.Value!.Accounts, entry => entry.Account.Id == FirstId).RefreshToken);
        Assert.Equal("refresh-" + SecondId, Assert.Single(store.Value.Accounts, entry => entry.Account.Id == SecondId).RefreshToken);
        Assert.Equal(SecondId, store.Value.ActiveAccountId);
        Assert.Equal(2, (await service.LoadAccountsAsync()).Accounts.Count);
    }

    /// <summary>移除非默认账号会使它的缓存不可再用，但保留其他账号的默认选择和缓存。</summary>
    [Fact]
    public async Task RemovingInactiveAccountDoesNotInvalidateOtherSession()
    {
        var store = new MemoryStore { Value = Both(SecondId) };
        var client = new RoutedClient(new());
        using var service = new MinecraftAccountService(Options, client, store);
        _ = await service.GetSessionForAccountAsync(FirstId);
        var second = await service.GetSessionForAccountAsync(SecondId);

        await service.RemoveAccountAsync(FirstId);

        Assert.Equal(SecondId, (await service.LoadAccountsAsync()).ActiveAccountId);
        Assert.Equal(SecondId, Assert.Single(store.Value!.Accounts).Account.Id);
        Assert.Same(second, await service.GetSessionForAccountAsync(SecondId));
        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => service.GetSessionForAccountAsync(FirstId));
        Assert.Equal(2, client.Refreshed.Count);
    }

    /// <summary>移除默认账号不自动选另一个 UUID，其他账号仍可被实例明确选用。</summary>
    [Fact]
    public async Task RemovingActiveAccountLeavesNoImplicitReplacement()
    {
        var store = new MemoryStore { Value = Both(FirstId) };
        using var service = new MinecraftAccountService(Options, new RoutedClient(new()), store);

        await service.RemoveAccountAsync(FirstId);

        var remaining = await service.LoadAccountsAsync();
        Assert.Null(remaining.ActiveAccountId);
        Assert.Equal(SecondId, Assert.Single(remaining.Accounts).Id);
        Assert.Null(await service.LoadAccountAsync());
        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => service.GetSessionAsync());
        Assert.Equal(SecondId, (await service.GetSessionForAccountAsync(SecondId)).Account.Id);
    }

    /// <summary>删除账号集合保存失败时，原账号和有效会话保持可用。</summary>
    [Fact]
    public async Task FailedRemovalDoesNotForgetAccountOrSession()
    {
        var store = new MemoryStore { Value = Both(FirstId) };
        using var service = new MinecraftAccountService(Options, new RoutedClient(new()), store);
        var first = await service.GetSessionForAccountAsync(FirstId);
        store.FailSave = true;

        await Assert.ThrowsAsync<IOException>(() => service.RemoveAccountAsync(FirstId));

        Assert.Equal(2, (await service.LoadAccountsAsync()).Accounts.Count);
        Assert.Same(first, await service.GetSessionForAccountAsync(FirstId));
        Assert.Equal(FirstId, (await service.LoadAccountAsync())!.Id);
    }

    /// <summary>实例记住的账号被移除后，不回退到账号页当前身份。</summary>
    [Fact]
    public async Task MissingTargetNeverFallsBackToCurrentAccount()
    {
        var store = new MemoryStore { Value = Both(SecondId) };
        var client = new RoutedClient(new());
        using var service = new MinecraftAccountService(Options, client, store);

        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => service.GetSessionForAccountAsync(MissingId));

        Assert.Empty(client.Refreshed);
        Assert.Equal(SecondId, (await service.LoadAccountAsync())!.Id);
    }

    /// <summary>并发请求两种身份各自只刷新一次，仍返回各自相同的缓存会话。</summary>
    [Fact]
    public async Task ConcurrentTargetedRequestsShareOnlyMatchingCache()
    {
        var store = new MemoryStore { Value = Both(FirstId) };
        var client = new RoutedClient(new());
        using var service = new MinecraftAccountService(Options, client, store);

        var requests = Enumerable.Range(0, 12).Select(index => service.GetSessionForAccountAsync(index % 2 == 0 ? FirstId : SecondId));
        var sessions = await Task.WhenAll(requests);

        Assert.Equal(2, client.Refreshed.Count);
        for (var index = 0; index < sessions.Length; index++)
            Assert.Same(sessions[index % 2], sessions[index]);
    }

    /// <summary>应用 ID 不符只影响该账号，不能因此借用另一个账号的刷新令牌。</summary>
    [Fact]
    public async Task ClientIdMismatchIsScopedToRequestedAccount()
    {
        var store = new MemoryStore
        {
            Value = Collection(SecondId,
                new() { ClientId = "cccccccc-2222-3333-4444-555555555555", Account = new(FirstId, "FirstPlayer"), RefreshToken = "refresh-" + FirstId },
                Entry(SecondId, "SecondPlayer"))
        };
        var client = new RoutedClient(new());
        using var service = new MinecraftAccountService(Options, client, store);

        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => service.GetSessionForAccountAsync(FirstId));
        Assert.Equal(SecondId, (await service.GetSessionForAccountAsync(SecondId)).Account.Id);

        Assert.Equal([SecondId], client.Refreshed);
    }

    /// <summary>兼容退出入口只移除默认账号，不清除账号集合里的其他登录。</summary>
    [Fact]
    public async Task LegacySignOutOnlyRemovesActiveAccount()
    {
        var store = new MemoryStore { Value = Both(FirstId) };
        using var service = new MinecraftAccountService(Options, new RoutedClient(new()), store);

        await service.SignOutAsync();
        await service.SignOutAsync();

        var remaining = await service.LoadAccountsAsync();
        Assert.Null(remaining.ActiveAccountId);
        Assert.Equal(SecondId, Assert.Single(remaining.Accounts).Id);
        Assert.Equal(0, store.Deletes);
    }

    /// <summary>损坏凭据在添加账号前被报告，不能通过新登录覆盖无法读取的旧集合。</summary>
    [Fact]
    public async Task CorruptVaultIsNotReplacedBySignInOrSignOut()
    {
        var store = new MemoryStore { LoadFailure = new MinecraftAuthenticationException("模拟损坏") };
        var client = new RoutedClient(new());
        using var service = new MinecraftAccountService(Options, client, store);

        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => service.SignInAsync());
        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => service.SignOutAsync());

        Assert.Equal(0, client.SignIns);
        Assert.Equal(0, store.Saves);
        Assert.Equal(0, store.Deletes);
    }

    /// <summary>构造固定的两个虚构账号及指定默认选择。</summary>
    private static StoredMinecraftAccounts Both(string? active) => Collection(active, Entry(FirstId, "FirstPlayer"), Entry(SecondId, "SecondPlayer"));

    /// <summary>构造加密存储使用的内存集合，不包含真实认证数据。</summary>
    private static StoredMinecraftAccounts Collection(string? active, params StoredMinecraftAccount[] entries) =>
        new() { ActiveAccountId = active, Accounts = entries };

    /// <summary>每个虚构刷新令牌可反推出预期 UUID，便于测试发现错用凭据。</summary>
    private static StoredMinecraftAccount Entry(string id, string name) =>
        new() { ClientId = ClientId, Account = new(id, name), RefreshToken = "refresh-" + id };

    /// <summary>内存存储在异常时不提交数据，模拟加密文件的原子替换边界。</summary>
    private sealed class MemoryStore : IMinecraftAccountStore
    {
        public StoredMinecraftAccounts? Value { get; set; }
        public bool FailSave { get; set; }
        public Exception? LoadFailure { get; set; }
        public int Saves { get; private set; }
        public int Deletes { get; private set; }

        /// <summary>返回已保存集合或模拟损坏读取。</summary>
        public Task<StoredMinecraftAccounts?> LoadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return LoadFailure is null ? Task.FromResult(Value) : Task.FromException<StoredMinecraftAccounts?>(LoadFailure);
        }

        /// <summary>只有成功保存才更新集合引用。</summary>
        public Task SaveAsync(StoredMinecraftAccounts accounts, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (FailSave) throw new IOException("模拟磁盘写入失败");
            Value = accounts;
            Saves++;
            return Task.CompletedTask;
        }

        /// <summary>记录整个凭据文件被删除的次数。</summary>
        public Task DeleteAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Deletes++;
            Value = null;
            return Task.CompletedTask;
        }
    }

    /// <summary>按刷新凭据路由虚构身份，允许检测跨账号令牌误用。</summary>
    private sealed class RoutedClient(TestClock clock) : IMicrosoftMinecraftAuthClient
    {
        public string SignInId { get; set; } = FirstId;
        public string SignInName { get; set; } = "FirstPlayer";
        public Exception? ExchangeFailure { get; set; }
        public List<string> Refreshed { get; } = [];
        public int SignIns { get; private set; }
        private readonly Dictionary<string, string> _names = new() { [FirstId] = "FirstPlayer", [SecondId] = "SecondPlayer" };

        /// <summary>模拟用户明确添加账号，刷新令牌和访问令牌都是测试字符串。</summary>
        public Task<MicrosoftTokens> SignInAsync(IProgress<MinecraftLoginPrompt>? progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SignIns++;
            _names[SignInId] = SignInName;
            return Task.FromResult(new MicrosoftTokens(SignInId, "signed-" + SignInId));
        }

        /// <summary>只按传入刷新令牌确定身份，而不是按当前默认账号返回结果。</summary>
        public Task<MicrosoftTokens> RefreshAsync(string refreshToken, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var id = refreshToken[(refreshToken.LastIndexOf('-') + 1)..];
            Refreshed.Add(id);
            return Task.FromResult(new MicrosoftTokens(id, "rotated-" + id));
        }

        /// <summary>交换同一虚构身份的会话，或模拟后续 Xbox/Minecraft 失败。</summary>
        public Task<MinecraftAccountSession> ExchangeAsync(MicrosoftTokens tokens, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (ExchangeFailure is { } failure) return Task.FromException<MinecraftAccountSession>(failure);
            var id = tokens.AccessToken;
            return Task.FromResult(new MinecraftAccountSession(new(id, _names.GetValueOrDefault(id, "OtherPlayer")),
                "game-access-" + id, clock.GetUtcNow().AddHours(1), ClientId, "123"));
        }
    }
}
