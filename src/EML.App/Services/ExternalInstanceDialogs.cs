using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using EML.Core.Minecraft;
using Microsoft.Win32;

namespace EML.App.Services;

/// <summary>使用既有主题窗口引导外部实例发现与复制；不读取账号凭证，也不移动源目录。</summary>
public sealed class ExternalInstanceDialogs(Window owner) : IExternalInstanceDialogs
{
    /// <summary>在模态选择器中异步发现实例；退出 EML 时可取消后台扫描并关闭窗口。</summary>
    public async Task<ExternalMinecraftInstance?> ChooseInstanceAsync(IExternalInstanceDiscovery discovery,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new ExternalInstanceSelectionDialog(discovery, cancellationToken) { Owner = owner };
        var result = dialog.ShowDialog() == true ? dialog.SelectedInstance : null;
        await dialog.WaitForDiscoveryAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>展示实际复制预览，保留可选中复制的源路径和目标根目录。</summary>
    public string? ConfirmCopy(FolderInstanceImportPreview preview)
    {
        var dialog = new FolderInstanceCopyDialog(preview) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.InstanceName : null;
    }
}

/// <summary>先扫描常见位置，再允许添加便携版或自定义目录；共享游戏目录按候选版本分别展示。</summary>
internal sealed class ExternalInstanceSelectionDialog : DialogWindow
{
    private readonly IExternalInstanceDiscovery _discovery;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _cancellation;
    private readonly ObservableCollection<ExternalMinecraftInstance> _instances = [];
    private readonly DataGrid _list;
    private readonly TextBox _details;
    private readonly TextBox _status;
    private readonly Button _refresh;
    private readonly Button _browse;
    private readonly Button _select;
    private bool _scanning;
    private bool _closed;
    private Task _scanTask = Task.CompletedTask;

    /// <summary>返回用户明确选中的候选项，尚未开始分析文件量或复制。</summary>
    internal ExternalMinecraftInstance? SelectedInstance => _list.SelectedItem as ExternalMinecraftInstance;

    /// <summary>搭建发现列表、手选入口和可复制说明；窗口显示后才启动磁盘扫描。</summary>
    internal ExternalInstanceSelectionDialog(IExternalInstanceDiscovery discovery, CancellationToken token)
        : base("从其他启动器导入", "复制已有实例到 EML，无需压缩。原实例保留，不迁移启动器账号。", 880, 710)
    {
        _discovery = discovery;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        _refresh = new Button { Content = "重新发现", Margin = new Thickness(0, 0, 8, 0) };
        _browse = new Button { Content = "选择实例 / 启动器文件夹…" };
        _refresh.Click += OnRefresh;
        _browse.Click += OnBrowse;
        toolbar.Children.Add(_refresh);
        toolbar.Children.Add(_browse);
        Body.Children.Add(toolbar);
        Body.Children.Add(DialogPrompt.Text("自动检查常见位置；便携版或自定义目录请手动选择。一次复制一个实例，共享 .minecraft 请选中需要的版本。"));
        _list = new DataGrid
        {
            ItemsSource = _instances, AutoGenerateColumns = false, CanUserAddRows = false,
            CanUserDeleteRows = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow, Height = 240, Margin = new Thickness(0, 12, 0, 8)
        };
        _list.Columns.Add(new DataGridTextColumn { Header = "实例", Binding = new Binding(nameof(ExternalMinecraftInstance.Name)), MinWidth = 150, Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _list.Columns.Add(new DataGridTextColumn { Header = "来源", Binding = new Binding(nameof(ExternalMinecraftInstance.Launcher)), MinWidth = 120, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _list.Columns.Add(new DataGridTextColumn { Header = "Minecraft", Binding = new Binding(nameof(ExternalMinecraftInstance.MinecraftVersion)), Width = 100 });
        _list.Columns.Add(new DataGridTextColumn { Header = "加载器", Binding = new Binding(nameof(ExternalMinecraftInstance.Loader)), MinWidth = 160, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        AutomationProperties.SetName(_list, "其他启动器中的实例");
        _list.SelectionChanged += OnSelectionChanged;
        Body.Children.Add(_list);
        _details = ReadOnlyText("选中一个实例以查看源路径。", 90);
        AutomationProperties.SetName(_details, "选中实例的源路径");
        Body.Children.Add(_details);
        _status = ReadOnlyText("正在准备发现实例……", 100);
        _status.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetName(_status, "发现结果与原因");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Body.Children.Add(_status);
        _select = AddAccept("下一步：预览复制");
        _select.IsEnabled = false;
        ContentRendered += OnFirstRender;
        Closed += OnDialogClosed;
        _cancellation = token.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (!_closed) Close(); })));
    }

    /// <summary>创建可复制但不可编辑的多行信息框，长路径不被裁掉。</summary>
    private static TextBox ReadOnlyText(string value, double maximumHeight) => new()
    {
        Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = maximumHeight
    };

    /// <summary>只在首次显示执行自动发现，结果仅展示在当前窗口。</summary>
    private async void OnFirstRender(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstRender;
        await StartDiscoveryAsync(null);
    }

    /// <summary>重新扫描默认位置，替换旧的候选结果。</summary>
    private async void OnRefresh(object sender, RoutedEventArgs e) => await StartDiscoveryAsync(null);

    /// <summary>让用户选择便携目录；发现出的实例追加到列表并按源路径和版本去重。</summary>
    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = "选择实例、.minecraft 或启动器数据文件夹", Multiselect = false };
            if (dialog.ShowDialog(this) == true) await StartDiscoveryAsync(dialog.FolderName);
        }
        catch (Exception ex) { if (!_closed) _status.Text = "无法选择文件夹：" + ex.Message; }
    }

