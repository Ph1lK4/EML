using System.Text;
using EML.Core.Minecraft.Launching;
using EML.Infrastructure.Minecraft.Launching;

namespace EML.Tests;

/// <summary>直接喂入固定字节，验证编码识别、跨块状态及有界长行，不依赖本机控制台或 Java 安装。</summary>
public sealed class LaunchOutputDecoderTests
{
    private static Encoding Legacy
    {
        get { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(936); }
    }

    /// <summary>每一种读取分割都保留中文和标点；UTF-8 额外覆盖四字节字符。</summary>
    [Theory]
    [InlineData(MinecraftLogEncoding.Auto, 65001)]
    [InlineData(MinecraftLogEncoding.Auto, 936)]
    [InlineData(MinecraftLogEncoding.Utf8, 65001)]
    [InlineData(MinecraftLogEncoding.SystemDefault, 936)]
    public void EveryByteBoundaryPreservesCompleteCharacters(MinecraftLogEncoding mode, int codePage)
    {
        _ = Legacy;
        var text = "中文日志，。路径：C:\\游戏\\实例\r\n尾行" + (codePage == 65001 ? "😀" : "");
        var bytes = Encoding.GetEncoding(codePage).GetBytes(text);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var decoder = new LaunchOutputDecoder(mode, Legacy);
            var actual = decoder.Append(bytes.AsSpan(0, split)) + decoder.Append(bytes.AsSpan(split)) + decoder.Append([], true);
            Assert.Equal(text, actual);
        }
    }

    /// <summary>UTF-8 与 GBK 逐行交替时重新识别，不能由第一行永久决定整条管道。</summary>
    [Fact]
    public void AutoCanReadDifferentEncodingsOnDifferentLines()
    {
        const string first = "UTF-8 中文😀\n";
        const string second = "GBK 中文\r\n";
        var bytes = Encoding.UTF8.GetBytes(first).Concat(Legacy.GetBytes(second)).Concat(Encoding.UTF8.GetBytes(first)).ToArray();
        var decoder = new LaunchOutputDecoder(MinecraftLogEncoding.Auto, Legacy);
        var actual = string.Concat(bytes.Select(value => decoder.Append([value]))) + decoder.Append([], true);
        Assert.Equal(first + second + first, actual);
    }

    /// <summary>同时合法的字节无法自动判定来源，显式系统模式必须优先遵从用户选择。</summary>
    [Fact]
    public void AmbiguousBytesHaveDocumentedAutoPreferenceAndManualOverride()
    {
        byte[] bytes = [0xc2, 0xa3];
        Assert.NotEqual(Encoding.UTF8.GetString(bytes), Legacy.GetString(bytes));
        Assert.Equal(Encoding.UTF8.GetString(bytes), new LaunchOutputDecoder(MinecraftLogEncoding.Auto, Legacy).Append(bytes, true));
        Assert.Equal(Legacy.GetString(bytes), new LaunchOutputDecoder(MinecraftLogEncoding.SystemDefault, Legacy).Append(bytes, true));
    }

    /// <summary>完整或跨读取块的 UTF-8 BOM 仅在流开头移除，正文中的 BOM 不丢失。</summary>
    [Theory]
    [InlineData(MinecraftLogEncoding.Auto)]
    [InlineData(MinecraftLogEncoding.Utf8)]
    public void InitialBomAndEmptyOutputAreHandled(MinecraftLogEncoding mode)
    {
        var decoder = new LaunchOutputDecoder(mode, Legacy);
        var bytes = Encoding.UTF8.GetBytes("\uFEFF中文\n\uFEFF正文");
        var actual = string.Concat(bytes.Select(value => decoder.Append([value]))) + decoder.Append([], true);
        Assert.Equal("中文\n\uFEFF正文", actual);
        Assert.Equal("", new LaunchOutputDecoder(mode, Legacy).Append([], true));
    }

    /// <summary>长行已确认编码后持续增量解码，多字节字符跨 64 KiB 或读取边界均保持完整。</summary>
    [Theory]
    [InlineData(65001)]
    [InlineData(936)]
    public void LargeNonAsciiLinesUseBoundedIncrementalDecoding(int codePage)
    {
        _ = Legacy;
        var encoding = Encoding.GetEncoding(codePage);
        var text = string.Concat(Enumerable.Repeat(codePage == 65001 ? "中文😀" : "中文。", 40000)) + "\n下一行";
        var bytes = encoding.GetBytes(text);
        var decoder = new LaunchOutputDecoder(MinecraftLogEncoding.Auto, Legacy);
        var actual = new StringBuilder();
        for (var offset = 0; offset < bytes.Length; offset += 4093)
            actual.Append(decoder.Append(bytes.AsSpan(offset, Math.Min(4093, bytes.Length - offset))));
        actual.Append(decoder.Append([], true));
        Assert.Equal(text, actual.ToString());
    }

    /// <summary>长 ASCII 前缀不能锁定 UTF-8，也不能把位于探测边界的 GBK 首字节误判。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AsciiPrefixDefersEncodingUntilThereIsEvidence(int tailBytes)
    {
        var text = new string('a', LaunchOutputDecoder.MaximumProbeBytes - tailBytes) + "中文日志\n";
        var bytes = Legacy.GetBytes(text);
        var decoder = new LaunchOutputDecoder(MinecraftLogEncoding.Auto, Legacy);
        var first = decoder.Append(bytes.AsSpan(0, LaunchOutputDecoder.MaximumProbeBytes));
        Assert.NotEmpty(first);
        var rest = decoder.Append(bytes.AsSpan(LaunchOutputDecoder.MaximumProbeBytes), true);
        Assert.Equal(text, first + rest);
    }

    /// <summary>无换行 ASCII 洪水及时释放有限片段，不累计整个进程输出。</summary>
    [Fact]
    public void AsciiFloodProducesBoundedProgressBeforeEndOfStream()
    {
        var decoder = new LaunchOutputDecoder(MinecraftLogEncoding.Auto, Legacy);
        var input = Encoding.ASCII.GetBytes(new string('x', 4096));
        var emitted = 0;
        for (var index = 0; index < 256; index++)
        {
            var chunk = decoder.Append(input);
            Assert.InRange(chunk.Length, 0, LaunchOutputDecoder.MaximumProbeBytes);
            emitted += chunk.Length;
        }
        Assert.Equal(1_048_576, emitted);
        Assert.Empty(decoder.Append([], true));
    }

    /// <summary>显式 UTF-8 的损坏尾字节只生成替代字符，不抛异常阻塞进程退出。</summary>
    [Fact]
    public void IncompleteExplicitUtf8TailIsFlushedWithoutThrowing()
    {
        var decoder = new LaunchOutputDecoder(MinecraftLogEncoding.Utf8, Legacy);
        Assert.Equal("", decoder.Append([0xe4, 0xb8]));
        Assert.Equal("\uFFFD", decoder.Append([], true));
        Assert.Throws<InvalidOperationException>(() => decoder.Append([]));
    }

    /// <summary>两个输出源独立维护未完成字符，不允许跨管道拼出不存在的文本。</summary>
    [Fact]
    public void PipeDecodersNeverSharePendingBytes()
    {
        var stdout = new LaunchOutputDecoder(MinecraftLogEncoding.Utf8, Legacy);
        var stderr = new LaunchOutputDecoder(MinecraftLogEncoding.Utf8, Legacy);
        Assert.Empty(stdout.Append([0xe4]));
        Assert.Equal("独立", stderr.Append(Encoding.UTF8.GetBytes("独立"), true));
        Assert.Equal("中", stdout.Append([0xb8, 0xad], true));
    }
}
