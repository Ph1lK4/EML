using System.Text;
using System.Text.RegularExpressions;

namespace EML.Core.Minecraft.Launching;

/// <summary>设置界面、保存服务和启动计划共用的输入规则，不执行输入的命令或脚本。</summary>
public static partial class MinecraftLaunchValidation
{
    /// <summary>离线模式验证自填名称；正版名称来自账号服务，仍统一验证内存和附加参数。</summary>
    public static void Validate(MinecraftLaunchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.UseMicrosoftAccount && (settings.PlayerName is null || !PlayerNamePattern().IsMatch(settings.PlayerName)))
            throw new ArgumentException("离线玩家名须为 1–16 位英文字母、数字或下划线。");
        if (settings.MinMemoryMb < 256 || settings.MaxMemoryMb < 512 || settings.MaxMemoryMb > 1048576 ||
            settings.MinMemoryMb > settings.MaxMemoryMb)
            throw new ArgumentException("初始内存至少 256 MB、最大内存至少 512 MB，初始值不能超过最大值。");
        if (settings.JavaPath is { } path && (path.Any(char.IsControl) || path.Length > 32760))
            throw new ArgumentException("Java 路径包含非法字符或过长。");
        var arguments = TokenizeJvmArguments(settings.AdditionalJvmArguments);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is "--add-opens" or "--add-exports" or "--add-reads" or "--add-modules" or "--enable-native-access")
            {
                if (++index >= arguments.Count || arguments[index].StartsWith('-') || arguments[index].StartsWith('@'))
                    throw new ArgumentException("JVM 参数缺少有效值：" + argument);
                continue;
            }
            // JVM 入口、类路径及原生库位置由已验证配置提供；内存只从明确的输入框生成。
            if (!argument.StartsWith('-') || argument == "--" || argument.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("-XX:InitialRAM", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("-XX:MaxRAM", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("-XX:MinRAM", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("-XX:InitialHeapSize", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("-XX:MaxHeapSize", StringComparison.OrdinalIgnoreCase) ||
                argument is "-cp" or "-classpath" or "-jar" or "-m" or "-p" ||
                argument.StartsWith("--class-path", StringComparison.Ordinal) || argument.StartsWith("--module", StringComparison.Ordinal) ||
                argument.StartsWith("-Djava.class.path", StringComparison.Ordinal) || argument.StartsWith("-Djava.library.path", StringComparison.Ordinal) ||
                argument.StartsWith("-Dorg.lwjgl.librarypath", StringComparison.Ordinal) || argument.StartsWith("-Dlog4j.configurationFile", StringComparison.Ordinal) ||
                argument is "-version" or "--version" or "-help" or "--help" or "-?" || argument.StartsWith("-Xbootclasspath", StringComparison.Ordinal))
                throw new ArgumentException("附加 JVM 参数不能更改启动入口、类路径、原生库、日志配置或内存；请使用对应设置。参数：" + argument);
        }
    }

    /// <summary>按空白分隔参数并支持单双引号；普通 Windows 反斜杠保持原值，不进行 shell 展开。</summary>
    public static IReadOnlyList<string> TokenizeJvmArguments(string text)
    {
        if (text is null || text.Length > 32768 || text.Any(character => char.IsControl(character) && !char.IsWhiteSpace(character)))
            throw new ArgumentException("附加 JVM 参数为空、过长或包含非法控制字符。");
        var arguments = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        var started = false;
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character == '\\' && quote != '\0')
            {
                var end = i;
                while (end < text.Length && text[end] == '\\') end++;
                var count = end - i;
                if (end < text.Length && text[end] == quote)
                {
                    // 引号前偶数个斜杠折半后闭合，奇数个折半后保留引号本身。
                    current.Append('\\', count / 2);
                    if (count % 2 == 0) quote = '\0';
                    else current.Append(text[end]);
                    i = end;
                }
                else
                {
                    // 路径中未紧邻引号的斜杠保持原样，例如 C:\Program Files\Java。
                    current.Append('\\', count);
                    i = end - 1;
                }
                started = true;
                continue;
            }
            if (character is '\'' or '"')
            {
                if (quote == '\0') { quote = character; started = true; continue; }
                if (quote == character) { quote = '\0'; continue; }
            }
            if (quote == '\0' && char.IsWhiteSpace(character))
            {
                if (started) { arguments.Add(current.ToString()); current.Clear(); started = false; }
                continue;
            }
            current.Append(character);
            started = true;
        }
        if (quote != '\0') throw new ArgumentException("附加 JVM 参数的引号未闭合。");
        if (started) arguments.Add(current.ToString());
        if (arguments.Count > 256 || arguments.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("JVM 参数过多或包含空参数。");
        return arguments;
    }

    [GeneratedRegex("\\A[A-Za-z0-9_]{1,16}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlayerNamePattern();
}
