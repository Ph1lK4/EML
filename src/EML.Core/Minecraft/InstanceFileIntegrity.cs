namespace EML.Core.Minecraft;

/// <summary>约束实际内容来源的校验依据；来源元数据中的其他文件摘要永远不加入本文件校验。</summary>
public static class InstanceFileIntegrity
{
    /// <summary>验证来源与大小、摘要的对应关系，然后返回该内容自己的完整性要求。</summary>
    public static IReadOnlyDictionary<string, string> HashesFor(InstanceFileRequirement file)
    {
        if (file.Origin is { } origin && !Enum.IsDefined(origin))
            throw new InvalidDataException("未知的文件内容来源。");
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
}
