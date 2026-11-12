namespace EML.Core.Minecraft;

/// <summary>约束实际内容来源的校验依据；来源元数据中的其他文件摘要永远不加入本文件校验。</summary>
public static class InstanceFileIntegrity
{
    /// <summary>验证来源与大小、摘要的对应关系，然后返回该内容自己的完整性要求。</summary>
    public static IReadOnlyDictionary<string, string> HashesFor(InstanceFileRequirement file)
    {
        if (file.Origin is { } origin && !Enum.IsDefined(origin))
            throw new InvalidDataException("未知的文件内容来源。");
        if (file.GithubReleaseOrigin is not null && file.Origin != InstanceFileOrigin.GithubReleaseDownload)
            throw new InvalidDataException("GitHub 发行文件身份不能混入其他内容来源。");
        if (file.Origin == InstanceFileOrigin.GithubReleaseDownload)
        {
            if (file.Source != InstanceFileSource.DirectDownload || file.Size is null or < 0 ||
                file.Hashes.Count != 1 || !file.Hashes.TryGetValue("sha256", out var hash) || !ValidHash(hash, 64) ||
                file.GithubReleaseOrigin is not { } github || !ValidGithubOrigin(github) ||
                file.ProjectId is not null || file.FileId is not null || file.CurseForgeOrigin is not null || file.ModrinthOrigin is not null ||
                file.Downloads.Count != 1 || !string.Equals(file.Downloads[0], GithubDownloadUrl(github), StringComparison.Ordinal))
                throw new InvalidDataException("GitHub 发行文件必须保留固定资产身份、官方大小和 SHA-256，且下载地址必须与来源一致。");
        }
        if (file.Origin is InstanceFileOrigin.EmbeddedOverride or InstanceFileOrigin.UserLocalImport or InstanceFileOrigin.MigratedPlayerData)
        {
            if (file.Source != InstanceFileSource.Embedded || file.Size is null or < 0 ||
                file.Hashes.Count != 1 || !file.Hashes.TryGetValue("sha256", out var hash) || !ValidHash(hash, 64) ||
                file.Downloads.Count != 0)
                throw new InvalidDataException("包内覆盖或用户选择的文件必须使用自身的大小与 SHA-256，不能混入平台下载摘要。");
            if (file.Origin == InstanceFileOrigin.EmbeddedOverride && string.IsNullOrWhiteSpace(file.ArchiveEntry))
                throw new InvalidDataException("包内覆盖文件缺少导入条目依据。");
        }
        if (file.Origin == InstanceFileOrigin.CurseForgeDownload ||
            file.Origin == InstanceFileOrigin.ModrinthDownload && file.Source == InstanceFileSource.CurseForge)
        {
            if (file.Source != InstanceFileSource.CurseForge || file.Size is null or < 0 ||
                !file.Hashes.TryGetValue("sha1", out var hash) || !ValidHash(hash, 40))
                throw new InvalidDataException("CurseForge 固定文件必须保留官方大小与 SHA-1 校验。");
        }
        if (file.Origin == InstanceFileOrigin.ModrinthDownload &&
            (file.Size is null or < 0 || file.Hashes.Count == 0 ||
             file.Source is not (InstanceFileSource.DirectDownload or InstanceFileSource.CurseForge)))
            throw new InvalidDataException("Modrinth 文件缺少其来源声明的大小和摘要。");
        return file.Hashes;
    }

    /// <summary>只接受固定长度的十六进制摘要，避免来源标签绕过完整性校验。</summary>
    private static bool ValidHash(string? hash, int length) => hash?.Length == length && hash.All(Uri.IsHexDigit);

    /// <summary>限制原作者仓库与发行文件标识，拒绝路径片段、查询参数和混合平台元数据。</summary>
    public static bool ValidGithubOrigin(GithubReleaseFileOrigin source) =>
        source.ReleaseId > 0 && source.AssetId > 0 && source.Repository is { Length: > 2 and <= 201 } &&
        source.Repository.Split('/') is { Length: 2 } parts && parts.All(part => part.Length > 0 && part is not ("." or "..") &&
            part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) &&
        source.Tag is { Length: > 0 and <= 256 } && !source.Tag.Any(char.IsControl) &&
        source.FileName is { Length: > 4 and <= 255 } &&
        (source.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || source.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) &&
        source.FileName.IndexOfAny(['/', '\\', ':', '<', '>', '"', '|', '?', '*']) < 0 && !source.FileName.Any(char.IsControl) &&
        !source.FileName.EndsWith(' ') && source.FileName is not ("." or "..");

    /// <summary>使用固定仓库、标签和文件名生成唯一的 HTTPS 发行下载地址。</summary>
    public static string GithubDownloadUrl(GithubReleaseFileOrigin source) =>
        $"https://github.com/{source.Repository}/releases/download/{Uri.EscapeDataString(source.Tag)}/{Uri.EscapeDataString(source.FileName)}";
}
