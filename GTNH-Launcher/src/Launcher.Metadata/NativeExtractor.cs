using System.IO.Compression;
using Launcher.Contracts;

namespace Launcher.Metadata;

/// <summary>把 natives jar 解压到实例的 natives 目录，跳过 META-INF 之类的垃圾。</summary>
public static class NativeExtractor
{
    public static int Extract(IEnumerable<NativeJar> natives, string destinationDirectory)
    {
        var root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);
        var written = 0;

        foreach (var native in natives)
        {
            using var archive = ZipFile.OpenRead(native.JarPath);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrEmpty(entry.Name)) continue; // 目录项
                if (native.Excludes.Any(exclude => name.StartsWith(exclude, StringComparison.OrdinalIgnoreCase))) continue;

                var target = Path.GetFullPath(Path.Combine(root, name));
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue; // 防 zip slip

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                written++;
            }
        }

        return written;
    }
}
