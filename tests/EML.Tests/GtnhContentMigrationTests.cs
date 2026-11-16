using System.Security.Cryptography;
using System.Text;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;
using EML.Core.Minecraft.Migration;
using EML.Infrastructure.Minecraft.Gtnh;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;

namespace EML.Tests;

public sealed partial class GtnhMigrationTests
{
    /// <summary>目标官方资源无论存在或缺失都保留来源，开启覆盖冲突也不能用旧实例文件替代。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationNeverOverwritesTargetRegisteredShader(bool missing)
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "shaderpacks/official.zip", "old unofficial bytes");
        var baseline = DeclareAddedShader(target, "official.zip", "new official bytes");
        if (missing) File.Delete(Path.Combine(target.Game, "shaderpacks/official.zip"));
        var plan = await PlanAsync(old, target, "shaders");
        var file = Assert.Single(plan.Files);
        Assert.Equal(MigrationFileAction.Skip, file.Action); Assert.Contains("新版", file.Detail!);
        Assert.Contains(plan.Notes, note => note.Contains("保留现有来源"));
        Assert.Equal("NoChanges", (await Service.MigrateAsync(plan, true)).Status);
        Assert.Equal(missing, !File.Exists(Path.Combine(target.Game, "shaderpacks/official.zip")));
        if (!missing) Assert.Equal("new official bytes", Read(target.Game, "shaderpacks/official.zip"));
        var manifest = InstanceManifestStore.Read(Path.Combine(target.Path, InstanceImportManifest.FileName));
        Assert.Equal(baseline.Hashes, Assert.Single(AddedContentStore.Read(target.Path, manifest).Files).Baseline.Hashes);
        Assert.Empty(MigrationDataStore.Read(target.Path, manifest).Files);
        Assert.Single(InstanceEffectiveRequirements.Build(target.Path, manifest).Requirements);
    }

    /// <summary>旧版官方光影明确提示重新选择适配版本，不会因游戏目录不同而降级为本地玩家数据。</summary>
    [Theory]
    [InlineData(".")]
    [InlineData("minecraft")]
    [InlineData(".minecraft")]
    public async Task SourceRegisteredShaderIsExplicitlySkippedWithoutLosingOrigin(string game)
    {
        var old = await CreateAsync("old", game); var target = await CreateAsync("new", "minecraft");
        var baseline = DeclareAddedShader(old, "official.zip", "official shader");
        var plan = await PlanAsync(old, target, "shaders");
        var file = Assert.Single(plan.Files);
        Assert.Equal(MigrationFileAction.Skip, file.Action);
        Assert.Contains("重新选择适配版本下载", file.Detail!); Assert.Contains("保留官方来源", file.ActionDisplay);
        Assert.Contains(plan.Notes, note => note.Contains("不转为玩家数据"));
        Assert.Equal("NoChanges", (await Service.MigrateAsync(plan, true)).Status);
        Assert.False(File.Exists(Path.Combine(target.Game, "shaderpacks/official.zip")));
        var sourceManifest = InstanceManifestStore.Read(Path.Combine(old.Path, InstanceImportManifest.FileName));
        Assert.Equal(baseline.Hashes, Assert.Single(AddedContentStore.Read(old.Path, sourceManifest).Files).Baseline.Hashes);
        Assert.False(File.Exists(Path.Combine(target.Path, MigrationDataStore.RelativePath)));
        Assert.False(File.Exists(Path.Combine(target.Path, AddedContentStore.RelativePath)));
    }

    /// <summary>已登记资源被玩家改写时仍明确跳过，不将改写后的摘要保存到新实例。</summary>
    [Fact]
    public async Task ModifiedRegisteredShaderCannotBecomeTrustedMigratedData()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        var baseline = DeclareAddedShader(old, "official.zip", "original shader");
        Write(old.Game, "shaderpacks/official.zip", "modified shader");
        var plan = await PlanAsync(old, target, "shaders");
        Assert.Equal(MigrationFileAction.Skip, Assert.Single(plan.Files).Action);
        Assert.Equal("NoChanges", (await Service.MigrateAsync(plan, true)).Status);
        Assert.False(File.Exists(Path.Combine(target.Game, "shaderpacks/official.zip")));
        Assert.Equal(baseline.Hashes, Assert.Single(AddedContentStore.Read(old.Path,
            InstanceManifestStore.Read(Path.Combine(old.Path, InstanceImportManifest.FileName))).Files).Baseline.Hashes);
    }

    /// <summary>预览后任一侧新增官方资源来源，旧预览失效，即使文件字节未发生变化也不继续迁移。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddedResourceRegistrationInvalidatesMigrationPreview(bool sourceChanged)
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        Write(old.Game, "shaderpacks/official.zip", "shader");
        Write(target.Game, "shaderpacks/official.zip", "shader");
        var plan = await PlanAsync(old, target, "shaders");
        DeclareAddedShader(sourceChanged ? old : target, "official.zip", "shader");
        await Assert.ThrowsAsync<IOException>(() => Service.MigrateAsync(plan, true));
        Assert.Equal("shader", Read(target.Game, "shaderpacks/official.zip"));
    }

    /// <summary>跳过官方光影不会阻止普通光影和设置按原有规则迁移。</summary>
    [Fact]
    public async Task UnmanagedShadersAndSettingsStillMigrateAlongsideProtectedResource()
    {
        var old = await CreateAsync("old"); var target = await CreateAsync("new");
        DeclareAddedShader(old, "official.zip", "official shader");
        Write(old.Game, "shaderpacks/personal.zip", "personal shader");
        Write(old.Game, "config/shaders.properties", "shaderPack=personal.zip");
        var plan = await PlanAsync(old, target, "shaders");
        Assert.Equal(MigrationFileAction.Skip, plan.Files.Single(file => file.SourcePath.EndsWith("official.zip")).Action);
        Assert.Equal("Completed", (await Service.MigrateAsync(plan, true)).Status);
        Assert.Equal("personal shader", Read(target.Game, "shaderpacks/personal.zip"));
        Assert.Equal("shaderPack=personal.zip", Read(target.Game, "config/shaders.properties"));
        Assert.False(File.Exists(Path.Combine(target.Game, "shaderpacks/official.zip")));
        var manifest = InstanceManifestStore.Read(Path.Combine(target.Path, InstanceImportManifest.FileName));
        Assert.DoesNotContain(MigrationDataStore.Read(target.Path, manifest).Files, file => file.Path.EndsWith("official.zip"));
    }

    /// <summary>以固定测试字节建立官方来源，迁移仅使用该记录识别保护范围，不执行 ZIP 内容。</summary>
    private static InstanceFileRequirement DeclareAddedShader(Instance instance, string name, string content)
    {
        Write(instance.Game, "shaderpacks/" + name, content);
        var baseline = new InstanceFileRequirement
        {
            Path = (instance.Relative == "." ? "" : instance.Relative + "/") + "shaderpacks/" + name,
            Source = InstanceFileSource.CurseForge, Origin = InstanceFileOrigin.CurseForgeDownload,
            ProjectId = 123, FileId = 456, Size = Encoding.UTF8.GetByteCount(content), Role = InstanceFileRole.Content,
            Hashes = new Dictionary<string, string> { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(content))) },
            Downloads = ["https://edge.forgecdn.net/files/1/2/" + name]
        };
        var manifest = InstanceManifestStore.Read(Path.Combine(instance.Path, InstanceImportManifest.FileName));
        AddedContentStore.Write(instance.Path, manifest, [new(GtnhAddonKind.Shader, baseline)]);
        return baseline;
    }
}
