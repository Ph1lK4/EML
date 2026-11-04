namespace EML.Core.Minecraft;

/// <summary>按阶段汇总安装结果，防止分类问题或失败文件被外层包装器遗漏。</summary>
public static class InstanceInstallResultPolicy
{
    /// <summary>合并内容与运行阶段；保留已准备配置，仅在必要内容和运行依赖均通过时发布启动路径。</summary>
    public static PrismInstallResult Merge(PrismInstallResult content, PrismInstallResult runtime)
    {
        var runtimeFiles = runtime.Files.Select(file => file with { IsRuntimeFile = true }).ToArray();
        var contentIssues = content.ContentIssues.Concat(runtime.ContentIssues)
            .Concat(content.Issues.Except(content.RuntimeIssues))
            .Concat(content.Files.Where(file => !file.IsRuntimeFile && InstanceFileStatePolicy.IsFailure(file)).Select(Describe))
            .Distinct().ToArray();
        var runtimeIssues = content.RuntimeIssues.Concat(runtime.RuntimeIssues)
            .Concat(runtime.Issues.Except(runtime.ContentIssues))
            .Concat(runtimeFiles.Where(InstanceFileStatePolicy.IsFailure).Select(Describe))
            .Concat(content.Files.Where(file => file.IsRuntimeFile && InstanceFileStatePolicy.IsFailure(file)).Select(Describe))
            .Distinct().ToList();
        var prepared = runtime.PreparedLaunchProfilePath ?? runtime.LaunchProfilePath;
        if (!content.IsCanceled && !runtime.IsCanceled && prepared is null && runtimeIssues.Count == 0)
            runtimeIssues.Add("运行依赖安装未生成启动配置，请查看组件解析结果。");
        var result = new PrismInstallResult
        {
            SourceFormat = content.SourceFormat, Files = content.Files.Concat(runtimeFiles).ToArray(),
            ContentIssues = contentIssues, RuntimeIssues = runtimeIssues,
            Issues = contentIssues.Concat(runtimeIssues).Distinct().ToArray(),
            Notes = content.Notes.Concat(runtime.Notes).Distinct().ToArray(),
            IsCanceled = content.IsCanceled || runtime.IsCanceled, PreparedLaunchProfilePath = prepared
        };
        return result with
        {
            LaunchProfilePath = result.ContentReady && result.RuntimeReady && runtime.LaunchReady
                ? runtime.LaunchProfilePath : null
        };
    }

    /// <summary>把失败文件转换为保留路径和原因的阶段问题。</summary>
    private static string Describe(InstanceDownloadFileResult file) => file.PathDisplay + "：" + file.Message;
}
