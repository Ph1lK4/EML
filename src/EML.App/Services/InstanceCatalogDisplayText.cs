using EML.Core.Minecraft.Catalog;

namespace EML.App.Services;

/// <summary>整合包名称和简介使用平台原文，不用本地翻译表替换作者内容。</summary>
public static class InstanceCatalogDisplayText
{
    /// <summary>平台提供什么名称就显示什么名称，原版只增加中文类型说明。</summary>
    public static string Name(InstanceCatalogProject project) =>
        project.Provider == InstanceCatalogProvider.Minecraft ? $"我的世界 Java 版（{project.Name}）" : project.Name;

    /// <summary>中文、英文及双语简介都按平台提供的内容展示，不额外生成翻译。</summary>
    public static string Summary(InstanceCatalogProject project) => project.Summary;

    /// <summary>只移除首尾空白；中文关键词由所选平台搜索，不再依赖收录名单。</summary>
    public static string SearchTerm(string input) => input.Trim();
}
