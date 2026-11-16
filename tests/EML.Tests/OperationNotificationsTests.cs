using EML.App.Services;

namespace EML.Tests;

/// <summary>验证通知携带真实路径和公开原因，不丢失中文字符或暴露内部异常。</summary>
public sealed class OperationNotificationsTests
{
    /// <summary>合法空白和中文标点保持原样，同一个路径只出现一次。</summary>
    [Fact]
    public void SuccessPreservesExactUnicodePathsAndRemovesDuplicates()
    {
        var sink = new Recorder();
        const string path = "C:\\实例\\\u3000中文（测试）\u3000\\ 文件.json\u00a0";
        sink.Success("导入成功", "实例已导入并完成补全。", path, null, "", path);
        var result = Assert.Single(sink.Results);
        Assert.Equal(OperationNoticeKind.Success, result.Kind);
        Assert.Equal(path, Assert.Single(result.Paths));
    }

    /// <summary>文件异常的明确文件名另列为路径，外层原因可复制且不带内部敏感诊断。</summary>
    [Fact]
    public void FailureIncludesFileNameButNotInnerExceptionOrStackTrace()
    {
        var sink = new Recorder();
        const string file = "C:\\实例\\中文包.zip";
        var error = new FileNotFoundException("找不到原始整合包。", file, new Exception("private diagnostic"));
        sink.Failure("恢复失败", error, "C:\\实例");
        var result = Assert.Single(sink.Results);
        Assert.Equal(OperationNoticeKind.Failure, result.Kind);
        Assert.Equal(error.Message, result.Message);
        Assert.Contains(file, result.Paths);
        Assert.DoesNotContain("private diagnostic", result.Message);
        Assert.DoesNotContain(nameof(FileNotFoundException), result.Message);
    }

    /// <summary>部分完成和主动取消保留各自状态，不能被界面显示为绿色成功或红色错误。</summary>
    [Fact]
    public void WarningAndCancellationRemainDistinctOutcomes()
    {
        var sink = new Recorder();
        sink.Warning("补全尚未完成", "实例已导入，但一个文件仍需手动下载。", "C:\\实例");
        sink.Cancelled("已取消导入", "未生成新实例。");
        Assert.Equal(new[] { OperationNoticeKind.Warning, OperationNoticeKind.Cancelled }, sink.Results.Select(result => result.Kind));
        Assert.Empty(sink.Results[1].Paths);
    }

    /// <summary>仅记录传入结果，不访问窗口、剪贴板或真实实例。</summary>
    private sealed class Recorder : IOperationNotifications
    {
        internal List<OperationNotice> Results { get; } = [];
        /// <summary>保存本次结果供断言使用。</summary>
        public void Report(OperationNotice notice) => Results.Add(notice);
    }
}
