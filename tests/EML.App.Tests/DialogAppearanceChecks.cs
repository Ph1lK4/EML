using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using EML.App.Services;
using EML.Core.Minecraft;

namespace EML.App.Tests;

/// <summary>在现有 STA/Application 中验证真实弹窗内容，不显示窗口或执行实例操作。</summary>
internal static class DialogAppearanceChecks
{
    /// <summary>覆盖安全默认操作、导入校验、长内容布局和迁移向导。</summary>
    internal static void Run()
    {
        VerifyConfirmation();
        VerifyImport();
        MigrationDialogChecks.Run(Render);
    }

    /// <summary>危险操作默认取消，路径可复制；最小宽度下操作栏仍可达。</summary>
    private static void VerifyConfirmation()
    {
        var path = @"C:\Users\玩家\Desktop\EML\instances\GTNH 2.9.0 · 带有本地记录的长期生存实例";
        var window = DialogPrompt.CreateConfirmation("删除这个实例？",
            "此操作包含实例内的存档、模组和配置，请先正常关闭游戏。\n\nEML 将请求 Windows 回收站；若系统提示永久删除，请谨慎选择。",
            "删除实例", DialogTone.Danger, "GTNH 2.9.0", path);
        try
        {
            Assert.Null(window.DialogResult);
            Assert.Same(window.CancelButton, window.FocusTarget);
            Assert.True(window.CancelButton.IsCancel); Assert.True(window.CancelButton.IsDefault);
            var accept = Assert.Single(window.Actions.Children.OfType<Button>(), button => !button.IsCancel);
            Assert.False(accept.IsDefault); Assert.Equal("删除实例", accept.Content);
            Assert.Same(Application.Current.FindResource("DangerButton"), accept.Style);
            Assert.False(window.ShowInTaskbar);
            Assert.Equal(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
            Assert.Equal(44, WindowChrome.GetWindowChrome(window).CaptionHeight);
            Assert.True(WindowChrome.GetIsHitTestVisibleInChrome(window.Surface.CloseAction));
            Assert.Equal("关闭对话框", AutomationProperties.GetName(window.Surface.CloseAction));
            Assert.True(UiMotion.GetEnter(window.Surface));
            Render(window, "delete-confirmation");
            var detail = Assert.Single(Descendants(window.Surface.BodyPanel).OfType<TextBox>());
            Assert.True(detail.IsReadOnly); Assert.Equal(path, detail.Text);
            detail.SelectAll(); Assert.Equal(path, detail.SelectedText);

            // 故意加入超长说明；实际布局应滚动正文，底部操作不会跟着正文滚走。
            window.Body.Children.Add(DialogPrompt.Text(string.Join("\n", Enumerable.Repeat(path, 25))));
            LayoutSurface(window, 440, 320, surface =>
            {
                Assert.True(surface.BodyScroll.ScrollableHeight > 0);
                Assert.True(surface.BodyScroll.ViewportHeight > 40);
                var footer = surface.FooterActions.TransformToAncestor(surface).TransformBounds(new Rect(surface.FooterActions.RenderSize));
                Assert.True(footer.Top >= 0 && footer.Bottom <= 320);
                Assert.True(footer.Right <= 440);
                surface.BodyScroll.ScrollToBottom(); surface.UpdateLayout();
                Assert.True(surface.BodyScroll.VerticalOffset > 0);
            });
            Assert.Null(window.DialogResult);
        }
        finally { window.Close(); }
    }

    /// <summary>导入继续使用 Enter，但空白名称只显示行内错误，修改后保留用户文本。</summary>
    private static void VerifyImport()
    {
        var preview = new InstanceImportPreview("GTNH 2.9.0", "Prism", "1.7.10", [], 4250, 296, 0,
            ["保留包内已有内容，导入后继续补全缺失依赖。"]);
        var window = new InstanceImportDialog(preview);
        try
        {
            var input = Assert.IsType<TextBox>(window.FocusTarget);
            Assert.Equal(128, input.MaxLength);
            Assert.Equal(preview.SuggestedName, window.InstanceName);
            Assert.False(window.CancelButton.IsDefault);
            var import = Assert.Single(window.Actions.Children.OfType<Button>(), button => button.IsDefault);
            Assert.Equal("导入实例", import.Content);
            input.Text = "   ";
            import.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(window.DialogResult); Assert.Equal("   ", window.InstanceName);
            var error = Assert.Single(window.Body.Children.OfType<TextBlock>(), block => block.Text == "请输入实例名称。");
            Assert.Equal(Visibility.Visible, error.Visibility);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(error));
            Render(window, "import-validation");
            input.Text = "GTNH 2.9.0 · 新冒险";
            Assert.Equal(Visibility.Collapsed, error.Visibility);
            Assert.Equal(input.Text, window.InstanceName);
            Render(window, "import-preview");
        }
        finally { window.Close(); }
    }

    /// <summary>渲染真实外壳内容，窗口保持未显示，预览文件只写到调用方指定目录。</summary>
    internal static void Render(DialogWindow window, string name)
    {
        LayoutSurface(window, window.Width, window.Height, surface =>
        {
            var directory = Environment.GetEnvironmentVariable("EML_DIALOG_PREVIEW_DIRECTORY");
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var fileName = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : name + ".png";
            using var file = File.Create(Path.Combine(directory, fileName)); encoder.Save(file);
        });
    }

    /// <summary>临时脱离未显示的 Window 完成离屏布局，再恢复所有权，不创建 HWND。</summary>
    private static void LayoutSurface(DialogWindow window, double width, double height, Action<EML.App.Views.DialogSurface> check)
    {
        var surface = window.Surface;
        window.Content = null;
        var host = new Border { Width = width, Height = height, Child = surface };
        try
        {
            host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
            Assert.True(surface.IsMeasureValid && surface.IsArrangeValid);
            check(surface);
        }
        finally { host.Child = null; window.Content = surface; }
    }

    /// <summary>遍历实际视觉树，包括只读详情输入框的模板。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is Control control) control.ApplyTemplate();
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
