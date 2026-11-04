using System.IO.Compression;
using EML.Core.Minecraft;

namespace EML.Infrastructure.Minecraft.Importing;

/// <summary>保存解析后的导入清单及文件提取计划，覆盖文件的优先级已处理完毕。</summary>
/// <param name="Manifest">统一的版本信息和文件需求。</param>
/// <param name="Copies">需要从压缩包提取的条目及其目标路径。</param>
/// <param name="PreserveRemoteRequirements">重新导入 EML 备份时，保留原始下载来源和校验值。</param>
internal sealed record InstanceArchivePlan(InstanceImportManifest Manifest, IReadOnlyList<InstanceArchiveCopy> Copies,
    bool PreserveRemoteRequirements = false);

/// <summary>描述一个压缩条目要写入实例中的哪个位置。</summary>
/// <param name="Entry">要读取的原始 ZIP 条目。</param>
/// <param name="TargetPath">相对于实例目录的目标路径。</param>
/// <param name="Directory">是否为目录；目录条目用于保留空目录。</param>
internal sealed record InstanceArchiveCopy(ZipArchiveEntry Entry, string TargetPath, bool Directory);
