using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;
using EML.Infrastructure.Minecraft.Migration;

namespace EML.Infrastructure.Minecraft;

/// <summary>
/// 负责重命名、回收和 ZIP 导出，不承担扫描职责。
/// 所有操作限制在 instances 的直接子目录，并拒绝链接，防止误操作其他位置。
/// </summary>
public sealed class MinecraftInstanceManager : IMinecraftInstanceManager
{
    private readonly string _instancesRoot;
    private readonly Action<string> _recycleDirectory;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>接收与扫描器相同的启动器根目录；默认使用 EXE 所在目录。</summary>
    public MinecraftInstanceManager(string? launcherRoot = null)
        : this(launcherRoot ?? AppContext.BaseDirectory, RecycleDirectory) { }

    /// <summary>测试注入回收操作，避免自动测试实际操作用户回收站。</summary>
    internal MinecraftInstanceManager(string launcherRoot, Action<string> recycleDirectory)
    {
        _instancesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(launcherRoot, "instances")));
        _recycleDirectory = recycleDirectory;
    }

    /// <summary>校验实例及内部链接后请求系统回收，同一服务的写入操作依次执行。</summary>
    public async Task DeleteAsync(string instancePath, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() =>
            {
                var source = ValidateInstance(instancePath);
                using var lease = InstanceOperationLease.Acquire(source);
                EnumerateContents(source, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _recycleDirectory(source);
                if (Directory.Exists(source)) throw new IOException("系统未完成删除，实例目录仍然存在。");
            }, cancellationToken);
        }
        finally { _operationLock.Release(); }
    }

    /// <summary>重命名返回新路径；如果写入 Prism 名称失败，尝试将目录恢复原名。</summary>
    public async Task<string> RenameAsync(string instancePath, string newName, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try { return await Task.Run(() => Rename(instancePath, newName, cancellationToken), cancellationToken); }
        finally { _operationLock.Release(); }
    }

    /// <summary>先导出到临时 ZIP，全部成功才发布目标文件，支持取消和进度。</summary>
    public async Task ExportAsync(string instancePath, string destinationZip,
        IProgress<InstanceExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try { await Task.Run(() => ExportCoreAsync(instancePath, destinationZip, progress, cancellationToken), cancellationToken); }
        finally { _operationLock.Release(); }
    }

    /// <summary>确认目标是直接子实例；导入清单与游戏目录采用和扫描器相同的路径、结构及链接校验。</summary>
    private string ValidateInstance(string instancePath)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instancePath));
        if (!string.Equals(Path.GetDirectoryName(path), _instancesRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能操作 instances 文件夹的直接子实例。");
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("实例目录已不存在，请刷新列表。");
        EnsureNoLinkedAncestors(path);
        MigrationJournalStore.EnsureSettled(path);
        var manifestPath = Path.Combine(path, InstanceImportManifest.FileName);
        if (File.Exists(manifestPath))
        {
            // 清单存在时必须先验证，不能让坏清单借助 .minecraft 等传统标记绕过检查。
            var manifest = InstanceManifestStore.Read(manifestPath);
            var game = manifest.GameDirectory == "." ? path : InstanceImportPaths.Within(path, manifest.GameDirectory);
            InstanceImportPaths.EnsureNoLinks(game);
            return path;
        }
        if (!Directory.Exists(Path.Combine(path, ".minecraft")) &&
            !Directory.Exists(Path.Combine(path, "minecraft")) &&
            !Directory.Exists(Path.Combine(path, "versions")) &&
            !File.Exists(Path.Combine(path, "mmc-pack.json")))
            throw new InvalidOperationException("该文件夹没有可识别的实例标记，已停止操作。");
        return path;
    }

    /// <summary>校验新名称，拒绝路径分隔符、非法字符及 Windows 保留名称。</summary>
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name != name.Trim() ||
            name.EndsWith('.') || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
            throw new ArgumentException("名称应为 1～128 个字符，不能含路径分隔符、非法字符或首尾空格、末尾句点。");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
            throw new ArgumentException("名称不能使用 Windows 保留设备名，例如 CON、NUL、COM1。");
    }

    /// <summary>移动实例目录并更新 Prism 名称，失败时尝试恢复原目录。</summary>
    private string Rename(string instancePath, string newName, CancellationToken token)
    {
        ValidateName(newName);
        var source = ValidateInstance(instancePath);
        using var lease = InstanceOperationLease.Acquire(source);
        EnumerateContents(source, token);
        var destination = Path.Combine(_instancesRoot, newName);
        var samePath = string.Equals(source, destination, StringComparison.OrdinalIgnoreCase);
        if (!samePath && (Directory.Exists(destination) || File.Exists(destination)))
            throw new IOException("已经存在同名文件或实例，请使用其他名称。");
        if (samePath && !string.Equals(source, destination, StringComparison.Ordinal) &&
            Directory.EnumerateFileSystemEntries(_instancesRoot).Any(p =>
                !string.Equals(p, source, StringComparison.Ordinal) && string.Equals(p, destination, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("存在大小写冲突的目录，请使用其他名称。");

        var current = source;
        string? temporaryConfigName = null;
        try
        {
            if (File.Exists(Path.Combine(source, "mmc-pack.json")))
            {
                var config = Path.Combine(source, "instance.cfg");
                if (File.Exists(config) && new FileInfo(config).Length > 1024 * 1024)
                    throw new InvalidDataException("instance.cfg 过大，无法安全修改。");
                var encoding = new UTF8Encoding(false, true) as Encoding;
                var original = "";
                if (File.Exists(config))
                {
                    using var reader = new StreamReader(config, encoding, detectEncodingFromByteOrderMarks: true);
                    original = reader.ReadToEnd();
                    encoding = reader.CurrentEncoding;
                }
                var updated = UpdatePrismName(original, newName);
                var manifestPath = Path.Combine(source, InstanceImportManifest.FileName);
                if (updated != original && File.Exists(manifestPath))
                {
                    var manifest = InstanceManifestStore.Read(manifestPath);
                    var requirement = InstanceEffectiveRequirements.Build(source, manifest).Requirements.SingleOrDefault(file =>
                        string.Equals(file.Path, "instance.cfg", StringComparison.OrdinalIgnoreCase));
                    if (requirement is not null && !InstanceFileChangePolicy.IsMutableConfiguration(
                            requirement, manifest.GameDirectory, manifest.SourceFormat))
                        throw new InvalidOperationException("instance.cfg 使用严格来源或用户确认的替换基线，无法自动改写名称；请先调整该文件的变更策略。");
                }
                temporaryConfigName = ".eml-name-" + Guid.NewGuid().ToString("N") + ".tmp";
                using var stream = new FileStream(Path.Combine(source, temporaryConfigName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, encoding);
                writer.Write(updated);
            }

            // 写入提交开始后不再响应取消，保证目录和配置同步完成或回滚。
            token.ThrowIfCancellationRequested();
            if (!string.Equals(source, destination, StringComparison.Ordinal))
            {
                if (samePath)
                {
                    var intermediate = Path.Combine(_instancesRoot, ".eml-rename-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(current, intermediate);
                    current = intermediate;
                }
                Directory.Move(current, destination);
                current = destination;
            }
            if (temporaryConfigName is not null)
                File.Move(Path.Combine(current, temporaryConfigName), Path.Combine(current, "instance.cfg"), overwrite: true);
            return current;
        }
        catch (Exception operationError)
        {
            if (!string.Equals(current, source, StringComparison.Ordinal))
            {
                try { Directory.Move(current, source); current = source; }
                catch (Exception rollbackError)
                {
                    throw new IOException($"重命名失败，自动恢复也失败。实例保留在：{current}。请刷新后检查。",
                        new AggregateException(operationError, rollbackError));
                }
            }
            throw;
        }
        finally
        {
            if (temporaryConfigName is not null) TryDeleteTemporary(Path.Combine(current, temporaryConfigName));
        }
    }

    /// <summary>只修改 General 节的 name，保留其他设置和注释；没有该节/字段时追加。</summary>
    internal static string UpdatePrismName(string original, string name)
    {
        var newline = original.Contains("\r\n") ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n").Split('\n').ToList();
        var inGeneral = false;
        var sawGeneral = false;
        var wroteName = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('['))
            {
                if (inGeneral && !wroteName) { lines.Insert(i++, "name=" + name); wroteName = true; }
                inGeneral = line.Equals("[General]", StringComparison.OrdinalIgnoreCase);
                sawGeneral |= inGeneral;
                continue;
            }
            var equal = line.IndexOf('=');
            if (inGeneral && equal > 0 && line[..equal].Trim().Equals("name", StringComparison.OrdinalIgnoreCase))
            { lines[i] = "name=" + name; wroteName = true; }
        }
        if (!wroteName)
        {
            if (!sawGeneral) lines.Add("[General]");
            lines.Add("name=" + name);
        }
        return string.Join(newline, lines);
    }

    /// <summary>收集包含隐藏文件和空目录的快照；发现链接时拒绝操作。</summary>
    private static List<(string Path, bool IsDirectory, long Length)> EnumerateContents(string source, CancellationToken token)
    {
        var entries = new List<(string, bool, long)>();
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            EnsureNoLinkedAncestors(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("实例包含链接文件或目录，请先处理后再操作：" + path);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                entries.Add((path, isDirectory, isDirectory ? 0 : new FileInfo(path).Length));
                if (isDirectory) pending.Push(path);
            }
        }
        return entries;
    }

    /// <summary>将实例内容写入 ZIP 根层；禁止写入实例内部或覆盖已有文件。</summary>
    private async Task ExportCoreAsync(string instancePath, string destinationZip,
        IProgress<InstanceExportProgress>? progress, CancellationToken token)
    {
        var source = ValidateInstance(instancePath);
        using var lease = InstanceOperationLease.Acquire(source);
        var destination = Path.GetFullPath(destinationZip);
        if (!Path.GetExtension(destination).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("导出文件必须使用 .zip 扩展名。");
        if (destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不能将 ZIP 导出到实例内部，请选择其他目录。");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("目标已经存在，请选择新的文件名。");
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("导出目录不存在。");
        EnsureNoLinkedAncestors(parent);
        var manifestPath = Path.Combine(source, InstanceImportManifest.FileName);
        var backup = File.Exists(manifestPath)
            ? InstanceBackupManifest.Create(source, InstanceManifestStore.Read(manifestPath)) : null;
        // 本机位置、用户选择状态和可再生缓存不直接传播；可信来源与选择已转入备份清单。
        var entries = EnumerateContents(source, token)
            .Where(entry => !InstanceFileChangePolicy.IsDerivedStatePath(Path.GetRelativePath(source, entry.Path).Replace('\\', '/'))).ToList();
        var total = entries.Sum(entry => entry.Length);
        long copied = 0;
        var temporary = Path.Combine(parent, ".eml-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                progress?.Report(new("开始导出", 0, total));
                token.ThrowIfCancellationRequested();
                var buffer = new byte[128 * 1024];
                var reportClock = Stopwatch.StartNew();
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    EnsureNoLinkedAncestors(entry.Path);
                    var relative = Path.GetRelativePath(source, entry.Path).Replace('\\', '/');
                    if (entry.IsDirectory) { zip.CreateEntry(relative + "/"); continue; }
                    var archived = zip.CreateEntry(relative, CompressionLevel.Fastest);
                    if (backup is not null && relative.Equals(InstanceImportManifest.FileName, StringComparison.OrdinalIgnoreCase))
                    {
                        using var manifestOutput = archived.Open();
                        await manifestOutput.WriteAsync(InstanceManifestStore.Serialize(backup.Manifest), token);
                        copied += entry.Length;
                        continue;
                    }
                    using var input = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);
                    if (input.Length != entry.Length) throw new IOException("文件在导出期间发生变化，请关闭游戏后重试：" + relative);
                    using var target = archived.Open();
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) != 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), token);
                        copied += read;
                        if (reportClock.ElapsedMilliseconds >= 100)
                        {
                            progress?.Report(new(relative, copied, total));
                            reportClock.Restart();
                        }
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            if (backup is not null) InstanceBackupManifest.EnsureUnchanged(source, backup);
            File.Move(temporary, destination); // 不覆盖旧文件；竞争创建的同名文件同样会保留。
            progress?.Report(new("完成", copied, total));
        }
        finally { TryDeleteTemporary(temporary); }
    }

    /// <summary>校验目标及其祖先，拒绝通过目录联接绕过 instances 范围或导出位置限制。</summary>
    private static void EnsureNoLinkedAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("该路径包含符号链接或目录联接，暂不支持操作：" + current);
    }

    /// <summary>只清理由本操作生成的唯一临时文件；失败不掩盖原始异常。</summary>
    private static void TryDeleteTemporary(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>请求 Windows 回收站；系统提示交给用户处理，取消作为异常返回。</summary>
    private static void RecycleDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("回收站功能仅支持 Windows。");
        FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
    }
}
