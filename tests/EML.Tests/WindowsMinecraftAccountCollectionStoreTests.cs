using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Minecraft.Accounts;
using EML.Infrastructure.Minecraft.Accounts;
using static EML.Tests.MicrosoftMinecraftAuthTests;

namespace EML.Tests;

/// <summary>在随机临时目录验证旧凭据迁移和多账号 DPAPI，不读取任何用户账号文件。</summary>
public sealed class WindowsMinecraftAccountCollectionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EML-multi-account-store-" + Guid.NewGuid().ToString("N"));
    private const string FirstId = "11111111222233334444555555555555";
    private const string SecondId = "aaaaaaaa222233334444555555555555";
    private string PathToStore => Path.Combine(_directory, "fake-accounts.dpapi");

    /// <summary>读取旧单账号仅在内存升级模型，原密文不变；后续保存才写入完整新版集合。</summary>
    [Fact]
    public async Task LegacyEncryptedAccountMigratesWithoutRewritingOnRead()
    {
        if (!OperatingSystem.IsWindows()) return;
        var first = Entry(FirstId, "FirstPlayer");
        var original = await WriteFixtureAsync(first);
        var store = new WindowsMinecraftAccountStore(PathToStore);

        var loaded = await store.LoadAsync(default);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.SchemaVersion);
        Assert.Equal(FirstId, loaded.ActiveAccountId);
        Assert.Equal(first.RefreshToken, Assert.Single(loaded.Accounts).RefreshToken);
        Assert.Equal(original, await File.ReadAllBytesAsync(PathToStore));
        await store.SaveAsync(new()
        {
            Accounts = [.. loaded.Accounts, Entry(SecondId, "SecondPlayer")],
            ActiveAccountId = SecondId
        }, default);
        var reopened = await new WindowsMinecraftAccountStore(PathToStore).LoadAsync(default);
        Assert.Equal(2, reopened!.Accounts.Count);
        Assert.Equal(SecondId, reopened.ActiveAccountId);
        Assert.Equal(first.RefreshToken, Assert.Single(reopened.Accounts, account => account.Account.Id == FirstId).RefreshToken);
    }

    /// <summary>新版集合整体加密，磁盘中不出现任何账号刷新令牌或短期访问令牌字段。</summary>
    [Fact]
    public async Task CollectionRoundTripEncryptsEveryRefreshToken()
    {
        if (!OperatingSystem.IsWindows()) return;
        var first = Entry(FirstId, "FirstPlayer");
        var second = Entry(SecondId, "SecondPlayer");
        var store = new WindowsMinecraftAccountStore(PathToStore);

        await store.SaveAsync(new() { Accounts = [first, second], ActiveAccountId = FirstId }, default);

        var bytes = await File.ReadAllBytesAsync(PathToStore);
        var ciphertextText = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(first.RefreshToken, ciphertextText);
        Assert.DoesNotContain(second.RefreshToken, ciphertextText);
        Assert.DoesNotContain("RefreshToken", ciphertextText);
        var loaded = await store.LoadAsync(default);
        Assert.Equal(FirstId, loaded!.ActiveAccountId);
        Assert.Equal(first.RefreshToken, Assert.Single(loaded.Accounts, entry => entry.Account.Id == FirstId).RefreshToken);
        Assert.Equal(second.RefreshToken, Assert.Single(loaded.Accounts, entry => entry.Account.Id == SecondId).RefreshToken);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    /// <summary>无默认账号的非空集合是合法状态，不能在读取时自动选中第一项。</summary>
    [Fact]
    public async Task CollectionWithoutActiveAccountRemainsUnselected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsMinecraftAccountStore(PathToStore);

        await store.SaveAsync(new() { Accounts = [Entry(FirstId, "FirstPlayer")], ActiveAccountId = null }, default);
        var loaded = await store.LoadAsync(default);

        Assert.Single(loaded!.Accounts);
        Assert.Null(loaded.ActiveAccountId);
    }

    /// <summary>取消新版保存不破坏旧单账号文件，也不留下临时明文或密文残片。</summary>
    [Fact]
    public async Task CanceledUpgradeKeepsLegacyBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var original = await WriteFixtureAsync(Entry(FirstId, "FirstPlayer"));
        var store = new WindowsMinecraftAccountStore(PathToStore);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new()
        {
            Accounts = [Entry(FirstId, "FirstPlayer"), Entry(SecondId, "SecondPlayer")], ActiveAccountId = SecondId
        }, new(true)));

        Assert.Equal(original, await File.ReadAllBytesAsync(PathToStore));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
        Assert.Equal(FirstId, (await store.LoadAsync(default))!.ActiveAccountId);
    }

    /// <summary>错误集合必须在保存前被拒绝，尤其不能让重复 UUID 或悬空默认选择覆盖旧凭据。</summary>
    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown-active")]
    [InlineData("too-many")]
    [InlineData("invalid-id")]
    [InlineData("long-token")]
    [InlineData("wrong-schema")]
    [InlineData("null-accounts")]
    public async Task InvalidCollectionCannotOverwriteExistingVault(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var original = await WriteFixtureAsync(Entry(FirstId, "FirstPlayer"));
        var store = new WindowsMinecraftAccountStore(PathToStore);

        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => store.SaveAsync(Invalid(scenario), default));

        Assert.Equal(original, await File.ReadAllBytesAsync(PathToStore));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    /// <summary>可解密但内容不合法的集合仍保留原文件，不能返回空集合来诱发覆盖。</summary>
    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown-active")]
    [InlineData("too-many")]
    [InlineData("invalid-id")]
    [InlineData("long-token")]
    [InlineData("wrong-schema")]
    [InlineData("null-accounts")]
    public async Task InvalidDecryptedCollectionIsReportedAndPreserved(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var original = await WriteFixtureAsync(Invalid(scenario));
        var store = new WindowsMinecraftAccountStore(PathToStore);

        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => store.LoadAsync(default));

        Assert.Equal(original, await File.ReadAllBytesAsync(PathToStore));
    }

    /// <summary>密文大小在解密前受限，避免超大文件被无限读入内存。</summary>
    [Fact]
    public async Task OversizedEncryptedFileIsRejectedBeforeDecryption()
    {
        Directory.CreateDirectory(_directory);
        await using (var output = new FileStream(PathToStore, FileMode.CreateNew, FileAccess.Write))
            output.SetLength(4L * 1024 * 1024 + 1);

        await Assert.ThrowsAsync<MinecraftAuthenticationException>(() => new WindowsMinecraftAccountStore(PathToStore).LoadAsync(default));

        Assert.Equal(4L * 1024 * 1024 + 1, new FileInfo(PathToStore).Length);
    }

    /// <summary>构造覆盖主要验证边界的无效集合，内容全部为测试生成的假凭据。</summary>
    private static StoredMinecraftAccounts Invalid(string scenario) => scenario switch
    {
        "duplicate" => new() { Accounts = [Entry(FirstId, "FirstPlayer"), Entry(FirstId.ToUpperInvariant(), "Duplicate")], ActiveAccountId = FirstId },
        "unknown-active" => new() { Accounts = [Entry(FirstId, "FirstPlayer")], ActiveAccountId = SecondId },
        "too-many" => new() { Accounts = Enumerable.Range(0, WindowsMinecraftAccountStore.MaximumAccounts + 1)
            .Select(_ => Entry(Guid.NewGuid().ToString("N"), "Player")).ToArray() },
        "invalid-id" => new() { Accounts = [Entry("not-a-uuid", "FirstPlayer")] },
        "long-token" => new() { Accounts = [new() { ClientId = ClientId, Account = new(FirstId, "FirstPlayer"), RefreshToken = new('x', 32769) }] },
        "wrong-schema" => new() { SchemaVersion = 999, Accounts = [Entry(FirstId, "FirstPlayer")] },
        "null-accounts" => new() { Accounts = null! },
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    /// <summary>构造带唯一假令牌的账号，断言加密时不会混入其他测试数据。</summary>
    private static StoredMinecraftAccount Entry(string id, string name) => new()
    {
        ClientId = ClientId, Account = new(id, name), RefreshToken = "fake-refresh-" + Guid.NewGuid().ToString("N")
    };

    /// <summary>仅加密本测试提供的旧版或损坏 JSON，绕过保存验证以覆盖真实读取迁移路径。</summary>
    private async Task<byte[]> WriteFixtureAsync<T>(T value)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] ciphertext;
        try
        {
            var protect = typeof(WindowsMinecraftAccountStore).GetMethod("Protect", BindingFlags.Static | BindingFlags.NonPublic)!;
            ciphertext = (byte[])protect.Invoke(null, [plaintext, false])!;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(PathToStore, ciphertext);
        return ciphertext;
    }

    /// <summary>只清理本测试随机创建的目录，不访问真实 LocalAppData 凭据路径。</summary>
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
