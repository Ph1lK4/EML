namespace EML.Core.Minecraft;

/// <summary>实例写入操作，与只读扫描接口分开，方便分别维护和测试。</summary>
public interface IMinecraftInstanceManager
{
    /// <summary>将实例交给系统回收站处理；取消或失败会抛出异常。</summary>
    Task DeleteAsync(string instancePath, CancellationToken cancellationToken = default);

    /// <summary>重命名实例目录，并同步 Prism 配置名称；返回新的绝对路径。</summary>
    Task<string> RenameAsync(string instancePath, string newName, CancellationToken cancellationToken = default);

    /// <summary>将实例内容导出为新 ZIP 文件，不覆盖已有文件；支持进度与取消。</summary>
    Task ExportAsync(string instancePath, string destinationZip,
        IProgress<InstanceExportProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>当前导出文件、已复制字节、总字节；空文件包完成时进度为 100%。</summary>
public sealed record InstanceExportProgress(string FileName, long BytesCopied, long TotalBytes)
{
    public double Percent => TotalBytes == 0 ? 100 : Math.Clamp(100.0 * BytesCopied / TotalBytes, 0, 100);
}
