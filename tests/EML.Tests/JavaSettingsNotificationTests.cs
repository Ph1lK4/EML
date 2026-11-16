using EML.App.Services;
using EML.App.ViewModels;
using EML.Core.Java;

namespace EML.Tests;

/// <summary>Java 检测结束时报告可见结果，路径取自检测结果而非推测。</summary>
public sealed class JavaSettingsNotificationTests
{
    /// <summary>成功检测的通知提供真实 Java 可执行文件路径。</summary>
    [Fact]
    public async Task SuccessfulDetectionReportsDetectedPaths()
    {
        const string path = @"C:\中文目录\Java 25\bin\java.exe";
        var notices = new Notices();
        var model = new JavaSettingsViewModel(new Locator([new(path, "25", "x64")]), notices);
        await model.DetectJavaCommand.ExecuteAsync(null);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(OperationNoticeKind.Success, notice.Kind);
        Assert.Equal(path, Assert.Single(notice.Paths));
    }

    /// <summary>没有 Java 属于需要处理的检测结果，不显示成功启动的误导信息。</summary>
    [Fact]
    public async Task EmptyDetectionReportsActionableWarning()
    {
        var notices = new Notices();
        var model = new JavaSettingsViewModel(new Locator([]), notices);
        await model.DetectJavaCommand.ExecuteAsync(null);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(OperationNoticeKind.Warning, notice.Kind);
        Assert.Contains("安装", notice.Message);
    }

    /// <summary>扫描异常保留实际失败原因，仅报告一次失败。</summary>
    [Fact]
    public async Task FailedDetectionReportsReason()
    {
        var notices = new Notices();
        var model = new JavaSettingsViewModel(new Locator([], new IOException("Java 目录不可访问")), notices);
        await model.DetectJavaCommand.ExecuteAsync(null);
        var notice = Assert.Single(notices.Items);
        Assert.Equal(OperationNoticeKind.Failure, notice.Kind);
        Assert.Contains("Java 目录不可访问", notice.Message);
    }

    /// <summary>提供受控检测结果，无需访问注册表或启动 Java。</summary>
    private sealed class Locator(IReadOnlyList<JavaInstallation> result, Exception? error = null) : IJavaLocator
    {
        /// <summary>返回测试结果或指定错误。</summary>
        public Task<IReadOnlyList<JavaInstallation>> FindAllAsync(CancellationToken cancellationToken = default) =>
            error is null ? Task.FromResult(result) : Task.FromException<IReadOnlyList<JavaInstallation>>(error);
    }

    /// <summary>保存通知，测试不创建实际弹窗。</summary>
    private sealed class Notices : IOperationNotifications
    {
        public List<OperationNotice> Items { get; } = [];
        /// <summary>按报告顺序记录结果。</summary>
        public void Report(OperationNotice notice) => Items.Add(notice);
    }
}
