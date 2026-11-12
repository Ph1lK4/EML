namespace EML.Core.Minecraft.Migration;

public enum GtnhMigrationMode { FullClient, ServerClient }
public enum MigrationFileAction { Copy, Identical, Conflict, Skip }

/// <summary>由包内标识确认的 GTNH 实例；名称本身不能授予迁移资格。</summary>
public sealed record GtnhInstanceInfo(string InstancePath, string Name, string GameDirectory, string Version);
/// <summary>一个可选择的数据组；世界单独列出，不混合两个同名存档。</summary>
public sealed record MigrationGroup(string Id, string Name, bool Recommended);
/// <summary>用户在预览前选择的数据范围；两个确认分别对应新版实测和外部游戏关闭。</summary>
public sealed record InstanceMigrationRequest(string SourceInstance, string TargetInstance, GtnhMigrationMode Mode,
    IReadOnlyList<string> Groups, bool TargetTested, bool GamesClosed)
{
    /// <summary>迁移模组前单独确认兼容风险；旧请求默认不授权模组迁移。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExtraModsRiskAccepted { get; init; }
}
/// <summary>预览文件的复制依据；玩家数据允许后续游玩变化，额外模组保持严格完整性。</summary>
public sealed record MigrationFile(string Group, string SourcePath, string TargetPath, long Size, string Sha256,
    long SourceSize, string SourceSha256, long? PreviousSize, string? PreviousSha256, MigrationFileAction Action)
{
    /// <summary>说明模组或受保护资源的跳过原因；不会被普通“覆盖冲突”开关改变。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }
    /// <summary>额外在线模组的官方来源随预览固定，复制字节的 SHA-256 不替代其官方摘要。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public InstanceFileRequirement? OnlineModBaseline { get; init; }
    public string ActionDisplay => Action switch { MigrationFileAction.Copy when Group == "language" => "合并语言项",
        MigrationFileAction.Copy => "复制", MigrationFileAction.Identical => "相同，跳过",
        MigrationFileAction.Skip when GtnhExtraModPolicy.IsGroup(Group) => "跳过，保留新版模组",
        MigrationFileAction.Skip => "跳过，保留官方来源", _ => "冲突，默认保留新版" };
}
/// <summary>不可直接作为写入许可；执行时重新生成预览并比对指纹。</summary>
public sealed record InstanceMigrationPlan(InstanceMigrationRequest Request, GtnhInstanceInfo Source,
    GtnhInstanceInfo Target, IReadOnlyList<MigrationFile> Files, IReadOnlyList<string> Notes, string Fingerprint);
/// <summary>记录迁移及恢复结果，不将文件复制完成等同于存档版本兼容。</summary>
public sealed record InstanceMigrationResult(string Id, string Status, int CopiedFiles, int SkippedFiles,
    string JournalPath, IReadOnlyList<string> Messages);
public sealed record InstanceMigrationProgress(string Stage, string Path, int Completed, int Total);
/// <summary>随备份保留的迁移记录；Rule 区分可变玩家数据和严格校验的额外模组，原包需求独立保存。</summary>
public sealed record MigratedPlayerFile(string Path, long Size, string Sha256, string MigrationId, string Rule = "gtnh-player-v1")
{
    /// <summary>本次迁移明确取代的旧用户选择；后续新确认的替换仍可收紧为严格校验。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SupersededLocalSha256 { get; init; }
    /// <summary>新版继续识别在线模组的固定平台身份；旧迁移记录省略时沿用本地基线。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public InstanceFileRequirement? OnlineModBaseline { get; init; }
}
public sealed record MigrationRecoveryInfo(string Id, string Status, DateTimeOffset CreatedAt);

/// <summary>只对 GTNH 提供预览、复制和最近一次恢复；源实例从不改写。</summary>
public interface IMinecraftInstanceMigrationService
{
    Task<GtnhInstanceInfo?> InspectAsync(string instancePath, CancellationToken token = default);
    Task<IReadOnlyList<MigrationGroup>> GetGroupsAsync(string sourceInstance, GtnhMigrationMode mode, CancellationToken token = default);
    Task<InstanceMigrationPlan> PreviewAsync(InstanceMigrationRequest request, CancellationToken token = default);
    Task<InstanceMigrationResult> MigrateAsync(InstanceMigrationPlan plan, bool overwriteConflicts,
        IProgress<InstanceMigrationProgress>? progress = null, CancellationToken token = default);
    Task<MigrationRecoveryInfo?> GetRecoveryAsync(string instancePath, CancellationToken token = default);
    Task<InstanceMigrationResult> RestoreAsync(string instancePath, string migrationId,
        IProgress<InstanceMigrationProgress>? progress = null, CancellationToken token = default);
}
