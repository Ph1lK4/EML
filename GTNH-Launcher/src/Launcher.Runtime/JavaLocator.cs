using Launcher.Metadata;

namespace Launcher.Runtime;

/// <summary>探测到的一个 Java 运行时。</summary>
public sealed record JavaCandidate(string Path, int Major, string Version)
{
    public override string ToString() => $"Java {Major}  ({Version})  ·  {Path}";
}

/// <summary>
/// 本机 Java 探测：JAVA_HOME + 常见安装目录 + Mojang 自带运行时。
/// 只负责「找到并认出大版本」，不负责下载 JRE；GTNH 的 Java 8 / 17-25 两个变体靠这个挑。
/// </summary>
public static class JavaLocator
{
    public static IReadOnlyList<JavaCandidate> FindAll()
    {
        var results = new List<JavaCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in Roots())
        {
            foreach (var candidate in Scan(root))
            {
                if (seen.Add(candidate.Path)) results.Add(candidate);
            }
        }

        return results.OrderByDescending(c => c.Major).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>在 root 下（含子目录，最多 4 层）找 bin\javaw.exe。</summary>
    public static IEnumerable<JavaCandidate> Scan(string root, int maxDepth = 4)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;

        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();

            if (ParseInstall(directory) is { } candidate) yield return candidate;

            if (depth >= maxDepth) continue;

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var child in children) queue.Enqueue((child, depth + 1));
        }
    }

    /// <summary>读 java home 下的 release 文件（JAVA_VERSION="1.8.0_51"）。</summary>
    public static JavaCandidate? ParseInstall(string javaHome)
    {
        var javaw = Path.Combine(javaHome, "bin", "javaw.exe");
        if (!File.Exists(javaw)) return null;

        var release = Path.Combine(javaHome, "release");
        if (!File.Exists(release)) return null;

        foreach (var line in File.ReadLines(release))
        {
            if (!line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal)) continue;

            var version = line["JAVA_VERSION=".Length..].Trim().Trim('"');
            var major = ParseMajor(version);
            return major > 0 ? new JavaCandidate(javaw, major, version) : null;
        }

        return null;
    }

    /// <summary>"1.8.0_51" → 8，"17.0.9" → 17，"21" → 21。</summary>
    public static int ParseMajor(string version)
    {
        var text = version.Trim().Trim('"');
        if (text.Length == 0) return 0;

        if (text.StartsWith("1.", StringComparison.Ordinal))
        {
            var rest = text[2..];
            var end = rest.IndexOfAny(new[] { '.', '_', '-' });
            return int.TryParse(end < 0 ? rest : rest[..end], out var legacy) ? legacy : 0;
        }

        var dot = text.IndexOf('.');
        var head = dot < 0 ? text : text[..dot];
        return int.TryParse(head, out var major) ? major : 0;
    }

    private static IEnumerable<string> Roots()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) yield return javaHome;

        foreach (var directory in KnownDirectories())
        {
            if (Directory.Exists(directory)) yield return directory;
        }
    }

    private static IEnumerable<string> KnownDirectories()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var vendors = new[]
        {
            "Java", "Eclipse Adoptium", "Eclipse Foundation", "AdoptOpenJDK", "Zulu",
            "BellSoft", "Amazon Corretto", "Semeru", "Microsoft",
        };

        foreach (var baseDirectory in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrEmpty(baseDirectory)) continue;
            foreach (var vendor in vendors) yield return Path.Combine(baseDirectory, vendor);
        }

        if (!string.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "Programs", "Eclipse Adoptium");

        // Mojang 启动器自带的 JRE：.minecraft/runtime/<component>/<platform>/<component>
        if (!string.IsNullOrEmpty(appData))
            yield return Path.Combine(appData, ".minecraft", "runtime");
    }
}
