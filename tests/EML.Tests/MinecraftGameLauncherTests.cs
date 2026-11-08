using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using EML.Core.Minecraft.Launching;
using EML.Infrastructure.Minecraft.Launching;

namespace EML.Tests;

/// <summary>用受控系统子进程验证输出、退出和取消；不启动 Minecraft 或执行整合包脚本。</summary>
public sealed class MinecraftGameLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-launch-process-" + Guid.NewGuid().ToString("N"));

    /// <summary>同时读取大量 stdout/stderr，保留退出码及日志，不被管道容量限制卡住。</summary>
    [Fact]
    public async Task ProcessDrainsBothStreamsAndPreservesExitCode()
    {
        var plan = PowerShellPlan("for ($i=0; $i -lt 1200; $i++) { [Console]::Out.WriteLine('out-' + $i); [Console]::Error.WriteLine('err-' + $i) }; exit 7");
        var seen = new ConcurrentQueue<MinecraftLaunchProgress>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var result = await new MinecraftGameLauncher(_root).RunPreparedAsync(plan,
            new InlineProgress(seen.Enqueue), timeout.Token);

        Assert.True(result.Started);
        Assert.False(result.IsCanceled);
        Assert.Equal(7, result.ExitCode);
        Assert.NotNull(result.LogPath);
        var log = await File.ReadAllTextAsync(result.LogPath!);
        Assert.Contains("[stdout] out-1199", log);
        Assert.Contains("[stderr] err-1199", log);
        Assert.Contains(seen, value => value.Stage == MinecraftLaunchStage.Running);
        Assert.Contains(seen, value => value.StandardError && value.Message == "err-1199");
        foreach (var name in new[] { "java", "jna", "lwjgl", "netty" })
            Assert.True(Directory.Exists(Path.Combine(plan.NativesDirectory, name)));
    }

    /// <summary>无换行的大输出分成有限片段，进度层不会收到无限增长的单条字符串。</summary>
    [Fact]
    public async Task LongLineIsConsumedInBoundedFragments()
    {
        var plan = PowerShellPlan("[Console]::Out.Write(('x' * 100000)); exit 0");
        var seen = new ConcurrentQueue<MinecraftLaunchProgress>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var result = await new MinecraftGameLauncher(_root).RunPreparedAsync(plan,
            new InlineProgress(seen.Enqueue), timeout.Token);

        Assert.Equal(0, result.ExitCode);
        var fragments = seen.Where(value => value.Stage == MinecraftLaunchStage.Output).ToArray();
        Assert.All(fragments, value => Assert.InRange(value.Message.Length, 1, 8193));
        Assert.Equal(100000, fragments.Sum(value => value.Message.Length));
    }

    /// <summary>取消仅停止本次创建的受控进程，返回实际退出状态并保留已写日志。</summary>
    [Fact]
    public async Task CancellationStopsOwnedProcessAndKeepsLog()
    {
        var plan = PowerShellPlan("[Console]::Out.WriteLine($PID); Start-Sleep -Seconds 120");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var pid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress(value =>
        {
            if (value.Stage == MinecraftLaunchStage.Output && int.TryParse(value.Message, out var observed))
            {
                pid.TrySetResult(observed);
                cancellation.Cancel();
            }
        });

        var result = await new MinecraftGameLauncher(_root).RunPreparedAsync(plan, progress, cancellation.Token);

        Assert.True(result.Started);
        Assert.True(result.IsCanceled);
        Assert.NotNull(result.ExitCode);
        Assert.True(File.Exists(result.LogPath));
        Assert.True(pid.Task.IsCompletedSuccessfully);
        Assert.False(IsRunning(await pid.Task));
    }

    /// <summary>界面回调异常不会停止输出消费、丢失退出码或遗留子进程。</summary>
    [Fact]
    public async Task ProgressFailureDoesNotAbandonProcess()
    {
        var plan = PowerShellPlan("[Console]::Out.WriteLine('finished'); exit 3");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var result = await new MinecraftGameLauncher(_root).RunPreparedAsync(plan,
            new InlineProgress(_ => throw new InvalidOperationException("display failure")), timeout.Token);

        Assert.True(result.Started);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("finished", await File.ReadAllTextAsync(result.LogPath!));
    }

    /// <summary>Java 9+ 创建自己的参数文件，启动失败也只清理本次文件，保留日志与已有同类文件。</summary>
    [Fact]
    public async Task FailedStartRemovesOnlyItsOwnArgumentFile()
    {
        var plan = PowerShellPlan("exit 0") with { JavaMajorVersion = 25, JavaPath = Path.Combine(_root, "missing-java.exe") };
        var launchDirectory = Path.Combine(plan.InstancePath, ".eml-runtime", "launch");
        Directory.CreateDirectory(launchDirectory);
        var existing = Path.Combine(launchDirectory, "existing.args");
        await File.WriteAllTextAsync(existing, "user-owned data");

        await Assert.ThrowsAsync<Win32Exception>(() => new MinecraftGameLauncher(_root).RunPreparedAsync(plan));

        Assert.Equal(new[] { existing }, Directory.EnumerateFiles(launchDirectory, "*.args").ToArray());
        Assert.Equal("user-owned data", await File.ReadAllTextAsync(existing));
    }

    /// <summary>日志介质失败后仍消费剩余管道消息，只发一次写入警告，不让游戏阻塞在输出。</summary>
    [Fact]
    public async Task LogFailureStillConsumesRemainingOutput()
    {
        var channel = Channel.CreateBounded<MinecraftLaunchProgress>(1);
        var seen = new ConcurrentQueue<MinecraftLaunchProgress>();
        var pump = MinecraftGameLauncher.PumpOutputAsync(channel.Reader, new FailingWriter(),
            new InlineProgress(seen.Enqueue), "test.log");
        for (var index = 0; index < 10; index++)
            await channel.Writer.WriteAsync(new(MinecraftLaunchStage.Output, "message-" + index));
        channel.Writer.Complete();

        await pump.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(seen, value => value.Message.Contains("日志写入失败"));
        Assert.Contains(seen, value => value.Message == "message-9");
    }

    /// <summary>已有取消令牌不会创建 natives、日志、参数文件或子进程。</summary>
    [Fact]
    public async Task PreCanceledRunDoesNotCreateLaunchFiles()
    {
        var plan = PowerShellPlan("exit 0");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MinecraftGameLauncher(_root)
            .RunPreparedAsync(plan, token: cancellation.Token));

        Assert.False(Directory.Exists(Path.Combine(plan.InstancePath, ".eml-runtime")));
        Assert.False(Directory.Exists(plan.NativesDirectory));
    }

    /// <summary>游戏目录不能逃出实例，受控子进程也不能绕过执行层目录边界。</summary>
    [Fact]
    public async Task OutsideWorkingDirectoryIsRejectedBeforeStarting()
    {
        var plan = PowerShellPlan("exit 0") with { GameDirectory = _root };

        await Assert.ThrowsAsync<InvalidDataException>(() => new MinecraftGameLauncher(_root).RunPreparedAsync(plan));

        Assert.False(Directory.Exists(Path.Combine(plan.InstancePath, ".eml-runtime")));
    }

    /// <summary>参数文件保留空串、空格、引号、反斜杠及控制字符，不写 BOM 或执行命令语法。</summary>
    [Fact]
    public void ArgumentFileQuotesAllArgumentsAndEscapesNativeSyntax()
    {
        var arguments = new[] { "", "path with spaces", "a\\b\"c", "a\nb\rc\td\fe", "#comment & | > $()", "中文" };

        var bytes = MinecraftGameLauncher.EncodeArgumentFile(arguments, new UTF8Encoding(false, true));

        Assert.Equal("\"\"\n\"path with spaces\"\n\"a\\\\b\\\"c\"\n\"a\\nb\\rc\\td\\fe\"\n\"#comment & | > $()\"\n\"中文\"\n",
            Encoding.UTF8.GetString(bytes));
        Assert.NotEqual((byte)0xef, bytes[0]);
    }

    /// <summary>Shift-JIS 的多字节字符也可能含反斜杠字节，必须逐字节转义才能保持原文。</summary>
    [Fact]
    public void ArgumentFileEscapesBackslashInsideMultibyteEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        var bytes = MinecraftGameLauncher.EncodeArgumentFile(["ソ"], encoding);

        Assert.Equal(new byte[] { 0x22, 0x83, 0x5c, 0x5c, 0x22, 0x0a }, bytes);
    }

    /// <summary>系统编码无法表示的字符必须明确失败，不能替换成问号后误启动。</summary>
    [Fact]
    public void ArgumentFileRejectsUnrepresentableCharactersAndNull()
    {
        var ascii = Encoding.GetEncoding(20127, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        Assert.Throws<EncoderFallbackException>(() => MinecraftGameLauncher.EncodeArgumentFile(["中文"], ascii));
        Assert.Throws<InvalidDataException>(() => MinecraftGameLauncher.EncodeArgumentFile(["a\0b"], Encoding.UTF8));
    }

    /// <summary>Java 8 超长参数给出限制说明，绝不截断 classpath 或静默丢参数。</summary>
    [Fact]
    public void Java8OverlongCommandIsRejectedExplicitly()
    {
        var plan = PowerShellPlan("exit 0") with { Arguments = [new string('a', 32768)] };

        if (OperatingSystem.IsWindows())
            Assert.Contains("Java 8", Assert.Throws<InvalidOperationException>(() => MinecraftGameLauncher.ValidateJava8CommandLength(plan)).Message);
    }

    /// <summary>执行配置不使用 shell，并明确重定向两条日志管道及游戏工作目录。</summary>
    [Fact]
    public void ProcessConfigurationDoesNotUseShell()
    {
        var plan = PowerShellPlan("exit 0");

        var info = MinecraftGameLauncher.CreateStartInfo(plan);

        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Equal(plan.GameDirectory, info.WorkingDirectory);
        Assert.Equal(plan.JavaPath, info.FileName);
    }

    /// <summary>游戏输出同时包含普通日志和令牌时，磁盘与界面都只能收到脱敏文本。</summary>
    [Fact]
    public async Task SensitiveGameOutputIsRedactedFromBothPipesAndLog()
    {
        var secret = "test-access-secret";
        var plan = PowerShellPlan("[Console]::Out.Write(('x' * 4090) + 'test-access-secret'); [Console]::Error.WriteLine('token:test-access-secret:uuid'); exit 0")
            with { SensitiveValues = [secret] };
        var seen = new ConcurrentQueue<MinecraftLaunchProgress>();
        var result = await new MinecraftGameLauncher(_root).RunPreparedAsync(plan, new InlineProgress(seen.Enqueue));
        Assert.Equal(0, result.ExitCode);
        var log = await File.ReadAllTextAsync(result.LogPath!);
        Assert.DoesNotContain(secret, log);
        Assert.Contains("[登录令牌已隐藏]", log);
        Assert.All(seen, value => Assert.DoesNotContain(secret, value.Message));
    }

    /// <summary>所有可能的读取分割点都必须能识别完整令牌，不能泄露跨块片段。</summary>
    [Fact]
    public void OutputRedactorHandlesEveryTokenBoundary()
    {
        const string secret = "fake-access-token";
        const string input = "prefix fake-access-token middle fake-access-token suffix";
        for (var boundary = 0; boundary <= input.Length; boundary++)
        {
            var redactor = new LaunchOutputRedactor([secret]);
            var result = redactor.Append(input[..boundary]) + redactor.Append(input[boundary..]) + redactor.Append("", true);
            Assert.Equal("prefix [登录令牌已隐藏] middle [登录令牌已隐藏] suffix", result);
        }
    }

    /// <summary>现代参数文件不包含令牌或组合 session，参数拼接后的原顺序保持一致。</summary>
    [Theory]
    [InlineData("access-secret")]
    [InlineData("token:access-secret:uuid")]
    public void ArgumentFileNeverReceivesAccessToken(string sensitiveArgument)
    {
        var plan = PowerShellPlan("unused") with { JavaMajorVersion = 25,
            Arguments = ["-Xmx8192M", "-cp", "long-classpath", "Main", "--accessToken", sensitiveArgument, "--gameDir", "."],
            SensitiveValues = ["access-secret"] };
        var split = MinecraftGameLauncher.ArgumentFilePrefixLength(plan);
        var prefix = plan.Arguments.Take(split).ToArray();
        var bytes = MinecraftGameLauncher.EncodeArgumentFile(prefix, Encoding.UTF8);
        Assert.DoesNotContain("access-secret", Encoding.UTF8.GetString(bytes));
        Assert.Equal(plan.Arguments, prefix.Concat(plan.Arguments.Skip(split)));
        Assert.Equal(5, split);
    }

    /// <summary>构建测试专用系统进程计划；脚本文本在测试源码固定，不读取包内脚本。</summary>
    private MinecraftLaunchPlan PowerShellPlan(string script)
    {
        var instance = Path.Combine(_root, "instances", "process-test");
        var game = Path.Combine(instance, ".minecraft");
        Directory.CreateDirectory(game);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return new(instance, game, powershell, 8, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script])
        {
            NativesDirectory = Path.Combine(game, "natives")
        };
    }

    /// <summary>检查本次测试记录的进程是否已经退出，进程回收也视为退出。</summary>
    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>仅清理本测试创建的随机根目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>同步转发测试观察，便于精确触发进程取消及显示失败。</summary>
    private sealed class InlineProgress(Action<MinecraftLaunchProgress> action) : IProgress<MinecraftLaunchProgress>
    {
        public void Report(MinecraftLaunchProgress value) => action(value);
    }

    /// <summary>模拟磁盘写入故障，验证失败不会终止日志消费者。</summary>
    private sealed class FailingWriter : StringWriter
    {
        public override Task WriteLineAsync(string? value) => Task.FromException(new IOException("disk unavailable"));
    }
}
