using EML.Core.Minecraft;

namespace EML.Tests;

/// <summary>验证来源、用途、变化和安装阶段的统一合同，避免跨服务重新放宽完整性要求。</summary>
public sealed class UnifiedFilePolicyTests
{
    /// <summary>根命名空间才是派生状态，游戏内容中同名目录不被排除。</summary>
    [Theory]
    [InlineData(".eml-runtime/curseforge/files.json", true)]
    [InlineData(".EML-RUNTIME\\source.json", true)]
    [InlineData(".minecraft/.eml-runtime/file.jar", false)]
    [InlineData(".eml-runtime-old/file.jar", false)]
    public void DerivedStateNamespaceIsAnchored(string path, bool expected) =>
        Assert.Equal(expected, InstanceFileChangePolicy.IsDerivedStatePath(path));

    /// <summary>Prism 名称文件按用途允许变化；同名普通内容和嵌入覆盖仍需严格匹配。</summary>
    [Fact]
    public void PrismMetadataDoesNotRequireRebaseliningTheImportManifest()
    {
        var file = Embedded("instance.cfg");
        Assert.Equal(InstanceFileRole.LauncherMetadata, InstanceFileChangePolicy.GetRole(file, ".minecraft", "Prism"));
        Assert.True(InstanceFileChangePolicy.IsMutableConfiguration(file, ".minecraft", "Prism"));
        Assert.False(InstanceFileChangePolicy.IsMutableConfiguration(file, ".minecraft", "Zip"));
        Assert.False(InstanceFileChangePolicy.IsMutableConfiguration(file with { Origin = InstanceFileOrigin.EmbeddedOverride }, ".minecraft", "Prism"));
    }

    /// <summary>用途标签不能解除明确来源或本地替换的校验要求。</summary>
    [Theory]
    [InlineData(InstanceFileOrigin.EmbeddedOverride)]
    [InlineData(InstanceFileOrigin.UserLocalImport)]
    [InlineData(InstanceFileOrigin.CurseForgeDownload)]
    [InlineData(InstanceFileOrigin.ModrinthDownload)]
    public void ExplicitOriginCannotBeExemptedByConfigurationRole(InstanceFileOrigin origin)
    {
        var file = Embedded(".minecraft/config/settings.cfg") with { Origin = origin, Role = InstanceFileRole.UserConfiguration };
        Assert.False(InstanceFileChangePolicy.IsMutableConfiguration(file, ".minecraft", "Modrinth"));
    }

    /// <summary>配置兼容范围精确到已知路径，不能借用途把模组或其他配置名任意放行。</summary>
    [Theory]
    [InlineData(".minecraft/config/settings.cfg", true)]
    [InlineData(".minecraft/defaultconfigs/settings.cfg", true)]
    [InlineData(".minecraft/options.txt", true)]
    [InlineData(".minecraft/unknown/settings.cfg", false)]
    [InlineData(".minecraft/mods/config.jar", false)]
    [InlineData("other/config/settings.cfg", false)]
    public void ConfigurationRulesStayWithinKnownGamePaths(string path, bool expected) =>
        Assert.Equal(expected, InstanceFileChangePolicy.IsMutableConfiguration(
            Embedded(path), ".minecraft", "Prism"));

    /// <summary>不允许用伪造的用途标签跳过任意内容文件的完整性校验。</summary>
    [Theory]
    [InlineData(InstanceFileRole.DerivedState)]
    [InlineData(InstanceFileRole.LauncherMetadata)]
    [InlineData(InstanceFileRole.UserConfiguration)]
    public void RoleCannotTurnOrdinaryModIntoMutableOrGeneratedState(InstanceFileRole role) =>
        Assert.Throws<InvalidDataException>(() => InstanceFileChangePolicy.GetRole(
            Embedded(".minecraft/mods/test.jar") with { Role = role }, ".minecraft", "Prism"));

    /// <summary>可变配置只有已存在的正常变化可以通过，缺失和读取错误仍阻断。</summary>
    [Theory]
    [InlineData(InstanceFileState.ChangedSinceImport, false)]
    [InlineData(InstanceFileState.Missing, true)]
    [InlineData(InstanceFileState.MissingEmbeddedFile, true)]
    [InlineData(InstanceFileState.Unreadable, true)]
    [InlineData(InstanceFileState.ChangedDuringCheck, true)]
    public void MutableFilesStillHaveExistenceAndReadabilityRequirements(InstanceFileState state, bool blocking)
    {
        var result = new InstanceFileCheckResult(Embedded(".minecraft/config/test.cfg"), state, "test")
            { IsAllowedConfigurationChange = true };
        Assert.Equal(blocking, InstanceFileStatePolicy.IsBlocking(result));
    }