    /// <summary>保存当前发现任务，关闭选择器后调用方可等待取消真正结束。</summary>
    private Task StartDiscoveryAsync(string? directory) => _scanTask = DiscoverAsync(directory);

    /// <summary>等待已经取消的后台扫描完成，避免退出 EML 时遗漏活动文件读取。</summary>
    internal Task WaitForDiscoveryAsync() => _scanTask;

    /// <summary>串行执行可取消的发现；关闭窗口后不更新界面，失败以可复制的行内文字说明。</summary>
    private async Task DiscoverAsync(string? directory)
    {
        if (_scanning || _closed) return;
        _scanning = true;
        _refresh.IsEnabled = _browse.IsEnabled = _select.IsEnabled = false;
        _status.Text = "正在只读发现实例……" + (directory is null ? "" : "\n" + directory);
        try
        {
            var result = await _discovery.DiscoverAsync(directory, _lifetime.Token);
            if (_closed) return;
            if (directory is null) _instances.Clear();
            foreach (var instance in result.Instances)
                if (!_instances.Any(existing => string.Equals(existing.SourcePath, instance.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.SelectionId, instance.SelectionId, StringComparison.Ordinal)))
                    _instances.Add(instance);
            _status.Text = (result.Instances.Count == 0 ? "此位置未发现可识别实例。可选择具体实例文件夹后重试。" : $"本次发现 {result.Instances.Count} 个实例。") +
                (directory is null ? "" : "\n所选目录：" + directory) +
                (result.Warnings.Count == 0 ? "" : "\n" + string.Join("\n", result.Warnings));
            if (_list.SelectedItem is null && _instances.Count > 0) _list.SelectedIndex = 0;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed) _status.Text = "无法完成目录发现：" + ex.Message + (directory is null ? "" : "\n" + directory);
        }
        finally
        {
            _scanning = false;
            if (!_closed)
            {
                _refresh.IsEnabled = _browse.IsEnabled = true;
                _select.IsEnabled = SelectedInstance is not null;
            }
            else _lifetime.Dispose();
        }
    }

    /// <summary>选择变化时展示两个独立路径，避免把启动器根目录误认成游戏目录。</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _select.IsEnabled = !_scanning && SelectedInstance is not null;
        _details.Text = SelectedInstance is { } instance
            ? $"实例目录：{instance.SourcePath}\n游戏目录：{instance.GameDirectory}\n版本选择：{instance.SelectionId ?? "独立实例"}"
            : "选中一个实例以查看源路径。";
    }

    /// <summary>取消扫描并释放注册，防止窗口关闭后继续处理结果。</summary>
    private void OnDialogClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _cancellation.Dispose();
        _lifetime.Cancel();
        if (!_scanning) _lifetime.Dispose();
    }
}

