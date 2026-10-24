using System.Text.RegularExpressions;

namespace Launcher.Metadata;

/// <summary>处理 ${xxx} 占位符。不认识的保留原样，方便一眼看出漏了什么。</summary>
public static class ArgumentBuilder
{
    private static readonly Regex Placeholder = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    public static string Expand(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder.Replace(template, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);

    /// <summary>1.7.10 是单行模板，按空白切开（占位符本身不含空格）。</summary>
    public static IReadOnlyList<string> SplitTemplate(string template) =>
        template.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>group:artifact:version[:classifier][@ext] → Maven 仓库内的相对路径。</summary>
    public static string MavenPath(string name)
    {
        var at = name.IndexOf('@');
        var coordinates = at >= 0 ? name[..at] : name;
        var extension = at >= 0 ? name[(at + 1)..] : "jar";

        var parts = coordinates.Split(':');
        if (parts.Length < 3)
            throw new FormatException($"无法解析 Maven 坐标：{name}");

        var (group, artifact, version) = (parts[0], parts[1], parts[2]);
        var classifier = parts.Length > 3 ? parts[3] : null;
        var file = classifier is null
            ? $"{artifact}-{version}.{extension}"
            : $"{artifact}-{version}-{classifier}.{extension}";

        return $"{group.Replace('.', '/')}/{artifact}/{version}/{file}";
    }
}
