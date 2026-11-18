using EML.Core.Minecraft.Launching;
using EML.Infrastructure.Minecraft.Launching;

namespace EML.Tests;

/// <summary>每个实例只记公开启动身份；使用随机测试目录，不访问真实账号或玩家数据。</summary>
public sealed class MinecraftLaunchIdentityStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-launch-identity-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Started = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

    /// <summary>没有新身份字段的旧设置仍正常读取，不默认切换玩家或模式。</summary>
    [Fact]
    public async Task LegacySettingsKeepTheirExistingIdentity()
    {
        var instance = Instance("legacy");
        var path = SettingsPath(instance);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{\"playerName\":\"LegacyPlayer\",\"useMicrosoftAccount\":false,\"maxMemoryMb\":4096}");

        var settings = await new MinecraftLaunchSettingsStore(_root).LoadAsync(instance);

        Assert.Equal("LegacyPlayer", settings.PlayerName);
        Assert.False(settings.UseMicrosoftAccount);
        Assert.Equal(4096, settings.MaxMemoryMb);
        Assert.Null(settings.PreferredMicrosoftAccount);
        Assert.Null(settings.LastLaunchIdentity);
    }

    /// <summary>离线启动只更新身份，未保存的 JVM 等参数不会覆盖上次明确保存的参数。</summary>
    [Fact]
    public async Task RememberingOfflineIdentityPreservesSavedRuntimeSettings()
    {
        var instance = Instance("offline");
        var store = new MinecraftLaunchSettingsStore(_root);
        var saved = new MinecraftLaunchSettings
        {
            PlayerName = "OldPlayer", UseMicrosoftAccount = true,
            PreferredMicrosoftAccount = new(MicrosoftMinecraftAuthTests.PlayerId, "OnlineAlex"),
            JavaPath = @"C:\Java\bin\java.exe", MinMemoryMb = 2048, MaxMemoryMb = 6144,
            AdditionalJvmArguments = "-XX:+UseG1GC", LogEncoding = MinecraftLogEncoding.SystemDefault
        };
        await store.SaveAsync(instance, saved);
        var identity = Offline("NewPlayer");

        await store.RememberAsync(instance, identity);
        var restored = await new MinecraftLaunchSettingsStore(_root).LoadAsync(instance);

        Assert.Equal(saved with { PlayerName = "NewPlayer", UseMicrosoftAccount = false, LastLaunchIdentity = identity }, restored);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(SettingsPath(instance))!, "*.tmp"));
    }

    /// <summary>正版身份记录实际玩家 UUID 和姓名，同时保留离线名称以及其他启动偏好。</summary>
    [Fact]
    public async Task RememberingOnlineIdentityPreservesOfflineNameAndContainsNoCredentials()
    {
        var instance = Instance("online");
        var store = new MinecraftLaunchSettingsStore(_root);
        var saved = new MinecraftLaunchSettings { PlayerName = "OfflineDraft", MaxMemoryMb = 4096 };
        await store.SaveAsync(instance, saved);
        var identity = Online();

        await store.RememberAsync(instance, identity);
        var restored = await new MinecraftLaunchSettingsStore(_root).LoadAsync(instance);
        var json = await File.ReadAllTextAsync(SettingsPath(instance));

        Assert.Equal(saved with
        {
            UseMicrosoftAccount = true,
            PreferredMicrosoftAccount = new(identity.PlayerId, identity.PlayerName), LastLaunchIdentity = identity
        }, restored);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientSecret", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(identity.PlayerId, json);
        Assert.Contains(identity.PlayerName, json);
    }

    /// <summary>不同实例分别保存默认身份，不会因为启动 B 而把 A 改成另一个玩家。</summary>
    [Fact]
    public async Task InstancesKeepIndependentLastIdentities()
    {
        var first = Instance("first");
        var second = Instance("second");
        var store = new MinecraftLaunchSettingsStore(_root);
        var offline = Offline("FirstPlayer");
        var online = Online();

        await store.RememberAsync(first, offline);
        await store.RememberAsync(second, online);

        Assert.Equal(offline, (await store.LoadAsync(first)).LastLaunchIdentity);
        Assert.Equal(online, (await store.LoadAsync(second)).LastLaunchIdentity);
        Assert.False((await store.LoadAsync(first)).UseMicrosoftAccount);
        Assert.True((await store.LoadAsync(second)).UseMicrosoftAccount);
    }

    /// <summary>用户随后显式保存的模式优先，但保存设置不能伪造新的实际启动记录。</summary>
    [Fact]
    public async Task ExplicitSaveChangesNextDefaultButPreservesActualLaunchHistory()
    {
        var instance = Instance("explicit");
        var store = new MinecraftLaunchSettingsStore(_root);
        var actual = Online();
        await store.RememberAsync(instance, actual);
        var next = new MinecraftLaunchSettings
        {
            PlayerName = "NextOffline", UseMicrosoftAccount = false, MaxMemoryMb = 4096,
            LastLaunchIdentity = Offline("NeverStarted")
        };

        await store.SaveAsync(instance, next);
        var restored = await store.LoadAsync(instance);

        Assert.Equal("NextOffline", restored.PlayerName);
        Assert.False(restored.UseMicrosoftAccount);
        Assert.Equal(4096, restored.MaxMemoryMb);
        Assert.Equal(actual, restored.LastLaunchIdentity);
    }

    /// <summary>过期界面快照不应在保存其他设置时清除刚记录的真实启动身份。</summary>
    [Fact]
    public async Task StaleSettingsSaveCannotEraseRecordedHistory()
    {
        var instance = Instance("stale");
        var firstStore = new MinecraftLaunchSettingsStore(_root);
        var secondStore = new MinecraftLaunchSettingsStore(_root);
        var stale = await firstStore.LoadAsync(instance);
        var actual = Offline("ActualPlayer");
        await secondStore.RememberAsync(instance, actual);

        await firstStore.SaveAsync(instance, stale with { MaxMemoryMb = 3072 });

        var restored = await secondStore.LoadAsync(instance);
        Assert.Equal(actual, restored.LastLaunchIdentity);
        Assert.Equal(3072, restored.MaxMemoryMb);
    }

    /// <summary>多个服务对象并发读改写时，参数保存不能把已发生的启动记录丢掉。</summary>
    [Fact]
    public async Task ConcurrentServiceInstancesKeepHistoryAndRuntimePreferences()
    {
        var instance = Instance("concurrent");
        var first = new MinecraftLaunchSettingsStore(_root);
        var second = new MinecraftLaunchSettingsStore(_root);
        var actual = Offline("ConcurrentUser");

        await Task.WhenAll(first.RememberAsync(instance, actual),
            second.SaveAsync(instance, new() { MaxMemoryMb = 3072 }));

        var restored = await first.LoadAsync(instance);
        Assert.Equal(actual, restored.LastLaunchIdentity);
        Assert.Equal(3072, restored.MaxMemoryMb);
    }

    /// <summary>非法或损坏的设置不能在自动记忆时被默认设置静默覆盖。</summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"playerName\":\"bad name!\"}")]
    public async Task RememberingDoesNotOverwriteCorruptSettings(string original)
    {
        var instance = Instance("corrupt");
        var path = SettingsPath(instance);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, original);

        await Assert.ThrowsAnyAsync<Exception>(() => new MinecraftLaunchSettingsStore(_root).RememberAsync(instance, Offline("NewPlayer")));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    /// <summary>公开身份也必须有合法 UUID 和姓名，不能把任意错误数据写成可用的默认账号。</summary>
    [Theory]
    [InlineData("not-a-uuid", "OnlineAlex")]
    [InlineData(MicrosoftMinecraftAuthTests.PlayerId, "bad name!")]
    [InlineData(MicrosoftMinecraftAuthTests.PlayerId, "OnlineAlex\n")]
    public async Task InvalidIdentityIsRejectedWithoutChangingSavedSettings(string playerId, string name)
    {
        var instance = Instance("invalid");
        var store = new MinecraftLaunchSettingsStore(_root);
        await store.SaveAsync(instance, new() { PlayerName = "KeepPlayer" });
        var original = await File.ReadAllBytesAsync(SettingsPath(instance));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.RememberAsync(instance, new(true, name, playerId, Started)));

        Assert.Equal(original, await File.ReadAllBytesAsync(SettingsPath(instance)));
    }

    /// <summary>磁盘中的非法身份或空启动时间会明确报错，不悄悄以 Steve 替代原来的账号。</summary>
    [Theory]
    [InlineData("{\"preferredMicrosoftAccount\":{\"id\":\"invalid\",\"name\":\"Player\"}}")]
    [InlineData("{\"lastLaunchIdentity\":{\"useMicrosoftAccount\":true,\"playerName\":\"OnlineAlex\",\"playerId\":\"123456781234123412341234567890ab\",\"startedAtUtc\":\"0001-01-01T00:00:00+00:00\"}}")]
    [InlineData("{\"lastLaunchIdentity\":{\"useMicrosoftAccount\":false,\"playerName\":\"Bad Name\",\"playerId\":\"123456781234123412341234567890ab\",\"startedAtUtc\":\"2026-11-18T00:00:00+00:00\"}}")]
    public async Task MalformedStoredIdentityDoesNotSilentlyFallBack(string original)
    {
        var instance = Instance("bad-stored-identity");
        var path = SettingsPath(instance);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, original);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => new MinecraftLaunchSettingsStore(_root).LoadAsync(instance));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    /// <summary>事先取消的记录操作不能改变旧默认值或留下未提交文件。</summary>
    [Fact]
    public async Task PreCanceledRememberLeavesSettingsUntouched()
    {
        var instance = Instance("canceled");
        var store = new MinecraftLaunchSettingsStore(_root);
        await store.SaveAsync(instance, new());
        var original = await File.ReadAllBytesAsync(SettingsPath(instance));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RememberAsync(instance, Offline("NewPlayer"), new(true)));

        Assert.Equal(original, await File.ReadAllBytesAsync(SettingsPath(instance)));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(SettingsPath(instance))!, "*.tmp"));
    }

    /// <summary>写入入口沿用实例边界，不能向任意文件夹写入玩家身份。</summary>
    [Fact]
    public async Task RememberRejectsPathsOutsideInstances()
    {
        Directory.CreateDirectory(_root);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new MinecraftLaunchSettingsStore(_root).RememberAsync(_root, Online()));

        Assert.False(Directory.Exists(Path.Combine(_root, ".eml-runtime")));
    }

    /// <summary>构造本测试独有的合法实例目录。</summary>
    private string Instance(string name)
    {
        var path = Path.Combine(_root, "instances", name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>读取生产设置相对位置，避免测试与实现各自保存另一份文件。</summary>
    private static string SettingsPath(string instance) => Path.Combine(instance, MinecraftLaunchSettingsStore.RelativePath);

    /// <summary>使用标准离线 UUID 算法生成固定可验证的公开身份。</summary>
    private static MinecraftLaunchIdentity Offline(string name) => new(false, name, MinecraftLaunchPlanner.OfflineUuid(name), Started);

    /// <summary>使用测试专用正版 UUID；不创建认证会话或凭据。</summary>
    private static MinecraftLaunchIdentity Online() => new(true, "OnlineAlex", MicrosoftMinecraftAuthTests.PlayerId, Started);

    /// <summary>只删除本测试随机创建的目录。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
