using EML.Core.Downloads;

namespace EML.Core.Minecraft;

/// <summary>根据最新检查结果补齐已知来源的必需文件；不替换现有文件。</summary>
public interface IMinecraftInstanceDownloader
{
    /// <summary>重新检查、顺序下载可校验的缺失文件，并返回完整复查结果；取消时保留已经完成的文件。</summary>
    Task<InstanceDownloadReport> DownloadMissingAsync(string instancePath,
        IProgress<InstanceDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>单个文件的处理结果；无法处理和下载失败都保留原因。</summary>
public enum InstanceDownloadState { Downloaded, Unavailable, Failed, DownloadedUnverified }

/// <summary>记录文件来源和本次下载结果，供界面列出需要手动处理的条目。</summary>
public sealed record InstanceDownloadFileResult(InstanceFileRequirement Requirement, InstanceDownloadState State, string Message)
{
    /// <summary>显示实例相对路径；平台尚未解析时展示来源引用。</summary>
    public string PathDisplay => Requirement.Path ?? $"CurseForge {Requirement.ProjectId}/{Requirement.FileId}（路径待解析）";
    /// <summary>将处理结果转换为简短的中文状态。</summary>
    public string StateDisplay => State switch
    {
        InstanceDownloadState.Downloaded => "已下载并校验",
        InstanceDownloadState.DownloadedUnverified => "已下载，来源无哈希",
        InstanceDownloadState.Unavailable => "需要处理",
        _ => "下载失败"
    };
}

/// <summary>下载记录和结束时的文件快照；文件检查通过不等于运行依赖齐全。</summary>
public sealed record InstanceDownloadReport(IReadOnlyList<InstanceDownloadFileResult> Files,
    InstanceCheckReport? CheckReport, bool IsCanceled = false, string? CheckError = null)
{
    /// <summary>Prism、Modrinth 或 CurseForge 安装结果，SourceFormat 区分来源；未接入的格式为 null。</summary>
    public PrismInstallResult? Runtime { get; init; }
}

/// <summary>分别报告准备、下载和复查阶段；每个阶段的百分比独立计算。</summary>
public sealed record InstanceDownloadProgress(string Stage, int CompletedFiles, int TotalFiles, string FileName,
    FileDownloadProgress? Download = null, InstanceCheckProgress? Check = null)
{
    /// <summary>准备和复查使用检查进度；下载按完成文件数及当前文件字节计算。</summary>
    public double Percent => Check is not null ? Check.Percent : TotalFiles <= 0 ? 0 :
        Math.Clamp(100.0 * (CompletedFiles + (Download?.TotalBytes is > 0
            ? Math.Min(1, (double)Download.BytesReceived / Download.TotalBytes.Value) : 0)) / TotalFiles, 0, 100);
}
