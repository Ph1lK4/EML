using System.Text;
using EML.Core.Minecraft.Accounts;
using EML.Infrastructure.Minecraft.Accounts;
using static EML.Tests.MicrosoftMinecraftAuthTests;

namespace EML.Tests;

/// <summary>验证账号事务、刷新轮换、并发和 DPAPI；所有凭证均为测试生成的假数据。</summary>
public sealed class MinecraftAccountServiceTests
{
    private static readonly MicrosoftLoginOptions Options = new() { ClientId = ClientId };
    private static StoredMinecraftAccount Stored(string refresh = "old-refresh") => new()
    { ClientId = ClientId, Account = new(PlayerId, "OnlineAlex"), RefreshToken = refresh };

    /// <summary>启动读取不调用网络；同一有效会话复用，临近过期时才刷新。</summary>
    [Fact]
    public async Task LoadIsLocalAndRefreshUsesCacheUntilExpiry()
    {
        var clock = new TestClock();
        var store = new FakeStore { Value = Stored() };
        var client = new FakeClient(clock);
        using var service = new MinecraftAccountService(Options, client, store, clock);
        Assert.Equal("OnlineAlex", (await service.LoadAccountAsync())!.Name);
        Assert.Equal(0, client.RefreshCalls);
        var first = await service.GetSessionAsync();
        Assert.Same(first, await service.GetSessionAsync());
        Assert.Equal(1, client.RefreshCalls);
        Assert.Equal("rotated-refresh", store.Value!.RefreshToken);
        clock.Now += TimeSpan.FromHours(2);
        Assert.NotSame(first, await service.GetSessionAsync());
        Assert.Equal(2, client.RefreshCalls);
    }