    /// <summary>外层错误设置配置标记，也不能放行带明确来源的内容变化。</summary>
    [Fact]
    public void ExplicitSourceStillBlocksWhenCallerSuppliesAnInconsistentChangeFlag()
    {
        var result = new InstanceFileCheckResult(Embedded(".minecraft/config/test.cfg") with
        {
            Origin = InstanceFileOrigin.EmbeddedOverride
        }, InstanceFileState.ChangedSinceImport, "changed") { IsAllowedConfigurationChange = true };
        Assert.True(InstanceFileStatePolicy.IsBlocking(result));
    }

    /// <summary>没有总问题列表的分类故障也必须传递，内容故障不掩盖运行阶段已经完成。</summary>
    [Fact]
    public void MergePreservesContentProblemsAndRuntimeProgress()
    {
        var result = InstanceInstallResultPolicy.Merge(new() { SourceFormat = "Modrinth", ContentIssues = ["missing mod"] }, ReadyRuntime());
        Assert.False(result.ContentReady);
        Assert.True(result.RuntimeReady);
        Assert.False(result.FilesReady);
        Assert.Equal("launch.json", result.PreparedLaunchProfilePath);
        Assert.Null(result.LaunchProfilePath);
        Assert.Contains("missing mod", result.Issues);
    }

    /// <summary>下层仅返回分类问题时，也不能被外层安装器包装为成功。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MergeCannotDropClassifiedRuntimeResultIssues(bool runtimeIssue)
    {
        var runtime = ReadyRuntime() with
        {
            RuntimeIssues = runtimeIssue ? ["runtime failure"] : [],
            ContentIssues = runtimeIssue ? [] : ["content failure"]
        };
        var result = InstanceInstallResultPolicy.Merge(new(), runtime);
        Assert.False(result.LaunchReady);
        Assert.Null(result.LaunchProfilePath);
        Assert.Equal(!runtimeIssue, result.RuntimeReady);
        Assert.Equal(runtimeIssue, result.ContentReady);
    }

    /// <summary>即使下层完全漏填问题列表，必需运行文件失败也不会丢失。</summary>
    [Fact]
    public void MergeDerivesRuntimeFailureFromRequiredFileResult()
    {
        var runtime = ReadyRuntime() with { Files = [new(Embedded("libraries/a.jar"), InstanceDownloadState.Failed, "bad hash")] };
        var result = InstanceInstallResultPolicy.Merge(new(), runtime);
        Assert.False(result.RuntimeReady);
        Assert.True(result.ContentReady);
        Assert.False(result.LaunchReady);
        Assert.Single(result.RuntimeIssues);
        Assert.True(Assert.Single(result.Files).IsRuntimeFile);
    }

    /// <summary>可选内容失败不阻断必要运行依赖，但必须保留文件结果供用户查看。</summary>
    [Fact]
    public void OptionalFailureDoesNotBlockFinalReadiness()
    {
        var content = new PrismInstallResult
        {
            Files = [new(Embedded("mods/optional.jar") with { Required = false }, InstanceDownloadState.Unavailable, "optional")]
        };
        var result = InstanceInstallResultPolicy.Merge(content, ReadyRuntime());
        Assert.True(result.LaunchReady);
        Assert.Single(result.Files);
    }

    /// <summary>运行配置已生成不等于最终允许启动，合并不得自作主张发布准备路径。</summary>
    [Fact]
    public void PreparedOnlyPathIsNeverPromotedToLaunchPermission()
    {
        var result = InstanceInstallResultPolicy.Merge(new(), new() { PreparedLaunchProfilePath = "launch.json" });
        Assert.True(result.RuntimeReady);
        Assert.False(result.LaunchReady);
        Assert.Null(result.LaunchProfilePath);
    }

    /// <summary>内容阶段取消时运行阶段尚未执行，不能伪造缺少启动配置的运行失败。</summary>
    [Fact]
    public void ContentCancellationDoesNotInventFailureForAnUnstartedRuntimeStage()
    {
        var result = InstanceInstallResultPolicy.Merge(new() { IsCanceled = true }, new());
        Assert.True(result.IsCanceled);
        Assert.False(result.ContentReady);
        Assert.False(result.RuntimeReady);
        Assert.False(result.LaunchReady);
        Assert.Empty(result.RuntimeIssues);
        Assert.Empty(result.Issues);
    }

    /// <summary>快速检查和没有清单都不能作为最终启动依据。</summary>
    [Theory]
    [InlineData(InstanceCheckMode.Quick, true)]
    [InlineData(InstanceCheckMode.Full, false)]
    public void IncompleteChecksCannotPublishReadiness(InstanceCheckMode mode, bool hasManifest)
    {
        var report = new InstanceCheckReport { InstancePath = "test", Mode = mode, HasManifest = hasManifest, LaunchProfilePath = "launch.json" };
        Assert.False(report.LaunchReady);
    }

    /// <summary>构造仅用于状态与用途测试的普通包内需求。</summary>
    private static InstanceFileRequirement Embedded(string path) => new() { Path = path, Source = InstanceFileSource.Embedded };

    /// <summary>构造独立运行阶段已成功的契约结果。</summary>
    private static PrismInstallResult ReadyRuntime() => new() { LaunchProfilePath = "launch.json" };
}
