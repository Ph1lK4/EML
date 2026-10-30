using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.Core.Minecraft;

namespace EML.App.ViewModels;

/// <summary>实例检查和补全的界面状态；与重命名、导入等操作共用忙碌状态，防止同时改写实例。</summary>
public partial class InstancesViewModel
{
    private readonly IMinecraftInstanceChecker _checker;
    private readonly IMinecraftInstanceDownloader _instanceDownloader;

    [ObservableProperty] private IReadOnlyList<InstanceFileCheckResult> checkFiles = [];
    [ObservableProperty] private IReadOnlyList<InstanceDownloadFileResult> downloadFiles = [];
    [ObservableProperty] private string checkSummary = "请选择实例并执行文件检查。";
    [ObservableProperty] private string checkNotes = "";
    [ObservableProperty] private string downloadSummary = "Prism、Modrinth 和 CurseForge 会补全支持的运行依赖；CurseForge 使用内置授权直接下载，受限制文件会提示手动处理。";
    [ObservableProperty] private int selectedDetailTab;

    /// <summary>切换、刷新或写入实例后使旧快照失效，避免把历史结果当成当前状态。</summary>
    private void ClearCheckResults()
    {
        CheckFiles = [];
        DownloadFiles = [];
        CheckSummary = "尚未检查；快速检查确认路径和大小，完整检查还会计算哈希。";
        CheckNotes = "只读检查导入清单以及 Prism、Modrinth、CurseForge 的本地缓存。首次补全会联网取得缺少的元数据和文件。";
        DownloadSummary = "Prism、Modrinth 和 CurseForge 会补全支持的运行依赖；CurseForge 使用内置授权直接下载，受限制文件会提示手动处理。";
    }

    /// <summary>检查文件存在性和大小，不读取全部文件计算哈希。</summary>
    [RelayCommand(CanExecute = nameof(CanManage), IncludeCancelCommand = true)]
    private Task QuickCheckAsync(CancellationToken token) => CheckAsync(InstanceCheckMode.Quick, token);

    /// <summary>流式计算清单声明的哈希，识别同样大小但内容已变化的文件。</summary>
    [RelayCommand(CanExecute = nameof(CanManage), IncludeCancelCommand = true)]
    private Task FullCheckAsync(CancellationToken token) => CheckAsync(InstanceCheckMode.Full, token);

    /// <summary>统一处理两种检查的进度、取消和异常，只发布本次完成的结果。</summary>
    private async Task CheckAsync(InstanceCheckMode mode, CancellationToken token)
    {
        if (!CanManage()) return;
        var instance = SelectedInstance!;
        ClearCheckResults();
        SelectedDetailTab = 1;
        IsBusy = true;
        var revision = ++_transferRevision;
        TransferPercent = 0;
        TransferStatus = mode == InstanceCheckMode.Full ? "正在完整检查……" : "正在快速检查……";
        StatusText = "正在读取清单和文件，请暂时不要修改实例内容。";
        try
        {
            var progress = new Progress<InstanceCheckProgress>(p =>
            {
                if (revision != _transferRevision || !IsBusy) return;
                TransferPercent = p.Percent;
                TransferStatus = $"检查 {p.CompletedFiles}/{p.TotalFiles}  {p.FileName}";
            });
            var report = await _checker.CheckAsync(instance.InstancePath, mode, progress, token);
            ++_transferRevision;
            ApplyCheckReport(report);
            TransferPercent = 100;
            TransferStatus = "文件检查结束";
            StatusText = "结果是本次检查的文件快照，不代表全部运行依赖齐全或一定能启动。";
        }
        catch (OperationCanceledException) { TransferStatus = "已取消检查"; StatusText = "本次检查未完成，请重新检查。"; }
        catch (Exception ex) { TransferStatus = "检查失败"; CheckSummary = "未获得有效的检查结果。"; StatusText = ex.Message; }
        finally { ++_transferRevision; IsBusy = false; }
    }

