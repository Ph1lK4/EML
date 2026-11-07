namespace EML.Core.Minecraft.Launching;

/// <summary>每个实例独立保存的本机启动设置；不改变导入清单及官方完整性依据。</summary>
public sealed record MinecraftLaunchSettings
{
    public string PlayerName { get; init; } = "Steve";
    /// <summary>空值自动选择符合实例要求的 Java，非空表示用户指定的 java.exe。</summary>
    public string? JavaPath { get; init; }
    public int MinMemoryMb { get; init; } = 1024;
    public int MaxMemoryMb { get; init; } = 8192;
    public string AdditionalJvmArguments { get; init; } = "";
}

/// <summary>验证后的启动参数列表；参数作为数据直接传给 Java，不经过命令行解释器。</summary>
public sealed record MinecraftLaunchPlan(string InstancePath, string GameDirectory, string JavaPath,
    int JavaMajorVersion, IReadOnlyList<string> Arguments)
{
    /// <summary>补全生成并验证的本机库目录，仅在实际运行时创建各库所需的临时子目录。</summary>
    public required string NativesDirectory { get; init; }
}

public enum MinecraftLaunchStage { Preparing, Running, Output }

/// <summary>启动阶段、进程输出及日志位置，界面只保留有限数量的日志行。</summary>
public sealed record MinecraftLaunchProgress(MinecraftLaunchStage Stage, string Message,
    bool StandardError = false, string? LogPath = null);

/// <summary>进程退出结果；成功创建进程不代表已经进入游戏。</summary>
public sealed record MinecraftLaunchResult(bool Started, int? ExitCode, bool IsCanceled, string? LogPath);

/// <summary>统一启动已补全实例；调用方通过取消令牌停止本次检查或自己启动的游戏进程。</summary>
public interface IMinecraftGameLauncher
{
    /// <summary>只读验证启动配置、关键运行路径及 Java，不扫描全部实例文件或写入文件。</summary>
    Task<MinecraftLaunchPlan> PrepareAsync(string instancePath, MinecraftLaunchSettings settings,
        IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>快速准备并启动 Java，旧 ZIP 可首次生成本地配置；等待退出或取消，不执行包内脚本。</summary>
    Task<MinecraftLaunchResult> RunAsync(string instancePath, MinecraftLaunchSettings settings,
        IProgress<MinecraftLaunchProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>读取和保存实例本机启动偏好，原始来源和运行生成状态不因此失效。</summary>
public interface IMinecraftLaunchSettingsStore
{
    Task<MinecraftLaunchSettings> LoadAsync(string instancePath, CancellationToken cancellationToken = default);
    Task SaveAsync(string instancePath, MinecraftLaunchSettings settings, CancellationToken cancellationToken = default);
}
