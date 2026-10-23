using System.Diagnostics;
using System.Text.RegularExpressions;
using EML.Core.Java;

namespace EML.Infrastructure.Java;

public sealed class WindowsJavaLocator : IJavaLocator
{
    //第一部分 FindAllAsync
    public async Task<IReadOnlyList<JavaInstallation>> FindAllAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<JavaInstallation>();

        var candidates = FindCandidatePaths()
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var javaPath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var installation = await InspectJavaAsync(
                javaPath,
                cancellationToken);

            if (installation is not null)
            {
                results.Add(installation);
            }
        }

        return results;
    }
    //第二部分 FindCandidatePaths 负责寻找可能的 Java 路径：JAVA_HOME\bin\java.exe , PATH 中每个目录\java.exe
    private static IEnumerable<string> FindCandidatePaths()
    {
        // 搜索 JAVA_HOME
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");

        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var javaPath = Path.Combine(
                javaHome.Trim().Trim('"'),
                "bin",
                "java.exe");

            if (File.Exists(javaPath))
            {
                yield return Path.GetFullPath(javaPath);
            }
        }

        // 搜索 PATH 中的所有目录
        var pathVariable = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            yield break;
        }

        foreach (var directory in pathVariable.Split(Path.PathSeparator))
        {
            var cleanDirectory = directory.Trim().Trim('"');

            if (string.IsNullOrWhiteSpace(cleanDirectory))
            {
                continue;
            }

            string javaPath;

            try
            {
                javaPath = Path.Combine(cleanDirectory, "java.exe");
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(javaPath))
            {
                yield return Path.GetFullPath(javaPath);
            }
        }
    }
    //第三部分 InspectJavaAsync 负责执行： java.exe -version
    private static async Task<JavaInstallation?> InspectJavaAsync(
        string javaPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = javaPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-version");

            using var process = new Process
            {
                StartInfo = startInfo
            };

            if (!process.Start())
            {
                return null;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            var error = await errorTask;
            var versionOutput = output + Environment.NewLine + error;

            if (process.ExitCode != 0)
            {
                return null;
            }

            return new JavaInstallation(
                ExecutablePath: javaPath,
                Version: ParseVersion(versionOutput),
                Architecture: ParseArchitecture(versionOutput));
        }
        catch
        {
            // 某个 Java 无法执行时，跳过它并继续检测其他路径。
            return null;
        }
    }

    private static string ParseVersion(string output)
    {
        var match = Regex.Match(
            output,
            @"version\s+""(?<version>[^""]+)""",
            RegexOptions.IgnoreCase);

        return match.Success
            ? match.Groups["version"].Value
            : "未知";
    }

    private static string ParseArchitecture(string output)
    {
        if (output.Contains(
                "64-Bit",
                StringComparison.OrdinalIgnoreCase))
        {
            return "64 位";
        }

        if (output.Contains(
                "32-Bit",
                StringComparison.OrdinalIgnoreCase))
        {
            return "32 位";
        }

        return "未知";
    }
}