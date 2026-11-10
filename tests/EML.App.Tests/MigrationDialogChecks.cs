using System.Windows;
using System.Windows.Controls;
using EML.App.Services;
using EML.Core.Minecraft.Migration;

namespace EML.App.Tests;

/// <summary>在已有 STA 与主题环境中检查真实迁移向导，不打开窗口、不调用迁移服务。</summary>
internal static class MigrationDialogChecks
{
    private static readonly GtnhInstanceInfo Old = new(@"C:\preview\GTNH-old", "GTNH 2.8.4 · 长期存档", @"C:\preview\GTNH-old\.minecraft", "2.8.4");
    private static readonly GtnhInstanceInfo New = new(@"C:\preview\GTNH-new", "GTNH 2.9.0 · 新实例", @"C:\preview\GTNH-new\.minecraft", "2.9.0");

    /// <summary>由现有 smoke 测试提供离屏渲染；每个未显示窗口均在断言后关闭。</summary>
    internal static void Run(Action<DialogWindow, string> render)
    {
        var owner = new Window();
        try
        {
            VerifySourceSelection(owner, render);
            VerifyGroupRequirements(owner, render);
            VerifyMigrationDecision(owner, render);
        }
        finally { owner.Close(); }
    }

    /// <summary>空列表无法继续；来源切换及时更新版本和完整路径，返回真实选择与服务器模式。</summary>
    private static void VerifySourceSelection(Window owner, Action<DialogWindow, string> render)
    {
        var empty = Create(owner, window =>
        {
            Assert.False(Accept(window).IsEnabled);
            Assert.Null(Assert.Single(Descendants(window.Body).OfType<ComboBox>()).SelectedItem);
            return false;
        });
        Assert.Null(empty.ChooseSource(New, []));

        var alternate = Old with { Name = "GTNH 2.8.4 · 服务器记录", GameDirectory = @"D:\另一实例\GTNH 2.8.4\.minecraft" };
        var service = Create(owner, window =>
        {
            Assert.True(Accept(window).IsEnabled);
            var source = Assert.Single(Descendants(window.Body).OfType<ComboBox>());
            Assert.Same(Old, source.SelectedItem);
            Assert.Contains(Descendants(window.Body).OfType<TextBlock>(), item => item.Text.Contains(Old.GameDirectory));
            source.SelectedItem = alternate;
            Assert.Contains(Descendants(window.Body).OfType<TextBlock>(), item =>
                item.Text.Contains(alternate.GameDirectory) && item.Text.Contains(alternate.Version));
            Assert.Contains(Descendants(window.Body).OfType<TextBox>(), item => item.Text.Contains(New.GameDirectory));
            Assert.Single(Descendants(window.Body).OfType<CheckBox>()).IsChecked = true;
            render(window, "dialog-migration-source.png");
            return true;
        });
        var choice = service.ChooseSource(New, [Old, alternate]);
        Assert.NotNull(choice);
        Assert.Same(alternate, choice.Source);
        Assert.Equal(GtnhMigrationMode.ServerClient, choice.Mode);
    }

    /// <summary>新版试运行、关闭游戏、迁移范围与额外模组风险共同控制下一步，取消模组后清除旧风险确认。</summary>
    private static void VerifyGroupRequirements(Window owner, Action<DialogWindow, string> render)
    {
        MigrationGroup[] groups =
        [
            new("world:长期世界", "长期世界存档", true),
            new("language", "中文语言偏好", false),
            new("mod:mods/extra.jar", "自加建筑辅助模组", true)
        ];
        var service = Create(owner, window =>
        {
            var next = Accept(window);
            var boxes = Descendants(window.Body).OfType<CheckBox>().ToArray();
            var world = Box(boxes, "长期世界存档");
            var language = Box(boxes, "中文语言偏好");
            var mod = Box(boxes, "自加建筑辅助模组");
            var tested = Box(boxes, "新版实例已补全依赖并成功试运行");
            var closed = Assert.Single(boxes, box => ContentText(box).StartsWith("旧版和新版的游戏都已正常退出"));
            var risk = Assert.Single(boxes, box => ContentText(box).StartsWith("我确认勾选的是自己添加的模组"));
            Assert.True(world.IsChecked == true);
            Assert.False(language.IsChecked == true);
            Assert.False(mod.IsChecked == true);
            Assert.Equal(Visibility.Collapsed, risk.Visibility);
            Assert.False(next.IsEnabled);
            tested.IsChecked = true;
            Assert.False(next.IsEnabled);
            closed.IsChecked = true;
            Assert.True(next.IsEnabled);
            world.IsChecked = false;
            Assert.False(next.IsEnabled);
            language.IsChecked = true;
            Assert.True(next.IsEnabled);
            mod.IsChecked = true;
            Assert.Equal(Visibility.Visible, risk.Visibility);
            Assert.False(next.IsEnabled);
            risk.IsChecked = true;
            Assert.True(next.IsEnabled);
            tested.IsChecked = false;
            Assert.False(next.IsEnabled);
            tested.IsChecked = true;
            mod.IsChecked = false;
            Assert.Equal(Visibility.Collapsed, risk.Visibility);
            Assert.False(risk.IsChecked == true);
            Assert.True(next.IsEnabled);
            mod.IsChecked = true;
            Assert.False(next.IsEnabled);
            risk.IsChecked = true;
            Assert.Single(Descendants(window.Body).OfType<Expander>()).IsExpanded = true;
            render(window, "dialog-migration-groups.png");
            return true;
        });
        var selection = service.ChooseGroups(Old, New, groups);
        Assert.NotNull(selection);
        Assert.Equal(new[] { "language", "mod:mods/extra.jar" }, selection.Groups);
        Assert.True(selection.TargetTested && selection.GamesClosed && selection.ExtraModsRiskAccepted);
        Assert.Null(Create(owner, _ => false).ChooseGroups(Old, New, groups));
    }

