// ════════════════════════════════════════════════════════════════════
//  文件：ILaunchPreflight.cs
//  位置：src\EML.Core\Launch
//  作用：定义「启动前检查」的接口和结果模型。
//
//  为什么需要它？
//    启动一次 1.7.10 要读几十个库、几百个资源对象、还有本机库（.dll）。
//    少任何一个，Java 都会在半路抛一串看不懂的异常，
//    用户根本不知道缺的是哪个文件。
//    先在启动前点一遍文件，缺就直说「缺哪个、多少个」，
//    比让他去翻运行日志快得多，也是 P0 卡片「启动前检查缺失文件」的核心。
//
//  它放在 Core：
//    Core 只放「接口 + 数据」，不碰磁盘；真正点文件的实现在 Infrastructure。
// ════════════════════════════════════════════════════════════════════

namespace EML.Core.Launch;

/// <summary>
/// 一个缺失的文件。
/// </summary>
/// <param name="Kind">
/// 分类，界面上按它分组显示：java / version / client / library / assets。
/// </param>
/// <param name="Label">给用户看的名字（例如客户端 jar 只显示文件名，不显示完整路径）。</param>
/// <param name="Path">完整路径，方便用户直接去找。</param>
public sealed record MissingFile(string Kind, string Label, string Path);

/// <summary>
/// 检查结果。
/// </summary>
/// <param name="Checked">一共检查了多少个文件（让用户知道检查真的做了事）。</param>
/// <param name="Missing">缺失清单；空表示齐全。</param>
public sealed record LaunchPreflightResult(int Checked, IReadOnlyList<MissingFile> Missing)
{
    /// <summary>true = 一个都不缺，可以直接启动。</summary>
    public bool IsReady => Missing.Count == 0;
}

/// <summary>
/// 启动前检查接口。
/// </summary>
public interface ILaunchPreflight
{
    /// <summary>
    /// 按一次启动请求，把需要的文件逐个点一遍。
    /// 不抛异常：文件缺失、版本 JSON 损坏都放进返回值里，
    /// 这样界面永远能显示一句人话，而不是弹一个堆栈。
    /// </summary>
    LaunchPreflightResult Check(LaunchRequest request);
}
