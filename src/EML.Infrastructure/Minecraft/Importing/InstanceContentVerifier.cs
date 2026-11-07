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
                if (await CheckExistingAsync(root, manifest, file, token).ConfigureAwait(false) == InstanceFileState.Missing)
                    throw new FileNotFoundException("必需文件不存在。");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { issues.Add((file.Path ?? $"CurseForge {file.ProjectId}/{file.FileId}") + "：" + ex.Message); }
        }
        return issues;
    }

    /// <summary>安装器共享现有内容判断；只在安全读取后接受正常变化，明确缺失才应用消耗策略。</summary>
    internal static async Task<InstanceFileState> CheckExistingAsync(string root, InstanceImportManifest manifest,
        InstanceFileRequirement file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // 来源结构先于可变策略验证，防止将不合法的校验声明误当作用户修改。
        _ = InstanceFileIntegrity.HashesFor(file);
        var path = InstanceImportPaths.Within(root, file.Path ?? throw new InvalidDataException("文件目标仍未解析。"));
        InstanceImportPaths.EnsureNoLinks(path);
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) throw new IOException("实例路径不是目录。");
        try
        {
            ValidateFilePath(root, file.Path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return MissingState();
        }
        try
        {
            if (await InstanceRuntimeInstaller.ExistingFileMatchesAsync(path, file, token).ConfigureAwait(false))
                return InstanceFileState.Verified;
            // 路径刚验证存在却在打开前消失属于竞态，不能假称标记已正常消耗。
            throw new IOException("文件在验证期间消失，请重新检查。");
        }
        catch (InvalidDataException) when (InstanceFileChangePolicy.IsMutableFile(file, manifest.GameDirectory, manifest.SourceFormat))
        {
            ValidateFilePath(root, file.Path);
            return InstanceFileState.ChangedSinceImport;
        }

        /// <summary>完整性声明未变时才把已知空更新标记缺失解释为正常消耗。</summary>
        InstanceFileState MissingState() => InstanceFileChangePolicy.AllowsMissing(file, manifest.GameDirectory, manifest.SourceFormat)
            ? InstanceFileState.Consumed : InstanceFileState.Missing;
    }

    /// <summary>逐层验证目录类型和链接；只有真实不存在抛缺失异常，父路径是文件等错误保持阻断。</summary>
    internal static void ValidateFilePath(string root, string relative)
    {
        var current = root;
        var rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0 || (rootAttributes & FileAttributes.Directory) == 0)
            throw new IOException("实例目录已变化或包含链接。");
        var segments = relative.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("路径含符号链接或目录联接。");
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (i < segments.Length - 1 && !isDirectory) throw new IOException("文件路径中的父目录实际是一个文件。");
            if (i == segments.Length - 1 && isDirectory) throw new IOException("预期文件的位置实际是一个文件夹。");
        }
    }
}
