using System.Globalization;
using EML.Core.Minecraft;
using EML.Core.Minecraft.Gtnh;

namespace EML.Infrastructure.Minecraft.Gtnh;

/// <summary>只为 GitHub 默认命名的资源包分配稳定本地名，官方资产身份及校验依据保持原样。</summary>
internal static class GithubResourceFileName
{
    /// <summary>default.zip 使用仓库与固定资产编号避免跨项目重名，其他类型和名称维持原名。</summary>
    internal static string ForInstall(GtnhAddonKind kind, GithubReleaseFileOrigin origin)
    {
        if (!InstanceFileIntegrity.ValidGithubOrigin(origin))
            throw new InvalidDataException("GitHub 资源缺少有效的固定资产身份。");
        if (kind != GtnhAddonKind.ResourcePack || !origin.FileName.Equals("default.zip", StringComparison.OrdinalIgnoreCase))
            return origin.FileName;
        // 固定 eml 前缀避免 Windows 设备名，仓库字符与最大长度由来源规则约束。
        return "eml-" + origin.Repository.Replace("/", "--", StringComparison.Ordinal).ToLowerInvariant() + "-" +
            origin.AssetId.ToString(CultureInfo.InvariantCulture) + ".zip";
    }

    /// <summary>接受旧记录的官方原名或唯一确定别名，不允许用户任意改名绕过来源绑定。</summary>
    internal static bool IsAllowed(GtnhAddonKind kind, GithubReleaseFileOrigin origin, string localName) =>
        localName.Equals(origin.FileName, StringComparison.Ordinal) ||
        localName.Equals(ForInstall(kind, origin), StringComparison.Ordinal);
}
