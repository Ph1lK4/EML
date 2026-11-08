using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EML.Core.Minecraft;

namespace EML.Infrastructure.Minecraft.Importing;

/// <summary>记录用户明确接受的本地替代文件；不会自动接受扫描到的变化，也不修改文件内容。</summary>
public sealed class InstanceLocalReplacementService(string? launcherRoot = null)
{
    internal const string RelativePath = ".eml-runtime/local-replacements.json";
    private const int MaximumBytes = 8 * 1024 * 1024;
    private readonly string _launcherRoot = Path.GetFullPath(launcherRoot ?? AppContext.BaseDirectory);
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 16
    };

    /// <summary>每条确认记录固定原需求指纹和用户实际选择时的文件摘要。</summary>
    private sealed record Replacement(string Path, string OriginalFingerprint, long Size, string Sha256);

    /// <summary>本机替换记录仅对同一份原始导入清单有效。</summary>
    private sealed record ReplacementState(int SchemaVersion, string ManifestFingerprint, IReadOnlyList<Replacement> Files);

    /// <summary>文件记录与同一次读取的原始状态摘要，用于识别操作期间撤回或修改的选择。</summary>
    private sealed record ReadState(IReadOnlyList<Replacement> Files, string? Fingerprint);

    /// <summary>固定本轮生效需求与用户替换记录；状态不存在也参与结束时的一致性检查。</summary>
    internal sealed record AppliedRequirements(IReadOnlyList<InstanceFileRequirement> Requirements, string? ReplacementFingerprint);

    /// <summary>显式接受某个现有 Embedded 文件作为用户替换，记录其大小和 SHA-256 供后续检查。</summary>
    public async Task<InstanceFileRequirement> AcceptExistingAsync(string instancePath, string relativePath,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = EmbeddedFileBaseline.ValidateInstance(_launcherRoot, instancePath);
            using var lease = Migration.InstanceOperationLease.Acquire(root);
            Migration.MigrationJournalStore.EnsureSettled(root);
            var manifest = InstanceManifestStore.Read(Path.Combine(root, InstanceImportManifest.FileName));
            var baseline = FindBaseline(root, manifest, relativePath);
            var target = InstanceImportPaths.Within(root, baseline.Path!);
            InstanceImportPaths.EnsureNoLinks(target);
            Replacement replacement;
            using (var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, useAsync: true))
            {
                if (input.Length > SafeInstanceArchive.MaxUncompressedBytes)
                    throw new InvalidDataException("用户替代文件超过 50 GiB。");
                var hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
                replacement = new(baseline.Path!, EmbeddedFileBaseline.RequirementFingerprint(baseline),
                    input.Length, Convert.ToHexStringLower(hash));
            }
            var previous = Read(root, manifest);
            var files = previous.Files.Where(item => !item.Path.Equals(baseline.Path, StringComparison.OrdinalIgnoreCase))
                .Append(replacement).ToArray();
            await WriteAsync(root, manifest, new(1, EmbeddedFileBaseline.ManifestFingerprint(manifest), files),
                cancellationToken).ConfigureAwait(false);
            return UseReplacement(baseline, replacement);
        }
        finally { _operationLock.Release(); }
    }

    /// <summary>只读叠加用户已确认的来源，不读取不存在的记录、不创建目录，也不信任当前文件的临时内容。</summary>
    internal static IReadOnlyList<InstanceFileRequirement> Apply(string instanceRoot, InstanceImportManifest manifest,
        IReadOnlyList<InstanceFileRequirement> requirements) => ApplyWithSnapshot(instanceRoot, manifest, requirements).Requirements;

    /// <summary>从同一次受保护读取生成生效需求及状态快照，避免先应用后取摘要的间隙。</summary>
    internal static AppliedRequirements ApplyWithSnapshot(string instanceRoot, InstanceImportManifest manifest,
        IReadOnlyList<InstanceFileRequirement> requirements)
    {
        var state = Read(instanceRoot, manifest);
        requirements = Migration.MigrationDataStore.IncludeExtraMods(requirements, Migration.MigrationDataStore.Read(instanceRoot, manifest));
        if (state.Files.Count == 0) return new(requirements, state.Fingerprint);
        var byPath = state.Files.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var applied = requirements.Select(requirement => requirement.Path is not null &&
            requirement.Source == InstanceFileSource.Embedded &&
            (requirement.Origin is null or InstanceFileOrigin.EmbeddedOverride or InstanceFileOrigin.UserLocalImport) &&
            byPath.TryGetValue(requirement.Path, out var replacement)
                ? UseReplacement(requirement, replacement) : requirement).ToArray();
        return new(applied, state.Fingerprint);
    }

    /// <summary>发布就绪前确认用户选择仍是本轮依据；记录删除、新增、变更或损坏都不能沿用旧结果。</summary>
    internal static void EnsureUnchanged(string instanceRoot, InstanceImportManifest manifest, AppliedRequirements snapshot)
    {
        if (Read(instanceRoot, manifest).Fingerprint != snapshot.ReplacementFingerprint)
            throw new IOException("用户本地替换记录在操作期间发生变化，请重新检查实例。");
    }

    /// <summary>替代来源只携带用户选定内容的摘要；平台 ID 和官方摘要元数据随原需求保留。</summary>
    private static InstanceFileRequirement UseReplacement(InstanceFileRequirement requirement, Replacement replacement) =>
        requirement with
        {
            Source = InstanceFileSource.Embedded, Origin = InstanceFileOrigin.UserLocalImport,
            Size = replacement.Size, Hashes = new Dictionary<string, string> { ["sha256"] = replacement.Sha256 },
            Downloads = [], ArchiveEntry = null
        };

    /// <summary>读取有大小限制的本机记录，清单变化时失效；未知路径和伪造原需求指纹均拒绝使用。</summary>
    private static ReadState Read(string instanceRoot, InstanceImportManifest manifest)
    {
        var path = InstanceImportPaths.Within(instanceRoot, RelativePath);
        InstanceImportPaths.EnsureNoLinks(path);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumBytes) throw new InvalidDataException("用户替换记录超过 8 MiB。");
            var state = JsonSerializer.Deserialize<ReplacementState>(input, Options)
                ?? throw new InvalidDataException("用户替换记录为空。");
            if (state.SchemaVersion != 1 || !IsSha256(state.ManifestFingerprint) || state.Files is null ||
                state.Files.Count > SafeInstanceArchive.MaxEntries)
                throw new InvalidDataException("用户替换记录格式无效。");
            // 同一 FileShare.Read 句柄持有期间读取记录和摘要，Windows 下其他写入者不能在两者之间替换文件。
            input.Position = 0;
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(input));
            if (state.ManifestFingerprint != EmbeddedFileBaseline.ManifestFingerprint(manifest)) return new([], fingerprint);
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var baselineManifest = WithExtraMods(instanceRoot, manifest);
            foreach (var file in state.Files)
            {
                if (file is null || file.Path is null || !known.Add(InstanceImportPaths.Relative(file.Path)) ||
                    file.Size < 0 || file.Size > SafeInstanceArchive.MaxUncompressedBytes || !IsSha256(file.Sha256))
                    throw new InvalidDataException("用户替换文件记录无效或路径重复。");
                var baseline = EmbeddedFileBaseline.Find(baselineManifest, file.Path);
                if (file.OriginalFingerprint != EmbeddedFileBaseline.RequirementFingerprint(baseline))
                    throw new InvalidDataException("用户替换记录与原始文件需求不一致。");
                InstanceImportPaths.EnsureNoLinks(InstanceImportPaths.Within(instanceRoot, file.Path));
            }
            return new(state.Files, fingerprint);
        }
        catch (FileNotFoundException) { return new([], null); }
        catch (DirectoryNotFoundException) { return new([], null); }
        catch (JsonException ex) { throw new InvalidDataException("用户替换记录 JSON 损坏。", ex); }
    }

    /// <summary>已迁入的额外模组具有独立严格基线，也允许用户随后显式接受新的本地版本。</summary>
    private static InstanceFileRequirement FindBaseline(string root, InstanceImportManifest manifest, string relativePath)
        => EmbeddedFileBaseline.Find(WithExtraMods(root, manifest), relativePath);

    /// <summary>一批替换共用同一份模组记录，避免每个文件重新读取大型迁移清单。</summary>
    private static InstanceImportManifest WithExtraMods(string root, InstanceImportManifest manifest)
    {
        var requirements = Migration.MigrationDataStore.IncludeExtraMods(manifest.Files, Migration.MigrationDataStore.Read(root, manifest));
        return manifest with { Files = requirements };
    }

    /// <summary>将确认记录原子写入自有状态文件，提交前重新核对原清单并响应取消。</summary>
    private static async Task WriteAsync(string instanceRoot, InstanceImportManifest manifest, ReplacementState state,
        CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("用户替换记录超过 8 MiB。");
        var path = InstanceImportPaths.Within(instanceRoot, RelativePath);
        InstanceImportPaths.EnsureNoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            EmbeddedFileBaseline.EnsureManifestUnchanged(instanceRoot, manifest);
            InstanceImportPaths.EnsureNoLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                InstanceImportPaths.EnsureNoLinks(temporary);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Trace.TraceWarning("无法清理用户替换记录临时文件 {0}：{1}", temporary, ex.Message); }
        }
    }

    /// <summary>验证 SHA-256 文本，防止损坏记录以空摘要放宽后续校验。</summary>
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
