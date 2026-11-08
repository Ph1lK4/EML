using System.Text;

namespace EML.Infrastructure.Minecraft.Launching;

/// <summary>在日志分行之前跨块脱敏，令牌跨越读取缓冲边界时也不泄露片段。</summary>
internal sealed class LaunchOutputRedactor
{
    private readonly string[] _secrets;
    private readonly int _lookAhead;
    private string _pending = "";

    /// <summary>过滤空值并优先匹配长令牌；每条输出管道各用一个实例。</summary>
    internal LaunchOutputRedactor(IReadOnlyList<string> secrets)
    {
        _secrets = secrets.Where(value => !string.IsNullOrEmpty(value)).Distinct().OrderByDescending(value => value.Length).ToArray();
        _lookAhead = _secrets.Length == 0 ? 0 : _secrets[0].Length;
    }

    /// <summary>保留不足以判断的末尾；流结束时一次冲洗，内存上限为读取块和最大令牌长度。</summary>
    internal string Append(string text, bool completed = false)
    {
        if (_secrets.Length == 0) return text;
        _pending += text;
        var safeEnd = completed ? _pending.Length : Math.Max(0, _pending.Length - _lookAhead + 1);
        var result = new StringBuilder();
        var index = 0;
        while (index < safeEnd)
        {
            var match = _secrets.FirstOrDefault(secret => _pending.AsSpan(index).StartsWith(secret, StringComparison.Ordinal));
            if (match is null) result.Append(_pending[index++]);
            else { result.Append("[登录令牌已隐藏]"); index += match.Length; }
        }
        _pending = _pending[index..];
        return result.ToString();
    }
}
