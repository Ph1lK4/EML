namespace EML.Core.Minecraft.Mods;

/// <summary>可随 EML 备份携带的明确用户操作；基线仍绑定原始内容，不从当前磁盘自动重建。</summary>
public sealed record ManagedModRecord
{
    public required string Id { get; init; }
    public required InstanceFileRequirement Baseline { get; init; }
    /// <summary>true 表示 EML 新增/接管，false 表示此前通过 GTNH 迁移登记的额外模组。</summary>
    public bool Added { get; init; }
    public LocalModState State { get; init; }
    /// <summary>回收前是否禁用，使恢复不会擅自重新启用模组。</summary>
    public bool RemovedWasDisabled { get; init; }
}