/// <summary>复制前展示真实统计并确认源游戏已退出，不把识别成功当作可启动保证。</summary>
internal sealed class FolderInstanceCopyDialog : DialogWindow
{
    private readonly TextBox _name;
    private readonly CheckBox _closedGame;
    private readonly TextBlock _error;
    private readonly TextBox _target;
    private readonly string _destinationRoot;

    /// <summary>名称仍由导入服务校验，不使用界面输入直接操作磁盘。</summary>
    internal string InstanceName => _name.Text;

    /// <summary>按目录复制而非归档解释预览，并明确联网补全与取消边界。</summary>
    internal FolderInstanceCopyDialog(FolderInstanceImportPreview preview)
        : base("复制导入实例", "仅创建 EML 副本；原实例及其存档不会被移动或删除。", 720, 720)
    {
        _destinationRoot = preview.DestinationRoot;
        Body.Children.Add(DialogPrompt.Text("新实例名称"));
        _name = new TextBox { Text = preview.Import.SuggestedName, MaxLength = 128, Margin = new Thickness(0, 6, 0, 12) };
        AutomationProperties.SetName(_name, "复制后新实例名称");
        Body.Children.Add(_name);
        Body.Children.Add(DialogPrompt.DetailCard("识别与复制量",
            $"来源：{preview.Instance.Launcher}\nMinecraft：{preview.Import.MinecraftVersion ?? "未知"} · {preview.Instance.Loader}\n" +
            $"文件：{preview.Import.EmbeddedFileCount:N0} 个\n总量：{preview.TotalBytes / (1024d * 1024 * 1024):0.###} GiB（{preview.TotalBytes:N0} 字节）"));
        Body.Children.Add(new TextBox
        {
            Text = "源实例：" + preview.Instance.SourcePath + "\n游戏目录：" + preview.Instance.GameDirectory,
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 6)
        });
        _target = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetName(_target, "复制目标路径");
        Body.Children.Add(_target);
        _name.TextChanged += OnNameChanged;
        UpdateTarget();
        Body.Children.Add(DialogPrompt.Text("复制完成后自动补全支持的运行依赖，可能联网。取消复制不会新增实例；取消后续补全会保留已复制实例。账号和登录凭证不会迁移，请在 EML 内重新登录。"));
        Body.Children.Add(DialogPrompt.Text("仅导入你信任的实例。复制时计算的 SHA-256 只记录本地文件状态，不代表官方认证或安全检测。"));
        if (preview.Import.Notes.Count > 0)
            Body.Children.Add(DialogPrompt.DetailCard("导入说明", string.Join("\n", preview.Import.Notes)));
        if (preview.SkippedItems.Count > 0)
            Body.Children.Add(DialogPrompt.DetailCard("不复制的内容",
                string.Join("\n", preview.SkippedItems.Take(20)) + (preview.SkippedItems.Count > 20 ? $"\n另有 {preview.SkippedItems.Count - 20} 项。" : "")));
        _closedGame = new CheckBox { Content = DialogPrompt.Text("我已关闭此实例的游戏，并暂停原启动器的更新和文件操作。"), Margin = new Thickness(0, 14, 0, 8) };
        Body.Children.Add(_closedGame);
        _error = DialogPrompt.Text("");
        _error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        Body.Children.Add(_error);
        var copy = new Button { Content = "复制并导入", MinWidth = 112 };
        copy.SetResourceReference(StyleProperty, "PrimaryButton");
        copy.Click += OnConfirm;
        Actions.Children.Add(copy);
        FocusTarget = _name;
    }

    /// <summary>名称修改只更新路径预览，真正路径限制在导入服务中再次执行。</summary>
    private void OnNameChanged(object sender, TextChangedEventArgs e) => UpdateTarget();

    /// <summary>以文本展示目标，非法名称不会在预览阶段产生文件系统访问。</summary>
    private void UpdateTarget() => _target.Text = "复制目标：" + _destinationRoot.TrimEnd('\\', '/') + "\\" + _name.Text;

    /// <summary>明确确认游戏已关闭和新名称非空，其他合法性由服务端再次检查。</summary>
    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { _error.Text = "请输入新实例名称。"; _name.Focus(); return; }
        if (_closedGame.IsChecked != true) { _error.Text = "请先关闭游戏并确认，避免复制到正在变化的存档。"; return; }
        DialogResult = true;
    }
}
