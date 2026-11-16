using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Downloads;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft;
using EML.Infrastructure.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Mods;

namespace EML.Tests;

/// <summary>独立临时实例验证资源登记、官方校验、备份与缺失恢复，不访问网络或真实玩家数据。</summary>
public sealed class GtnhContentRegistrarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-gtnh-content-" + Guid.NewGuid().ToString("N"));
    private string Instance => Path.Combine(_root, "instances", "GTNH");
    private string Source => Path.Combine(_root, "incoming.zip");
    private GtnhContentRegistrar Service => new(_root);

    /// <summary>两个仓库同叫 default.zip 时使用不同本地名，官方文件名、地址和摘要均不改写。</summary>
    [Fact]
    public async Task GithubDefaultResourcePacksCoexistWithOriginalOrigins()
    {
        await InitializeAsync();
        var first = DefaultGithubBaseline("ModernityGTNH/Modernity-GTNH", 101);
        var second = DefaultGithubBaseline("ModernityGTNH/Modernity-GTNH-Dark-UI", 202);
        await Service.RegisterDownloadedAsync(Instance, Source, first, GtnhAddonKind.ResourcePack);
        await Service.RegisterDownloadedAsync(Instance, Source, second, GtnhAddonKind.ResourcePack);

        Assert.NotEqual(first.Path, second.Path);
        var records = AddedContentStore.Read(Instance, Manifest()).Files;
        Assert.Equal(2, records.Count);
        foreach (var baseline in new[] { first, second })
        {
            var stored = Assert.Single(records, record => record.Baseline.Path == baseline.Path).Baseline;
            Assert.Equal(baseline.GithubReleaseOrigin, stored.GithubReleaseOrigin);
            Assert.Equal("default.zip", stored.GithubReleaseOrigin!.FileName);
            Assert.Equal(baseline.Downloads, stored.Downloads);
            Assert.Equal(baseline.Hashes, stored.Hashes);
            Assert.Equal(await File.ReadAllBytesAsync(Source), await File.ReadAllBytesAsync(Path.Combine(Instance, stored.Path!)));
        }
        Assert.All((await new MinecraftInstanceChecker(_root).CheckAsync(Instance)).Files,
            file => Assert.Equal(InstanceFileState.Verified, file.State));
    }

    /// <summary>同仓库的不同资产也具有固定独立别名，大小写不同的仓库身份不会产生额外名字。</summary>
    [Fact]
    public void DefaultAliasIsDeterministicAndAssetSpecific()
    {
        var origin = new GithubReleaseFileOrigin { Repository = "ModernityGTNH/Modernity-GTNH-Dark-UI",
            Tag = "v1", ReleaseId = 1, AssetId = 202, FileName = "default.zip" };
        var name = GithubResourceFileName.ForInstall(GtnhAddonKind.ResourcePack, origin);
        Assert.Equal("eml-modernitygtnh--modernity-gtnh-dark-ui-202.zip", name);
        Assert.Equal(name, GithubResourceFileName.ForInstall(GtnhAddonKind.ResourcePack,
            origin with { Repository = origin.Repository.ToLowerInvariant() }));
        Assert.NotEqual(name, GithubResourceFileName.ForInstall(GtnhAddonKind.ResourcePack, origin with { AssetId = 203 }));
        Assert.Equal("default.zip", GithubResourceFileName.ForInstall(GtnhAddonKind.Shader, origin));
        Assert.Equal("named.zip", GithubResourceFileName.ForInstall(GtnhAddonKind.ResourcePack, origin with { FileName = "named.zip" }));
    }

    /// <summary>别名资源缺失时仍从官方原名 URL 下载，写回别名路径并按原 SHA-256 检查。</summary>
    [Fact]
    public async Task MissingAliasedResourceRestoresUsingOfficialFilenameAndHash()
    {
        await InitializeAsync();
        var baseline = DefaultGithubBaseline("ModernityGTNH/Modernity-GTNH-Dark-UI", 202);
        AddedContentStore.Write(Instance, Manifest(), [new(GtnhAddonKind.ResourcePack, baseline)]);
        var checker = new MinecraftInstanceChecker(_root);
        Assert.Equal(InstanceFileState.Missing, Assert.Single((await checker.CheckAsync(Instance)).Files).State);
        var downloader = new DownloadFixture(await File.ReadAllBytesAsync(Source));

        await new MinecraftInstanceDownloader(_root, checker, downloader, zipInstaller: new EmptyInstaller()).DownloadMissingAsync(Instance);

        var request = Assert.Single(downloader.Requests);
        Assert.Equal(baseline.Hashes, request.Hashes);
        Assert.Equal(baseline.Downloads, request.Urls);
        Assert.EndsWith("/default.zip", Assert.Single(request.Urls));
        Assert.True(File.Exists(Path.Combine(Instance, baseline.Path!)));
        Assert.Equal(InstanceFileState.Verified, Assert.Single((await checker.CheckAsync(Instance)).Files).State);
    }

    /// <summary>备份往返既保存别名路径也保留官方来源，导入后检查仍按同一份官方需求执行。</summary>
    [Fact]
    public async Task BackupRoundTripPreservesGithubAliasAndOfficialIntegrity()
    {
        await InitializeAsync();
        var baseline = DefaultGithubBaseline("ModernityGTNH/Modernity-GTNH-Dark-UI", 202);
        await Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack);
        var archive = Path.Combine(_root, "aliased-backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(Instance, archive);
        var imported = await new MinecraftInstanceImporter(_root).ImportAsync(archive, "AliasedRestored");

        var stored = Assert.Single(imported.Manifest.AddedContentFiles!).Baseline;
        Assert.Equal(baseline.Path, stored.Path);
        Assert.Equal(baseline.GithubReleaseOrigin, stored.GithubReleaseOrigin);
        Assert.Equal(baseline.Downloads, stored.Downloads);
        Assert.Equal(baseline.Hashes, stored.Hashes);
        Assert.Equal(InstanceFileState.Verified, Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(imported.InstancePath)).Files).State);
    }

    /// <summary>旧数据里直接使用 default.zip 的记录继续有效，无需改写玩家现有文件。</summary>
    [Fact]
    public async Task ExistingDefaultFilenameRecordRemainsCompatible()
    {
        await InitializeAsync();
        var baseline = DefaultGithubBaseline("ModernityGTNH/Modernity-GTNH-Dark-UI", 202) with
            { Path = ".minecraft/resourcepacks/default.zip" };
        await Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack);
        var effective = Assert.Single(InstanceEffectiveRequirements.Build(Instance, Manifest()).Requirements);
        Assert.Equal(baseline.Path, effective.Path);
        Assert.Equal(baseline.GithubReleaseOrigin, effective.GithubReleaseOrigin);
        Assert.Equal(InstanceFileState.Verified, Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(Instance)).Files).State);
    }

    /// <summary>任意改名、不同仓库或资产编号的别名均不能伪装成当前官方文件。</summary>
    [Theory]
    [InlineData("renamed.zip")]
    [InlineData("eml-modernitygtnh--modernity-gtnh-dark-ui-203.zip")]
    [InlineData("eml-anotherowner--modernity-gtnh-dark-ui-202.zip")]
    [InlineData("eml-modernitygtnh--modernity-gtnh-dark-ui-202.zip/child.zip")]
    public async Task ArbitraryGithubAliasesAreRejected(string alias)
    {
        await InitializeAsync();
        var baseline = DefaultGithubBaseline("ModernityGTNH/Modernity-GTNH-Dark-UI", 202) with
            { Path = ".minecraft/resourcepacks/" + alias };
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack));
        Assert.Empty(AddedContentStore.Read(Instance, Manifest()).Files);
    }

    /// <summary>只为 GitHub 的 default.zip 资源包开放别名，其他原名、光影和 Modrinth 来源仍严格同名。</summary>
    [Theory]
    [InlineData("named")]
    [InlineData("shader")]
    [InlineData("modrinth")]
    public async Task AliasExceptionDoesNotApplyToOtherNamesKindsOrProviders(string variant)
    {
        await InitializeAsync();
        var baseline = DefaultGithubBaseline("ModernityGTNH/Modernity-GTNH-Dark-UI", 202);
        var kind = GtnhAddonKind.ResourcePack;
        if (variant == "named")
        {
            var origin = baseline.GithubReleaseOrigin! with { FileName = "named.zip" };
            baseline = baseline with { GithubReleaseOrigin = origin, Downloads = [InstanceFileIntegrity.GithubDownloadUrl(origin)] };
        }
        else if (variant == "shader")
        {
            kind = GtnhAddonKind.Shader;
            baseline = baseline with { Path = baseline.Path!.Replace("resourcepacks/", "shaderpacks/", StringComparison.Ordinal) };
        }
        else baseline = Baseline(GtnhAddonProvider.Modrinth) with
        {
            Path = baseline.Path,
            ModrinthOrigin = new() { ProjectId = "project1", VersionId = "version1", FileName = "default.zip" }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.RegisterDownloadedAsync(Instance, Source, baseline, kind));
        Assert.Empty(AddedContentStore.Read(Instance, Manifest()).Files);
    }

    /// <summary>别名不放宽官方内容验证，错误 SHA-256 仍不能登记或发布。</summary>
    [Fact]
    public async Task AliasedResourceStillRejectsWrongOfficialHash()
    {
        await InitializeAsync();
        var baseline = DefaultGithubBaseline("ModernityGTNH/Modernity-GTNH-Dark-UI", 202) with
            { Hashes = new Dictionary<string, string> { ["sha256"] = new string('0', 64) } };
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack));
        Assert.Empty(AddedContentStore.Read(Instance, Manifest()).Files);
        Assert.False(File.Exists(Path.Combine(Instance, baseline.Path!)));
    }

    /// <summary>为 default.zip 测试建立原始官方身份，只有实际目标路径使用共享别名规则。</summary>
    private InstanceFileRequirement DefaultGithubBaseline(string repository, long assetId)
    {
        var origin = new GithubReleaseFileOrigin { Repository = repository, Tag = "v1", ReleaseId = 1,
            AssetId = assetId, FileName = "default.zip" };
        return Baseline(GtnhAddonProvider.Github) with
        {
            Path = ".minecraft/resourcepacks/" + GithubResourceFileName.ForInstall(GtnhAddonKind.ResourcePack, origin),
            GithubReleaseOrigin = origin, Downloads = [InstanceFileIntegrity.GithubDownloadUrl(origin)]
        };
    }

    /// <summary>三个平台的资源与光影保留各自官方摘要，不改原始清单，也不登记进模组状态。</summary>
    [Theory]
    [InlineData(GtnhAddonProvider.Github, GtnhAddonKind.ResourcePack)]
    [InlineData(GtnhAddonProvider.CurseForge, GtnhAddonKind.ResourcePack)]
    [InlineData(GtnhAddonProvider.Modrinth, GtnhAddonKind.ResourcePack)]
    [InlineData(GtnhAddonProvider.Github, GtnhAddonKind.Shader)]
    [InlineData(GtnhAddonProvider.CurseForge, GtnhAddonKind.Shader)]
    [InlineData(GtnhAddonProvider.Modrinth, GtnhAddonKind.Shader)]
    public async Task RegistrationPreservesPlatformBaselineAndOriginalManifest(GtnhAddonProvider provider, GtnhAddonKind kind)
    {
        await InitializeAsync(kind);
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(Instance, InstanceImportManifest.FileName));
        var baseline = Baseline(provider, kind);
        await Service.RegisterDownloadedAsync(Instance, Source, baseline, kind);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(Path.Combine(Instance, InstanceImportManifest.FileName)));
        Assert.Equal(await File.ReadAllBytesAsync(Source), await File.ReadAllBytesAsync(Path.Combine(Instance, baseline.Path!)));
        var state = Assert.Single(AddedContentStore.Read(Instance, Manifest()).Files);
        Assert.Equal(kind, state.Kind);
        Assert.Equal(EmbeddedFileBaseline.RequirementFingerprint(baseline), EmbeddedFileBaseline.RequirementFingerprint(state.Baseline));
        var effective = Assert.Single(InstanceEffectiveRequirements.Build(Instance, Manifest()).Requirements);
        Assert.Equal(baseline.Hashes, effective.Hashes); Assert.Equal(baseline.Origin, effective.Origin);
        Assert.Empty(ModManagementStore.Read(Instance, Manifest()).Files);
        Assert.False(Directory.Exists(Path.Combine(Instance, ".minecraft", "mods")));
        var checkedFile = Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(Instance)).Files);
        Assert.Equal(InstanceFileState.Verified, checkedFile.State);
    }

    /// <summary>登记层独立核对下载内容，错误的大小或各平台官方摘要均不能留下可信记录。</summary>
    [Theory]
    [InlineData(GtnhAddonProvider.Github, false)]
    [InlineData(GtnhAddonProvider.CurseForge, false)]
    [InlineData(GtnhAddonProvider.Modrinth, false)]
    [InlineData(GtnhAddonProvider.Modrinth, true)]
    public async Task WrongOfficialIntegrityDoesNotPublishResource(GtnhAddonProvider provider, bool wrongSize)
    {
        await InitializeAsync(); var baseline = Baseline(provider);
        baseline = wrongSize ? baseline with { Size = baseline.Size + 1 } : baseline with
        { Hashes = baseline.Hashes.ToDictionary(pair => pair.Key, pair => new string('0', pair.Value.Length)) };
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack));
        Assert.Empty(AddedContentStore.Read(Instance, Manifest()).Files);
        Assert.False(File.Exists(Path.Combine(Instance, baseline.Path!))); Assert.True(File.Exists(Source));
    }

    /// <summary>资源注册只开放相应目录的直接 ZIP 文件，不能写模组、配置、子目录或另一个游戏目录。</summary>
    [Theory]
    [InlineData(".minecraft/mods/内容.zip", GtnhAddonKind.ResourcePack)]
    [InlineData(".minecraft/config/内容.zip", GtnhAddonKind.ResourcePack)]
    [InlineData("minecraft/resourcepacks/内容.zip", GtnhAddonKind.ResourcePack)]
    [InlineData(".minecraft/resourcepacks/sub/内容.zip", GtnhAddonKind.ResourcePack)]
    [InlineData(".minecraft/resourcepacks/内容.jar", GtnhAddonKind.ResourcePack)]
    [InlineData(".minecraft/resourcepacks/内容.zip", GtnhAddonKind.Shader)]
    [InlineData(".minecraft/resourcepacks/内容.zip", GtnhAddonKind.Mod)]
    [InlineData(".minecraft/shaderpacks/内容.zip", GtnhAddonKind.ShaderLoader)]
    public async Task ForeignPathsOrModKindsCannotUseResourceRegistration(string path, GtnhAddonKind kind)
    {
        await InitializeAsync();
        var baseline = Baseline(GtnhAddonProvider.CurseForge) with { Path = path };
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.RegisterDownloadedAsync(Instance, Source, baseline, kind));
        Assert.Empty(AddedContentStore.Read(Instance, Manifest()).Files);
    }

    /// <summary>GitHub ZIP 资源也不能反向借用模组注册入口写进 mods。</summary>
    [Fact]
    public async Task GithubResourceZipCannotUseModRegistration()
    {
        await InitializeAsync();
        var baseline = Baseline(GtnhAddonProvider.Github) with { Path = ".minecraft/mods/内容（测试）.zip" };
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalModManager(_root).RegisterDownloadedAsync(Instance, Source, baseline));
        Assert.Empty(ModManagementStore.Read(Instance, Manifest()).Files);
        Assert.False(File.Exists(Path.Combine(Instance, baseline.Path!))); Assert.True(File.Exists(Source));
    }

    /// <summary>原包已声明的同名文件即使缺失也受保护，不能被新增资源记录取代。</summary>
    [Fact]
    public async Task OriginalPackRequirementCannotBeReplaced()
    {
        await InitializeAsync(); var baseline = Baseline(GtnhAddonProvider.CurseForge);
        await InstanceManifestStore.WriteAsync(Path.Combine(Instance, InstanceImportManifest.FileName),
            Manifest() with { Files = [baseline] }, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack));
        Assert.Empty(AddedContentStore.Read(Instance, Manifest()).Files);
        Assert.False(File.Exists(Path.Combine(Instance, baseline.Path!)));
    }

    /// <summary>未登记的现有文件保持原样，即使字节相同也不会被登记操作自动接管。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingUserFileIsNeverOverwrittenOrAdopted(bool sameContent)
    {
        await InitializeAsync(); var baseline = Baseline(GtnhAddonProvider.Modrinth);
        var target = Path.Combine(Instance, baseline.Path!); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var existing = sameContent ? await File.ReadAllBytesAsync(Source) : Encoding.UTF8.GetBytes("玩家自己的文件");
        await File.WriteAllBytesAsync(target, existing);
        var error = await Assert.ThrowsAsync<IOException>(() => Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack));
        Assert.Contains(Path.GetFullPath(target), error.Message);
        Assert.Equal(existing, await File.ReadAllBytesAsync(target));
        Assert.Empty(AddedContentStore.Read(Instance, Manifest()).Files);
    }

    /// <summary>官方摘要正确也必须是可读的 ZIP，不能将 HTML 错误页面等文件当成资源安装。</summary>
    [Fact]
    public async Task InvalidZipIsRejectedEvenWithMatchingDigest()
    {
        await InitializeAsync(); await File.WriteAllTextAsync(Source, "not a zip");
        var baseline = Baseline(GtnhAddonProvider.Modrinth);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack));
        Assert.Empty(AddedContentStore.Read(Instance, Manifest()).Files);
    }

    /// <summary>错误种类、外层包装、普通文件或空着色器都不能被报告为已安装的可识别资源。</summary>
    [Theory]
    [InlineData(GtnhAddonKind.ResourcePack, "readme.txt", "manual")]
    [InlineData(GtnhAddonKind.ResourcePack, "包裹目录/pack.mcmeta", "{\"pack\":{\"pack_format\":1}}")]
    [InlineData(GtnhAddonKind.ResourcePack, "shaders/gbuffers_basic.vsh", "void main(){}")]
    [InlineData(GtnhAddonKind.Shader, "pack.mcmeta", "{\"pack\":{\"pack_format\":1}}")]
    [InlineData(GtnhAddonKind.Shader, "包裹目录/shaders/gbuffers_basic.fsh", "void main(){}")]
    [InlineData(GtnhAddonKind.Shader, "shaders/readme.txt", "manual")]
    [InlineData(GtnhAddonKind.Shader, "shaders/gbuffers_basic.fsh", "")]
    [InlineData(GtnhAddonKind.Shader, "shaders/../gbuffers_basic.vsh", "void main(){}")]
    public async Task UnrecognizedArchiveStructureIsRejectedWithoutRepacking(GtnhAddonKind kind, string entry, string content)
    {
        await InitializeAsync(); WriteArchive((entry, content));
        var original = await File.ReadAllBytesAsync(Source); var baseline = Baseline(GtnhAddonProvider.Modrinth, kind);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service.RegisterDownloadedAsync(Instance, Source, baseline, kind));
        Assert.Contains("作者页", error.Message); Assert.Contains("手动处理压缩结构", error.Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(Source));
        Assert.Empty(AddedContentStore.Read(Instance, Manifest()).Files);
        Assert.False(File.Exists(Path.Combine(Instance, baseline.Path!)));
    }

    /// <summary>根元数据必须是有界有效 JSON，并提供数值型正整数 pack_format。</summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"pack\":{}}")]
    [InlineData("{\"pack\":{\"pack_format\":\"1\"}}")]
    [InlineData("{\"pack\":{\"pack_format\":0}}")]
    [InlineData("{\"pack\":{\"pack_format\":1.5}}")]
    [InlineData("{\"pack\":{\"pack_format\":2147483648}}")]
    public async Task InvalidPackMetadataIsRejected(string metadata)
    {
        await InitializeAsync(); WriteArchive(("pack.mcmeta", metadata));
        var baseline = Baseline(GtnhAddonProvider.Modrinth);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack));
        Assert.Contains("手动处理压缩结构", error.Message);
        Assert.Empty(AddedContentStore.Read(Instance, Manifest()).Files);
    }

    /// <summary>重复或超限元数据明确拒绝，不能任取一个条目或无限解压小型配置。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousOrOversizedPackMetadataIsRejected(bool oversized)
    {
        await InitializeAsync();
        const string valid = "{\"pack\":{\"pack_format\":1}}";
        if (oversized) WriteArchive(("pack.mcmeta", valid + new string(' ', 256 * 1024)));
        else WriteArchive(("pack.mcmeta", valid), ("pack.mcmeta", valid));
        var baseline = Baseline(GtnhAddonProvider.Modrinth);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack));
        Assert.Contains("pack.mcmeta", error.Message); Assert.Contains("作者页", error.Message);
        Assert.Empty(AddedContentStore.Read(Instance, Manifest()).Files);
    }

    /// <summary>GTNH 使用的格式一和带 BOM 的合法元数据可读；不把结构检查当作游戏版本兼容结论。</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(55, false)]
    public async Task ValidPackMetadataIsAcceptedWithoutChangingBytes(int format, bool bom)
    {
        await InitializeAsync();
        WriteArchive(("pack.mcmeta", (bom ? "\uFEFF" : "") + "{\"pack\":{\"pack_format\":" + format + ",\"description\":\"测试\"}}"));
        var original = await File.ReadAllBytesAsync(Source); var baseline = Baseline(GtnhAddonProvider.Modrinth);
        await Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(Instance, baseline.Path!)));
    }

    /// <summary>光影的维度子目录中的真实程序文件仍属于合法 shaders 入口。</summary>
    [Fact]
    public async Task ShaderProgramInDimensionDirectoryIsRecognized()
    {
        await InitializeAsync(GtnhAddonKind.Shader);
        WriteArchive(("shaders/world0/gbuffers_basic.fsh", "#version 120\nvoid main(){gl_FragData[0]=vec4(1.0);}"));
        var baseline = Baseline(GtnhAddonProvider.Modrinth, GtnhAddonKind.Shader);
        await Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.Shader);
        Assert.True(File.Exists(Path.Combine(Instance, baseline.Path!)));
    }

    /// <summary>备份往返完整保留新增资源来源和官方摘要，不重建为包内 SHA-256。</summary>
    [Theory]
    [InlineData(GtnhAddonProvider.Github)]
    [InlineData(GtnhAddonProvider.CurseForge)]
    [InlineData(GtnhAddonProvider.Modrinth)]
    public async Task ExportImportKeepsIndependentOfficialContent(GtnhAddonProvider provider)
    {
        await InitializeAsync(GtnhAddonKind.Shader); var baseline = Baseline(provider, GtnhAddonKind.Shader);
        await Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.Shader);
        var archive = Path.Combine(_root, "backup.zip");
        await new MinecraftInstanceManager(_root).ExportAsync(Instance, archive);
        var imported = await new MinecraftInstanceImporter(_root).ImportAsync(archive, "Restored");
        var stored = Assert.Single(imported.Manifest.AddedContentFiles!);
        Assert.Equal(GtnhAddonKind.Shader, stored.Kind);
        Assert.Equal(baseline.Origin, stored.Baseline.Origin); Assert.Equal(baseline.Source, stored.Baseline.Source);
        Assert.Equal(baseline.Hashes, stored.Baseline.Hashes);
        Assert.Equal(baseline.GithubReleaseOrigin, stored.Baseline.GithubReleaseOrigin);
        Assert.Equal(baseline.ModrinthOrigin, stored.Baseline.ModrinthOrigin);
        Assert.Equal(baseline.ProjectId, stored.Baseline.ProjectId); Assert.Equal(baseline.FileId, stored.Baseline.FileId);
        Assert.DoesNotContain(imported.Manifest.Files, file => file.Path == baseline.Path);
        Assert.False(File.Exists(Path.Combine(imported.InstancePath, AddedContentStore.RelativePath)));
        Assert.Equal(InstanceFileState.Verified, Assert.Single((await new MinecraftInstanceChecker(_root).CheckAsync(imported.InstancePath)).Files).State);
    }

    /// <summary>来源已保存但文件移动前中断时，检查报告缺失，通用补全使用原官方来源恢复。</summary>
    [Theory]
    [InlineData(GtnhAddonProvider.Github)]
    [InlineData(GtnhAddonProvider.CurseForge)]
    [InlineData(GtnhAddonProvider.Modrinth)]
    public async Task RecordedButMissingContentIsRepairedFromOfficialBaseline(GtnhAddonProvider provider)
    {
        await InitializeAsync(); var baseline = Baseline(provider);
        AddedContentStore.Write(Instance, Manifest(), [new(GtnhAddonKind.ResourcePack, baseline)]);
        var checker = new MinecraftInstanceChecker(_root);
        Assert.Equal(InstanceFileState.Missing, Assert.Single((await checker.CheckAsync(Instance)).Files).State);
        var downloader = new DownloadFixture(await File.ReadAllBytesAsync(Source));
        await new MinecraftInstanceDownloader(_root, checker, downloader, zipInstaller: new EmptyInstaller()).DownloadMissingAsync(Instance);
        var request = Assert.Single(downloader.Requests);
        Assert.Equal(baseline.Hashes, request.Hashes); Assert.Equal(baseline.Downloads, request.Urls);
        Assert.Equal(await File.ReadAllBytesAsync(Source), await File.ReadAllBytesAsync(Path.Combine(Instance, baseline.Path!)));
        Assert.Equal(InstanceFileState.Verified, Assert.Single((await checker.CheckAsync(Instance)).Files).State);
    }

    /// <summary>中断后被未知内容占用的目标不会因已有来源记录被信任或覆盖。</summary>
    [Fact]
    public async Task RecordedContentDoesNotTrustOrOverwriteUnknownExistingBytes()
    {
        await InitializeAsync(); var baseline = Baseline(GtnhAddonProvider.CurseForge);
        AddedContentStore.Write(Instance, Manifest(), [new(GtnhAddonKind.ResourcePack, baseline)]);
        var target = Path.Combine(Instance, baseline.Path!); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var changed = await File.ReadAllBytesAsync(Source); changed[^1] ^= 1; await File.WriteAllBytesAsync(target, changed);
        var checker = new MinecraftInstanceChecker(_root);
        Assert.NotEqual(InstanceFileState.Verified, Assert.Single((await checker.CheckAsync(Instance)).Files).State);
        var downloader = new DownloadFixture(await File.ReadAllBytesAsync(Source));
        await new MinecraftInstanceDownloader(_root, checker, downloader, zipInstaller: new EmptyInstaller()).DownloadMissingAsync(Instance);
        Assert.Empty(downloader.Requests); Assert.Equal(changed, await File.ReadAllBytesAsync(target));
    }

    /// <summary>一次中断留下的完全相同官方记录允许重新安装缺失文件，不产生重复来源。</summary>
    [Fact]
    public async Task SameTrustedRecordCanResumeMissingFileRegistration()
    {
        await InitializeAsync(); var baseline = Baseline(GtnhAddonProvider.Modrinth);
        AddedContentStore.Write(Instance, Manifest(), [new(GtnhAddonKind.ResourcePack, baseline)]);
        await Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack);
        Assert.Single(AddedContentStore.Read(Instance, Manifest()).Files);
        Assert.True(File.Exists(Path.Combine(Instance, baseline.Path!)));
    }

    /// <summary>其他导入清单的资源记录不能沿用到当前实例。</summary>
    [Fact]
    public async Task SidecarFromDifferentManifestIsRejected()
    {
        await InitializeAsync(); var baseline = Baseline(GtnhAddonProvider.Modrinth);
        MigrationIO.WriteJson(Path.Combine(Instance, AddedContentStore.RelativePath),
            new AddedContentStore.State(1, new string('0', 64), [new(GtnhAddonKind.ResourcePack, baseline)]));
        Assert.Throws<InvalidDataException>(() => InstanceEffectiveRequirements.Build(Instance, Manifest()));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack));
        Assert.False(File.Exists(Path.Combine(Instance, baseline.Path!)));
    }

    /// <summary>操作期间新增资源来源变化会令旧检查快照失效，不能沿用先前的就绪结果。</summary>
    [Fact]
    public async Task SnapshotDetectsContentSourceChanges()
    {
        await InitializeAsync(); var manifest = Manifest(); var snapshot = InstanceEffectiveRequirements.Build(Instance, manifest);
        AddedContentStore.Write(Instance, manifest, [new(GtnhAddonKind.ResourcePack, Baseline(GtnhAddonProvider.Modrinth))]);
        Assert.Throws<IOException>(() => InstanceEffectiveRequirements.EnsureUnchanged(Instance, manifest, snapshot));
    }

    /// <summary>新增可选字段为空时不参与旧清单序列化，保持既有本地状态的绑定指纹。</summary>
    [Fact]
    public async Task EmptyAddedContentFieldDoesNotChangeLegacySerialization()
    {
        await InitializeAsync(); var manifest = Manifest();
        Assert.Null(manifest.AddedContentFiles);
        Assert.DoesNotContain("AddedContentFiles", JsonSerializer.Serialize(manifest));
        Assert.DoesNotContain("addedContentFiles", Encoding.UTF8.GetString(InstanceManifestStore.Serialize(manifest)));
    }

    /// <summary>游戏持有的同一个实例锁会阻止资源写入，不能绕开启动与迁移互斥。</summary>
    [Fact]
    public async Task InstanceLeasePreventsInstallation()
    {
        await InitializeAsync(); var baseline = Baseline(GtnhAddonProvider.Modrinth);
        using var lease = InstanceOperationLease.Acquire(Instance);
        await Assert.ThrowsAsync<IOException>(() => Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack));
        Assert.Empty(AddedContentStore.Read(Instance, Manifest()).Files);
    }

    /// <summary>未完成迁移或模组事务必须先恢复，资源登记不穿插写入。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnfinishedInstanceTransactionPreventsInstallation(bool modOperation)
    {
        await InitializeAsync(); var manifest = Manifest(); var baseline = Baseline(GtnhAddonProvider.Modrinth);
        if (modOperation)
            MigrationIO.WriteJson(Path.Combine(Instance, ModOperationJournal.RelativePath),
                new { schemaVersion = 1, id = Guid.NewGuid().ToString("N"), status = "Pending" });
        else MigrationJournalStore.Write(Instance, new()
        {
            Id = Guid.NewGuid().ToString("N"), GameDirectory = manifest.GameDirectory,
            ManifestFingerprint = EmbeddedFileBaseline.ManifestFingerprint(manifest), SourceName = "Old", Status = "Applying"
        });
        await Assert.ThrowsAnyAsync<Exception>(() => Service.RegisterDownloadedAsync(Instance, Source, baseline, GtnhAddonKind.ResourcePack));
        Assert.Empty(AddedContentStore.Read(Instance, manifest).Files);
        Assert.False(File.Exists(Path.Combine(Instance, baseline.Path!)));
    }

    /// <summary>预先取消不复制文件、不写来源记录，也不会删除下载暂存输入。</summary>
    [Fact]
    public async Task CancelledRegistrationDoesNotPublishAnything()
    {
        await InitializeAsync(); var baseline = Baseline(GtnhAddonProvider.Modrinth);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.RegisterDownloadedAsync(
            Instance, Source, baseline, GtnhAddonKind.ResourcePack, cancellation.Token));
        Assert.Empty(AddedContentStore.Read(Instance, Manifest()).Files); Assert.True(File.Exists(Source));
    }

    /// <summary>生成带 GTNH 身份特征的最小实例及无害资源 ZIP。</summary>
    private async Task InitializeAsync(GtnhAddonKind kind = GtnhAddonKind.ResourcePack)
    {
        Directory.CreateDirectory(Instance);
        foreach (var (relative, text) in new[] { ("config/txloader/load/mainmenu/version.txt", "GTNH 2.9.0"),
            ("config/GregTech/GregTech.cfg", "fixture"), ("config/bartworks.cfg", "fixture") })
        {
            var path = Path.Combine(Instance, ".minecraft", relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text);
        }
        await InstanceManifestStore.WriteAsync(Path.Combine(Instance, InstanceImportManifest.FileName), new()
        { Name = "GTNH", SourceFormat = "Zip", GameDirectory = ".minecraft", MinecraftVersion = "1.7.10" }, default);
        WriteArchive(kind == GtnhAddonKind.Shader
            ? [("shaders/gbuffers_basic.vsh", "#version 120\nvoid main(){gl_Position=ftransform();}")]
            : [("pack.mcmeta", "{\"pack\":{\"pack_format\":1,\"description\":\"测试资源\"}}")]);
    }

    /// <summary>在本测试输入文件中构造精确 ZIP 结构，以验证入口识别而不解压内容。</summary>
    private void WriteArchive(params (string Path, string Text)[] entries)
    {
        using var output = new FileStream(Source, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var (path, text) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
    }

    /// <summary>读取原清单，用于确认登记过程没有改写它。</summary>
    private InstanceImportManifest Manifest() => InstanceManifestStore.Read(Path.Combine(Instance, InstanceImportManifest.FileName));

    /// <summary>按测试平台建立严格的独立官方来源；摘要只来源于已知测试 ZIP。</summary>
    private InstanceFileRequirement Baseline(GtnhAddonProvider provider, GtnhAddonKind kind = GtnhAddonKind.ResourcePack)
    {
        const string name = "内容（测试）.zip";
        var bytes = File.ReadAllBytes(Source);
        var github = new GithubReleaseFileOrigin { Repository = "GTNewHorizons/TestResources", Tag = "v1", ReleaseId = 1, AssetId = 2, FileName = name };
        return new()
        {
            Path = ".minecraft/" + (kind == GtnhAddonKind.Shader ? "shaderpacks/" : "resourcepacks/") + name,
            Source = provider == GtnhAddonProvider.CurseForge ? InstanceFileSource.CurseForge : InstanceFileSource.DirectDownload,
            Origin = provider switch { GtnhAddonProvider.Github => InstanceFileOrigin.GithubReleaseDownload,
                GtnhAddonProvider.CurseForge => InstanceFileOrigin.CurseForgeDownload, _ => InstanceFileOrigin.ModrinthDownload },
            Role = InstanceFileRole.Content, Required = true, Size = bytes.Length,
            Hashes = provider switch
            {
                GtnhAddonProvider.Github => new Dictionary<string, string> { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) },
                GtnhAddonProvider.CurseForge => new Dictionary<string, string> { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(bytes)) },
                _ => new Dictionary<string, string> { ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(bytes)) }
            },
            Downloads = [provider == GtnhAddonProvider.Github ? InstanceFileIntegrity.GithubDownloadUrl(github) :
                "https://" + (provider == GtnhAddonProvider.CurseForge ? "edge.forgecdn.net" : "cdn.modrinth.com") + "/resources/" + Uri.EscapeDataString(name)],
            GithubReleaseOrigin = provider == GtnhAddonProvider.Github ? github : null,
            ModrinthOrigin = provider == GtnhAddonProvider.Modrinth ? new() { ProjectId = "project1", VersionId = "version1", FileName = name } : null,
            ProjectId = provider == GtnhAddonProvider.CurseForge ? 123 : null,
            FileId = provider == GtnhAddonProvider.CurseForge ? 456 : null
        };
    }

    /// <summary>替代网络下载并保存请求，断言补全仍使用原平台的官方摘要。</summary>
    private sealed class DownloadFixture(byte[] bytes) : IFileDownloader
    {
        public List<FileDownloadRequest> Requests { get; } = [];
        public async Task<FileDownloadResult> DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request); Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(request.DestinationPath, bytes, cancellationToken);
            return new(request.DestinationPath, bytes.Length, true);
        }
    }

    /// <summary>隔离运行环境安装，本组仅验证资源来源和文件恢复。</summary>
    private sealed class EmptyInstaller : IPrismInstanceInstaller
    {
        public Task<PrismInstallResult> InstallAsync(string instancePath, IProgress<InstanceDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new PrismInstallResult { SourceFormat = "Zip" });
    }

    /// <summary>只删除本测试随机创建的临时目录，不触及用户实例。</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
