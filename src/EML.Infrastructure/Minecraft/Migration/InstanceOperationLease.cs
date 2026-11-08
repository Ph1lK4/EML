using EML.Infrastructure.Minecraft.Importing;
using System.Security.Cryptography;
using System.Text;

namespace EML.Infrastructure.Minecraft.Migration;

/// <summary>运行与迁移共用跨进程文件锁；进程退出后系统自动释放，不依赖残留标记。</summary>
internal sealed class InstanceOperationLease : IDisposable
{
    private readonly List<FileStream> _locks = [];
    /// <summary>按固定顺序锁定两个实例，任一占用时释放已取得的锁并明确失败。</summary>
    internal static InstanceOperationLease Acquire(params string[] roots)
    {
        var lease = new InstanceOperationLease();
        try
        {
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                // 锁放在启动器私有目录，避免 Windows 的打开子文件句柄阻止实例目录正常重命名。
                // 实例已经由调用方校验为 instances 的直接子目录，按规范化路径生成稳定键。
                var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                var launcher = Path.GetDirectoryName(Path.GetDirectoryName(normalized))!;
                var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToUpperInvariant())));
                var path = MigrationIO.SafePath(launcher, ".eml-runtime/instance-locks/" + key + ".lock");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                lease._locks.Add(new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            return lease;
        }
        catch (IOException ex) { lease.Dispose(); throw new IOException("无法取得实例操作锁；实例可能正在由 EML 使用，请等待当前操作结束。", ex); }
        catch { lease.Dispose(); throw; }
    }
    /// <summary>只释放本次持有句柄，不删除可能由另一进程正在使用的锁文件。</summary>
    public void Dispose() { foreach (var handle in _locks) handle.Dispose(); _locks.Clear(); }
}
