using EML.Core.Downloads;
using EML.Core.Modrinth;

namespace EML.Infrastructure.Minecraft.Modrinth.Api;

/// <summary>把 API 版本文件交给通用下载器；负责单个文件，不隐式安装模组依赖或导入整合包。</summary>
public sealed class ModrinthVersionDownloadService
{
    private readonly IFileDownloader _downloader;

    /// <summary>复用应用配置的通用下载器及其超时、哈希校验和禁止覆盖规则。</summary>
    public ModrinthVersionDownloadService(IFileDownloader downloader)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        _downloader = downloader;
    }

    /// <summary>下载选定文件；模组可传入 mods 目录，整合包可传入待导入包的缓存目录。</summary>
    public Task<FileDownloadResult> DownloadAsync(ModrinthVersion version, string destinationDirectory,
        string? filename = null, IProgress<FileDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
        _downloader.DownloadAsync(CreateDownloadRequest(version, destinationDirectory, filename), progress, cancellationToken);

    /// <summary>生成独立下载请求，保留上游大小与所有支持的哈希，不从名称推测下载地址。</summary>
    public static FileDownloadRequest CreateDownloadRequest(ModrinthVersion version, string destinationDirectory, string? filename = null)
    {
        var file = SelectFile(version, filename);
        if (string.IsNullOrWhiteSpace(destinationDirectory) || !Path.IsPathFullyQualified(destinationDirectory))
            throw new ArgumentException("下载目录必须是绝对路径。", nameof(destinationDirectory));
        ValidateFilename(file.Filename);
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            url.UserInfo.Length != 0 || url.Fragment.Length != 0)
            throw new InvalidDataException("Modrinth 文件必须使用不带凭证或片段的 HTTPS 下载地址。");
        if (file.Size < 0) throw new InvalidDataException("Modrinth 文件大小不能为负数。");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (file.Hashes is not null)
        {
            foreach (var pair in file.Hashes)
            {
                var algorithm = pair.Key.ToLowerInvariant();
                var length = algorithm switch { "sha1" => 40, "sha256" => 64, "sha512" => 128, _ => 0 };
                if (length == 0) continue;
                if (pair.Value is null || pair.Value.Length != length || !pair.Value.All(Uri.IsHexDigit))
                    throw new InvalidDataException($"Modrinth 文件的 {algorithm} 哈希格式无效。");
                if (!hashes.TryAdd(algorithm, pair.Value)) throw new InvalidDataException("Modrinth 文件包含重复的哈希算法。");
            }
        }
        if (hashes.Count == 0) throw new InvalidDataException("Modrinth 文件缺少可验证的哈希，不能作为已校验资源下载。");
        return new FileDownloadRequest
        {
            DestinationPath = Path.Combine(Path.GetFullPath(destinationDirectory), file.Filename),
            Urls = [url.AbsoluteUri], ExpectedSize = file.Size, Hashes = hashes,
            DisplayName = file.Filename, GroupName = "Modrinth 文件下载"
        };
    }

    /// <summary>优先选唯一 primary 文件；多文件且无明确默认项时要求调用方指定文件名。</summary>
    public static ModrinthVersionFile SelectFile(ModrinthVersion version, string? filename = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Files is null || version.Files.Count == 0) throw new InvalidDataException("该 Modrinth 版本没有可下载文件。");
        if (filename is not null)
        {
            var selected = version.Files.Where(file => string.Equals(file.Filename, filename, StringComparison.Ordinal)).ToArray();
            if (selected.Length != 1) throw new ArgumentException("指定文件名不存在或有重复记录。", nameof(filename));
            return selected[0];
        }
        var primary = version.Files.Where(file => file.Primary).ToArray();
        if (primary.Length == 1) return primary[0];
        if (primary.Length == 0 && version.Files.Count == 1) return version.Files[0];
        throw new InvalidDataException("该版本有多个可选文件，请明确选择文件名后下载。");
    }

    /// <summary>拒绝路径穿越和 Windows 设备名，防止上游文件名逃逸下载目录。</summary>
    private static void ValidateFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.Length > 240 || filename is "." or ".." ||
            filename.EndsWith('.') || filename.EndsWith(' ') ||
            filename.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)))
            throw new InvalidDataException("Modrinth 文件名包含不安全的路径字符。");
        var stem = filename.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             stem[3] is >= '1' and <= '9'))
            throw new InvalidDataException("Modrinth 文件名不能是 Windows 设备名。");
    }
}
