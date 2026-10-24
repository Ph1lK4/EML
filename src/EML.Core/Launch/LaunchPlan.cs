namespace EML.Core.Launch;

/// <summary>
/// 启动计划：一条完整的 java 命令行。
///
/// 为什么要有这个类型，而不是直接拼一个 string？
///   1) 以后真正启动游戏时，需要把「程序路径」和「参数」分开传
///      （走 ProcessStartInfo.ArgumentList 比拼字符串安全，路径里有空格也不会断）；
///   2) 工作目录是启动的一部分，单独放着不容易漏；
///   3) 窗口现在只显示 CommandLine，将来加启动按钮时不用改这里。
///
/// 第一步只把它显示在窗口里，不负责启动进程。
/// </summary>
/// <param name="JavaPath">
/// java.exe 的完整路径。显示时会加引号，防止路径里有空格。
/// </param>
/// <param name="Arguments">
/// java 后面的全部参数：内存、classpath、主类、游戏参数都在这里面。
/// 参数之间用空格分隔，整串是给「人看」和「复制」用的；
/// 真正启动时应拆成列表传，不要把这串直接塞给 shell。
/// </param>
/// <param name="WorkingDirectory">
/// 启动时的工作目录，一般是游戏目录 <c>.minecraft</c>。
/// 游戏会在这个目录下写 logs、options.txt、saves 等。
/// </param>
public sealed record LaunchPlan(
    string JavaPath,
    string Arguments,
    string WorkingDirectory)
{
    /// <summary>
    /// 可以直接复制到命令行里执行的完整字符串。
    /// 注意：这里只做「给 java 路径加引号」，不负责转义参数里的特殊字符；
    /// 参数里的路径已经由 <see cref="VanillaLaunchPlanner"/> 单独处理过。
    /// </summary>
    public string CommandLine => $"\"{JavaPath}\" {Arguments}";
}
