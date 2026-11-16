using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EML.Core.Minecraft;

namespace EML.App.Services;

/// <summary>导入预览：展示识别结果，以及导入完成后的自动补全范围。</summary>
internal sealed class InstanceImportDialog : DialogWindow
{
    private readonly TextBox _name;
    private readonly TextBlock _nameError;
    /// <summary>返回用户输入的实例名称，最终合法性由导入服务检查。</summary>
    public string InstanceName => _name.Text;

    /// <summary>创建简洁的名称输入、识别详情和确认按钮，不直接访问磁盘。</summary>
    public InstanceImportDialog(InstanceImportPreview preview)
        : base("导入实例", "确认整合包信息，为新实例取一个容易辨认的名字。", 640, 630)
    {
        Body.Children.Add(DialogPrompt.Text("新实例名称（同时作为文件夹名）"));
        _name = new TextBox { Text = preview.SuggestedName, MaxLength = 128, Margin = new Thickness(0, 8, 0, 6) };
        AutomationProperties.SetName(_name, "新实例名称");
        Body.Children.Add(_name);
        _nameError = DialogPrompt.Text("请输入实例名称。");
        _nameError.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        _nameError.Visibility = Visibility.Collapsed;
        _nameError.Margin = new Thickness(0, 0, 0, 8);
        AutomationProperties.SetLiveSetting(_nameError, AutomationLiveSetting.Polite);
        Body.Children.Add(_nameError);
        _name.TextChanged += OnNameChanged;
        var details = $"格式：{preview.Format}\nMinecraft：{preview.MinecraftVersion ?? "未知"}\n" +
            $"包内文件：{preview.EmbeddedFileCount}\n有下载地址的文件声明：{preview.DownloadFileCount}\n" +
            $"待解析的 CurseForge 文件声明：{preview.UnresolvedFileCount}";
        var detailCard = DialogPrompt.DetailCard("识别结果", details);
        detailCard.Margin = new Thickness(0, 12, 0, 16);
        Body.Children.Add(detailCard);
        var completion = preview.Format.Equals("Prism", StringComparison.OrdinalIgnoreCase) ||
             preview.Format.Equals("Modrinth", StringComparison.OrdinalIgnoreCase) ||
             preview.Format.Equals("CurseForge", StringComparison.OrdinalIgnoreCase)
                ? "导入后会自动联网下载缺失的必需模组和支持的运行依赖，并执行完整检查。取消下载会保留已导入实例，可稍后继续补全。"
                : "导入包内文件并保存已有清单；通用 ZIP 可能缺少完整下载信息，可在导入后检查文件。";
        Body.Children.Add(DialogPrompt.Text(completion));
        if (preview.Notes.Count > 0)
        {
            var notes = DialogPrompt.DetailCard("整合包说明", string.Join("\n", preview.Notes));
            notes.Margin = new Thickness(0, 16, 0, 0);
            Body.Children.Add(notes);
        }
        // 导入保留 Enter 提交；带校验的按钮不能使用直接关闭窗口的 AddAccept。
        var import = new Button { Content = "导入实例", IsDefault = true, MinWidth = 108, Margin = new Thickness(8, 0, 0, 0) };
        import.SetResourceReference(StyleProperty, "PrimaryButton");
        import.Click += OnConfirm;
        CancelButton.IsDefault = false;
        Actions.Children.Add(import);
        FocusTarget = _name;
    }

    /// <summary>用户补全名称时收起行内错误，不重置输入或弹出额外窗口。</summary>
    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_name.Text)) _nameError.Visibility = Visibility.Collapsed;
    }

    /// <summary>拦截空名称；其他文件系统名称规则由导入服务统一检查。</summary>
    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _nameError.Visibility = Visibility.Visible;
            _name.Focus();
            return;
        }
        DialogResult = true;
    }
}