    /// <summary>刷新阶段断网不能删除原账号；本次正版启动失败，不返回过期会话。</summary>
    [Fact]
    public async Task RefreshFailureKeepsSavedAccount()
    {
        var store = new FakeStore { Value = Stored() };
        var client = new FakeClient(new()) { RefreshFailure = new MinecraftAuthenticationException("网络失败") };
        using var service = new MinecraftAccountService(Options, client, store);
        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => service.GetSessionAsync());
        Assert.Equal("old-refresh", store.Value!.RefreshToken);
        Assert.Equal("OnlineAlex", (await service.LoadAccountAsync())!.Name);
        Assert.Equal(0, store.Deletes);
    }

    /// <summary>Microsoft 已轮换后 Xbox 失败，新的 refresh_token 仍落盘供下次重试。</summary>
    [Fact]
    public async Task RotatedRefreshTokenSurvivesLaterExchangeFailure()
    {
        var store = new FakeStore { Value = Stored() };
        var client = new FakeClient(new()) { ExchangeFailure = new MinecraftAuthenticationException("Xbox 暂时不可用") };
        using var service = new MinecraftAccountService(Options, client, store);
        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => service.GetSessionAsync());
        Assert.Equal("rotated-refresh", store.Value!.RefreshToken);
        Assert.Equal(PlayerId, store.Value.Account.Id);
    }

    /// <summary>更换账号取消或许可检查失败不能替换当前账号的磁盘和内存记录。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledSignInPreservesPreviousAccount(bool canceled)
    {
        var store = new FakeStore { Value = Stored() };
        var client = new FakeClient(new()) { ExchangeFailure = canceled ? new OperationCanceledException() : new MinecraftAuthenticationException("无许可") };
        using var service = new MinecraftAccountService(Options, client, store);
        await service.LoadAccountAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => service.SignInAsync());
        Assert.Equal("old-refresh", store.Value!.RefreshToken);
        Assert.Equal(PlayerId, (await service.LoadAccountAsync())!.Id);
    }

    /// <summary>安全存储失败不报告新账号登录成功，原账号仍可被界面读取。</summary>
    [Fact]
    public async Task FailedSaveDoesNotCommitNewSession()
    {
        var store = new FakeStore { Value = Stored(), FailSave = true };
        var client = new FakeClient(new()) { PlayerName = "NewName" };
        using var service = new MinecraftAccountService(Options, client, store);
        await service.LoadAccountAsync();
        await Assert.ThrowsAsync<IOException>(() => service.SignInAsync());
        Assert.Equal("OnlineAlex", (await service.LoadAccountAsync())!.Name);
    }

    /// <summary>并发启动共用一次刷新，不能同时轮换同一刷新凭证。</summary>
    [Fact]
    public async Task ConcurrentSessionRequestsRefreshOnce()
    {
        var store = new FakeStore { Value = Stored() };
        var client = new FakeClient(new());
        using var service = new MinecraftAccountService(Options, client, store);
        var sessions = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => service.GetSessionAsync())));
        Assert.Equal(1, client.RefreshCalls);
        Assert.All(sessions, session => Assert.Same(sessions[0], session));
    }

    /// <summary>退出删除本机凭证，之后请求正版会话明确失败。</summary>
    [Fact]
    public async Task LogoutClearsDiskAndCachedSession()
    {
        var store = new FakeStore { Value = Stored() };
        using var service = new MinecraftAccountService(Options, new FakeClient(new()), store);
        await service.GetSessionAsync();
        await service.SignOutAsync();
        Assert.Null(await service.LoadAccountAsync());
        Assert.Null(store.Value);
        Assert.Equal(1, store.Deletes);
        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => service.GetSessionAsync());
    }

    /// <summary>应用 ID 改变后不能用其他应用的刷新凭证发送网络请求。</summary>
    [Fact]
    public async Task ClientIdChangeRequiresExplicitLogin()
    {
        var store = new FakeStore { Value = Stored() };
        var client = new FakeClient(new());
        using var service = new MinecraftAccountService(new() { ClientId = Guid.NewGuid().ToString() }, client, store);
        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => service.GetSessionAsync());
        Assert.Equal(0, client.RefreshCalls);
    }

    /// <summary>刷新后不同 UUID 被拒绝，同 UUID 的改名可以正常更新。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAcceptsRenameButNotDifferentIdentity(bool changedId)
    {
        var store = new FakeStore { Value = Stored() };
        var client = new FakeClient(new()) { PlayerName = "Renamed", Id = changedId ? Guid.NewGuid().ToString("N") : PlayerId };
        using var service = new MinecraftAccountService(Options, client, store);
        if (changedId) await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => service.GetSessionAsync());
        else Assert.Equal("Renamed", (await service.GetSessionAsync()).Account.Name);
        Assert.Equal(PlayerId, store.Value!.Account.Id);
    }

    /// <summary>真实 Windows 加密往返不在文件中暴露 refresh_token；取消保存不破坏原文件。</summary>
    [Fact]
    public async Task DpapiRoundTripEncryptsAndPreservesOnCancellation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "EML-account-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "test.dpapi");
        try
        {
            var store = new WindowsMinecraftAccountStore(path);
            Assert.Null(await store.LoadAsync(default));
            var secret = "test-refresh-" + Guid.NewGuid();
            await store.SaveAsync(Stored(secret), default);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(bytes));
            Assert.Equal(secret, (await store.LoadAsync(default))!.RefreshToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Stored("replacement"), new(true)));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            await store.DeleteAsync(default);
            Assert.Null(await store.LoadAsync(default));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>损坏的 DPAPI 密文不会被当作未登录或自动删除。</summary>
    [Fact]
    public async Task CorruptedDpapiFileIsReportedAndPreserved()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "EML-account-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.dpapi");
        try
        {
            await File.WriteAllTextAsync(path, "corrupted");
            await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => new WindowsMinecraftAccountStore(path).LoadAsync(default));
            Assert.Equal("corrupted", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FakeStore : IMinecraftAccountStore
    {
        internal StoredMinecraftAccount? Value;
        internal bool FailSave;
        internal int Deletes;
        public Task<StoredMinecraftAccount?> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(StoredMinecraftAccount account, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (FailSave) throw new IOException("test disk failure"); Value = account; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken token) { Deletes++; Value = null; return Task.CompletedTask; }
    }
    private sealed class FakeClient(TestClock clock) : IMicrosoftMinecraftAuthClient
    {
        internal int RefreshCalls;
        internal Exception? RefreshFailure;
        internal Exception? ExchangeFailure;
        internal string PlayerName = "OnlineAlex";
        internal string Id = PlayerId;
        public Task<MicrosoftTokens> SignInAsync(IProgress<MinecraftLoginPrompt>? progress, CancellationToken token) => Task.FromResult(new MicrosoftTokens("msa", "new-refresh"));
        public Task<MicrosoftTokens> RefreshAsync(string refreshToken, CancellationToken token)
        { RefreshCalls++; return RefreshFailure is null ? Task.FromResult(new MicrosoftTokens("msa", "rotated-refresh")) : Task.FromException<MicrosoftTokens>(RefreshFailure); }
        public Task<MinecraftAccountSession> ExchangeAsync(MicrosoftTokens tokens, CancellationToken token) => ExchangeFailure is null
            ? Task.FromResult(new MinecraftAccountSession(new(Id, PlayerName), "minecraft-token", clock.Now.AddHours(1), ClientId, "123"))
            : Task.FromException<MinecraftAccountSession>(ExchangeFailure);
    }
}
