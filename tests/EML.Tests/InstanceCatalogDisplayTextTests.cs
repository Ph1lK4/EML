using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Minecraft.Catalog;

namespace EML.Tests;

/// <summary>整合包展示仅使用平台原文，不以固定项目名单替换作者内容。</summary>
public sealed class InstanceCatalogDisplayTextTests
{
    /// <summary>平台返回的中文名称和简介直接显示，无需本地收录项目身份。</summary>
    [Theory]
    [InlineData(InstanceCatalogProvider.Modrinth, "unknown1")]
    [InlineData(InstanceCatalogProvider.CurseForge, "90000001")]
    public void PlatformChineseIsShownWithoutCuratedIdentity(InstanceCatalogProvider provider, string id)
    {
        var project = Project(provider, id, "作者的中文整合包名", "作者提供的中文简介。");
        var row = new InstanceDownloadRow(project);
        Assert.Same(project, row.Project);
        Assert.Equal(project.Name, row.DisplayName);
        Assert.Equal(project.Summary, row.DisplaySummary);
    }

    /// <summary>曾在本地目录中收录的项目也保留平台实时英文，不再强行套用旧中文。</summary>
    [Theory]
    [InlineData(InstanceCatalogProvider.Modrinth, "1KVo5zza", "Fabulously Optimized")]
    [InlineData(InstanceCatalogProvider.CurseForge, "252507", "GT New Horizons")]
    [InlineData(InstanceCatalogProvider.CurseForge, "466901", "Prominence II")]
    public void PreviouslyCuratedProjectUsesPlatformText(InstanceCatalogProvider provider, string id, string name)
    {
        var project = Project(provider, id, name, "The current author's English summary.");
        var row = new InstanceDownloadRow(project);
        Assert.Equal(name, row.DisplayName);
        Assert.Equal(project.Summary, row.DisplaySummary);
        Assert.Same(project, row.Project);
    }

    /// <summary>作者自己提供的中英双语名称与简介完整保留，不猜测或重复追加译名。</summary>
    [Fact]
    public void BilingualPlatformFieldsRemainUnchanged()
    {
        var project = Project(InstanceCatalogProvider.Modrinth, "testpack",
            "探索世界 / Explore the World", "建设工厂。Build factories. 日本語の紹介もあります。");
        Assert.Equal(project.Name, InstanceCatalogDisplayText.Name(project));
        Assert.Equal(project.Summary, InstanceCatalogDisplayText.Summary(project));
    }

    /// <summary>所有搜索词仅移除首尾空格，不把中文别名改写为维护者选择的英文项目。</summary>
    [Theory]
    [InlineData("  格雷科技新视野  ", "格雷科技新视野")]
    [InlineData("极致优化", "极致优化")]
    [InlineData("卓越2", "卓越2")]
    [InlineData("作者的新整合包", "作者的新整合包")]
    [InlineData("  Fabulously Optimized  ", "Fabulously Optimized")]
    [InlineData("1.21.1", "1.21.1")]
    [InlineData("  ", "")]
    public void SearchKeepsPlatformQuery(string input, string expected) =>
        Assert.Equal(expected, InstanceCatalogDisplayText.SearchTerm(input));

    /// <summary>无简介时不虚构中文文案，未知平台项目同样可正常显示名称。</summary>
    [Fact]
    public void EmptySummaryStaysEmpty()
    {
        var project = Project(InstanceCatalogProvider.Modrinth, "unknown1", "New Pack", "");
        Assert.Equal("New Pack", InstanceCatalogDisplayText.Name(project));
        Assert.Equal("", InstanceCatalogDisplayText.Summary(project));
    }

    /// <summary>官方原版继续使用页面中文标签，同时保留准确版本号和目录说明。</summary>
    [Fact]
    public void MinecraftLabelKeepsVersionAndCatalogSummary()
    {
        var project = Project(InstanceCatalogProvider.Minecraft, "1.7.10", "Minecraft 1.7.10", "正式版 · Java 版官方原版");
        var row = new InstanceDownloadRow(project);
        Assert.Contains("我的世界 Java 版", row.DisplayName);
        Assert.Contains("1.7.10", row.DisplayName);
        Assert.Equal(project.Summary, row.DisplaySummary);
        Assert.Same(project, row.Project);
    }

    /// <summary>已加载详情之后出现错误身份也必须保留原先详情，不允许替换成其他整合包。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrongDetailIdentityPreservesPreviousDetails(bool differentProvider)
    {
        var project = Project(InstanceCatalogProvider.Modrinth, "testpack", "Original name", "Original summary");
        var row = new InstanceDownloadRow(project);
        var accepted = project with { Name = "平台中文名", Summary = "平台中文简介", Description = "已核对身份的正文" };
        row.SetDetails(accepted);
        var invalid = accepted with
        {
            Id = differentProvider ? project.Id : "otherpack",
            Provider = differentProvider ? InstanceCatalogProvider.CurseForge : project.Provider,
            Name = "错误名称", Summary = "错误简介", Description = "错误正文"
        };
        Assert.Throws<InvalidDataException>(() => row.SetDetails(invalid));
        Assert.Same(project, row.Project);
        Assert.Same(accepted, row.Details);
        Assert.Equal("平台中文名", row.DisplayName);
        Assert.Equal("平台中文简介", row.DisplaySummary);
    }

    /// <summary>只创建平台展示数据，测试不访问实际项目或修改下载身份。</summary>
    private static InstanceCatalogProject Project(InstanceCatalogProvider provider, string id, string name, string summary) => new()
    { Provider = provider, Id = id, Name = name, Summary = summary };
}
