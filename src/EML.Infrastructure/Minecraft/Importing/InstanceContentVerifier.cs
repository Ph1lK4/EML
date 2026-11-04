using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Runtime;

namespace EML.Infrastructure.Minecraft.Importing;

/// <summary>安装后统一复核必需内容；只放行策略明确允许的配置变化，不放行来源声明损坏。</summary>
internal static class InstanceContentVerifier
{
    /// <summary>保持现有文件不变，返回缺失、完整性和访问问题；取消继续向上传递。</summary>
    internal static async Task<IReadOnlyList<string>> VerifyRequiredAsync(string root, InstanceImportManifest manifest,
        IReadOnlyList<InstanceFileRequirement> requirements, CancellationToken token)
    {
        var issues = new List<string>();
        foreach (var file in requirements.Where(file => file.Required && !InstanceFileChangePolicy.IsDerivedStatePath(file.Path)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                // 必须先验证来源结构；不能把结构异常误认为玩家修改了配置。
                _ = InstanceFileIntegrity.HashesFor(file);
                var path = InstanceImportPaths.Within(root, file.Path ?? throw new InvalidDataException("文件目标仍未解析。"));
                InstanceImportPaths.EnsureNoLinks(path);
                try
                {
                    if (!await InstanceRuntimeInstaller.ExistingFileMatchesAsync(path, file, token).ConfigureAwait(false))
                        throw new FileNotFoundException("必需文件不存在。");
                }
                catch (InvalidDataException) when (InstanceFileChangePolicy.IsMutableConfiguration(
                    file, manifest.GameDirectory, manifest.SourceFormat)) { }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { issues.Add((file.Path ?? $"CurseForge {file.ProjectId}/{file.FileId}") + "：" + ex.Message); }
        }
        return issues;
    }
}
