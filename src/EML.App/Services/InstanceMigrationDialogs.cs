using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EML.Core.Minecraft.Migration;

namespace EML.App.Services;

/// <summary>分步显示迁移方向、范围和文件预览，任何取消均发生在写入之前。</summary>
public sealed class InstanceMigrationDialogs(Window owner) : IInstanceMigrationDialogs
{
    /// <summary>目标固定为列表选中的新版；来源仅显示已识别的其他 GTNH 实例。</summary>
    public MigrationSourceChoice? ChooseSource(GtnhInstanceInfo target, IReadOnlyList<GtnhInstanceInfo> sources)
    {
        var (window, body, buttons) = Create("从旧 GTNH 实例迁入数据", 700, 420);
        body.Children.Add(Text($"接收实例：{target.Name}（{target.Version}）\n{target.GameDirectory}\n\n选择包含存档和本地记录的旧实例："));
        var source = new ComboBox { Margin = new(0, 12, 0, 12), ItemsSource = sources,
            DisplayMemberPath = nameof(GtnhInstanceInfo.Name), SelectedIndex = 0 };
        body.Children.Add(source);
        var sourcePath = Text("");
        // 来源路径始终显示，避免仅凭相似名称选反迁移方向。
        source.SelectionChanged += (_, _) => sourcePath.Text = source.SelectedItem is GtnhInstanceInfo item
            ? $"GTNH {item.Version}\n{item.GameDirectory}" : "";
        if (sources.Count > 0) sourcePath.Text = $"GTNH {sources[0].Version}\n{sources[0].GameDirectory}";
        body.Children.Add(sourcePath);
        var server = new CheckBox { Content = "我主要游玩服务器（默认只选地图、NEI 全局记录和语言）", Margin = new(0, 18, 0, 0) };
        body.Children.Add(server);
        var next = Accept(window, buttons, "下一步"); next.IsEnabled = sources.Count > 0;
        return window.ShowDialog() == true && source.SelectedItem is GtnhInstanceInfo selected
            ? new(selected, server.IsChecked == true ? GtnhMigrationMode.ServerClient : GtnhMigrationMode.FullClient) : null;
    }

