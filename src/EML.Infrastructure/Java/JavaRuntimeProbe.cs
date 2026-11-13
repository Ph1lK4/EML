using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using EML.Core.Java;

namespace EML.Infrastructure.Java;

/// <summary>查询一个明确指定的 Java；不继承可能注入代理的 Java 环境选项。</summary>
public static partial class JavaRuntimeProbe
{
    /// <summary>有界等待版本与架构查询，取消或超时只清理本次创建的检测进程。</summary>
    public static async Task<JavaInstallation> ProbeAsync(string path, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !Path.GetFileName(path).Equals("java.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请指定现有 java.exe 的完整路径。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process { StartInfo = new(path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-XshowSettings:properties");
        process.StartInfo.ArgumentList.Add("-version");
        foreach (var variable in new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS" })
            process.StartInfo.Environment.Remove(variable);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new IOException("无法查询 Java。");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            var output = stdout.Result + "\n" + stderr.Result;
            var version = VersionPattern().Match(output);
            var architecture = ArchitecturePattern().Match(output);
            if (process.ExitCode != 0 || !version.Success || !architecture.Success)
                throw new IOException("无法识别指定 Java 的版本或架构。");
            return new(Path.GetFullPath(path), version.Groups[1].Value, NormalizeArchitecture(architecture.Groups[1].Value));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Java 版本查询超时。"); }
        finally
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); } }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    /// <summary>统一 API 架构、探测输出和检测页面展示的架构名称。</summary>
    internal static string NormalizeArchitecture(string architecture) => architecture.Trim().ToLowerInvariant() switch
    {
        "amd64" or "x86_64" or "x64" or "x64（64 位）" or "x64 (64-bit)" => "x64",
        "aarch64" or "arm64" or "arm64（64 位）" => "aarch64",
        "x86" or "i386" or "i486" or "i586" or "i686" or "x86（32 位）" or "x86 (32-bit)" => "x86",
        _ => throw new ArgumentException("不支持的 Java 架构：" + architecture)
    };

    /// <summary>兼容 Java 8 的旧式版本号和现代版本号。</summary>
    internal static int MajorVersion(string version)
    {
        var value = version.StartsWith("1.", StringComparison.Ordinal) ? version[2..] : version;
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : 0;
    }

    [GeneratedRegex(@"(?m)^\s*java.version\s*=\s*(\S+)")] private static partial Regex VersionPattern();
    [GeneratedRegex(@"(?m)^\s*os.arch\s*=\s*(\S+)")] private static partial Regex ArchitecturePattern();
}
