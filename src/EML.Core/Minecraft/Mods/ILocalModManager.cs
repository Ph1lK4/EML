namespace EML.Core.Minecraft.Mods;

/// <summary>用户期望的加载状态；与内容来源和实际磁盘完整性分开保存。</summary>
public enum LocalModState { Enabled, Disabled, Removed }

/// <summary>一个归档可声明多个模组；文件始终作为启禁和回收的操作单位。</summary>
public sealed record LocalModIdentity(string Id, string Name, string Version);

/// <summary>列表快照不代表完整哈希检查已通过，Path 是相对实例根目录的稳定操作标识。</summary>
public sealed record LocalModEntry
{
    public required string Path { get; init; }
    public string? CurrentPath { get; init; }
    public required string FileName { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "未知";
    public IReadOnlyList<LocalModIdentity> Identities { get; init; } = [];
    public LocalModState State { get; init; }
    public string SourceDescription { get; init; } = "来源未确认";
    public string? Problem { get; init; }
    public bool IsPresent { get; init; }
    public bool CanManage { get; init; }
    public bool CanAdopt { get; init; }
    public bool CanRestore { get; init; }
    public long? Size { get; init; }
    public string StateText => State switch { LocalModState.Enabled => "已启用", LocalModState.Disabled => "已禁用", _ => "已移除" };
}

/// <summary>当前实例的轻量模组目录与说明；不会遍历其他实例或执行模组代码。</summary>
public sealed record LocalModCatalog(IReadOnlyList<LocalModEntry> Files, IReadOnlyList<string> Notes);

/// <summary>在实例操作锁内管理玩家额外模组；原包清单声明文件受到保护。</summary>
public interface ILocalModManager
{
    /// <summary>读取模组列表和用户状态，不执行全量内容哈希检查。</summary>
    Task<LocalModCatalog> ListAsync(string instancePath, CancellationToken token = default);
    /// <summary>检查重复后复制一个本地模组，并保存用户确认的内容基线。</summary>
    Task AddAsync(string instancePath, string sourcePath, CancellationToken token = default);
    /// <summary>经用户确认将未登记的外部模组纳入管理，不能接管原包声明文件。</summary>
    Task AdoptAsync(string instancePath, string path, CancellationToken token = default);
    /// <summary>切换已管理模组的加载状态，保留其来源和完整性要求。</summary>
    Task SetEnabledAsync(string instancePath, string path, bool enabled, CancellationToken token = default);
    /// <summary>将模组移入实例回收区并记录主动移除，不永久删除文件。</summary>
    Task RemoveAsync(string instancePath, string path, CancellationToken token = default);
    /// <summary>从回收区恢复到移除前的状态，目标冲突时拒绝覆盖。</summary>
    Task RestoreAsync(string instancePath, string path, CancellationToken token = default);
    /// <summary>完成或回滚中断的管理操作；发生外部变更时保留文件并报告冲突。</summary>
    Task RecoverAsync(string instancePath, CancellationToken token = default);
}
