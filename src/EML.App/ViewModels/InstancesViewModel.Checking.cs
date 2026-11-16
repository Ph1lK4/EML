using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
using EML.Core.Minecraft;

namespace EML.App.ViewModels;

/// <summary>实例检查和补全的界面状态；与重命名、导入等操作共用忙碌状态，防止同时改写实例。</summary>
public partial class InstancesViewModel
{
    private readonly IMinecraftInstanceChecker _checker;
    private readonly IMinecraftInstanceDownloader _instanceDownloader;
    private CancellationTokenSource? _downloadCancellation;

    [ObservableProperty] private IReadOnlyList<InstanceFileCheckResult> checkFiles = [];
    [ObservableProperty] private IReadOnlyList<InstanceDownloadFileResult> downloadFiles = [];
    [ObservableProperty] private string checkSummary = "请选择实例并执行文件检查。";
    [ObservableProperty] private string checkNotes = "";
    [ObservableProperty] private string downloadSummary = "Prism、Modrinth 和 CurseForge 会补全支持的运行依赖；CurseForge 受限文件会尝试 Modrinth 同内容来源，无法精确匹配时提示手动处理。";
    [ObservableProperty] private int selectedDetailTab;

    /// <summary>切换、刷新或写入实例后使旧快照失效，避免把历史结果当成当前状态。</summary>
    private void ClearCheckResults()
    {
        CheckFiles = [];
        DownloadFiles = [];
        CheckSummary = "尚未检查；快速检查确认路径和大小，完整检查还会计算哈希。";
        CheckNotes = "只读检查导入清单以及 Prism、Modrinth、CurseForge 的本地缓存。首次补全会联网取得缺少的元数据和文件。";
        DownloadSummary = "Prism、Modrinth 和 CurseForge 会补全支持的运行依赖；CurseForge 受限文件会尝试 Modrinth 同内容来源，无法精确匹配时提示手动处理。";
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
            var problems = report.Files.Where(NeedsFileAttention).ToArray();
            var hasIssues = !report.HasManifest || problems.Length > 0 || report.ContentIssues.Count > 0 || report.RuntimeIssues.Count > 0;
            _notifications.Report(CreateOperationNotice(hasIssues ? OperationNoticeKind.Warning : OperationNoticeKind.Success,
                hasIssues ? "文件检查完成，有待处理项" : "文件检查完成",
                CheckSummary + "\n" + StatusText + "\n" + string.Join("\n", problems.Select(file => $"{file.PathDisplay}：{file.Message}")
                    .Concat(report.ContentIssues).Concat(report.RuntimeIssues)),
                new[] { instance.InstancePath }.Concat(problems.Select(file => NoticeFilePath(instance.InstancePath, file.Requirement.Path)))));
        }
        catch (OperationCanceledException) { TransferStatus = "已取消检查"; StatusText = "本次检查未完成，请重新检查。"; _notifications.Cancelled("文件检查已取消", StatusText, instance.InstancePath); }
        catch (Exception ex) { TransferStatus = "检查失败"; CheckSummary = "未获得有效的检查结果。"; StatusText = ex.Message; _notifications.Failure("文件检查失败", ex, instance.InstancePath); }
        finally { ++_transferRevision; IsBusy = false; }
    }

    /// <summary>重新检查后下载缺失项，再完整复查；取消时保留已完成文件并清空过期的检查状态。</summary>
    [RelayCommand(CanExecute = nameof(CanManage), IncludeCancelCommand = true)]
    private async Task DownloadMissingAsync(CancellationToken token)
    {
        if (!CanManage()) return;
        var instancePath = SelectedInstance!.InstancePath;
        IsBusy = true;
        try { _notifications.Report(await RunDownloadAsync(instancePath, token)); }
        finally { IsBusy = false; }
    }

    /// <summary>手动补全与导入后的补全使用同一个取消入口。</summary>
    [RelayCommand(CanExecute = nameof(CanCancelDependencyDownload))]
    private void CancelDependencyDownload()
    {
        _downloadCancellation?.Cancel();
        CancelDependencyDownloadCommand.NotifyCanExecuteChanged();
    }

    /// <summary>仅当前补全阶段仍可取消时启用按钮。</summary>
    private bool CanCancelDependencyDownload() => _downloadCancellation is { IsCancellationRequested: false };

    /// <summary>对固定实例路径补全并复查；调用方持有忙碌状态，导入阶段无需执行另一个界面命令。</summary>
    private async Task<OperationNotice> RunDownloadAsync(string instancePath, CancellationToken operationToken, bool afterImport = false)
    {
        ClearCheckResults();
        SelectedDetailTab = 2;
        var revision = ++_transferRevision;
        TransferPercent = 0;
        TransferStatus = afterImport ? "导入完成，正在自动补全依赖……" : "正在重新检查缺失文件……";
        StatusText = afterImport ? "实例已导入，正在自动下载依赖并复查；取消后可继续补全。" :
            "已有文件将保留；下载失败的条目可以再次尝试。";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        _downloadCancellation = cancellation;
        CancelDependencyDownloadCommand.NotifyCanExecuteChanged();
        var token = cancellation.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var progress = new Progress<InstanceDownloadProgress>(p =>
            {
                if (revision != _transferRevision || !IsBusy) return;
                TransferPercent = p.Percent;
                TransferStatus = $"{p.Stage} {p.CompletedFiles}/{p.TotalFiles}  {p.FileName}";
                if (p.Download is { } download)
                    TransferStatus += $"  {download.BytesReceived / 1048576.0:F1} MiB";
            });
            var report = await _instanceDownloader.DownloadMissingAsync(instancePath, progress, token);
            ++_transferRevision;
            DownloadFiles = report.Files;
            if (report.CheckReport is { } checkReport) ApplyCheckReport(checkReport);
            var downloaded = report.Files.Count(f => f.State == InstanceDownloadState.Downloaded);
            var unverified = report.Files.Count(f => f.State == InstanceDownloadState.DownloadedUnverified);
            var restored = report.Files.Count(f => f.State == InstanceDownloadState.RestoredEmbeddedFile);
            var failed = report.Files.Count(f => f.State == InstanceDownloadState.Failed);
            var unavailable = report.Files.Count(f => f.State is InstanceDownloadState.Unavailable or InstanceDownloadState.MissingEmbeddedFile);
            DownloadSummary = $"已下载并校验 {downloaded} 项，原包恢复 {restored} 项，来源无哈希 {unverified} 项，下载失败 {failed} 项，需要处理 {unavailable} 项。";
            if (report.Runtime is { } runtime)
            {
                var finalCheckPassed = report.LaunchReady;
                DownloadSummary += runtime.FilesReady && finalCheckPassed ? $"\n{runtime.SourceFormat} 运行文件与启动配置已准备，需使用匹配 Java 由启动模块启动。" :
                    runtime.FilesReady ? $"\n{runtime.SourceFormat} 启动配置已生成，但最终复查尚未通过，请查看文件检查结果。" :
                    runtime.RuntimeReady ? $"\n{runtime.SourceFormat} 运行依赖已准备，内容仍需修复。" :
                    $"\n{runtime.SourceFormat} 尚未完成准备：" + string.Join("；", runtime.Issues
                        .Concat(runtime.ContentIssues).Concat(runtime.RuntimeIssues).Distinct().Take(5));
                if (runtime.Notes.Count > 0) DownloadSummary += "\n" + string.Join("\n", runtime.Notes.Take(5));
                if (runtime.ContentIssues.Count > 0) DownloadSummary += $"\n内容文件待修复 {runtime.ContentIssues.Count} 项，修复前不能启动。";
                if (runtime.RuntimeIssues.Count > 0) DownloadSummary += $"\n运行依赖待处理 {runtime.RuntimeIssues.Count} 项。";
                if ((runtime.PreparedLaunchProfilePath ?? runtime.LaunchProfilePath) is { } profile)
                    DownloadSummary += "\n已准备的运行配置：" + profile;
            }
            if (report.IsCanceled || report.CheckError is not null)
            {
                DownloadSummary += report.IsCanceled ? " 本次下载或复查已取消，已完成文件保留，请重新检查。" :
                    " 复查未完成，请重新检查：" + report.CheckError;
                TransferStatus = report.IsCanceled ? "已取消下载或复查" : "复查未完成";
                StatusText = "已完成文件保留，详细原因见上方补全结果；请重新检查后继续。";
                return DownloadNotice(report, instancePath, afterImport);
            }
            if (report.CheckReport is { HasManifest: false })
                DownloadSummary = "没有 eml-instance.json，缺少检查和下载依据，本次没有下载文件。";
            TransferPercent = 100;
            TransferStatus = report.Runtime is { FilesReady: false } ? "补全结束，仍有待处理的内容文件或运行依赖" : "下载流程和复查已结束";
            StatusText = "补全结果见上方列表；请在“文件检查”中查看复查结果及待处理依赖。";
            return DownloadNotice(report, instancePath, afterImport);
        }
        catch (OperationCanceledException)
        {
            TransferStatus = "已取消下载或复查";
            DownloadSummary = "已经完成的下载会保留；本次未获得最终检查结果，请重新检查。";
            StatusText = DownloadSummary;
            return CreateOperationNotice(OperationNoticeKind.Cancelled, afterImport ? "实例已导入，补全已取消" : "补全已取消",
                (afterImport ? "实例已导入并保留。\n" : "") + DownloadSummary, [instancePath]);
        }
        catch (Exception ex)
        {
            TransferStatus = "下载流程未完成";
            DownloadSummary = "已完成的文件会保留；请重新检查后重试。";
            StatusText = ex.Message + " " + DownloadSummary;
            return CreateOperationNotice(afterImport ? OperationNoticeKind.Warning : OperationNoticeKind.Failure,
                afterImport ? "实例已导入，补全未完成" : "补全失败",
                (afterImport ? "实例已导入并保留。\n" : "") + StatusText, [instancePath, NoticeExceptionPath(ex)]);
        }
        finally
        {
            ++_transferRevision;
            _downloadCancellation = null;
            CancelDependencyDownloadCommand.NotifyCanExecuteChanged();
            if (afterImport)
                StatusText = "实例已导入并保留。" + StatusText + " 未完成时可点击“补全实例依赖”继续。";
        }
    }

    /// <summary>把整批下载和复查的真实状态汇总为一次通知，保留每项原因及可复制文件路径。</summary>
    private OperationNotice DownloadNotice(InstanceDownloadReport report, string instancePath, bool afterImport)
    {
        var pendingDownloads = report.Files.Where(file => file.State is InstanceDownloadState.Failed or
            InstanceDownloadState.Unavailable or InstanceDownloadState.MissingEmbeddedFile or InstanceDownloadState.DownloadedUnverified).ToArray();
        var pendingChecks = report.CheckReport?.Files.Where(NeedsFileAttention).ToArray() ?? [];
        var issues = pendingDownloads.Select(file => $"{file.PathDisplay}：{file.Message}")
            .Concat(pendingChecks.Select(file => $"{file.PathDisplay}：{file.Message}"))
            .Concat(report.CheckReport?.ContentIssues ?? []).Concat(report.CheckReport?.RuntimeIssues ?? [])
            .Concat(report.Runtime?.Issues ?? []).Concat(report.Runtime?.ContentIssues ?? []).Concat(report.Runtime?.RuntimeIssues ?? [])
            .Distinct(StringComparer.Ordinal).ToArray();
        var kind = report.IsCanceled ? OperationNoticeKind.Cancelled : report.CheckError is not null ? OperationNoticeKind.Failure :
            report.LaunchReady && pendingDownloads.Length == 0 && pendingChecks.Length == 0 && issues.Length == 0
                ? OperationNoticeKind.Success : OperationNoticeKind.Warning;
        var title = kind switch
        {
            OperationNoticeKind.Success => afterImport ? "实例导入与补全完成" : "实例补全完成",
            OperationNoticeKind.Cancelled => afterImport ? "实例已导入，补全已取消" : "补全已取消",
            _ => afterImport ? "实例已导入，补全仍有待处理项" : "补全仍有待处理项"
        };
        var message = (afterImport ? "实例已导入并保留。\n" : "") + DownloadSummary;
        if (issues.Length > 0) message += "\n\n待处理原因：\n" + string.Join("\n", issues);
        if (!report.LaunchReady && issues.Length == 0 && !report.IsCanceled && report.CheckError is null)
            message += "\n完整的启动就绪条件尚未确认，请查看文件检查与运行依赖结果。";
        return CreateOperationNotice(kind, title, message, new[] { instancePath,
                report.Runtime?.PreparedLaunchProfilePath ?? report.Runtime?.LaunchProfilePath }
            .Concat(pendingDownloads.Select(file => NoticeFilePath(instancePath, file.Requirement.Path)))
            .Concat(pendingChecks.Select(file => NoticeFilePath(instancePath, file.Requirement.Path))));
    }

    /// <summary>允许的用户配置变化和游戏消耗不当作损坏，其余待处理事实进入结果通知。</summary>
    private static bool NeedsFileAttention(InstanceFileCheckResult file) => !file.IsAllowedConfigurationChange && !file.IsAllowedMissing &&
        file.State is not (InstanceFileState.Verified or InstanceFileState.PresentUnverified or InstanceFileState.Consumed);

    /// <summary>将清单中的文件路径与本次固定实例配对，缺少路径的平台引用仍通过正文解释。</summary>
    private static string? NoticeFilePath(string instancePath, string? relative) =>
        string.IsNullOrEmpty(relative) ? null : System.IO.Path.Combine(instancePath, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>保留文件异常明确给出的路径，不从错误文字推测路径，也不展示内部异常。</summary>
    private static string? NoticeExceptionPath(Exception error) => error switch
    {
        System.IO.FileNotFoundException file => file.FileName,
        System.IO.FileLoadException file => file.FileName,
        _ => null
    };

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
            var missing = report.Files.Count(f => f.State is (InstanceFileState.Missing or InstanceFileState.MissingEmbeddedFile) && f.Requirement.Required);
            var optional = report.Files.Count(f => f.State is (InstanceFileState.Missing or InstanceFileState.MissingEmbeddedFile) && !f.Requirement.Required);
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
        if ((report.PreparedLaunchProfilePath ?? report.LaunchProfilePath) is { } launch)
            CheckNotes += "\n已生成运行配置：" + launch + (report.LaunchReady ? "" : "（最终文件检查尚未通过）");
    }
}
