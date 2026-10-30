namespace EML.Core.Minecraft;

/// <summary>只读检查导入清单和可解析的运行依赖；检查结果不等于真实启动成功。</summary>
public interface IMinecraftInstanceChecker
{
    /// <summary>检查指定实例的清单文件，支持快速检查、完整哈希校验及取消。</summary>
    Task<InstanceCheckReport> CheckAsync(string instancePath, InstanceCheckMode mode = InstanceCheckMode.Full,
        IProgress<InstanceCheckProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>快速检查只判断路径和大小；完整检查会同时校验所有已声明的哈希。</summary>
public enum InstanceCheckMode { Quick, Full }

/// <summary>文件检查状态；已变化与无法确认的情况不作为通过处理。</summary>
public enum InstanceFileState
{
    Verified, PresentUnverified, Missing, SizeMismatch, HashMismatch,
    ChangedSinceImport, Unresolved, Unreadable, ChangedDuringCheck
}

/// <summary>保留文件需求和当前结果，供界面展示与后续下载计划使用。</summary>
public sealed record InstanceFileCheckResult(InstanceFileRequirement Requirement, InstanceFileState State, string Message)
{
    /// <summary>运行解析器派生的依赖与导入文件分开处理，避免使用旧的直接下载筛选重复安装。</summary>
    public bool IsRuntimeFile { get; init; }
    /// <summary>仅配置白名单中的包内变化可在最终准备检查时放行，不能把 JAR 变化误当成用户设置。</summary>
    public bool IsAllowedConfigurationChange { get; init; }
    /// <summary>优先显示相对路径，未解析的 CurseForge 项显示项目和文件 ID。</summary>
    public string PathDisplay => Requirement.Path ?? $"CurseForge {Requirement.ProjectId}/{Requirement.FileId}";

    /// <summary>将内部状态转换为界面可读的简短名称。</summary>
    public string StateDisplay => State switch
    {
        InstanceFileState.Verified => "校验通过",
        InstanceFileState.PresentUnverified => "存在，未校验内容",
        InstanceFileState.Missing => "缺失",
        InstanceFileState.SizeMismatch => "大小不符",
        InstanceFileState.HashMismatch => "哈希不符",
        InstanceFileState.ChangedSinceImport => "导入后已变化",
        InstanceFileState.Unresolved => "待解析来源",
        InstanceFileState.Unreadable => "无法读取",
        InstanceFileState.ChangedDuringCheck => "检查时发生变化",
        _ => "未知"
    };

    /// <summary>把是否必需独立显示，避免把可选文件缺失当作必需文件缺失。</summary>
    public string RequiredDisplay => Requirement.Required ? "必需" : "可选";
}

/// <summary>一次只读检查的快照，不写回导入清单，也不承诺实例能够启动。</summary>
public sealed record InstanceCheckReport
{
    public required string InstancePath { get; init; }
    public InstanceCheckMode Mode { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
    public bool HasManifest { get; init; }
    public IReadOnlyList<InstanceFileCheckResult> Files { get; init; } = [];
    public IReadOnlyList<InstanceComponentRequirement> Components { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public bool IsPrismRuntime { get; init; }
    /// <summary>已经接入运行依赖解析的来源格式；未知格式为空，兼容原 Prism 标记。</summary>
    public string? RuntimeSourceFormat { get; init; }
    public string? LaunchProfilePath { get; init; }
    public IReadOnlyList<string> RuntimeIssues { get; init; } = [];
}

/// <summary>按清单文件数量报告检查进度；读取大文件时仍会报告当前文件名。</summary>
public sealed record InstanceCheckProgress(int CompletedFiles, int TotalFiles, string FileName)
{
    /// <summary>把已处理文件数转换为百分比；空清单视为处理结束。</summary>
    public double Percent => TotalFiles == 0 ? 100 : Math.Clamp(100.0 * CompletedFiles / TotalFiles, 0, 100);
}
