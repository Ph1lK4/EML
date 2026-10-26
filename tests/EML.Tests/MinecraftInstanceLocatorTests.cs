using EML.Infrastructure.Minecraft;

namespace EML.Tests;

public sealed class MinecraftInstanceLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eml-instance-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>在本测试独有的临时 instances 目录创建清单，避免依赖真实整合包或修改用户文件。</summary>
    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, "instances", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>为指定临时实例创建 .minecraft 目录，用于模拟无需 versions 的 Prism 布局。</summary>
    private void Game(string name) => Directory.CreateDirectory(Path.Combine(_root, "instances", name, ".minecraft"));
    /// <summary>统一调用被测扫描器，显式传入临时根目录，并允许测试取消行为。</summary>
    private Task<IReadOnlyList<EML.Core.Minecraft.MinecraftInstance>> Scan(CancellationToken token = default) =>
        new LauncherRootMinecraftInstanceLocator(_root).FindAllAsync(token);

    [Fact]
    /// <summary>验证中文和空格名称、原版版本识别，以及返回的实例路径和游戏目录都是正确的绝对路径。</summary>
    public async Task VanillaUsesMetadataAndAbsolutePaths()
    {
        Write("中文 测试/.minecraft/versions/custom/custom.json", """{"id":"1.7.10","mainClass":"net.minecraft.client.main.Main"}""");
        var instance = Assert.Single(await Scan());
        Assert.Equal("中文 测试", instance.Name);
        Assert.Equal("1.7.10", instance.MinecraftVersionDisplay);
        Assert.Equal("原版", instance.LoaderDisplay);
        Assert.Equal(Path.Combine(_root, "instances", "中文 测试"), instance.InstancePath);
        Assert.Equal(Path.Combine(instance.InstancePath, ".minecraft"), instance.GameDirectory);
        Assert.Empty(instance.Warnings);
    }

    [Fact]
    /// <summary>验证合并 Forge 清单从 game 补丁获取本体版本，不把资源索引或自定义 ID 当成游戏版本。</summary>
    public async Task MergedForgeReadsGamePatchNotAssetIndexOrFolderName()
    {
        Write("pack/.minecraft/versions/custom/custom.json", """
        {"id":"custom","assetIndex":{"id":"wrong"},"patches":[
          {"id":"game","version":"1.7.10"},{"id":"forge","version":"10.13.4.1614"}]}
        """);
        var instance = Assert.Single(await Scan());
        Assert.Equal("custom", Assert.Single(instance.VersionIds));
        Assert.Equal("1.7.10", instance.MinecraftVersionDisplay);
        Assert.Equal("Forge 10.13.4.1614", instance.LoaderDisplay);
    }

    [Fact]
    /// <summary>验证缺少补丁列表时，仍可从旧版 Forge 的 Maven 坐标读取 Minecraft 版本。</summary>
    public async Task ForgeCoordinateWorksWithoutPatches()
    {
        Write("pack/.minecraft/versions/custom/custom.json", """
        {"id":"custom","libraries":[{"name":"net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10"}]}
        """);
        var instance = Assert.Single(await Scan());
        Assert.Equal("1.7.10", instance.MinecraftVersionDisplay);
        Assert.StartsWith("Forge ", instance.LoaderDisplay);
    }

    [Fact]
    /// <summary>验证 Prism 无需 versions；名称只取 General 节，且支持名称中的等号与组件 cachedVersion 回退。</summary>
    public async Task PrismReadsGeneralNameAndComponentsWithoutVersionsFolder()
    {
        Game("renamed");
        Write("renamed/instance.cfg", "[Other]\nname=wrong\n[General]\nname=我的 GTNH = 测试\n");
        Write("renamed/mmc-pack.json", """
        {"formatVersion":1,"components":[
          {"uid":"net.minecraft","version":"1.7.10"},
          {"uid":"net.minecraftforge","cachedVersion":"10.13.4.1614"}]}
        """);
        var instance = Assert.Single(await Scan());
        Assert.Equal("我的 GTNH = 测试", instance.Name);
        Assert.Equal("Prism", instance.Format);
        Assert.Equal("1.7.10", instance.MinecraftVersionDisplay);
        Assert.Equal("Forge 10.13.4.1614", instance.LoaderDisplay);
    }

    [Fact]
    /// <summary>验证 JSON 损坏或根类型错误时保留提示，并继续识别其他实例和正常版本。</summary>
    public async Task BrokenMetadataDoesNotHideOtherInstancesOrVersions()
    {
        Write("broken/.minecraft/versions/broken/broken.json", "{");
        Write("good/.minecraft/versions/bad/bad.json", "[]");
        Write("good/.minecraft/versions/ok/ok.json", """{"id":"1.20.1","mainClass":"net.minecraft.client.main.Main"}""");
        var result = await Scan();
        Assert.Equal(2, result.Count);
        Assert.All(result, i => Assert.NotEmpty(i.Warnings));
        Assert.Equal("1.20.1", result.Single(i => i.Name == "good").MinecraftVersionDisplay);
    }

    [Fact]
    /// <summary>验证正常父版本继承，同时检查循环继承不会无限递归，而是产生未知版本与提示。</summary>
    public async Task InheritedVersionAndCycleAreHandled()
    {
        Write("pack/.minecraft/versions/base/base.json", """{"id":"1.20.1","mainClass":"net.minecraft.client.main.Main"}""");
        Write("pack/.minecraft/versions/fabric/fabric.json", """{"id":"fabric","inheritsFrom":"base","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.0"}]}""");
        Write("cycle/.minecraft/versions/a/a.json", """{"id":"a","inheritsFrom":"b"}""");
        Write("cycle/.minecraft/versions/b/b.json", """{"id":"b","inheritsFrom":"a"}""");
        var result = await Scan();
        var fabric = result.Single(i => i.Name == "pack").Versions.Single(v => v.VersionId == "fabric");
        Assert.Equal("1.20.1", fabric.MinecraftVersion);
        Assert.Equal("Fabric 0.16.0", fabric.Loader);
        Assert.All(result.Single(i => i.Name == "cycle").Versions, v => Assert.Equal("未知", v.MinecraftVersion));
    }

    [Fact]
    /// <summary>验证不存在的实例目录返回空列表，已取消的请求抛出取消异常。</summary>
    public async Task MissingRootIsEmptyAndCancellationPropagates()
    {
        Assert.Empty(await Scan());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scan(cancellation.Token));
    }

    [Fact]
    /// <summary>验证不会递归把实例目录内部的嵌套整合包当成另一个实例。</summary>
    public async Task OnlyDirectChildrenAreScanned()
    {
        Write("container/nested/.minecraft/versions/v/v.json", """{"id":"1.7.10","mainClass":"net.minecraft.client.main.Main"}""");
        Assert.Empty(await Scan());
    }

    [Fact]
    /// <summary>验证证据不足时显示未知，不根据看似版本号的目录名称或资源索引猜测。</summary>
    public async Task UnknownVersionIsNotGuessedFromFolderOrAssets()
    {
        Write("1.99.99/.minecraft/versions/custom/custom.json", """{"id":"custom","assetIndex":{"id":"1.7.10"}}""");
        var instance = Assert.Single(await Scan());
        Assert.Equal("未知", instance.MinecraftVersionDisplay);
        Assert.NotEmpty(instance.Warnings);
    }

    [Fact]
    /// <summary>验证 Prism 的 minecraft 目录别名，以及错误格式版本的容错提示。</summary>
    public async Task PrismAlternativeGameFolderAndInvalidFormat()
    {
        Directory.CreateDirectory(Path.Combine(_root, "instances", "prism", "minecraft"));
        Write("prism/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.21"}]}""");
        Game("bad");
        Write("bad/mmc-pack.json", """{"formatVersion":"bad"}""");
        var result = await Scan();
        Assert.EndsWith(Path.Combine("prism", "minecraft"), result.Single(i => i.Name == "prism").GameDirectory);
        Assert.NotEmpty(result.Single(i => i.Name == "bad").Warnings);
    }

    /// <summary>每个测试结束后只清理该测试创建的唯一临时目录，避免测试数据累积。</summary>
    public void Dispose()
    {
        // 仅删除本测试创建的唯一临时目录。
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
