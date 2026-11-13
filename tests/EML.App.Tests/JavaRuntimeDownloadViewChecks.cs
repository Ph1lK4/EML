using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using EML.App.ViewModels;
using EML.App.Views;
using EML.Core.Java;

namespace EML.App.Tests;

/// <summary>复用已有 STA 和应用主题，检查 Java 下载控件绑定及小窗口滚动，不发起真实下载。</summary>
internal static class JavaRuntimeDownloadViewChecks
{
    /// <summary>用生产模型驱动页面，验证筛选、查询、安装结果与可复制路径。</summary>
    internal static void Run(Border host)
    {
        var service = new RuntimeService();
        var model = new JavaSettingsViewModel(new EmptyLocator(), runtimes: service) { SelectedArchitecture = "x64" };
        var page = new JavaEnvironmentView { DataContext = model };
        host.Child = page;
        Flush(host);
        var major = Assert.IsType<ComboBox>(page.FindName("JavaMajorSelector"));
        var architecture = Assert.IsType<ComboBox>(page.FindName("JavaArchitectureSelector"));
        var query = Assert.IsType<Button>(page.FindName("QueryJavaButton"));
        var install = Assert.IsType<Button>(page.FindName("DownloadJavaButton"));
        var packages = Assert.IsType<ComboBox>(page.FindName("JavaPackageSelector"));
        var root = Assert.IsType<TextBox>(page.FindName("JavaInstallationRoot"));
        Assert.Equal(21, major.SelectedItem);
        Assert.Equal("x64", architecture.SelectedItem);
        Assert.True(query.IsEnabled);
        Assert.False(install.IsEnabled);
        Assert.True(root.IsReadOnly);
        Assert.Equal(service.InstallationRoot, root.Text);

        major.SetCurrentValue(Selector.SelectedItemProperty, 25);
        Assert.Equal(25, model.SelectedMajorVersion);
        Assert.NotNull(BindingOperations.GetBindingExpression(major, Selector.SelectedItemProperty));
        model.QueryPackagesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Flush(host);
        Assert.Same(model.SelectedPackage, packages.SelectedItem);
        Assert.True(install.IsEnabled);
        Assert.Contains("25.0.1", Assert.IsType<TextBlock>(page.FindName("JavaPackageDetails")).Text);

        model.DownloadJavaCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Flush(host);
        Assert.Equal(service.Installed, Assert.Single(model.Installations));
        Assert.Contains("安装完成", Assert.IsType<TextBlock>(page.FindName("JavaDownloadStatus")).Text);

        var scroll = Assert.IsType<ScrollViewer>(page.FindName("JavaDetailsScroll"));
        Assert.True(scroll.ViewportHeight > 0);
        Assert.True(scroll.ScrollableHeight > 0, "Java 下载与环境详情在小窗口中必须可滚动。");
        scroll.ScrollToBottom();
        Flush(host);
        Assert.True(scroll.VerticalOffset > 0);
        var scrollBounds = scroll.TransformToAncestor(page).TransformBounds(new Rect(scroll.RenderSize));
        Assert.True(scrollBounds.Left >= 0 && scrollBounds.Right <= page.ActualWidth + 1);
        Assert.True(scrollBounds.Top >= 0 && scrollBounds.Bottom <= page.ActualHeight + 1);

        model.IsBusy = true;
        Flush(host);
        Assert.False(major.IsEnabled);
        Assert.False(architecture.IsEnabled);
        Assert.False(packages.IsEnabled);
        Assert.False(query.IsEnabled);
        Assert.False(install.IsEnabled);
        model.IsBusy = false;
        host.Child = null;
    }

    /// <summary>执行布局和排队绑定，保持现有应用的 UI 线程。</summary>
    private static void Flush(Border host)
    {
        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
        host.UpdateLayout();
    }

    /// <summary>页面不会触发启动时扫描；手动扫描只得到空结果。</summary>
    private sealed class EmptyLocator : IJavaLocator
    {
        /// <summary>不访问注册表或用户磁盘。</summary>
        public Task<IReadOnlyList<JavaInstallation>> FindAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JavaInstallation>>([]);
    }

    /// <summary>立即完成内存查询与安装，避免页面验证发起真实网络或进程。</summary>
    private sealed class RuntimeService : IJavaRuntimeService
    {
        public string InstallationRoot => @"C:\EML-test-only\中文启动器\runtimes\java";
        public JavaInstallation Installed { get; } = new(@"C:\EML-test-only\中文启动器\runtimes\java\25\bin\java.exe", "25.0.1", "x64");
        /// <summary>返回符合筛选的单个官方包形状数据。</summary>
        public Task<IReadOnlyList<JavaRuntimePackage>> GetPackagesAsync(int majorVersion, string architecture, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JavaRuntimePackage>>([new(majorVersion, "25.0.1+8", architecture, "jdk.zip", "https://example.invalid/jdk.zip", new string('a', 64), 100_000_000)]);
        /// <summary>返回固定已验证环境，不执行任何安装写入。</summary>
        public Task<JavaInstallation> InstallAsync(JavaRuntimePackage package, IProgress<JavaRuntimeInstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Installed);
    }
}
