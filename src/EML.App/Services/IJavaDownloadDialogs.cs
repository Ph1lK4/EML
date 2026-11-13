using EML.Core.Java;

namespace EML.App.Services;

/// <summary>启动时由用户明确确认下载 Java；不读取账户或隐式保存实例设置。</summary>
public interface IJavaDownloadDialogs
{
    bool ConfirmDownload(string instanceName, JavaRuntimeRequiredException requirement, string installationRoot);
}