    /// <summary>确认但不覆盖返回 false，取消返回 null；只有显式勾选冲突覆盖才返回 true。</summary>
    private static void VerifyMigrationDecision(Window owner, Action<DialogWindow, string> render)
    {
        var plan = new InstanceMigrationPlan(new(Old.InstancePath, New.InstancePath, GtnhMigrationMode.FullClient,
            ["options"], true, true), Old, New,
            [new("options", "options.txt", "options.txt", 128, new string('a', 64), 128, new string('a', 64),
                120, new string('b', 64), MigrationFileAction.Conflict) { Detail = "默认保留新版；勾选后先备份，再使用旧版个人设置。" }],
            ["旧实例保持不变，迁移后请先验证存档。"], "preview-only");
        (bool? Response, bool Overwrite, bool? Expected)[] cases =
        [
            (null, false, null), (false, false, null), (true, false, false), (true, true, true)
        ];
        foreach (var item in cases)
        {
            var service = Create(owner, window =>
            {
                var grid = Assert.Single(Descendants(window.Body).OfType<DataGrid>());
                Assert.True(grid.IsReadOnly);
                Assert.Equal(5, grid.Columns.Count);
                var replace = Assert.Single(Descendants(window.Body).OfType<CheckBox>());
                Assert.False(replace.IsChecked == true);
                Assert.True(replace.IsEnabled);
                replace.IsChecked = item.Overwrite;
                if (item.Response == true && !item.Overwrite)
                {
                    render(window, "dialog-migration-preview.png");
                    // 实际长说明应撑开行高，不能被主页面列表的固定行高裁切。
                    var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(0));
                    Assert.True(row.ActualHeight > 42);
                }
                return item.Response;
            });
            Assert.Equal(item.Expected, service.ConfirmMigration(plan));
        }
        var withoutConflicts = plan with { Files = [plan.Files[0] with { Action = MigrationFileAction.Copy }] };
        Assert.False(Create(owner, window =>
        {
            Assert.False(Assert.Single(Descendants(window.Body).OfType<CheckBox>()).IsEnabled);
            return true;
        }).ConfirmMigration(withoutConflicts));
    }

    /// <summary>注入只读观察回调并释放窗口，不调用 ShowDialog 或生成系统窗口句柄。</summary>
    private static InstanceMigrationDialogs Create(Window owner, Func<DialogWindow, bool?> inspect) => new(owner, window =>
    {
        try
        {
            Assert.False(window.IsVisible);
            Assert.True(window.CancelButton.IsDefault);
            Assert.True(window.CancelButton.IsCancel);
            return inspect(window);
        }
        finally { window.Close(); }
    });

    /// <summary>定位实际确认按钮，避免测试重新构造业务门槛。</summary>
    private static Button Accept(DialogWindow window) => Assert.Single(window.Actions.Children.OfType<Button>(), button => !ReferenceEquals(button, window.CancelButton));

    /// <summary>按用户可见文案找到对应复选框。</summary>
    private static CheckBox Box(IEnumerable<CheckBox> boxes, string text) => Assert.Single(boxes, box => ContentText(box) == text);

    /// <summary>复选框使用可换行的正文控件承载可读名称。</summary>
    private static string ContentText(CheckBox box) => Assert.IsType<TextBlock>(box.Content).Text;

    /// <summary>逻辑树包含未展开模组区域，无需创建可见窗口即可检验默认状态与勾选响应。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants(child)) yield return item;
    }
}
