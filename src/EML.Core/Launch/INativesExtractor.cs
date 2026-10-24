// ════════════════════════════════════════════════════════════════════
//  文件：INativesExtractor.cs
//  位置：src\EML.Core\Launch
//  作用：把版本需要的「本机库」（.dll）解压到 natives 目录。
//
//  为什么需要它？
//    1.7.10 的 lwjgl / jinput / twitch 这些库，真正被 Java 加载的不是 jar，
//    而是 jar 里打包的 .dll。Java 通过 -Djava.library.path 找这些 dll，
//    所以启动前必须先把它们解压到一个固定目录。
//    不解压就会报：UnsatisfiedLinkError: no lwjgl in java.library.path
// ════════════════════════════════════════════════════════════════════

namespace EML.Core.Launch;

/// <summary>
/// 本机库解压器。
/// </summary>
public interface INativesExtractor
{
    /// <summary>
    /// 读出 <c>&lt;游戏目录&gt;\versions\&lt;版本&gt;\&lt;版本&gt;.json</c> 里
    /// 声明的 windows 本机库，把 jar 里的 .dll 解压到
    /// <c>&lt;游戏目录&gt;\versions\&lt;版本&gt;\&lt;版本&gt;-natives</c>。
    /// </summary>
    /// <param name="gameDirectory">游戏目录（.minecraft）。</param>
    /// <param name="versionName">版本名，例如 1.7.10。</param>
    /// <returns>
    /// 解压出来的 natives 目录，可直接给 <c>-Djava.library.path</c>；
    /// 没有任何本机库时返回空串。
    /// </returns>
    /// <exception cref="FileNotFoundException">游戏目录里缺版本 JSON 时抛出。</exception>
    string Extract(string gameDirectory, string versionName);
}
