using EML.Core.Minecraft.Launching;
using EML.Infrastructure.Minecraft.Launching;

namespace EML.Tests;

/// <summary>验证有限、按来源分离的致命日志判定；普通错误和未知状态不能冒充游戏成功或失败。</summary>
public sealed class LaunchFailureDetectorTests
{
    private const string SdlError = "org.lwjglx.SDLException: Could not initialize SDL., SDL error: Haptic error CoCreateInstance";

    /// <summary>只识别有具体语义的失败签名，同时覆盖双管道和无换行尾部。</summary>
    [Theory]
    [InlineData(SdlError, "SdlInitialization")]
    [InlineData("Caused by: " + SdlError, "SdlInitialization")]
    [InlineData("[12:34:56] [RFB-Main/ERROR] [FML]: " + SdlError, "SdlInitialization")]
    [InlineData("[main/FATAL]: " + SdlError, "SdlInitialization")]
    [InlineData("[12:34:56] [Client thread/INFO] [STDOUT]: [org.lwjglx.Sys:firstTimeInit:83]: " + SdlError, "SdlInitialization")]
    [InlineData("[12:34:56] [Client thread/WARN] [STDERR]: [java.lang.Throwable:printStackTrace:655]: " + SdlError, "SdlInitialization")]
    [InlineData("Error occurred during initialization of VM", "JvmInitialization")]
    [InlineData("Error: Could not create the Java Virtual Machine.", "JvmInitialization")]
    [InlineData("# A fatal error has been detected by the Java Runtime Environment:", "JvmCrash")]
    [InlineData("#@!@# Game crashed! Crash report saved to: #@!@# C:/audit/crash.txt", "MinecraftCrash")]
    [InlineData("Exception in thread \"main\" java.lang.IllegalStateException: failed", "UncaughtGameThreadException")]
    [InlineData("Exception in thread \"Render thread\" java.lang.IllegalStateException: failed", "UncaughtGameThreadException")]
    [InlineData("Exception in thread \"Client thread\" java.lang.IllegalStateException: failed", "UncaughtGameThreadException")]
    public void KnownSignaturesSurviveEachPipeAndEndOfStream(string text, string code)
    {
        foreach (var standardError in new[] { false, true })
        {
            var detector = new LaunchFailureDetector();
            detector.Append(text, standardError);
            detector.Complete(standardError);
            Assert.Equal(code, detector.Failure?.Code);
            Assert.Contains(text, detector.Failure!.Evidence);
            Assert.NotEmpty(detector.Failure.Summary);
        }
    }

    /// <summary>一般日志、聊天、局部异常和成功阶段文字都不足以推断整个游戏失败。</summary>
    [Theory]
    [InlineData("[main/ERROR] Failed to load optional texture, using default")]
    [InlineData("[main/WARN] Missing optional mod integration")]
    [InlineData("java.lang.Exception: optional download failed, retrying")]
    [InlineData("Caused by: java.io.IOException: retrying")]
    [InlineData("org.lwjglx.SDLException: optional controller lookup failed")]
    [InlineData("Exception in thread \"Worker-1\" java.lang.IllegalStateException: optional task")]
    [InlineData("[main/INFO] Example: " + SdlError)]
    [InlineData("[main/INFO] " + SdlError)]
    [InlineData("[main/ERROR] [CHAT] " + SdlError)]
    [InlineData("[main/INFO] [STDOUT] [CHAT] " + SdlError)]
    [InlineData("[main/INFO] [STDOUT]: Example: " + SdlError)]
    [InlineData("[STDOUT]: " + SdlError)]
    [InlineData("Chat says: " + SdlError)]
    [InlineData("---- Minecraft Crash Report ----")]
    [InlineData("[main/ERROR]: ---- Minecraft Crash Report ----")]
    [InlineData("Documentation: ---- Minecraft Crash Report ----")]
    [InlineData("[main/INFO] Setting user: Steve")]
    [InlineData("[main/INFO] Sound engine started")]
    [InlineData("[main/ERROR] ERROR FATAL Exception SDLException")]
    public void NonfatalAndQuotedTextDoesNotSetFailure(string text)
    {
        var detector = new LaunchFailureDetector();
        detector.Append(text + "\n", standardError: true);
        detector.Complete(true);
        Assert.Null(detector.Failure);
    }

    /// <summary>每一个读取分割点都能重组同一管道的错误，不依赖 ReadAsync 恰好返回完整一行。</summary>
    [Theory]
    [InlineData("", "\r")]
    [InlineData("", "\n")]
    [InlineData("", "\r\n")]
    [InlineData("[12:34:56] [Client thread/INFO] [STDOUT]: [org.lwjglx.Sys:firstTimeInit:83]: ", "\n")]
    public void SignatureSurvivesEveryReadBoundary(string prefix, string newline)
    {
        var text = prefix + SdlError;
        for (var split = 0; split <= text.Length; split++)
        {
            var detector = new LaunchFailureDetector();
            detector.Append(text[..split], false);
            detector.Append(text[split..] + newline, false);
            Assert.Equal("SdlInitialization", detector.Failure?.Code);
            Assert.Equal(text, detector.Failure!.Evidence);
        }
    }