    /// <summary>玩家数据与额外模组分开选择；模组默认不选，需单独确认来源及兼容风险。</summary>
    public MigrationSelection? ChooseGroups(GtnhInstanceInfo source, GtnhInstanceInfo target, IReadOnlyList<MigrationGroup> groups)
    {
        var (window, body, buttons) = Create("选择迁移内容", 780, 650);
        body.Children.Add(Text($"{source.Name}（{source.Version}） → {target.Name}（{target.Version}）\n" +
            "同名世界另存；其他冲突默认保留新版。语言项只迁移偏好，不复制旧版汉化包。"));
        var boxes = groups.Select(group => (Group: group, Box: new CheckBox
        { Content = Text(group.Name), IsChecked = !GtnhExtraModPolicy.IsGroup(group.Id) && group.Recommended, Margin = new(0, 5, 0, 5) })).ToArray();
        var list = new StackPanel { Margin = new(0, 10, 0, 10) };
        foreach (var item in boxes.Where(item => !GtnhExtraModPolicy.IsGroup(item.Group.Id))) list.Children.Add(item.Box);
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var modBoxes = boxes.Where(item => GtnhExtraModPolicy.IsGroup(item.Group.Id)).ToArray();
        if (modBoxes.Length > 0)
        {
            var mods = new StackPanel();
            mods.Children.Add(Text("仅勾选自己额外添加的模组。Prism 导出包内原有模组无法自动区分官方与自定义，请不要整包迁移旧模组。"));
            var warning = Text(GtnhExtraModPolicy.Warning); warning.Foreground = System.Windows.Media.Brushes.DarkOrange;
            mods.Children.Add(warning);
            var modList = new StackPanel();
            foreach (var item in modBoxes) modList.Children.Add(item.Box);
            mods.Children.Add(new ScrollViewer { Content = modList, MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            body.Children.Add(new Expander { Header = $"可选：迁移自己添加的模组（{modBoxes.Length} 个候选，默认不选）",
                Content = mods, IsExpanded = false, Margin = new(0, 8, 0, 12) });
        }
        var modRisk = new CheckBox { Content = Text("我确认勾选的是自己添加的模组，并已了解它们可能与新版不兼容、导致崩溃或存档异常"),
            Visibility = Visibility.Collapsed, Margin = new(0, 6, 0, 12) };
        body.Children.Add(modRisk);
        body.Children.Add(Text("版本跨度较大时应逐个大版本进入存档再退出。迁移完成不代表存档兼容，也不会自动确认缺失方块。"));
        var tested = new CheckBox { Content = "新版实例已补全依赖并成功试运行", Margin = new(0, 12, 0, 6) };
        var closed = new CheckBox { Content = "旧版和新版的游戏都已正常退出（包括其他启动器启动的游戏）", Margin = new(0, 0, 0, 6) };
        body.Children.Add(tested); body.Children.Add(closed);
        var next = Accept(window, buttons, "生成文件预览"); next.IsEnabled = false;
        // 两项确认和至少一项范围均明确选中，才能进行预览。
        void Refresh(object? sender, RoutedEventArgs args)
        {
            var hasMods = modBoxes.Any(item => item.Box.IsChecked == true);
            modRisk.Visibility = hasMods ? Visibility.Visible : Visibility.Collapsed;
            if (!hasMods) modRisk.IsChecked = false;
            next.IsEnabled = tested.IsChecked == true && closed.IsChecked == true && boxes.Any(item => item.Box.IsChecked == true) &&
                (!hasMods || modRisk.IsChecked == true);
        }
        foreach (var box in boxes.Select(item => item.Box).Append(tested).Append(closed).Append(modRisk))
        { box.Checked += Refresh; box.Unchecked += Refresh; }
        return window.ShowDialog() == true ? new(boxes.Where(item => item.Box.IsChecked == true)
            .Select(item => item.Group.Id).ToArray(), tested.IsChecked == true, closed.IsChecked == true)
            { ExtraModsRiskAccepted = modRisk.IsChecked == true } : null;
    }

    /// <summary>完整列出文件动作和大小；覆盖开关仅影响显式列出的冲突，世界始终另存。</summary>
    public bool? ConfirmMigration(InstanceMigrationPlan plan)
    {
        var (window, body, buttons) = Create("确认迁移预览", 940, 670);
        var conflicts = plan.Files.Count(file => file.Action == MigrationFileAction.Conflict);
        var bytes = plan.Files.Where(file => file.Action is not (MigrationFileAction.Identical or MigrationFileAction.Skip)).Sum(file => file.Size);
        body.Children.Add(Text($"{plan.Source.Name} → {plan.Target.Name}\n{plan.Files.Count} 个文件，{conflicts} 个冲突，最多复制 {bytes / 1048576d:F1} MiB。覆盖前会备份新版原文件。"));
        var grid = new DataGrid { ItemsSource = plan.Files, AutoGenerateColumns = false, IsReadOnly = true,
            CanUserAddRows = false, Height = 330, Margin = new(0, 12, 0, 12), EnableRowVirtualization = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "旧版文件", Binding = new Binding(nameof(MigrationFile.SourcePath)), Width = new(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "新版目标", Binding = new Binding(nameof(MigrationFile.TargetPath)), Width = new(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "动作", Binding = new Binding(nameof(MigrationFile.ActionDisplay)), Width = 150 });
        grid.Columns.Add(new DataGridTextColumn { Header = "字节", Binding = new Binding(nameof(MigrationFile.Size)), Width = 90 });
        var detailStyle = new Style(typeof(TextBlock));
        detailStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        grid.Columns.Add(new DataGridTextColumn { Header = "说明", Binding = new Binding(nameof(MigrationFile.Detail)),
            Width = 220, ElementStyle = detailStyle });
        body.Children.Add(grid);
        body.Children.Add(Text(string.Join("\n", plan.Notes)));
        var replace = new CheckBox { Content = "覆盖上面列出的冲突文件（使用旧版个人数据替换新版文件）",
            IsEnabled = conflicts > 0, IsChecked = false, Margin = new(0, 12, 0, 0) };
        body.Children.Add(replace);
        Accept(window, buttons, "开始迁移");
        return window.ShowDialog() == true ? replace.IsChecked == true : null;
    }

    /// <summary>默认取消；明确提醒恢复仅适用于未被后续游玩修改的数据。</summary>
    public bool ConfirmRestore(string instanceName, MigrationRecoveryInfo recovery) => MessageBox.Show(owner,
        $"恢复 {instanceName} 在 {recovery.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} 迁移前的文件？\n\n" +
        "请先正常退出此实例的游戏。若文件在迁移后再次变化，会保留当前文件并拒绝自动覆盖。\n" +
        "恢复只涉及这次迁移，旧实例不受影响。",
        "恢复上次迁移", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    /// <summary>建立可滚动内容和固定底部按钮，较小屏幕仍可查看所有确认信息。</summary>
    private (Window Window, StackPanel Body, StackPanel Buttons) Create(string title, double width, double height)
    {
        var window = new Window { Owner = owner, Title = title, Width = Math.Min(width, SystemParameters.WorkArea.Width),
            Height = Math.Min(height, SystemParameters.WorkArea.Height), MinWidth = 500, MinHeight = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var dock = new DockPanel { Margin = new(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
        var body = new StackPanel();
        dock.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Content = dock;
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Padding = new(16, 7, 16, 7), Margin = new(0, 0, 10, 0) });
        return (window, body, buttons);
    }

    /// <summary>确认按钮只关闭对话框并返回选择，磁盘写入由 ViewModel 调用服务。</summary>
    private static Button Accept(Window window, Panel buttons, string title)
    {
        var button = new Button { Content = title, Padding = new(16, 7, 16, 7) };
        button.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(button); return button;
    }
    /// <summary>说明文本支持自动换行。</summary>
    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap };
}
