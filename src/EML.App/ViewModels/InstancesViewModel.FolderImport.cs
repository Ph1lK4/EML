using CommunityToolkit.Mvvm.Input;
using EML.App.Services;
using EML.Core.Minecraft;

namespace EML.App.ViewModels;

/// <summary>其他启动器的文件夹复制入口，使用独立服务但复用实例提交后的补全与结果通知。</summary>
public partial class InstancesViewModel
{
    private readonly IExternalInstanceDiscovery? _externalDiscovery;
    private readonly IMinecraftFolderImporter? _folderImporter;
    private readonly IExternalInstanceDialogs? _externalDialogs;

    /// <summary>仅在完整接入复制服务且没有其他实例操作时允许打开导入向导。</summary>
    private bool CanImportFolder() => !IsBusy && _externalDiscovery is not null && _folderImporter is not null && _externalDialogs is not null;

    /// <summary>发现、预览、复制与补全持续持有忙碌状态；提交后的失败或取消保留新实例。</summary>
    [RelayCommand(CanExecute = nameof(CanImportFolder), IncludeCancelCommand = true)]
    private async Task ImportFolderAsync(CancellationToken token)
    {
        if (!CanImportFolder()) return;
        IsBusy = true;
        var revision = ++_transferRevision;
        ExternalMinecraftInstance? source = null;
        InstanceImportResult? imported = null;
        try
        {
            TransferPercent = 0;
            TransferStatus = "正在选择其他启动器的实例……";
            source = await _externalDialogs!.ChooseInstanceAsync(_externalDiscovery!, token);
            if (source is null) { TransferStatus = "已取消文件夹选择"; StatusText = "没有新增实例。"; return; }
            token.ThrowIfCancellationRequested();
            TransferPercent = 0;
            TransferStatus = "正在分析实例文件夹……";
            var preview = await _folderImporter!.InspectAsync(source, token);
            token.ThrowIfCancellationRequested();
            if (Instances.Any(instance => string.Equals(instance.Name, preview.Import.SuggestedName, StringComparison.OrdinalIgnoreCase)))
                preview = preview with { Import = preview.Import with { Notes = [.. preview.Import.Notes,
                    "EML 中已有同名实例，请填写不同的新名称；复制不会覆盖已有实例。"] } };
            var name = _externalDialogs.ConfirmCopy(preview);
            if (name is null) { TransferStatus = "已取消复制导入"; StatusText = "没有新增实例。"; return; }
            token.ThrowIfCancellationRequested();
            StatusText = "正在复制实例文件，原目录保持不变……";
            var progress = new Progress<InstanceImportProgress>(value =>
            {
                if (revision != _transferRevision || !IsBusy) return;
                TransferPercent = value.Percent;
                TransferStatus = $"复制 {value.Percent:F1}%  {value.FileName}";
            });
            imported = await _folderImporter.ImportAsync(source, name, progress, token);
            await CompleteImportedInstanceAsync(imported, source.SourcePath, token);
        }
        catch (OperationCanceledException)
        {
            TransferStatus = imported is null ? "已取消复制导入" : "实例已导入，后续操作已取消";
            StatusText = imported is null ? "没有新增实例，原目录保持不变。" : "已复制的实例保留，可点击“补全实例依赖”继续。";
            _notifications.Cancelled(TransferStatus, StatusText, source?.SourcePath, imported?.InstancePath ?? InstancesRoot);
        }
        catch (Exception ex)
        {
            TransferStatus = imported is null ? "文件夹导入失败" : "实例已导入，后续操作未完成";
            StatusText = (imported is null ? "" : "已复制的实例保留，可点击“补全实例依赖”继续。") + ex.Message;
            if (imported is null) _notifications.Failure(TransferStatus, ex, source?.SourcePath, InstancesRoot);
            else _notifications.Warning(TransferStatus, StatusText, source?.SourcePath, imported.InstancePath, NoticeExceptionPath(ex));
        }
        finally { ++_transferRevision; IsBusy = false; }
    }
}
