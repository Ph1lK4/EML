using EML.Core.Minecraft.Launching;
using EML.Infrastructure.Minecraft.Launching;

namespace EML.Tests;

/// <summary>验证日志编码偏好的兼容性、独立保存和非法值保护，不访问用户实例。</summary>
public sealed class MinecraftLogEncodingSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-log-encoding-settings-" + Guid.NewGuid().ToString("N"));

    /// <summary>旧版本记录缺少编码字段时使用自动模式，不改写原记录或 JVM 参数。</summary>
    [Fact]
    public async Task ExistingSettingsWithoutEncodingUseAutomaticMode()
    {
        var instance = CreateInstance("legacy");
        var path = Path.Combine(instance, MinecraftLaunchSettingsStore.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string original = "{\"playerName\":\"LegacyPlayer\",\"additionalJvmArguments\":\"-Dfile.encoding=GBK\"}";
        await File.WriteAllTextAsync(path, original);

        var settings = await new MinecraftLaunchSettingsStore(_root).LoadAsync(instance);

        Assert.Equal(MinecraftLogEncoding.Auto, settings.LogEncoding);
        Assert.Equal("LegacyPlayer", settings.PlayerName);
        Assert.Equal("-Dfile.encoding=GBK", settings.AdditionalJvmArguments);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    /// <summary>新实例不创建设置文件即可取得自动模式。</summary>
    [Fact]
    public async Task MissingSettingsUseAutomaticModeWithoutWriting()
    {
        var instance = CreateInstance("new");

        var settings = await new MinecraftLaunchSettingsStore(_root).LoadAsync(instance);

        Assert.Equal(MinecraftLogEncoding.Auto, settings.LogEncoding);
        Assert.False(File.Exists(Path.Combine(instance, MinecraftLaunchSettingsStore.RelativePath)));
    }

    /// <summary>三种模式可独立往返，不影响其他实例的默认值或游戏内容。</summary>
    [Theory]
    [InlineData(MinecraftLogEncoding.Auto)]
    [InlineData(MinecraftLogEncoding.Utf8)]
    [InlineData(MinecraftLogEncoding.SystemDefault)]
    public async Task EncodingRoundTripIsPerInstance(MinecraftLogEncoding encoding)
    {
        var instance = CreateInstance("configured");
        var other = CreateInstance("other");
        var marker = Path.Combine(instance, "keep.txt");
        await File.WriteAllTextAsync(marker, "原有游戏内容");
        var settings = new MinecraftLaunchSettings { LogEncoding = encoding, AdditionalJvmArguments = "-Dfile.encoding=GBK" };
        var store = new MinecraftLaunchSettingsStore(_root);

        await store.SaveAsync(instance, settings);

        Assert.Equal(settings, await store.LoadAsync(instance));
        Assert.Equal(MinecraftLogEncoding.Auto, (await store.LoadAsync(other)).LogEncoding);
        Assert.Equal("原有游戏内容", await File.ReadAllTextAsync(marker));
    }

    /// <summary>非法枚举不能替换已有有效偏好，避免界面显示自动而运行层收到未知模式。</summary>
    [Fact]
    public async Task UndefinedEncodingCannotOverwriteExistingSettings()
    {
        var instance = CreateInstance("invalid-save");
        var store = new MinecraftLaunchSettingsStore(_root);
        var original = new MinecraftLaunchSettings { LogEncoding = MinecraftLogEncoding.Utf8 };
        await store.SaveAsync(instance, original);
        var path = Path.Combine(instance, MinecraftLaunchSettingsStore.RelativePath);
        var bytes = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(instance,
            original with { LogEncoding = (MinecraftLogEncoding)999 }));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(original, await store.LoadAsync(instance));
    }

    /// <summary>读取非法编码值时保留原文件并明确失败，不偷偷重写为默认值。</summary>
    [Fact]
    public async Task UndefinedPersistedEncodingIsReportedAndPreserved()
    {
        var instance = CreateInstance("invalid-load");
        var path = Path.Combine(instance, MinecraftLaunchSettingsStore.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string original = "{\"logEncoding\":999}";
        await File.WriteAllTextAsync(path, original);

        await Assert.ThrowsAsync<ArgumentException>(() => new MinecraftLaunchSettingsStore(_root).LoadAsync(instance));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    /// <summary>只创建随机测试根目录下的直接子实例。</summary>
    private string CreateInstance(string name)
    {
        var path = Path.Combine(_root, "instances", name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>清理本测试创建的临时文件，不访问真实实例。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
