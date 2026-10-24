namespace EML.Core.Launch;

/// <summary>
/// 启动计划器：把「用哪个 Java、哪个游戏目录、哪个版本」变成一条可执行的 java 命令行。
///
/// 放在 Core 的原因：
///   App 只依赖这个接口，不依赖具体怎么拼；
///   将来支持 Forge / Prism 时，换一个实现类即可，窗口不用改。
///
/// 实现方（Infrastructure）要遵守的约定：
///   1) 只读文件，不下载、不联网、不启动进程；
///   2) 输入不合法（比如版本 JSON 缺失）时抛异常，异常消息要能直接给人看；
///   3) 同一个 LaunchRequest 多次调用，结果应该一致（不依赖全局状态）。
/// </summary>
public interface ILaunchPlanner
{
    /// <summary>
    /// 生成启动计划。
    /// </summary>
    /// <param name="request">这次启动的输入（Java、目录、玩家名、内存）。</param>
    /// <returns>拼好的 <see cref="LaunchPlan"/>。</returns>
    /// <exception cref="System.IO.FileNotFoundException">
    /// 游戏目录里找不到对应的版本 JSON 时抛出，消息里会写清楚缺哪个文件。
    /// </exception>
    LaunchPlan Build(LaunchRequest request);
}
