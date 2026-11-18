using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using EML.App.Services;
using EML.Core.Minecraft;

namespace EML.App.Tests;

/// <summary>在现有主题与 STA 环境中验证真实文件夹导入窗口，不显示窗口、不扫描用户目录。</summary>
internal static class FolderImportDialogChecks
{
    /// <summary>复制预览路径可复制，关闭游戏确认不能跳过；发现列表初始无法误提交。</summary>
    internal static void Run(Action<DialogWindow, string> render)
    {
        var candidate = new ExternalMinecraftInstance("GTNH 中文，本地存档", "Prism Launcher", @"D:\Prism\instances\旧实例",
            @"D:\Prism\instances\旧实例\.minecraft", "1.7.10", "Forge 10.13.4.1614");
        var preview = new FolderInstanceImportPreview(candidate,
            new(candidate.Name, "Prism", "1.7.10", [], 4321, 0, 0, ["保留现有模组和存档；复制后补全运行依赖。"]),
            1024L * 1024 * 2048, @"C:\Games\EML\instances", [@"D:\Prism\accounts.json"]);
        var window = new FolderInstanceCopyDialog(preview);
        try
        {
            Assert.True(window.CancelButton.IsDefault);
            var input = Assert.IsType<TextBox>(window.FocusTarget);
            var confirm = Assert.Single(window.Actions.Children.OfType<Button>(), button => !button.IsCancel);
            var closed = Assert.Single(window.Body.Children.OfType<CheckBox>());
            Assert.False(closed.IsChecked == true);
            confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Null(window.DialogResult);
            Assert.Contains(window.Body.Children.OfType<TextBlock>(), text => text.Text.Contains("请先关闭游戏"));
            input.Text = "中文实例（新副本）";
            var target = Assert.Single(window.Body.Children.OfType<TextBox>(), text => text.Text.StartsWith("复制目标："));
            Assert.True(target.IsReadOnly);
            Assert.Contains(input.Text, target.Text);
            target.SelectAll();
            Assert.Equal(target.Text, target.SelectedText);
            Assert.Contains(window.Body.Children.OfType<TextBox>(), text => text.Text.Contains(candidate.GameDirectory));
            Assert.Contains(window.Body.Children.OfType<TextBlock>(), text => text.Text.Contains("不代表官方认证"));
            closed.IsChecked = true;
            input.Text = " ";
            confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Null(window.DialogResult);
            Assert.Contains(window.Body.Children.OfType<TextBlock>(), text => text.Text.Contains("请输入新实例名称"));
            input.Text = candidate.Name;
            render(window, "folder-import-preview");
        }
        finally { window.Close(); }

        var discovery = new NoDiscovery();
        var select = new ExternalInstanceSelectionDialog(discovery, CancellationToken.None);
        try
        {
            Assert.Null(select.SelectedInstance);
            Assert.False(Assert.Single(select.Actions.Children.OfType<Button>(), button => !button.IsCancel).IsEnabled);
            var list = Assert.Single(select.Body.Children.OfType<DataGrid>());
            Assert.True(list.IsReadOnly);
            Assert.Equal(4, list.Columns.Count);
            Assert.Equal(0, discovery.Calls);
            render(select, "folder-import-discovery");
            Assert.All(list.Columns, column => Assert.True(column.ActualWidth >= 100));
            ((System.Collections.ObjectModel.ObservableCollection<ExternalMinecraftInstance>)list.ItemsSource).Add(candidate);
            list.SelectedItem = candidate;
            Assert.Same(candidate, select.SelectedInstance);
            Assert.True(Assert.Single(select.Actions.Children.OfType<Button>(), button => !button.IsCancel).IsEnabled);
            render(select, "folder-import-discovered");
        }
        finally { select.Close(); }
    }

    /// <summary>离屏布局不得触发真实文件发现。</summary>
    private sealed class NoDiscovery : IExternalInstanceDiscovery
    {
        internal int Calls { get; private set; }
        /// <summary>返回空候选并记录调用次数。</summary>
        public Task<ExternalInstanceDiscoveryResult> DiscoverAsync(string? directory = null, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new ExternalInstanceDiscoveryResult([], [])); }
    }
}
