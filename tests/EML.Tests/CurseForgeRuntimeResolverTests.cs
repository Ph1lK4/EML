using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.CurseForge;
using EML.Infrastructure.Minecraft.Modrinth;

namespace EML.Tests;

/// <summary>验证 CurseForge 固定运行版本、主加载器选择和独立缓存；测试不访问网络或执行 Java。</summary>
public sealed class CurseForgeRuntimeResolverTests : IDisposable
{
    private const string Metadata = ".eml-runtime/curseforge/metadata/";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-curseforge-runtime-" + Guid.NewGuid().ToString("N"));
    private InstanceImportManifest _manifest = new()
    {
        SourceFormat = "CurseForge", Name = "测试包", MinecraftVersion = "26.3",
        Components = [new("minecraft", "26.3")]
    };

    /// <summary>创建独立实例，并写入官方格式的原版元数据及对应 SHA1 索引。</summary>
    public CurseForgeRuntimeResolverTests()
    {
        Directory.CreateDirectory(_root);
        Minecraft(Metadata);
    }

    /// <summary>向测试目录写入 UTF-8 元数据并自动建立父目录。</summary>
    private void Write(string relative, string content)
    {
        var target = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content, new UTF8Encoding(false));
    }

    /// <summary>为指定来源缓存生成相同的固定原版配置，避免跨来源误用缓存。</summary>
    private void Minecraft(string metadataRoot)
    {
        const string json = """
            {"id":"26.3","mainClass":"net.minecraft.client.main.Main",
            "downloads":{"client":{"url":"https://example.invalid/client.jar","sha1":"1111111111111111111111111111111111111111","size":12}},
            "assetIndex":{"id":"34","url":"https://example.invalid/assets.json","sha1":"2222222222222222222222222222222222222222","size":30},
            "javaVersion":{"majorVersion":25}}
            """;
        Write(metadataRoot + "minecraft/26.3.json", json);
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(json)));
        Write(metadataRoot + "minecraft/manifest-v2.json", JsonSerializer.Serialize(new
        {
            versions = new[] { new { id = "26.3", url = "https://piston-meta.mojang.com/v1/packages/test/26.3.json", sha1 } }
        }));
    }

    /// <summary>保存固定 Fabric/Quilt 元数据，不依赖网络服务当前可用性。</summary>
    private void Loader(string id, string version)
    {
        var normalized = id switch { "fabric" => "fabric-loader", "quilt" => "quilt-loader", _ => id };
        var coordinate = (normalized == "fabric-loader" ? "net.fabricmc:fabric-loader:" : "org.quiltmc:quilt-loader:") + version;
        Write(Metadata + normalized + "/26.3/" + version + ".json", JsonSerializer.Serialize(new
        {
            inheritsFrom = "26.3", mainClass = "example.LoaderMain",
            libraries = new[] { new { name = coordinate, url = "https://maven.fabricmc.net/" } }
        }));
    }

    /// <summary>按固定平台执行只读解析，保证结果与测试宿主环境无关。</summary>
    private PrismRuntimePlan Resolve() => new CurseForgeRuntimeResolver("windows", "x86_64", runtimeOsVersion: "10.0").Resolve(_root, _manifest);

    /// <summary>原版 CurseForge 计划使用独立来源、版本前缀及官方 Java 要求。</summary>
    [Fact]
    public void VanillaPlanUsesCurseForgeIdentityAndDoesNotWrite()
    {
        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray();
        var plan = Resolve();
        Assert.Equal("CurseForge", plan.SourceFormat);
        Assert.Equal("EML-CurseForge", plan.VersionIdPrefix);
        Assert.Equal("26.3", plan.MinecraftVersion);
        Assert.Equal(new[] { 25 }, plan.CompatibleJavaMajors);
        Assert.Empty(plan.BlockingIssues);
        Assert.Empty(plan.MetadataRequests);
        Assert.Equal(files, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    /// <summary>缺少 CurseForge 缓存时不能误用同实例下 Modrinth 缓存。</summary>
    [Fact]
    public void MissingMetadataRequestsOnlyCurseForgeCache()
    {
        Minecraft(".eml-runtime/modrinth/metadata/");
        File.Delete(Path.Combine(_root, Metadata + "minecraft/manifest-v2.json"));
        var request = Assert.Single(Resolve().MetadataRequests);
        Assert.Equal(Metadata + "minecraft/manifest-v2.json", request.RelativePath);
        Assert.Equal("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", request.Url);
    }

    /// <summary>CurseForge 的 Fabric/Quilt 简写和标准组件名都解析到固定官方配置。</summary>
    [Theory]
    [InlineData("fabric", "fabric-loader", "0.19.5")]
    [InlineData("fabric-loader", "fabric-loader", "0.19.5")]
    [InlineData("quilt", "quilt-loader", "0.30.0")]
    [InlineData("quilt-loader", "quilt-loader", "0.30.0")]
    public void SingleLoaderSupportsKnownComponentAliases(string id, string normalized, string version)
    {
        _manifest = _manifest with { Components = [new("minecraft", "26.3"), new(id, version)] };
        var request = Assert.Single(Resolve().MetadataRequests);
        Assert.Equal(Metadata + normalized + "/26.3/" + version + ".json", request.RelativePath);
        Assert.EndsWith("/26.3/" + version + "/profile/json", request.Url);
        Loader(id, version);
        var plan = Resolve();
        Assert.Empty(plan.BlockingIssues);
        Assert.Equal("example.LoaderMain", plan.MainClass);
    }

    /// <summary>唯一主加载器优先于多个备选，未选择的 Forge 不导致已选 Fabric 被阻断。</summary>
    [Fact]
    public void UniquePrimaryIgnoresAlternativeLoaders()
    {
        _manifest = _manifest with
        {
            Components = [new("minecraft", "26.3"), new("forge", "99.0.0") { Primary = false },
                new("fabric", "0.19.5") { Primary = true }, new("quilt", "0.30.0") { Primary = false }]
        };
        Loader("fabric", "0.19.5");
        var plan = Resolve();
        Assert.Empty(plan.BlockingIssues);
        Assert.Empty(plan.MetadataRequests);
        Assert.Contains(plan.Notes, note => note.Contains("备选", StringComparison.Ordinal));
        Assert.Contains(plan.Files, file => file.MavenName == "net.fabricmc:fabric-loader:0.19.5");
        Assert.DoesNotContain(plan.Files, file => file.MavenName?.Contains("quilt", StringComparison.Ordinal) == true);
    }

    /// <summary>多个 primary 主加载器无论类型是否已支持都必须阻断。</summary>
    [Fact]
    public void MultiplePrimaryLoadersAreBlocked()
    {
        _manifest = _manifest with
        {
            Components = [new("minecraft", "26.3"), new("fabric", "0.19.5") { Primary = true }, new("quilt", "0.30.0") { Primary = true }]
        };
        var plan = Resolve();
        Assert.Contains(plan.BlockingIssues, issue => issue.Contains("多个 primary", StringComparison.Ordinal));
        Assert.Empty(plan.MetadataRequests);
    }

    /// <summary>无 primary 且存在多个候选时不随意选择第一个加载器。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void AmbiguousLoadersWithoutPrimaryAreBlocked(bool? primary)
    {
        _manifest = _manifest with
        {
            Components = [new("minecraft", "26.3"), new("fabric", "0.19.5") { Primary = primary }, new("quilt", "0.30.0") { Primary = primary }]
        };
        var plan = Resolve();
        Assert.Contains(plan.BlockingIssues, issue => issue.Contains("未声明唯一 primary", StringComparison.Ordinal));
        Assert.Empty(plan.MetadataRequests);
    }

    /// <summary>唯一候选即使未标 primary 也可确定；false 不会把唯一版本误当成可省略。</summary>
    [Fact]
    public void SoleNonPrimaryLoaderRemainsRequired()
    {
        _manifest = _manifest with { Components = [new("minecraft", "26.3"), new("fabric", "0.19.5") { Primary = false }] };
        Assert.Equal("fabric-loader", Assert.Single(Resolve().MetadataRequests).Id);
    }

    /// <summary>Forge/NeoForge 不可只装原版后报告成功，处理器尚未接入时保留明确阻断。</summary>
    [Theory]
    [InlineData("forge", "47.3.0")]
    [InlineData("neoforge", "21.1.0")]
    public void InstallerProcessorLoadersAreNotReportedAsVanillaReady(string id, string version)
    {
        _manifest = _manifest with { Components = [new("minecraft", "26.3"), new(id, version) { Primary = true }] };
        Assert.Contains(Resolve().BlockingIssues, issue => issue.Contains("安装器处理器", StringComparison.Ordinal));
    }

    /// <summary>更改非主备选仍影响来源指纹，避免清单变更后复用旧完成状态。</summary>
    [Fact]
    public void FingerprintIncludesOriginalPrimaryAndAlternativeDeclarations()
    {
        Loader("fabric", "0.19.5");
        _manifest = _manifest with { Components = [new("minecraft", "26.3"), new("fabric", "0.19.5") { Primary = true }, new("forge", "47.3.0") { Primary = false }] };
        var before = Resolve().Fingerprint;
        _manifest = _manifest with { Components = [new("minecraft", "26.3"), new("fabric", "0.19.5") { Primary = true }, new("forge", "47.3.1") { Primary = false }] };
        Assert.NotEqual(before, Resolve().Fingerprint);
    }

    /// <summary>两个公开来源入口不能接收另一平台清单，避免缓存和完成状态混用。</summary>
    [Fact]
    public void PublicResolversRejectOtherSourceFormats()
    {
        Assert.Throws<InvalidDataException>(() => new ModrinthRuntimeResolver().Resolve(_root, _manifest));
        _manifest = _manifest with { SourceFormat = "Modrinth" };
        Assert.Throws<InvalidDataException>(() => Resolve());
    }

    /// <summary>原版元数据请求继承官方索引 SHA1；篡改本地内容必须失败。</summary>
    [Fact]
    public void MinecraftMetadataRetainsOfficialHashValidation()
    {
        var path = Path.Combine(_root, Metadata + "minecraft/26.3.json");
        File.AppendAllText(path, " ");
        Assert.Throws<InvalidDataException>(() => Resolve());
        File.Delete(path);
        Assert.Equal(40, Assert.Single(Resolve().MetadataRequests).RequirementHashes["sha1"].Length);
    }

    /// <summary>结束时只清理当前测试创建的临时实例。</summary>
    public void Dispose() => Directory.Delete(_root, true);
}
