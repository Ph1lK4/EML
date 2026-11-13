using System.Windows;
using EML.Core.Java;

namespace EML.App.Services;

/// <summary>缺少或指定的 Java 不兼容时，说明下载来源和影响后再征得用户同意。</summary>
public sealed class WpfJavaDownloadDialogs(Window owner) : IJavaDownloadDialogs
{
    /// <summary>确认只安装到 EML 托管目录，下载完成后继续本次启动，不修改系统 Java。</summary>
    public bool ConfirmDownload(string instanceName, JavaRuntimeRequiredException requirement, string installationRoot)
    {
        var architecture = requirement.Architecture switch
        {
            "x64" => "x64（64 位）", "x86" => "x86（32 位）", "arm64" or "aarch64" => "ARM64", _ => requirement.Architecture
        };
        var message = requirement.Message + "\n\n" +
            $"此实例需要 Java {string.Join('/', requirement.CompatibleMajorVersions)}，架构 {architecture}。\n" +
            "是否从 Eclipse Adoptium 官方下载兼容的 Temurin Java，并在校验安装完成后继续启动？\n" +
            "仅安装到下方 EML 目录，不覆盖已有 Java、不修改系统环境变量。\n" +
            "本次将使用新 Java，玩家身份和其他参数不变；需要永久记住时请另行保存启动设置。";
        return DialogPrompt.Confirm(owner, "需要兼容的 Java 环境", message, "下载 Java 并继续启动",
            DialogTone.Warning, "实例：" + instanceName + " · Java 安装目录", installationRoot);
    }
}