    /// <summary>加载画面的硬件信息报告不是崩溃，裸输出与 Forge 重定向输出都不能误报。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("[12:34:56] [Client thread/INFO] [STDOUT]: [cpw.mods.fml.client.SplashProgress:start:235]: ")]
    public void LoadingScreenDebugReportDoesNotSetFailure(string prefix)
    {
        var detector = new LaunchFailureDetector();
        foreach (var line in new[] { "---- Minecraft Crash Report ----", "Description: Loading screen debug info",
            "This is just a prompt for computer specs to be printed. THIS IS NOT A ERROR" })
            detector.Append(prefix + line + "\n", false);
        detector.Complete(false);
        Assert.Null(detector.Failure);
    }

    /// <summary>登录令牌跨任意读取边界时先完成脱敏，诊断证据只保留替代文本。</summary>
    [Fact]
    public void RedactionPrecedesDetectionAtEveryTokenBoundary()
    {
        const string token = "fixture-secret-not-a-real-token";
        var text = SdlError + " " + token;
        for (var split = SdlError.Length; split <= text.Length; split++)
        {
            var redactor = new LaunchOutputRedactor([token]);
            var detector = new LaunchFailureDetector();
            detector.Append(redactor.Append(text[..split]), false);
            detector.Append(redactor.Append(text[split..]), false);
            detector.Append(redactor.Append("", completed: true), false);
            detector.Complete(false);
            Assert.Equal(SdlError + " [登录令牌已隐藏]", detector.Failure?.Evidence);
        }
    }

    /// <summary>stdout/stderr 的相邻片段不能被拼成一个从未实际出现的错误。</summary>
    [Fact]
    public void DifferentPipesNeverCreateSyntheticSignature()
    {
        var detector = new LaunchFailureDetector();
        detector.Append("org.lwjglx.SDLException: ", false);
        detector.Append("Could not initialize SDL.\n", true);
        detector.Complete(false);
        detector.Complete(true);
        Assert.Null(detector.Failure);
    }

    /// <summary>大量后续日志不覆盖首个错误；行首和最终证据均有固定上限。</summary>
    [Fact]
    public void HugeLinesAndOutputFloodKeepBoundedFirstEvidence()
    {
        var detector = new LaunchFailureDetector();
        detector.Append(SdlError + new string('x', 1_000_000) + "\n", false);
        var first = detector.Failure;
        detector.Append(string.Concat(Enumerable.Repeat("ordinary log\n", 100_000)), true);
        detector.Append("---- Minecraft Crash Report ----\n", false);
        Assert.Same(first, detector.Failure);
        Assert.Equal("SdlInitialization", first?.Code);
        Assert.InRange(first!.Evidence.Length, 1, 1060);
        Assert.Contains("完整内容见日志", first.Evidence);
    }

    /// <summary>超长普通行被限制后仍能识别下一行的失败，不能把前一行尾部当作新行。</summary>
    [Fact]
    public void HugeNonfatalLineDoesNotBlockNextErrorOrInventOne()
    {
        var detector = new LaunchFailureDetector();
        detector.Append(new string('x', 100_000) + SdlError + "\n", false);
        Assert.Null(detector.Failure);
        detector.Append(SdlError + "\r", false);
        Assert.Equal("SdlInitialization", detector.Failure?.Code);
    }

    /// <summary>并发观察两条真实输出管道时保持完整证据；下一次运行使用新检测器不继承旧失败。</summary>
    [Fact]
    public async Task ConcurrentPipesKeepEvidenceAndNewRunStartsClean()
    {
        var detector = new LaunchFailureDetector();
        await Task.WhenAll(Task.Run(() => detector.Append(SdlError + "\n", true)),
            Task.Run(() => detector.Append(new string('x', 100_000) + "\n", false)));
        Assert.Equal(SdlError, detector.Failure?.Evidence);
        var next = new LaunchFailureDetector();
        next.Append("ordinary output", false);
        next.Complete(false);
        Assert.Null(next.Failure);
    }

    /// <summary>结果模型不增加“游戏成功”状态；取消优先于诊断，零退出码不能覆盖明确的错误。</summary>
    [Theory]
    [InlineData(true, 0, false, false, MinecraftLaunchOutcome.ProcessExited)]
    [InlineData(true, 0, false, true, MinecraftLaunchOutcome.Failed)]
    [InlineData(true, 7, false, false, MinecraftLaunchOutcome.Failed)]
    [InlineData(true, null, false, false, MinecraftLaunchOutcome.Failed)]
    [InlineData(false, 0, false, false, MinecraftLaunchOutcome.Failed)]
    [InlineData(false, null, true, false, MinecraftLaunchOutcome.Canceled)]
    [InlineData(true, 0, true, true, MinecraftLaunchOutcome.Canceled)]
    [InlineData(true, 143, true, true, MinecraftLaunchOutcome.Canceled)]
    public void OutcomeUsesProcessAndDiagnosticFacts(bool started, int? code, bool canceled,
        bool failure, MinecraftLaunchOutcome expected)
    {
        var result = new MinecraftLaunchResult(started, code, canceled, "fake.log")
        { Failure = failure ? new("SdlInitialization", "SDL 初始化失败", SdlError) : null };
        Assert.Equal(expected, result.Outcome);
    }
}
