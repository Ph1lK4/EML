using System.Windows;
using System.Windows.Controls;
using EML.Core.Minecraft;

namespace EML.App.Services;

/// <summary>导入预览：展示识别结果，以及导入完成后的自动补全范围。</summary>
internal sealed class InstanceImportDialog : Window
{
    private readonly TextBox _name;
    /// <summary>返回用户输入的实例名称，最终合法性由导入服务检查。</summary>
    public string InstanceName => _name.Text;

    /// <summary>创建简洁的名称输入、识别详情和确认按钮，不直接访问磁盘。</summary>
    public InstanceImportDialog(InstanceImportPreview preview)
    {
        Title = "导入实例";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock { Text = "新实例名称（同时作为文件夹名）" });
        _name = new TextBox { Text = preview.SuggestedName, MaxLength = 128, Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(6) };
        content.Children.Add(_name);
        var details = $"格式：{preview.Format}\nMinecraft：{preview.MinecraftVersion ?? "未知"}\n" +
            $"包内文件：{preview.EmbeddedFileCount}\n有下载地址的文件声明：{preview.DownloadFileCount}\n" +
            $"待解析的 CurseForge 文件声明：{preview.UnresolvedFileCount}\n\n" +
            (preview.Format.Equals("Prism", StringComparison.OrdinalIgnoreCase) ||
             preview.Format.Equals("Modrinth", StringComparison.OrdinalIgnoreCase) ||
             preview.Format.Equals("CurseForge", StringComparison.OrdinalIgnoreCase)
                ? "导入后会自动联网下载缺失的必需模组和支持的运行依赖，并执行完整检查。取消下载会保留已导入实例，可稍后继续补全。\n\n"
                : "导入包内文件并保存已有清单；通用 ZIP 可能缺少完整下载信息，可在导入后检查文件。\n\n") +
            string.Join("\n", preview.Notes);
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap }
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        var import = new Button { Content = "导入", IsDefault = true, Padding = new Thickness(16, 6, 16, 6) };
        import.Click += OnConfirm;
        buttons.Children.Add(cancel);
        buttons.Children.Add(import);
        content.Children.Add(buttons);
        Content = content;
    }

    /// <summary>拦截空名称；其他文件系统名称规则由导入服务统一检查。</summary>
    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            MessageBox.Show(this, "请输入实例名称。", "名称不能为空");
            return;
        }
        DialogResult = true;
    }
}
