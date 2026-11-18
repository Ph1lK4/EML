using System.Text.Json.Serialization;
using EML.Core.Minecraft.Accounts;

namespace EML.Core.Minecraft.Launching;

/// <summary>只控制进程日志读取；自动优先严格 UTF-8，不能解码时回退到系统代码页。</summary>
public enum MinecraftLogEncoding { Auto, Utf8, SystemDefault }

/// <summary>每个实例独立保存的本机启动设置；不改变导入清单及官方完整性依据。</summary>
public sealed record MinecraftLaunchSettings
{
    public string PlayerName { get; init; } = "Steve";
    /// <summary>仅保存身份选择，不保存账号令牌；旧设置默认仍使用离线身份。</summary>
    public bool UseMicrosoftAccount { get; init; }
    /// <summary>此实例明确选择的正版公开身份；空值兼容旧设置，令牌仍由独立账号服务管理。</summary>
    public MinecraftAccount? PreferredMicrosoftAccount { get; init; }
    /// <summary>最近一次实际创建游戏进程使用的公开身份；手动保存参数不伪造游玩记录。</summary>
    public MinecraftLaunchIdentity? LastLaunchIdentity { get; init; }
    /// <summary>空值自动选择符合实例要求的 Java，非空表示用户指定的 java.exe。</summary>
    public string? JavaPath { get; init; }
    public int MinMemoryMb { get; init; } = 1024;
    public int MaxMemoryMb { get; init; } = 8192;
    public string AdditionalJvmArguments { get; init; } = "";
    /// <summary>每个实例可单独覆盖日志编码，不改变 Java 文件编码或整合包文件。</summary>
    public MinecraftLogEncoding LogEncoding { get; init; } = MinecraftLogEncoding.Auto;
}

/// <summary>仅记录模式、玩家名、UUID 与进程启动时间；不包含凭证，也不证明游戏已完成加载。</summary>
public sealed record MinecraftLaunchIdentity(bool UseMicrosoftAccount, string PlayerName, string PlayerId,
    DateTimeOffset StartedAtUtc);

/// <summary>验证后的启动参数列表；参数作为数据直接传给 Java，不经过命令行解释器。</summary>
public sealed record MinecraftLaunchPlan(string InstancePath, string GameDirectory, string JavaPath,
    int JavaMajorVersion, [property: JsonIgnore] IReadOnlyList<string> Arguments)
{
    /// <summary>补全生成并验证的本机库目录，仅在实际运行时创建各库所需的临时子目录。</summary>
    public required string NativesDirectory { get; init; }
    /// <summary>只在执行层用于脱敏和参数隔离，禁止序列化到实例或日志。</summary>
    [JsonIgnore] public IReadOnlyList<string> SensitiveValues { get; init; } = [];
    /// <summary>与本次启动设置一致的输出解码方式；标准输出和错误输出分别维护解码状态。</summary>
    public MinecraftLogEncoding LogEncoding { get; init; } = MinecraftLogEncoding.Auto;
    /// <summary>参数实际使用的公开身份；准备阶段时间为空，执行层在进程创建后填写真实时间。</summary>
    public MinecraftLaunchIdentity? Identity { get; init; }
    public override string ToString() => $"Minecraft launch: Java {JavaMajorVersion}, {InstancePath}";
}

public enum MinecraftLaunchStage { Preparing, Running, Output }

/// <summary>启动阶段、进程输出及日志位置，界面只保留有限数量的日志行。</summary>
public sealed record MinecraftLaunchProgress(MinecraftLaunchStage Stage, string Message,
    bool StandardError = false, string? LogPath = null)
{
    /// <summary>仅 Running 阶段提供实际启动身份，防止把准备成功误记为已使用。</summary>
    public MinecraftLaunchIdentity? Identity { get; init; }
    /// <summary>身份记录写入失败不终止游戏，由界面单独显示非致命提示。</summary>
    public string? IdentityPersistenceWarning { get; init; }
}

/// <summary>区分进程结束、明确失败和用户取消；不根据退出码推断游戏已成功加载。</summary>
public enum MinecraftLaunchOutcome { ProcessExited, Failed, Canceled }

/// <summary>本次输出中的明确致命错误；证据由执行层先脱敏并限制长度，不推测底层故障原因。</summary>
public sealed record MinecraftLaunchFailure(string Code, string Summary, string Evidence);

/// <summary>进程退出结果；保留原构造参数兼容调用方，零退出码不能覆盖明确的失败证据。</summary>
public sealed record MinecraftLaunchResult(bool Started, int? ExitCode, bool IsCanceled, string? LogPath)
{
    public MinecraftLaunchFailure? Failure { get; init; }

    /// <summary>主动取消优先；无进程、未知/非零退出码或明确致命日志均不作为正常完成。</summary>
    public MinecraftLaunchOutcome Outcome => IsCanceled ? MinecraftLaunchOutcome.Canceled
        : !Started || ExitCode != 0 || Failure is not null ? MinecraftLaunchOutcome.Failed
        : MinecraftLaunchOutcome.ProcessExited;
}

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

/// <summary>执行层仅在进程创建后更新身份偏好，不自动保存未确认的 Java、内存或 JVM 草稿。</summary>
public interface IMinecraftLaunchIdentityStore
{
    Task RememberAsync(string instancePath, MinecraftLaunchIdentity identity, CancellationToken cancellationToken = default);
}
