using System.Text.Json;
using System.Text.Json.Serialization;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Migration;

namespace EML.Infrastructure.Minecraft.Importing;

/// <summary>保存统一清单，供导入、扫描和未来的下载/校验服务共用。</summary>
internal static class InstanceManifestStore
{
    // EML 记录包内全部文件（包括存档和地图），不能沿用平台组件清单的 8 MiB 上限。
    internal const int MaximumBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        // 只压缩磁盘上的 JSON 排版；模型和语义指纹的序列化方式不变，兼容已有来源记录。
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false, MaxDepth = 64,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    /// <summary>将 JSON 转为统一清单，验证字段并规范化文件路径。</summary>
    internal static InstanceImportManifest Parse(JsonElement json)
    {
        var manifest = json.Deserialize<InstanceImportManifest>(Options) ?? throw new InvalidDataException("EML 清单为空。");
        Validate(manifest);
        return manifest with
        {
            GameDirectory = manifest.GameDirectory == "." ? "." : InstanceImportPaths.Relative(manifest.GameDirectory),
            Files = manifest.Files.Select(file => file with
            {
                Path = file.Path is null ? null : InstanceImportPaths.Relative(file.Path),
                ArchiveEntry = file.ArchiveEntry is null ? null : InstanceImportPaths.Relative(file.ArchiveEntry)
            }).ToArray()
        };
    }

    /// <summary>从本地文件读取已验证的清单，限制文件大小并拒绝链接路径。</summary>
    internal static InstanceImportManifest Read(string path)
    {
        InstanceImportPaths.EnsureNoLinks(path);
        using var input = File.OpenRead(path);
        if (input.Length > MaximumBytes) throw new InvalidDataException("EML 清单超过 64 MiB。");
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 64 });
        return Parse(document.RootElement);
    }

    /// <summary>验证并保存清单为 JSON，支持取消且限制生成文件的大小。</summary>
    internal static async Task WriteAsync(string path, InstanceImportManifest manifest, CancellationToken token)
    {
        await File.WriteAllBytesAsync(path, Serialize(manifest), token);
    }

    /// <summary>导出与本地保存使用同一套验证和编码，避免备份丢失来源字段。</summary>
    internal static byte[] Serialize(InstanceImportManifest manifest)
    {
        Validate(manifest);
        using var buffer = new ManifestBuffer();
        JsonSerializer.Serialize(buffer, manifest, Options);
        return buffer.ToArray();
    }

    /// <summary>生成清单时边写边限制大小，避免先分配完整的超限 JSON 才报错。</summary>
    private sealed class ManifestBuffer : MemoryStream
    {
        /// <summary>检查 JSON 序列化器写入的字节块，保持与本地和备份读取器相同的上限。</summary>
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacityForWrite(buffer.Length);
            base.Write(buffer);
        }

        /// <summary>数组写入入口应用相同的大小保护。</summary>
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacityForWrite(count);
            base.Write(buffer, offset, count);
        }

        /// <summary>在扩展缓冲区前拒绝超过清单上限的写入。</summary>
        private void EnsureCapacityForWrite(int count)
        {
            if (Position > MaximumBytes - (long)count)
                throw new InvalidDataException("生成的实例清单超过 64 MiB。");
        }
    }

    /// <summary>检查清单版本、文件路径、HTTPS 地址和哈希格式，拒绝不完整或冲突的声明。</summary>
    internal static void Validate(InstanceImportManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.SourceFormat) || manifest.Name is null ||
            manifest.Files is null || manifest.Components is null || manifest.Notes is null ||
            manifest.Files.Count > SafeInstanceArchive.MaxEntries || manifest.Components.Count > 1024)
            throw new InvalidDataException("不支持或损坏的 EML 实例清单。");
        if (manifest.GameDirectory != ".") InstanceImportPaths.Relative(manifest.GameDirectory);
        if (manifest.MigratedPlayerFiles is not null) MigrationDataStore.Validate(manifest, manifest.MigratedPlayerFiles);
        if (manifest.ManagedMods is not null) Mods.ModManagementStore.Validate(manifest, manifest.ManagedMods);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in manifest.Components)
            if (component is null || string.IsNullOrWhiteSpace(component.Id) || string.IsNullOrWhiteSpace(component.Version))
                throw new InvalidDataException("组件声明缺少 ID 或版本。");
        foreach (var file in manifest.Files)
        {
            if (file is null || file.Size < 0 || file.Hashes is null || file.Downloads is null || !Enum.IsDefined(file.Source))
                throw new InvalidDataException("无效的文件需求。");
            if (file.Path is not null)
            {
                var path = InstanceImportPaths.Relative(file.Path);
                if (path.Equals(InstanceImportManifest.FileName, StringComparison.OrdinalIgnoreCase) || !known.Add(path))
                    throw new InvalidDataException("文件需求重复或指向 EML 清单自身。");
            }
            else if (file.Source != InstanceFileSource.CurseForge) throw new InvalidDataException("文件需求缺少目标路径。");
            if (file.Source == InstanceFileSource.CurseForge && (file.ProjectId is null or <= 0 || file.FileId is null or <= 0))
                throw new InvalidDataException("CurseForge 需求缺少有效项目/文件 ID。");
            if (file.Source == InstanceFileSource.DirectDownload && file.Downloads.Count == 0)
                throw new InvalidDataException("下载需求缺少 URL。");
            foreach (var url in file.Downloads)
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new InvalidDataException("下载地址必须是有效的 HTTPS URL。");
            foreach (var (algorithm, hash) in file.Hashes)
            {
                var expected = algorithm switch { "sha1" => 40, "sha256" => 64, "sha512" => 128, _ => 0 };
                if (expected == 0 || hash is null || hash.Length != expected || !hash.All(Uri.IsHexDigit))
                    throw new InvalidDataException("不支持的哈希类型或无效校验值。");
            }
            // 未解析平台引用可以暂缺大小/摘要；实际文件验证阶段再执行来源完整性约束。
            if (file.Origin is { } origin && !Enum.IsDefined(origin)) throw new InvalidDataException("未知的文件内容来源。");
            if (file.Origin == InstanceFileOrigin.GithubReleaseDownload || file.GithubReleaseOrigin is not null)
                _ = InstanceFileIntegrity.HashesFor(file);
            if (file.Origin == InstanceFileOrigin.MigratedPlayerData)
            {
                if (!EML.Core.Minecraft.Migration.GtnhPlayerDataPolicy.IsMigrated(file, manifest.GameDirectory) || file.Required)
                    throw new InvalidDataException("迁入玩家文件不能改变运行文件或必要内容的校验策略。");
                _ = InstanceFileIntegrity.HashesFor(file);
            }
            if (file.Role is { } role && (!Enum.IsDefined(role) ||
                role != InstanceFileChangePolicy.GetRole(file, manifest.GameDirectory, manifest.SourceFormat)))
                throw new InvalidDataException("文件用途与来源或 EML 管理范围不一致。");
            if (file.ArchiveEntry is not null) InstanceImportPaths.Relative(file.ArchiveEntry);
        }
        InstanceImportPaths.ValidateLayout(manifest.Files.Where(f => f.Path is not null).Select(f => (f.Path!, false)));
    }
}
