namespace EML.Core.Java;

/// <summary>保存一次 Java 检测得到的路径、版本和架构。</summary>
/// <param name="ExecutablePath">可运行的 java.exe 绝对路径。</param>
/// <param name="Version">Java 报告的完整版本号。</param>
/// <param name="Architecture">供界面显示的处理器架构与位数。</param>
public sealed record JavaInstallation(
    string ExecutablePath,
    string Version,
    string Architecture);