    /// <summary>重新检查后下载缺失项，再完整复查；取消时保留已完成文件并清空过期的检查状态。</summary>
    [RelayCommand(CanExecute = nameof(CanManage), IncludeCancelCommand = true)]
    private async Task DownloadMissingAsync(CancellationToken token)
    {
        if (!CanManage()) return;
        var instance = SelectedInstance!;
        ClearCheckResults();
        SelectedDetailTab = 2;
        IsBusy = true;
        var revision = ++_transferRevision;
        TransferPercent = 0;
        TransferStatus = "正在重新检查缺失文件……";
        StatusText = "已有文件将保留；下载失败的条目可以再次尝试。";
        try
        {
            var progress = new Progress<InstanceDownloadProgress>(p =>
            {
                if (revision != _transferRevision || !IsBusy) return;
                TransferPercent = p.Percent;
                TransferStatus = $"{p.Stage} {p.CompletedFiles}/{p.TotalFiles}  {p.FileName}";
                if (p.Download is { } download)
                    TransferStatus += $"  {download.BytesReceived / 1048576.0:F1} MiB";
            });
            var report = await _instanceDownloader.DownloadMissingAsync(instance.InstancePath, progress, token);
            ++_transferRevision;
            DownloadFiles = report.Files;
            if (report.CheckReport is { } checkReport) ApplyCheckReport(checkReport);
            var downloaded = report.Files.Count(f => f.State == InstanceDownloadState.Downloaded);
            var unverified = report.Files.Count(f => f.State == InstanceDownloadState.DownloadedUnverified);
            var failed = report.Files.Count(f => f.State == InstanceDownloadState.Failed);
            var unavailable = report.Files.Count(f => f.State == InstanceDownloadState.Unavailable);
            DownloadSummary = $"已下载并校验 {downloaded} 项，来源无哈希 {unverified} 项，下载失败 {failed} 项，需要处理 {unavailable} 项。";
            if (report.Runtime is { } runtime)
            {
                var finalCheckPassed = !report.IsCanceled && report.CheckError is null &&
                    report.CheckReport is { HasManifest: true, LaunchProfilePath: not null, RuntimeIssues.Count: 0 } finalCheck &&
                    !finalCheck.Files.Any(f => f.Requirement.Required &&
                        f.State is not (InstanceFileState.Verified or InstanceFileState.PresentUnverified) &&
                        !(f.State == InstanceFileState.ChangedSinceImport && f.IsAllowedConfigurationChange));
                DownloadSummary += runtime.FilesReady && finalCheckPassed ? $"\n{runtime.SourceFormat} 运行文件与启动配置已准备，需使用匹配 Java 由启动模块启动。" :
                    runtime.FilesReady ? $"\n{runtime.SourceFormat} 启动配置已生成，但最终复查尚未通过，请查看文件检查结果。" :
                    $"\n{runtime.SourceFormat} 尚未完成准备：" + string.Join("；", runtime.Issues.Take(5));
                if (runtime.Notes.Count > 0) DownloadSummary += "\n" + string.Join("\n", runtime.Notes.Take(5));
                if (runtime.LaunchProfilePath is { } profile) DownloadSummary += "\n启动配置：" + profile;
            }
            if (report.IsCanceled || report.CheckError is not null)
            {
                DownloadSummary += report.IsCanceled ? " 本次下载或复查已取消，已完成文件保留，请重新检查。" :
                    " 复查未完成，请重新检查：" + report.CheckError;
                TransferStatus = report.IsCanceled ? "已取消下载或复查" : "复查未完成";
                StatusText = "已完成文件保留，详细原因见上方补全结果；请重新检查后继续。";
                return;
            }
            if (report.CheckReport is { HasManifest: false })
                DownloadSummary = "没有 eml-instance.json，缺少检查和下载依据，本次没有下载文件。";
            TransferPercent = 100;
            TransferStatus = report.Runtime is { FilesReady: false } ? "补全结束，仍有待处理的运行依赖" : "下载流程和复查已结束";
            StatusText = "补全结果见上方列表；请在“文件检查”中查看复查结果及待处理依赖。";
        }
        catch (OperationCanceledException)
        {
            TransferStatus = "已取消下载或复查";
            DownloadSummary = "已经完成的下载会保留；本次未获得最终检查结果，请重新检查。";
            StatusText = DownloadSummary;
        }
        catch (Exception ex)
        {
            TransferStatus = "下载流程未完成";
            DownloadSummary = "已完成的文件会保留；请重新检查后重试。";
            StatusText = ex.Message + " " + DownloadSummary;
        }
        finally { ++_transferRevision; IsBusy = false; }
    }

    /// <summary>展示检查范围和各类结果；包内基线变化单列，不将用户设置改动称为损坏。</summary>
    private void ApplyCheckReport(InstanceCheckReport report)
    {
        CheckFiles = report.Files;
        if (!report.HasManifest)
            CheckSummary = "没有 eml-instance.json，缺少文件检查依据，无法判断完整性。";
        else
        {
            var verified = report.Files.Count(f => f.State == InstanceFileState.Verified);
            var unverified = report.Files.Count(f => f.State == InstanceFileState.PresentUnverified);
            var missing = report.Files.Count(f => f.State == InstanceFileState.Missing && f.Requirement.Required);
            var optional = report.Files.Count(f => f.State == InstanceFileState.Missing && !f.Requirement.Required);
            var changed = report.Files.Count(f => f.State == InstanceFileState.ChangedSinceImport);
            var mismatch = report.Files.Count(f => f.State is InstanceFileState.SizeMismatch or InstanceFileState.HashMismatch);
            var unresolved = report.Files.Count(f => f.State == InstanceFileState.Unresolved);
            var unreadable = report.Files.Count(f => f.State is InstanceFileState.Unreadable or InstanceFileState.ChangedDuringCheck);
            CheckSummary = $"{(report.Mode == InstanceCheckMode.Full ? "完整" : "快速")}检查 · {report.CheckedAt.ToLocalTime():MM-dd HH:mm:ss} · 共 {report.Files.Count} 项\n" +
                $"哈希通过 {verified}，存在但未校验 {unverified}，缺失必需 {missing} / 可选 {optional}，" +
                $"导入后变化 {changed}，大小或哈希不符 {mismatch}，待解析 {unresolved}，无法确认 {unreadable}。";
        }
        var runtimeFormat = report.RuntimeSourceFormat ?? (report.IsPrismRuntime ? "Prism" : null);
        var componentText = report.Components.Count == 0 ? "" : (runtimeFormat is not null ? $"\n{runtimeFormat} 组件：" : "\n待展开的组件：") +
            string.Join("、", report.Components.Take(12).Select(c => $"{c.Id} {c.Version}")) +
            (report.Components.Count > 12 ? $" 等 {report.Components.Count} 项" : "");
        CheckNotes = string.Join("\n", report.Notes) + componentText;
        if (report.LaunchProfilePath is { } launch) CheckNotes += "\n已生成启动配置：" + launch;
    }
}
