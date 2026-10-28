namespace EML.Core.Launch;

/// <summary>
/// 一次启动请求需要的全部输入。
///
/// 分区说明：
///   一、Java  —— 用哪个 java.exe（来自 Java 检测结果，不在这一步重新检测）
///   二、目录  —— 游戏目录在哪（.minecraft 的路径）
///   三、身份  —— 版本名与玩家名（离线模式，没有正版令牌）
///   四、内存  —— 最大堆内存，单位 MB
///   五、额外参数 —— 用户自己填的 JVM 参数 / 游戏参数（可空）
///
/// 这一步只用来拼命令行，所以字段很少；
/// 以后做下载、实例、正版账号时，在对应功能里加字段，不要往这里硬塞。
/// </summary>
/// <param name="JavaPath">
/// java.exe 的完整路径，例如
/// <c>C:\Program Files\Java\jre8\bin\java.exe</c>。
/// 传进来的值不做检查，路径不存在会在真正启动时由系统报错。
/// </param>
/// <param name="GameDirectory">
/// 游戏目录，通常是 <c>.minecraft</c>。
/// 版本 JSON、libraries、assets、versions 都按这个目录往下找。
/// </param>
/// <param name="VersionName">
/// 版本名，第一步固定 <c>1.7.10</c>。
/// 它会原样写进 <c>--version</c> 参数，窗口标题栏也会显示它。
/// </param>
/// <param name="PlayerName">
/// 玩家名，离线模式用。
/// 会写进 <c>--username</c>，同时参与离线 UUID 的计算。
/// </param>
/// <param name="MaxMemoryMb">
/// 最大堆内存，单位 MB，默认 2048。
/// 最终会变成 <c>-Xmx2048M</c> 这样的参数。
/// </param>
/// <param name="ExtraJvmArguments">
/// 额外的 JVM 参数（可空），例如 <c>-XX:+UseG1GC</c>。
/// 会插在 <c>-Xmx</c> 之后、<c>-cp</c> 之前；没有就传 null。
/// </param>
/// <param name="ExtraGameArguments">
/// 额外的游戏参数（可空），例如 <c>--width 1280 --height 720</c>。
/// 会追加在游戏参数最后；没有就传 null。
/// </param>
public sealed record LaunchRequest(
    string JavaPath,
    string GameDirectory,
    string VersionName,
    string PlayerName,
    int MaxMemoryMb = 2048,
    string? ExtraJvmArguments = null,
    string? ExtraGameArguments = null);
