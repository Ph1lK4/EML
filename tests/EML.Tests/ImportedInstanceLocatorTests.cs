using System.Text.Json;
using System.Text.Json.Serialization;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft;

namespace EML.Tests;

/// <summary>验证离线导入后的扫描行为，所有数据只写入本测试独占的临时目录。</summary>
public sealed class ImportedInstanceLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eml-import-locator-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>写入测试文件并自动创建父目录；不依赖用户真实实例。</summary>
    private void Write(string relative, string contents)
    {
        var path = Path.Combine(_root, "instances", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    /// <summary>直接序列化模拟本地 EML 清单，允许测试扫描器拒绝用户手工修改的无效声明。</summary>
    private void Manifest(string folder, InstanceImportManifest manifest) =>
        Write(folder + "/" + InstanceImportManifest.FileName, JsonSerializer.Serialize(manifest, JsonOptions));

    /// <summary>统一使用测试根目录扫描，防止测试读取桌面上的游戏内容。</summary>
    private Task<IReadOnlyList<MinecraftInstance>> Scan() => new LauncherRootMinecraftInstanceLocator(_root).FindAllAsync();

    /// <summary>Modrinth 尚未下载 versions 时仍显示声明的版本、加载器，并把文件数量描述为声明而非缺失结论。</summary>
    [Fact]
    public async Task ModrinthUsesDeclarationsWithoutClaimingMissingVersions()
    {
        Write("pack/.minecraft/config/example.cfg", "config");
        Manifest("pack", new()
        {
            SourceFormat = "Modrinth", Name = "original", MinecraftVersion = "1.20.1",
            Components = [new("minecraft", "1.20.1"), new("fabric-loader", "0.16.0")],
            Files = [new() { Path = ".minecraft/mods/example.jar", Source = InstanceFileSource.DirectDownload,
                Downloads = ["https://example.invalid/example.jar"] }]
        });
        var instance = Assert.Single(await Scan());
        Assert.Equal("pack", instance.Name);
        Assert.Equal("Modrinth", instance.Format);
        Assert.Equal("1.20.1", instance.MinecraftVersionDisplay);
        Assert.Equal("Fabric 0.16.0", instance.LoaderDisplay);
        Assert.Contains("导入完成，完整性尚未检查。", instance.Warnings);
        Assert.Contains(instance.Warnings, warning => warning.Contains("声明 1 个可直接下载"));
        Assert.DoesNotContain(instance.Warnings, warning => warning.Contains("未找到 versions"));
    }

    /// <summary>CurseForge 未解析 ID 时保留待解析状态，游戏目录可以是验证过的嵌套相对目录。</summary>
    [Fact]
    public async Task CurseForgeUsesCustomGameDirectoryAndUnresolvedReferences()
    {
        Write("pack/data/game/mods/readme.txt", "mods");
        Manifest("pack", new()
        {
            SourceFormat = "CurseForge", GameDirectory = "data/game", MinecraftVersion = "1.19.2",
            Components = [new("forge", "43.4.0") { Primary = true }],
            Files = [new() { Source = InstanceFileSource.CurseForge, ProjectId = 123, FileId = 456 }]
        });
        var instance = Assert.Single(await Scan());
        Assert.Equal("CurseForge", instance.Format);
        Assert.Equal(Path.Combine(instance.InstancePath, "data", "game"), instance.GameDirectory);
        Assert.Equal("1.19.2", instance.MinecraftVersionDisplay);
        Assert.Equal("Forge 43.4.0", instance.LoaderDisplay);
        Assert.Contains(instance.Warnings, warning => warning.Contains("1 个 CurseForge 文件引用待解析"));
        Assert.DoesNotContain(instance.Warnings, warning => warning.Contains("未找到 versions"));
    }

    /// <summary>通用模组 ZIP 即使没有 versions 也列出实例，但不能根据目录名猜测 Minecraft 版本。</summary>
    [Fact]
    public async Task GenericModOnlyZipIsVisibleWithUnknownVersion()
    {
        Write("1.99.99/.minecraft/mods/example.jar", "dummy");
        Manifest("1.99.99", new() { SourceFormat = "Zip", Name = "stale-name" });
        var instance = Assert.Single(await Scan());
        Assert.Equal("1.99.99", instance.Name);
        Assert.Equal("通用 ZIP", instance.Format);
        Assert.Equal("未知", instance.MinecraftVersionDisplay);
        Assert.Contains(instance.Warnings, warning => warning.StartsWith("通用 ZIP 未提供"));
    }

    /// <summary>真实 versions 元数据优先于历史导入声明，游戏目录为点号时就是实例根目录。</summary>
    [Fact]
    public async Task InstalledStandardMetadataOverridesManifestFallback()
    {
        Write("renamed/versions/actual/actual.json", """{"id":"1.21.1","mainClass":"net.minecraft.client.main.Main"}""");
        Manifest("renamed", new()
        {
            SourceFormat = "Zip", Name = "old-name", GameDirectory = ".", MinecraftVersion = "1.7.10",
            Components = [new("forge", "10.13.4.1614")]
        });
        var instance = Assert.Single(await Scan());
        Assert.Equal("renamed", instance.Name);
        Assert.Equal(instance.InstancePath, instance.GameDirectory);
        Assert.Equal("1.21.1", instance.MinecraftVersionDisplay);
        Assert.Equal("原版", instance.LoaderDisplay);
        Assert.EndsWith(Path.Combine("actual", "actual.json"), Assert.Single(instance.Versions).MetadataPath);
    }

    /// <summary>ZIP 首次准备后只显示原始版本；用户以 EML 命名的正常版本仍保留。</summary>
    [Theory]
    [InlineData("1.7.10")]
    [InlineData("EML-Standard-my-custom")]
    public async Task GeneratedLaunchProfileDoesNotDuplicateOriginalZipVersion(string sourceId)
    {
        var original = JsonSerializer.Serialize(new
        {
            id = sourceId, mainClass = "net.minecraft.client.main.Main",
            patches = new[] { new { id = "game", version = "1.7.10" } }
        });
        Write($"pack/.minecraft/versions/{sourceId}/{sourceId}.json", original);
        Write("pack/.minecraft/versions/EML-Standard-generated/EML-Standard-generated.json", JsonSerializer.Serialize(new
        {
            id = "EML-Standard-generated", mainClass = "net.minecraft.client.main.Main",
            emlRuntimeFingerprint = new string('a', 64),
            emlLaunchProfile = ".minecraft/versions/EML-Standard-generated/EML-Standard-generated.eml-launch.json"
        }));
        Manifest("pack", new() { SourceFormat = "Zip", MinecraftVersion = "1.7.10" });

        var instance = Assert.Single(await Scan());

        Assert.Equal(sourceId, Assert.Single(instance.VersionIds));
        Assert.Equal("1.7.10", instance.MinecraftVersionDisplay);
        Assert.Equal("原版", instance.LoaderDisplay);
        Assert.DoesNotContain(instance.Warnings, warning => warning.Contains("无法从元数据确认"));
        Assert.Equal(original, File.ReadAllText(Path.Combine(_root, "instances", "pack", ".minecraft", "versions", sourceId, sourceId + ".json")));
    }

    /// <summary>只含派生描述的已补全平台包继续显示清单声明，不显示内部 ID 或额外未知版本提示。</summary>
    [Theory]
    [InlineData("Modrinth")]
    [InlineData("CurseForge")]
    public async Task GeneratedOnlyProfilesKeepImportedManifestFallback(string sourceFormat)
    {
        var id = "EML-" + sourceFormat + "-generated";
        Write($"pack/.minecraft/versions/{id}/{id}.json", JsonSerializer.Serialize(new
        {
            id, mainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
            emlRuntimeFingerprint = new string('b', 64),
            emlLaunchProfile = $".minecraft/versions/{id}/{id}.eml-launch.json"
        }));
        Manifest("pack", new()
        {
            SourceFormat = sourceFormat, MinecraftVersion = "1.20.1",
            Components = [new("minecraft", "1.20.1"), new("fabric-loader", "0.16.0")]
        });

        var instance = Assert.Single(await Scan());

        Assert.Equal("1.20.1", Assert.Single(instance.VersionIds));
        Assert.Equal("Fabric 0.16.0", instance.LoaderDisplay);
        Assert.EndsWith(InstanceImportManifest.FileName, Assert.Single(instance.Versions).MetadataPath);
        Assert.DoesNotContain(instance.Warnings, warning => warning.Contains("无法从元数据确认") || warning.Contains("versions 中没有"));
    }

    /// <summary>导入的 Prism 仍以 mmc-pack 和当前 instance.cfg 为准，不能显示导入前的旧名称或旧版本。</summary>
    [Fact]
    public async Task PrismUsesActualComponentAndCurrentConfiguredName()
    {
        Write("prism/minecraft/mods/readme.txt", "mods");
        Write("prism/instance.cfg", "[General]\nname=新的实例名称\n");
        Write("prism/mmc-pack.json", """
            {"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"},
            {"uid":"net.minecraftforge","version":"10.13.4.1614"}]}
            """);
        Manifest("prism", new()
        {
            SourceFormat = "Prism", Name = "旧的实例名称", GameDirectory = "minecraft", MinecraftVersion = "1.20.1"
        });
        var instance = Assert.Single(await Scan());
        Assert.Equal("新的实例名称", instance.Name);
        Assert.Equal("Prism", instance.Format);
        Assert.Equal("1.7.10", instance.MinecraftVersionDisplay);
        Assert.Equal("Forge 10.13.4.1614", instance.LoaderDisplay);
    }

    /// <summary>普通和导入后的 Prism 实例都忽略禁用组件，避免显示已经停用的游戏版本或加载器。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrismSkipsDisabledComponents(bool imported)
    {
        Write("prism/.minecraft/config/example.cfg", "config");
        Write("prism/mmc-pack.json", """
            {"formatVersion":1,"components":[
                {"uid":"net.minecraft","version":"1.7.10","disabled":true},
                {"uid":"net.minecraft","version":"1.20.1"},
                {"uid":"net.minecraftforge","version":"47.3.0","disabled":true},
                {"uid":"net.fabricmc.fabric-loader","version":"0.16.0","disabled":false}]}
            """);
        if (imported)
            Manifest("prism", new() { SourceFormat = "Prism", MinecraftVersion = "1.20.1" });
        var instance = Assert.Single(await Scan());
        Assert.Equal("1.20.1", instance.MinecraftVersionDisplay);
        Assert.Equal("Fabric 0.16.0", instance.LoaderDisplay);
    }

    /// <summary>损坏的实际版本 JSON 保留错误提示，但仍能显示导入时声明的版本需求。</summary>
    [Fact]
    public async Task CorruptMetadataKeepsWarningAndUsesDeclaredVersion()
    {
        Write("pack/.minecraft/versions/broken/broken.json", "{");
        Manifest("pack", new() { SourceFormat = "Modrinth", MinecraftVersion = "1.20.1" });
        var instance = Assert.Single(await Scan());
        Assert.Equal("1.20.1", instance.MinecraftVersionDisplay);
        Assert.Contains(instance.Warnings, warning => warning.StartsWith("broken："));
    }

    /// <summary>游戏目录被移走后保留实例和已知版本，并明确提示实际目录不存在。</summary>
    [Fact]
    public async Task MissingGameDirectoryStillShowsImportedInstance()
    {
        Manifest("pack", new() { SourceFormat = "Modrinth", MinecraftVersion = "1.20.1" });
        var instance = Assert.Single(await Scan());
        Assert.Equal("1.20.1", instance.MinecraftVersionDisplay);
        Assert.Contains("导入清单指定的游戏目录不存在。", instance.Warnings);
    }

    /// <summary>篡改后的越界游戏路径不会读取实例外的版本文件，且不影响其他合法实例。</summary>
    [Fact]
    public async Task InvalidManifestPathDoesNotEscapeInstanceOrHideOtherPacks()
    {
        Manifest("bad", new() { SourceFormat = "Zip", GameDirectory = "../outside" });
        Write("good/.minecraft/versions/v/v.json", """{"id":"1.20.1","mainClass":"net.minecraft.client.main.Main"}""");
        var result = await Scan();
        Assert.Equal(2, result.Count);
        var invalid = result.Single(instance => instance.Name == "bad");
        Assert.Equal("未知", invalid.Format);
        Assert.Empty(invalid.Versions);
        Assert.Contains(invalid.Warnings, warning => warning.StartsWith("实例读取失败："));
        Assert.Equal("1.20.1", result.Single(instance => instance.Name == "good").MinecraftVersionDisplay);
    }

    /// <summary>组件声明也能提供 Minecraft 版本，旧清单保存的完整 CurseForge 加载器 ID 仍可显示。</summary>
    [Fact]
    public async Task ComponentOnlyFallbackUnderstandsRawCurseForgeLoaderId()
    {
        Write("pack/.minecraft/config/example.cfg", "config");
        Manifest("pack", new()
        {
            SourceFormat = "CurseForge",
            Components = [new("minecraft", "1.20.1"), new("forge-47.3.0", "forge-47.3.0")]
        });
        var instance = Assert.Single(await Scan());
        Assert.Equal("1.20.1", instance.MinecraftVersionDisplay);
        Assert.Equal("Forge 47.3.0", instance.LoaderDisplay);
    }

    /// <summary>只清理测试自己生成的唯一临时目录，不接触真实游戏实例。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
