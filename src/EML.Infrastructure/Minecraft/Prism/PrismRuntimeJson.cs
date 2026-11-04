using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Prism;

/// <summary>Prism 元数据的基础读取与平台规则；不访问网络，不执行补丁中的内容。</summary>
internal static class PrismRuntimeJson
{
    /// <summary>读取有大小限制的普通 JSON 文件，拒绝链接并保留原始字节供指纹计算。</summary>
    internal static (JsonElement Json, byte[] Bytes) Read(string path)
    {
        InstanceImportPaths.EnsureNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("Prism 元数据超过 8 MiB。");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        if (buffer.Length > 8 * 1024 * 1024) throw new InvalidDataException("Prism 元数据超过 8 MiB。");
        var bytes = buffer.ToArray();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Prism 元数据必须是对象。");
        return (document.RootElement.Clone(), bytes);
    }

    /// <summary>读取字符串属性；属性类型错误时拒绝静默丢失参数。</summary>
    internal static string? Text(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var found) || found.ValueKind == JsonValueKind.Null) return null;
        if (found.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{name} 必须为字符串。");
        return found.GetString();
    }

    /// <summary>返回数组或空数组，拒绝把错误格式误判为没有依赖。</summary>
    internal static IEnumerable<JsonElement> Array(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var found)) return [];
        if (found.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{name} 必须为数组。");
        if (found.GetArrayLength() > 100000) throw new InvalidDataException($"{name} 条目过多。");
        return found.EnumerateArray();
    }

    /// <summary>组件标识和固定版本必须能安全作为单个路径片段，不能包含分隔符。</summary>
    internal static string Segment(string value)
    {
        var safe = InstanceImportPaths.Relative(value);
        if (safe.Contains('/') || !Regex.IsMatch(safe, "^[A-Za-z0-9_.+\\-]+$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("组件标识或版本含不支持的字符：" + value);
        return safe;
    }

    /// <summary>按 Prism 的精确平台名及旧式 x86 平台别名匹配规则。</summary>
    internal static bool PlatformMatches(string target, string os, string architecture) =>
        target == os + "-" + architecture || ((architecture is "x86" or "x86_64") && target == os);

    /// <summary>依次应用 allow/disallow；不能处理的规则给出阻断原因，而不是猜测适用范围。</summary>
    internal static bool Active(JsonElement value, string os, string architecture, List<string> issues)
    {
        if (!value.TryGetProperty("rules", out var rules)) return true;
        if (rules.ValueKind != JsonValueKind.Array) throw new InvalidDataException("rules 必须为数组。");
        if (rules.GetArrayLength() == 0) return true;
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object) throw new InvalidDataException("规则必须为对象。");
            if (rule.TryGetProperty("features", out _))
            {
                issues.Add("遇到依赖启动功能状态的 features 规则，当前无法可靠选择。");
                return false;
            }
            var matches = true;
            if (rule.TryGetProperty("os", out var system))
            {
                var name = Text(system, "name");
                if (name is not null) matches = PlatformMatches(name, os, architecture);
                var arch = Text(system, "arch");
                if (arch is not null)
                    matches &= arch switch { "x86" => architecture == "x86", "x86_64" or "amd64" => architecture == "x86_64", "aarch64" or "arm64" => architecture == "arm64", _ => arch == architecture };
                if (Text(system, "version") is { Length: > 0 })
                {
                    issues.Add("遇到限定操作系统版本的规则，当前解析器尚未支持。");
                    return false;
                }
            }
            var action = Text(rule, "action");
            if (action is not ("allow" or "disallow")) throw new InvalidDataException("不支持的 rules.action。");
            if (matches) allowed = action == "allow";
        }
        return allowed;
    }

    /// <summary>验证 HTTPS 下载地址，不接受凭据、其他协议或由文件名猜出的地址。</summary>
    internal static string Url(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
            throw new InvalidDataException("运行依赖必须使用不含凭据的 HTTPS URL。");
        return uri.AbsoluteUri;
    }

    /// <summary>比较由数字和文本组成的版本段，让 17.0、2.10 等版本按数字顺序去重。</summary>
    internal static int CompareVersions(string left, string right)
    {
        var a = Regex.Matches(left.ToLowerInvariant(), "[0-9]+|[a-z]+", RegexOptions.CultureInvariant).Select(match => match.Value).ToArray();
        var b = Regex.Matches(right.ToLowerInvariant(), "[0-9]+|[a-z]+", RegexOptions.CultureInvariant).Select(match => match.Value).ToArray();
        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            if (index >= a.Length) return RemainingVersionSign(b, index) * -1;
            if (index >= b.Length) return RemainingVersionSign(a, index);
            var an = BigInteger.TryParse(a[index], NumberStyles.None, CultureInfo.InvariantCulture, out var av);
            var bn = BigInteger.TryParse(b[index], NumberStyles.None, CultureInfo.InvariantCulture, out var bv);
            var comparison = an && bn ? av.CompareTo(bv) : an ? 1 : bn ? -1 : string.CompareOrdinal(a[index], b[index]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    /// <summary>末尾的零不提升版本，常见预发布标签低于无标签正式版。</summary>
    private static int RemainingVersionSign(string[] parts, int start)
    {
        for (var index = start; index < parts.Length; index++)
        {
            if (parts[index] is "alpha" or "beta" or "pre" or "rc" or "snapshot") return -1;
            if (parts[index] is not ("0" or "final" or "release")) return 1;
        }
        return 0;
    }
}

/// <summary>解析 Prism 使用的 Maven 坐标，下载路径来自坐标而非任意 artifact.path。</summary>
internal sealed record PrismMavenName(string Group, string Artifact, string Version, string? Classifier, string Extension)
{
    internal string Identity => $"{Group}:{Artifact}:{Classifier}@{Extension}";
    internal string FileName => $"{Artifact}-{Version}{(Classifier is null ? "" : "-" + Classifier)}.{Extension}";
    internal string RelativePath => Group.Replace('.', '/') + "/" + Artifact + "/" + Version + "/" + FileName;

    /// <summary>接受 group:artifact:version[:classifier][@extension]，拒绝路径穿越和模糊坐标。</summary>
    internal static PrismMavenName Parse(string value)
    {
        var suffix = value.Split('@');
        if (suffix.Length > 2) throw new InvalidDataException("无效 Maven 扩展名：" + value);
        var parts = suffix[0].Split(':');
        if (parts.Length is not (3 or 4)) throw new InvalidDataException("无效 Maven 坐标：" + value);
        foreach (var part in parts) PrismRuntimeJson.Segment(part);
        foreach (var group in parts[0].Split('.')) PrismRuntimeJson.Segment(group);
        var extension = suffix.Length == 2 ? PrismRuntimeJson.Segment(suffix[1]) : "jar";
        return new(parts[0], parts[1], parts[2], parts.Length == 4 ? parts[3] : null, extension);
    }
}
