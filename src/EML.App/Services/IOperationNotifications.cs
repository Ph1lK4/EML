using System.IO;

namespace EML.App.Services;

/// <summary>一次完整操作的结果，取消和部分完成分别展示，不冒充成功或失败。</summary>
public enum OperationNoticeKind { Success, Warning, Failure, Cancelled }

/// <summary>提供可选中复制的说明和原始路径，不依赖 WPF 或状态栏文本解析。</summary>
public sealed record OperationNotice(OperationNoticeKind Kind, string Title, string Message, IReadOnlyList<string> Paths);

/// <summary>接收已完成的操作结果，由界面统一安排弹窗。</summary>
public interface IOperationNotifications
{
    /// <summary>提交一个结果；不应阻塞业务流程或改变业务结果。</summary>
    void Report(OperationNotice notice);
}

/// <summary>未接入界面的调用方保持原有行为，测试可注入记录器。</summary>
public sealed class NullOperationNotifications : IOperationNotifications
{
    public static NullOperationNotifications Instance { get; } = new();
    /// <summary>空实现不创建窗口或写入日志。</summary>
    public void Report(OperationNotice notice) { }
}

/// <summary>构造一致的结果消息；路径只去重，保留中文标点、空格和原始内容。</summary>
public static class OperationNotificationExtensions
{
    /// <summary>报告完整成功的用户操作。</summary>
    public static void Success(this IOperationNotifications sink, string title, string message, params string?[] paths) =>
        Send(sink, OperationNoticeKind.Success, title, message, paths);

    /// <summary>报告部分完成或仍需处理的内容，不将其标为完整成功。</summary>
    public static void Warning(this IOperationNotifications sink, string title, string message, params string?[] paths) =>
        Send(sink, OperationNoticeKind.Warning, title, message, paths);

    /// <summary>报告已整理或脱敏的失败原因，保留业务服务给出的具体说明。</summary>
    public static void Failure(this IOperationNotifications sink, string title, string message, params string?[] paths) =>
        Send(sink, OperationNoticeKind.Failure, title, message, paths);

    /// <summary>报告异常的公开原因；文件异常有明确文件名时另列为可复制路径，不显示堆栈或内部凭据。</summary>
    public static void Failure(this IOperationNotifications sink, string title, Exception error, params string?[] paths)
    {
        var filename = error switch
        {
            FileNotFoundException file => file.FileName,
            FileLoadException file => file.FileName,
            _ => null
        };
        Send(sink, OperationNoticeKind.Failure, title, error.Message, [.. paths, filename]);
    }

    /// <summary>将用户主动取消与失败区分，并说明已完成文件是否保留。</summary>
    public static void Cancelled(this IOperationNotifications sink, string title, string message, params string?[] paths) =>
        Send(sink, OperationNoticeKind.Cancelled, title, message, paths);

    /// <summary>过滤空路径并稳定去重，不从错误文字猜测或改写实际路径。</summary>
    private static void Send(IOperationNotifications sink, OperationNoticeKind kind, string title, string message, string?[] paths) =>
        sink.Report(new(kind, title, string.IsNullOrWhiteSpace(message) ? "操作未返回详细说明。" : message,
            paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
}
