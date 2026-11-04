using System.Text.Json;
using EML.Core.Minecraft;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Prism;

/// <summary>记录 EML 生成的启动配置和本机库基线；原导入清单仍保持原样。</summary>
internal sealed record PrismRuntimeState
{
    public int SchemaVersion { get; init; } = 1;
    public string Fingerprint { get; init; } = "";
    public string LaunchProfilePath { get; init; } = "";
    public IReadOnlyList<InstanceFileRequirement> GeneratedFiles { get; init; } = [];
}

/// <summary>读写独立运行准备状态，并在源组件改变后使旧状态失效。</summary>
internal static class PrismRuntimeStateStore
{
    internal const string RelativePath = ".eml-runtime/state.json";
    internal const string ModrinthRelativePath = ".eml-runtime/modrinth/state.json";
    internal const string CurseForgeRelativePath = ".eml-runtime/curseforge/state.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>只读取当前组件指纹对应的状态；不存在或旧状态返回 null，损坏状态明确抛出错误。</summary>
    internal static PrismRuntimeState? Read(PrismRuntimePlan plan)
    {
        var path = InstanceImportPaths.Within(plan.InstancePath, StatePath(plan));
        InstanceImportPaths.EnsureNoLinks(path);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 8 * 1024 * 1024) throw new InvalidDataException("运行准备状态超过大小限制。");
            var state = JsonSerializer.Deserialize<PrismRuntimeState>(input, Options) ?? throw new InvalidDataException("运行准备状态为空。");
            Validate(state);
            return state.Fingerprint == plan.Fingerprint ? state : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>全部生成操作成功后原子保存自有状态，不修改原 eml-instance.json。</summary>
    internal static async Task WriteAsync(PrismRuntimePlan plan, PrismRuntimeState state, CancellationToken token)
    {
        Validate(state);
        var path = InstanceImportPaths.Within(plan.InstancePath, StatePath(plan));
        InstanceImportPaths.EnsureNoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("运行准备状态超过大小限制。");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await output.WriteAsync(bytes, token);
            token.ThrowIfCancellationRequested();
            InstanceImportPaths.EnsureNoLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { InstanceImportPaths.EnsureNoLinks(temporary); File.Delete(temporary); } }
    }

    /// <summary>按来源隔离安装状态，并保持既有 Prism 状态位置不变。</summary>
    private static string StatePath(PrismRuntimePlan plan) => plan.SourceFormat switch
    {
        "Prism" => RelativePath,
        "Modrinth" => ModrinthRelativePath,
        "CurseForge" => CurseForgeRelativePath,
        _ => throw new InvalidDataException("不支持的运行计划来源。")
    };

    /// <summary>检查版本、指纹及生成文件的安全相对路径，拒绝清单自身和重复目标。</summary>
    private static void Validate(PrismRuntimeState state)
    {
        if (state.SchemaVersion != 1 || state.Fingerprint.Length != 64 || !state.Fingerprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("无效的运行准备状态指纹。");
        InstanceImportPaths.Relative(state.LaunchProfilePath);
        InstanceManifestStore.Validate(new InstanceImportManifest { Files = state.GeneratedFiles });
        if (!state.GeneratedFiles.Any(file => file.Path == state.LaunchProfilePath))
            throw new InvalidDataException("运行准备状态未记录启动配置。");
    }
}
