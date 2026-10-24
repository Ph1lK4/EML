using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text.RegularExpressions;
using EML.Core.Java;
using Microsoft.Win32;

namespace EML.Infrastructure.Java;

/// <summary>发现 Windows 已安装和常见位置中的 Java；不扫描整块硬盘。</summary>
public sealed class WindowsJavaLocator : IJavaLocator
{
    private readonly string[] _additionalSearchRoots;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    // 便携 Java 可由设置页传入额外目录；原来的无参构造方式仍然可用。
    public WindowsJavaLocator(IEnumerable<string>? additionalSearchRoots = null)
    {
        _additionalSearchRoots = additionalSearchRoots?.ToArray() ?? [];
    }

    public async Task<IReadOnlyList<JavaInstallation>> FindAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("此检测器用于 Windows。");

        // 目录枚举和注册表访问也放到后台，避免阻塞 WPF 界面。
        var candidates = await Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("此检测器用于 Windows。");
            return Discover(cancellationToken);
        }, cancellationToken);
        var found = new ConcurrentDictionary<string, JavaInstallation>(PathComparer);
        await Parallel.ForEachAsync(candidates, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        }, async (path, token) =>
        {
            var installation = await InspectAsync(path, token);
            if (installation is not null)
                found.TryAdd(installation.ExecutablePath, installation);
        });

        return found.Values
            .OrderByDescending(item => MajorVersion(item.Version))
            .ThenBy(item => item.ExecutablePath, PathComparer)
            .ToArray();
    }

    [SupportedOSPlatform("windows")]
    private string[] Discover(CancellationToken token)
    {
        var executables = new HashSet<string>(PathComparer);
        var roots = new Dictionary<string, int>(PathComparer);

        void AddRoot(string? path, int depth)
        {
            var normalized = Normalize(path);
            if (normalized is not null && Directory.Exists(normalized))
                roots[normalized] = Math.Max(roots.GetValueOrDefault(normalized), depth);
        }

        foreach (var target in new[] { EnvironmentVariableTarget.Process,
                     EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            foreach (var name in new[] { "JAVA_HOME", "JDK_HOME", "JRE_HOME" })
                AddRoot(Environment.GetEnvironmentVariable(name, target), 3);

            foreach (var path in (Environment.GetEnvironmentVariable("PATH", target) ?? "")
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                AddExecutable(path, executables);
        }

        // JavaSoft 注册信息 + 已安装程序登记的 InstallLocation；支持不同盘符。
        foreach (var path in RegistryLocations(token))
            AddRoot(path, 4);

        foreach (var path in _additionalSearchRoots)
            AddRoot(path, 6);

        // Windows 提供实际目录；不写死用户名、盘符或 Java 发行商名称。
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), 4);
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), 4);
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 5);
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 5);
        AddRoot(AppContext.BaseDirectory, 5);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            foreach (var name in new[] { ".jdks", ".jvm", ".jabba", ".sdkman" })
                AddRoot(Path.Combine(userProfile, name), 5);
        }

        foreach (var (path, depth) in roots)
        {
            token.ThrowIfCancellationRequested();
            ScanRoot(path, depth, executables, token);
        }
        return executables.OrderBy(path => path, PathComparer).ToArray();
    }

    private static void ScanRoot(string root, int maxDepth,
        HashSet<string> executables, CancellationToken token)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = new HashSet<string>(PathComparer);
        var clock = Stopwatch.StartNew();
        // 每个来源最多访问 5000 个目录、耗时约 3 秒（不含单次系统 I/O）。
        while (queue.Count > 0 && visited.Count < 5000 && clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            token.ThrowIfCancellationRequested();
            var (path, depth) = queue.Dequeue();
            if (!visited.Add(path)) continue;
            AddExecutable(path, executables);
            if (depth >= maxDepth) continue;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
                };
                foreach (var child in Directory.EnumerateDirectories(path, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (queue.Count + visited.Count >= 5000 || clock.Elapsed >= TimeSpan.FromSeconds(3))
                        break;
                    queue.Enqueue((child, depth + 1));
                }
            }
            catch (Exception ex) when (IsExpected(ex)) { }
        }
    }

    private static void AddExecutable(string? directory, HashSet<string> paths)
    {
        var normalized = Normalize(directory);
        if (normalized is null) return;
        // javaw 与 java 属于同一运行时；使用控制台入口可靠读取版本输出。
        var executable = Path.Combine(normalized, "java.exe");
        if (File.Exists(executable)) paths.Add(executable);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> RegistryLocations(CancellationToken token)
    {
        var locations = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var javaSoft = root.OpenSubKey(@"SOFTWARE\JavaSoft");
                if (javaSoft is not null) ReadJavaKeys(javaSoft, 4, locations, token);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using var item = uninstall.OpenSubKey(name);
                        // 不依赖发行商名字；查找已登记软件中的 Java 可执行文件。
                        if (item?.GetValue("InstallLocation") is string location &&
                            !string.IsNullOrWhiteSpace(location)) locations.Add(location);
                    }
                    catch (Exception ex) when (IsExpected(ex)) { }
                }
            }
            catch (Exception ex) when (IsExpected(ex)) { }
        }
        return locations;
    }

    [SupportedOSPlatform("windows")]
    private static void ReadJavaKeys(RegistryKey key, int remainingDepth,
        List<string> locations, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (key.GetValue("JavaHome") is string home) locations.Add(home);
            if (remainingDepth == 0) return;
            foreach (var name in key.GetSubKeyNames())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var child = key.OpenSubKey(name);
                    if (child is not null) ReadJavaKeys(child, remainingDepth - 1, locations, token);
                }
                catch (Exception ex) when (IsExpected(ex)) { }
            }
        }
        catch (Exception ex) when (IsExpected(ex)) { }
    }

    private static async Task<JavaInstallation?> InspectAsync(string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-XshowSettings:properties");
        process.StartInfo.ArgumentList.Add("-version");
        // 检测时不执行用户为游戏或其他程序设置的 Java 代理等选项。
        foreach (var name in new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS" })
            process.StartInfo.Environment.Remove(name);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!process.Start()) return null;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            // Task.WhenAll 观察两个读取任务和进程退出，避免管道阻塞。
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) return null;
            return ParseInstallation(path, stdout.Result + "\n" + stderr.Result);
        }
        catch (OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return null; // 单个运行时超时不影响其他结果。
        }
        catch (Exception ex) when (IsExpected(ex) || ex is Win32Exception)
        {
            return null;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
        }
    }

    internal static JavaInstallation? ParseInstallation(string path, string output)
    {
        var version = ReadProperty(output, "java.version");
        if (string.IsNullOrWhiteSpace(version)) return null;
        var home = Normalize(ReadProperty(output, "java.home"));
        if (home is null) return null;
        var canonical = Path.Combine(home, "bin", "java.exe");
        if (!File.Exists(canonical)) canonical = path;
        // 相同 java.home 的 Oracle 入口、PATH 入口和实际文件合并成一条。
        var arch = ReadProperty(output, "os.arch").ToLowerInvariant();
        var bitness = ReadProperty(output, "sun.arch.data.model");
        var display = arch switch
        {
            "aarch64" or "arm64" => "ARM64（64 位）",
            "amd64" or "x86_64" => "x64（64 位）",
            "x86" or "i386" or "i486" or "i586" or "i686" => "x86（32 位）",
            _ => bitness is "32" or "64" ? $"{bitness} 位（{arch}）" : $"未知（{arch}）"
        };
        return new JavaInstallation(canonical, version, display);
    }

    private static string ReadProperty(string output, string name) =>
        Regex.Match(output, @"^\s*" + Regex.Escape(name) + @"\s*=\s*([^\r\n]+)",
            RegexOptions.Multiline).Groups[1].Value.Trim();

    private static int MajorVersion(string version)
    {
        var match = Regex.Match(version, @"^(?:1\.)?(\d+)");
        return int.TryParse(match.Groups[1].Value, out var major) ? major : 0;
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : null;
        }
        catch (Exception ex) when (IsExpected(ex)) { return null; }
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException;
}
