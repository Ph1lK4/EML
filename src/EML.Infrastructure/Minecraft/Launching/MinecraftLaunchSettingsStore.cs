using System.Text.Json;
using EML.Core.Minecraft.Launching;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Launching;

/// <summary>保存实例本机启动偏好，派生状态目录不进入原包校验和内容备份。</summary>
public sealed class MinecraftLaunchSettingsStore(string launcherRoot) : IMinecraftLaunchSettingsStore
{
    internal const string RelativePath = ".eml-runtime/launch/settings.json";
    private const int MaximumSettingsBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>没有记录时使用默认值；损坏或非法记录明确报告，避免静默回退丢失用户设置。</summary>
    public async Task<MinecraftLaunchSettings> LoadAsync(string instancePath, CancellationToken cancellationToken = default)
    {
        var path = SettingsPath(instancePath);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (stream.Length > MaximumSettingsBytes) throw new InvalidDataException("启动设置文件过大。");
            var settings = await JsonSerializer.DeserializeAsync<MinecraftLaunchSettings>(stream, Options, cancellationToken)
                ?? throw new InvalidDataException("启动设置文件为空。");
            MinecraftLaunchValidation.Validate(settings);
            return settings;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (JsonException ex) { throw new InvalidDataException("启动设置 JSON 损坏，请检查设置文件。", ex); }
    }

    /// <summary>先验证设置，再用临时文件原子替换本服务的记录；不修改导入清单或运行配置。</summary>
    public async Task SaveAsync(string instancePath, MinecraftLaunchSettings settings, CancellationToken cancellationToken = default)
    {
        MinecraftLaunchValidation.Validate(settings);
        cancellationToken.ThrowIfCancellationRequested();
        // 以实际 JSON 字节应用读写一致的上限，避免 Unicode 转义后保存出无法读取的设置。
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
        if (bytes.Length > MaximumSettingsBytes) throw new InvalidDataException("启动设置文件过大，请缩短 Java 路径或附加 JVM 参数。");
        var path = SettingsPath(instancePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                await output.WriteAsync(bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _ = SettingsPath(instancePath);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            InstanceImportPaths.EnsureNoLinks(temporary);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>限制在启动器直接子实例并拒绝链接，避免保存到其他目录。</summary>
    private string SettingsPath(string instancePath)
    {
        var root = EmbeddedFileBaseline.ValidateInstance(launcherRoot, instancePath);
        var path = InstanceImportPaths.Within(root, RelativePath);
        InstanceImportPaths.EnsureNoLinks(path);
        return path;
    }
}
