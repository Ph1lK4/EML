using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using EML.Core.Minecraft.Launching;
using EML.Infrastructure.Minecraft.Launching;

namespace EML.Tests;

/// <summary>固定原始字节经真实进程管道进入生产解码、脱敏、诊断和日志；不运行游戏或访问用户数据。</summary>
public sealed class LaunchOutputEncodingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-output-encoding-" + Guid.NewGuid().ToString("N"));
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private const string FatalPrefix = "org.lwjglx.SDLException: Could not initialize SDL. ";

    /// <summary>两条管道各自解码，UTF-8 中文及旧 Java 的系统编码不能互相污染。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoReadsUtf8AndNativeEncodingOnIndependentPipes(bool utf8OnError)
    {
        const string unicode = "新版日志：中文路径与标点，。😀";
        var native = NativeEncoding();
        // 非中文系统可能不能表示中文；验证真实可表示的字节，不假设每台机器都是 CP936。
        var nativeBytes = native.GetBytes("旧版日志：中文路径\n");
        var nativeText = native.GetString(nativeBytes).TrimEnd('\n');
        var script = RawWrite(Encoding.UTF8.GetBytes(unicode + "\n"), utf8OnError) +
            RawWrite(nativeBytes, !utf8OnError) + "exit 0";

        var capture = await RunAsync(CreatePlan(script));

        Assert.Equal(MinecraftLaunchOutcome.ProcessExited, capture.Result.Outcome);
        Assert.Null(capture.Result.Failure);
        Assert.Equal(unicode, Assert.Single(capture.Output, item => item.StandardError == utf8OnError).Message);
        Assert.Equal(nativeText, Assert.Single(capture.Output, item => item.StandardError != utf8OnError).Message);
        Assert.Contains(Prefix(utf8OnError) + unicode, capture.DiskLog);
        Assert.Contains(Prefix(!utf8OnError) + nativeText, capture.DiskLog);
    }

    /// <summary>同一输出管道按行交替使用不同编码，自动模式不因第一行编码锁死后续普通行。</summary>
    [Fact]
    public async Task AutoHandlesUtf8AndNativeLinesInTheSamePipe()
    {
        const string first = "第一行：UTF-8 中文";
        const string last = "最后一行：无换行😀";
        var native = NativeEncoding();
        var middleBytes = native.GetBytes("系统编码：旧模组日志\n");
        var middle = native.GetString(middleBytes).TrimEnd('\n');
        var payload = Encoding.UTF8.GetBytes(first + "\n").Concat(middleBytes)
            .Concat(Encoding.UTF8.GetBytes(last)).ToArray();

        var capture = await RunAsync(CreatePlan(RawWrite(payload, false) + "exit 0"));

        Assert.Equal(new[] { first, middle, last }, capture.Output.Select(item => item.Message).ToArray());
        Assert.Contains(Prefix(false) + first, capture.DiskLog);
        Assert.Contains(Prefix(false) + middle, capture.DiskLog);
        Assert.Contains(Prefix(false) + last, capture.DiskLog);
    }

    /// <summary>显式 UTF-8 逐字节写入且末尾没有换行，完整中文、标点及代理对仍保留。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitUtf8PreservesByteBoundariesAndTail(bool standardError)
    {
        const string expected = "中文工作区：测试路径，。😀";
        var plan = CreatePlan(RawWrite(Encoding.UTF8.GetBytes(expected), standardError, oneByte: true) + "exit 0")
            with { LogEncoding = MinecraftLogEncoding.Utf8 };

        var capture = await RunAsync(plan);

        var message = Assert.Single(capture.Output);
        Assert.Equal(standardError, message.StandardError);
        Assert.Equal(expected, message.Message);
        Assert.Contains(Prefix(standardError) + expected, capture.DiskLog);
        Assert.DoesNotContain('\uFFFD', capture.DiskLog);
    }

    /// <summary>手动选择系统编码时不再把恰好合法的 UTF-8 字节优先识别为 UTF-8。</summary>
    [Fact]
    public async Task ExplicitNativeEncodingWinsForAmbiguousUtf8Bytes()
    {
        byte[] ambiguous = [0xc2, 0xa3];
        var native = NativeEncoding();
        var expected = native.GetString(ambiguous);
        var payload = ambiguous.Concat(new byte[] { (byte)'\n' }).ToArray();
        var plan = CreatePlan(RawWrite(payload, false) + "exit 0")
            with { LogEncoding = MinecraftLogEncoding.SystemDefault };

        var capture = await RunAsync(plan);

        Assert.Equal(expected, Assert.Single(capture.Output).Message);
        Assert.Contains(Prefix(false) + expected, capture.DiskLog);
        // 系统本身为 UTF-8 时两者相同；其他代码页下这个断言明确区分两种模式。
        if (native.CodePage != Encoding.UTF8.CodePage)
            Assert.NotEqual(Encoding.UTF8.GetString(ambiguous), expected);
    }

    /// <summary>显式系统模式也使用增量解码，不能把旧 Java 多字节中文拆成替代字符。</summary>
    [Fact]
    public async Task ExplicitNativeEncodingPreservesByteBoundaries()
    {
        var native = NativeEncoding();
        var payload = native.GetBytes("系统编码：旧版 Java 的中文尾行");
        var expected = native.GetString(payload);
        var plan = CreatePlan(RawWrite(payload, true, oneByte: true) + "exit 0")
            with { LogEncoding = MinecraftLogEncoding.SystemDefault };

        var capture = await RunAsync(plan);

        Assert.Equal(expected, Assert.Single(capture.Output).Message);
        Assert.Contains(Prefix(true) + expected, capture.DiskLog);
    }

    /// <summary>超过自动识别前缀上限的中文长行仍有界分段，边界上的四字节字符不会损坏。</summary>
    [Fact]
    public async Task AutoKeepsUnicodeAcrossVeryLongLineFragments()
    {
        const string unit = "中文😀";
        const int repeats = 10000;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(unit));
        var script = $"$payload = [Convert]::FromBase64String('{encoded}'); " +
            "$stream = [Console]::OpenStandardOutput(); " +
            $"for ($index = 0; $index -lt {repeats}; $index++) {{ $stream.Write($payload, 0, $payload.Length) }}; " +
            "$stream.Flush(); exit 0";

        var capture = await RunAsync(CreatePlan(script));

        var expected = string.Concat(Enumerable.Repeat(unit, repeats));
        Assert.Equal(expected, string.Concat(capture.Output.Select(item => item.Message)));
        Assert.True(capture.Output.Length > 1);
        Assert.All(capture.Output, item => Assert.InRange(item.Message.Length, 1, 8193));
        var savedParts = capture.DiskLog.Split('\n').Where(line => line.StartsWith(Prefix(false), StringComparison.Ordinal))
            .Select(line => line[Prefix(false).Length..].TrimEnd('\r'));
        Assert.Equal(expected, string.Concat(savedParts));
        Assert.DoesNotContain('\uFFFD', capture.DiskLog);
    }

    /// <summary>正确解码仍先于脱敏和致命诊断，分块的假令牌不能进入进度、错误证据或磁盘日志。</summary>
    [Theory]
    [InlineData(MinecraftLogEncoding.Auto)]
    [InlineData(MinecraftLogEncoding.Utf8)]
    public async Task ChineseFatalEvidenceAndSplitSecretsStayCorrect(MinecraftLogEncoding mode)
    {
        const string secret = "fixture-access-token-never-a-real-credential";
        const string reason = "初始化失败：中文路径，设备不可用。";
        var payload = Encoding.UTF8.GetBytes(FatalPrefix + reason + " " + secret);
        var plan = CreatePlan(RawWrite(payload, true, oneByte: true) + "exit 0")
            with { LogEncoding = mode, SensitiveValues = [secret] };

        var capture = await RunAsync(plan);

        Assert.Equal(0, capture.Result.ExitCode);
        Assert.Equal(MinecraftLaunchOutcome.Failed, capture.Result.Outcome);
        var failure = Assert.IsType<MinecraftLaunchFailure>(capture.Result.Failure);
        Assert.Contains(reason, failure.Evidence);
        Assert.Contains("[登录令牌已隐藏]", failure.Evidence);
        Assert.DoesNotContain(secret, failure.Evidence);
        Assert.All(capture.Output, item => Assert.DoesNotContain(secret, item.Message));
        Assert.Contains(reason, capture.DiskLog);
        Assert.Contains("[登录令牌已隐藏]", capture.DiskLog);
        Assert.DoesNotContain(secret, capture.DiskLog);
    }

    /// <summary>捕获生产解码后的进度，并以严格 UTF-8 验证真实落盘文件，不接受损坏字节被静默替换。</summary>
    private async Task<Capture> RunAsync(MinecraftLaunchPlan plan)
    {
        var progress = new ConcurrentQueue<MinecraftLaunchProgress>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var result = await new MinecraftGameLauncher(_root).RunPreparedAsync(plan,
            new InlineProgress(progress.Enqueue), timeout.Token);
        Assert.True(result.Started);
        Assert.False(result.IsCanceled);
        Assert.NotNull(result.LogPath);
        var bytes = await File.ReadAllBytesAsync(result.LogPath);
        var log = StrictUtf8.GetString(bytes);
        return new(result, progress.Where(item => item.Stage == MinecraftLaunchStage.Output).ToArray(), log);
    }

    /// <summary>脚本仅用 Base64 还原固定字节，排除 PowerShell 源码或控制台编码影响实验结果。</summary>
    private static string RawWrite(byte[] bytes, bool standardError, bool oneByte = false)
    {
        var stream = standardError ? "Error" : "Output";
        var script = $"$payload = [Convert]::FromBase64String('{Convert.ToBase64String(bytes)}'); " +
            $"$stream = [Console]::OpenStandard{stream}(); ";
        if (oneByte)
            return script + "foreach ($value in $payload) { $stream.WriteByte($value); $stream.Flush(); Start-Sleep -Milliseconds 2 }; ";
        return script + "$stream.Write($payload, 0, $payload.Length); $stream.Flush(); ";
    }

    /// <summary>执行系统 PowerShell 固定测试脚本，仅使用当前测试独占的临时实例目录。</summary>
    private MinecraftLaunchPlan CreatePlan(string script)
    {
        var instance = Path.Combine(_root, "instances", "fixture");
        var game = Path.Combine(instance, ".minecraft");
        Directory.CreateDirectory(game);
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return new(instance, game, executable, 8, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script])
        { NativesDirectory = Path.Combine(game, "natives") };
    }

    /// <summary>测试使用 Windows ANSI 代码页，与生产系统编码策略一致，不修改控制台或系统配置。</summary>
    private static Encoding NativeEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(OperatingSystem.IsWindows() ? (int)GetACP() : Encoding.UTF8.CodePage);
    }

    /// <summary>拼接生产日志消费者使用的管道标识。</summary>
    private static string Prefix(bool standardError) => standardError ? "[stderr] " : "[stdout] ";

    /// <summary>仅清理由本测试创建的随机临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    private sealed record Capture(MinecraftLaunchResult Result, MinecraftLaunchProgress[] Output, string DiskLog);

    /// <summary>同步交付进度以便断言所有进程输出，不依赖界面 Dispatcher 的调度顺序。</summary>
    private sealed class InlineProgress(Action<MinecraftLaunchProgress> report) : IProgress<MinecraftLaunchProgress>
    {
        public void Report(MinecraftLaunchProgress value) => report(value);
    }
}
