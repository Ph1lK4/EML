using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Migration;

/// <summary>迁移专用安全路径、限量枚举和可恢复的原子元数据写入。</summary>
internal static class MigrationIO
{
    internal const int MaximumFiles = 200_000;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    /// <summary>拒绝越界、链接及把普通文件作为祖先目录的路径。</summary>
    internal static string SafePath(string root, string relative)
    {
        var path = InstanceImportPaths.Within(root, relative);
        InstanceImportPaths.EnsureNoLinks(path);
        for (var parent = Path.GetDirectoryName(path); parent is not null && parent.Length >= root.Length; parent = Path.GetDirectoryName(parent))
            if (File.Exists(parent)) throw new IOException("父目录被文件占用：" + parent);
        return path;
    }
    /// <summary>有界遍历玩家数据；逐层拒绝链接，保留相对路径。</summary>
    internal static IReadOnlyList<string> Files(string root, string relative, CancellationToken token)
    {
        var path = SafePath(root, relative);
        if (File.Exists(path)) return [InstanceImportPaths.Relative(relative)];
        if (!Directory.Exists(path)) return [];
        var result = new List<string>();
        var pending = new Stack<string>(); pending.Push(path);
        var entries = 0;
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > MaximumFiles) throw new IOException("迁移文件数量过多，请分批迁移。");
                var value = InstanceImportPaths.Relative(Path.GetRelativePath(root, entry).Replace('\\', '/'));
                SafePath(root, value);
                if (Directory.Exists(entry)) pending.Push(entry); else result.Add(value);
            }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    /// <summary>稳定读取 SHA-256；不存在返回 null，访问失败和目录冲突均报错。</summary>
    internal static async Task<(long Size, string Hash)?> SnapshotAsync(string path, CancellationToken token)
    {
        InstanceImportPaths.EnsureNoLinks(path);
        if (Directory.Exists(path)) throw new IOException("文件位置被目录占用：" + path);
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            var before = File.GetLastWriteTimeUtc(path);
            var size = input.Length;
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
            if (size != input.Length || before != File.GetLastWriteTimeUtc(path)) throw new IOException("读取时文件发生变化：" + path);
            return (size, hash);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    /// <summary>严格 UTF-8 读取小型 options；只替换语言一项，其他文本及换行保持不变。</summary>
    internal static byte[] MergeLanguage(byte[] source, byte[] target)
    {
        if (source.Length > 1024 * 1024 || target.Length > 1024 * 1024) throw new InvalidDataException("options.txt 过大。");
        var encoding = new UTF8Encoding(false, true);
        var oldText = encoding.GetString(source).TrimStart('\uFEFF');
        var newText = encoding.GetString(target);
        var languages = oldText.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("lang:", StringComparison.Ordinal)).ToArray();
        if (languages.Length != 1 || !System.Text.RegularExpressions.Regex.IsMatch(languages[0], @"\Alang:[A-Za-z]{2,3}_[A-Za-z]{2,3}\z"))
            throw new InvalidDataException("旧 options.txt 没有唯一有效的语言项。");
        var lines = newText.Split('\n');
        var found = false;
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].TrimStart('\uFEFF').StartsWith("lang:", StringComparison.Ordinal))
            {
                if (found) throw new InvalidDataException("新版 options.txt 存在重复语言项。");
                lines[i] = (lines[i].StartsWith('\uFEFF') ? "\uFEFF" : "") + languages[0] + (lines[i].EndsWith('\r') ? "\r" : "");
                found = true;
            }
        if (found) return encoding.GetBytes(string.Join('\n', lines));
        var ending = newText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return encoding.GetBytes(newText + (newText.Length > 0 && !newText.EndsWith('\n') ? ending : "") + languages[0] + ending);
    }
    /// <summary>原子发布自己的小型状态文件，写盘后再替换完成标记。</summary>
    internal static void WriteJson<T>(string path, T value)
    {
        InstanceImportPaths.EnsureNoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("迁移记录超过大小限制。");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            InstanceImportPaths.EnsureNoLinks(path);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    /// <summary>读取限量的内部状态；损坏时明确停止而非自动忽略。</summary>
    internal static T ReadJson<T>(string path)
    {
        InstanceImportPaths.EnsureNoLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 64 * 1024 * 1024) throw new InvalidDataException("迁移记录过大。");
        return JsonSerializer.Deserialize<T>(input, Json) ?? throw new InvalidDataException("迁移记录为空。");
    }
}
