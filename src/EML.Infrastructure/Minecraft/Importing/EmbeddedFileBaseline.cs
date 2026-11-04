using System.Security.Cryptography;
using System.Text.Json;
using EML.Core.Minecraft;

namespace EML.Infrastructure.Minecraft.Importing;

/// <summary>从原始导入声明提取覆盖文件基线；远端缓存不得改变该基线。</summary>
internal static class EmbeddedFileBaseline
{
    /// <summary>确认实例属于指定启动器，拒绝目录联接以及 instances 外的任意路径。</summary>
    internal static string ValidateInstance(string launcherRoot, string instancePath)
    {
        var instances = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(launcherRoot, "instances")));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instancePath));
        if (!string.Equals(Path.GetDirectoryName(root), instances, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能操作 instances 文件夹的直接子实例。");
        InstanceImportPaths.EnsureNoLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("实例目录已不存在。");
        return root;
    }

    /// <summary>仅以导入记录确认嵌入来源，不根据文件夹名称或远端哈希推测。</summary>
    internal static InstanceFileRequirement Find(InstanceImportManifest manifest, string relativePath)
    {
        var path = InstanceImportPaths.Relative(relativePath);
        var baseline = manifest.Files.SingleOrDefault(file =>
            string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));
        if (baseline is null || baseline.Source != InstanceFileSource.Embedded ||
            baseline.Origin is not (null or InstanceFileOrigin.EmbeddedOverride or InstanceFileOrigin.UserLocalImport) || baseline.Size is null or < 0 ||
            !baseline.Hashes.TryGetValue("sha256", out var hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("文件没有可信的原包大小和 SHA-256 记录：" + path);
        return baseline;
    }

    /// <summary>给整份导入声明生成指纹，使本机恢复位置和主动替换记录无法沿用到另一份清单。</summary>
    internal static string ManifestFingerprint(InstanceImportManifest manifest) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest)));

    /// <summary>给单个原始需求生成指纹，绑定路径、来源和导入时的完整性记录。</summary>
    internal static string RequirementFingerprint(InstanceFileRequirement requirement) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(requirement)));

    /// <summary>提交恢复文件或本机状态前检查导入声明，防止操作期间被切换。</summary>
    internal static void EnsureManifestUnchanged(string instanceRoot, InstanceImportManifest manifest)
    {
        var current = InstanceManifestStore.Read(Path.Combine(instanceRoot, InstanceImportManifest.FileName));
        if (ManifestFingerprint(current) != ManifestFingerprint(manifest))
            throw new IOException("导入清单在操作期间发生变化，请重新检查实例。");
    }
}
