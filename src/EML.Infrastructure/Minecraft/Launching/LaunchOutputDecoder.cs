using System.Text;
using EML.Core.Minecraft.Launching;

namespace EML.Infrastructure.Minecraft.Launching;

/// <summary>有界解码进程原始字节；不依赖宿主控制台编码，不修改 JVM 或配置文件编码。</summary>
internal sealed class LaunchOutputDecoder
{
    internal const int MaximumProbeBytes = 64 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly MinecraftLogEncoding _mode;
    private readonly Encoding _systemEncoding;
    private readonly byte[] _pending = new byte[MaximumProbeBytes];
    private int _pendingCount;
    private Encoding? _selectedEncoding;
    private Decoder? _decoder;
    private bool _atStart = true;
    private bool _completed;

    /// <summary>调用方注入本机代码页，测试可固定为 GBK 等编码；每条输出管道各用一个实例。</summary>
    internal LaunchOutputDecoder(MinecraftLogEncoding mode, Encoding systemEncoding)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        ArgumentNullException.ThrowIfNull(systemEncoding);
        _mode = mode;
        _systemEncoding = (Encoding)systemEncoding.Clone();
        _systemEncoding.DecoderFallback = DecoderFallback.ReplacementFallback;
        if (mode != MinecraftLogEncoding.Auto)
        {
            _selectedEncoding = mode == MinecraftLogEncoding.Utf8 ? Utf8 : _systemEncoding;
            _decoder = _selectedEncoding.GetDecoder();
        }
    }

    /// <summary>接收连续字节；显式编码跨读取块保持状态，自动模式先累计完整行再作有限的编码选择。</summary>
    internal string Append(ReadOnlySpan<byte> bytes, bool completed = false)
    {
        if (_completed) throw new InvalidOperationException("输出解码已经结束。");
        _completed = completed;
        if (_mode != MinecraftLogEncoding.Auto) return RemoveInitialBom(Decode(bytes, completed));

        var output = new StringBuilder();
        while (!bytes.IsEmpty)
        {
            var length = Math.Min(bytes.Length, _pending.Length - _pendingCount);
            var newline = bytes[..length].IndexOf((byte)'\n');
            if (newline >= 0) length = newline + 1;
            bytes[..length].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += length;
            bytes = bytes[length..];
            if (newline >= 0 || _pendingCount == _pending.Length)
                FlushPending(output, endOfLine: newline >= 0);
        }
        if (completed) FlushPending(output, endOfLine: true);
        return output.ToString();
    }

    /// <summary>普通行分别识别；超长行最多探测 64 KiB，之后固定该行编码并用 Decoder 保留跨段字符。</summary>
    private void FlushPending(StringBuilder output, bool endOfLine)
    {
        if (_pendingCount == 0 && _decoder is null) return;
        var bytes = _pending.AsSpan(0, _pendingCount);
        if (_decoder is null)
        {
            if (!endOfLine)
            {
                var asciiCount = 0;
                while (asciiCount < bytes.Length && bytes[asciiCount] < 0x80) asciiCount++;
                // 纯 ASCII 不能证明编码。若非 ASCII 恰在片段末尾，保留少量字节等下一块，
                // 避免只有一个 GBK 首字节时把它误当成尚未写完的 UTF-8 字符并锁死整行。
                if (asciiCount > 0 && bytes.Length - asciiCount <= 3)
                {
                    output.Append(RemoveInitialBom(Encoding.ASCII.GetString(bytes[..asciiCount])));
                    bytes[asciiCount..].CopyTo(_pending);
                    _pendingCount -= asciiCount;
                    return;
                }
            }
            // 仅验证前缀时不冲洗：块尾不完整的 UTF-8 字符不能被误判为系统编码。
            try
            {
                _ = StrictUtf8.GetDecoder().GetCharCount(bytes, flush: endOfLine);
                _selectedEncoding = Utf8;
            }
            catch (DecoderFallbackException) { _selectedEncoding = _systemEncoding; }
            _decoder = _selectedEncoding.GetDecoder();
        }
        output.Append(RemoveInitialBom(Decode(bytes, endOfLine)));
        _pendingCount = 0;
        if (endOfLine) { _decoder = null; _selectedEncoding = null; }
    }

    /// <summary>同一 Decoder 处理相邻字节片段，避免多字节中文或代理对被读取边界截断。</summary>
    private string Decode(ReadOnlySpan<byte> bytes, bool flush)
    {
        var characters = new char[_selectedEncoding!.GetMaxCharCount(bytes.Length)];
        var count = _decoder!.GetChars(bytes, characters, flush);
        return new string(characters, 0, count);
    }

    /// <summary>只去掉流开头已解码的 BOM；后续正文中的相同字符保持不变。</summary>
    private string RemoveInitialBom(string text)
    {
        if (!_atStart || text.Length == 0) return text;
        _atStart = false;
        return text[0] == '\uFEFF' ? text[1..] : text;
    }
}
