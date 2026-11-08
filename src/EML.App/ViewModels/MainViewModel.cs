namespace EML.App.ViewModels;

/// <summary>
/// 主窗口数据入口，组合两个页面的 ViewModel。
/// 类名后的参数是 C# 主构造函数，由窗口传入页面对象。
/// </summary>
/// <param name="java">Java 检测页面的数据与命令。</param>
/// <param name="instances">本地实例页面的数据与命令。</param>
public sealed class MainViewModel(JavaSettingsViewModel java, InstancesViewModel instances, MinecraftAccountViewModel? accounts = null)
{
    /// <summary>Java 标签页的数据来源。</summary>
    public JavaSettingsViewModel Java { get; } = java;
    /// <summary>本地实例标签页的数据来源。</summary>
    public InstancesViewModel Instances { get; } = instances;
    /// <summary>账号属于启动器，独立于任意实例的导入、备份和迁移。</summary>
    public MinecraftAccountViewModel? Accounts { get; } = accounts;
}
