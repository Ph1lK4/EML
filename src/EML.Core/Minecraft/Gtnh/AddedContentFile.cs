namespace EML.Core.Minecraft.Gtnh;

/// <summary>玩家明确下载的资源包或光影；保留平台原始来源，独立于整合包原清单。</summary>
public sealed record AddedContentFile(GtnhAddonKind Kind, InstanceFileRequirement Baseline);
