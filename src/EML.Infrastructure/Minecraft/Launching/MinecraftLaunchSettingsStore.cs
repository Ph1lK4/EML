using System.Text.Json;
using EML.Core.Minecraft.Accounts;
using EML.Core.Minecraft.Launching;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Launching;

/// <summary>保存实例本机启动偏好，派生状态目录不进入原包校验和内容备份。</summary>
public sealed class MinecraftLaunchSettingsStore(string launcherRoot) : IMinecraftLaunchSettingsStore, IMinecraftLaunchIdentityStore
{
    internal const string RelativePath = ".eml-runtime/launch/settings.json";
    private const int MaximumSettingsBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    // 记录很小，共享短锁可跨服务对象协调读改写，且不会因实例数量增长而永久积累锁对象。
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>没有记录时使用默认值；损坏或非法记录明确报告，避免静默回退丢失用户设置。</summary>
    public async Task<MinecraftLaunchSettings> LoadAsync(string instancePath, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(instancePath, cancellationToken).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    /// <summary>在共享短锁内读取旧格式或新格式，缺少新增身份字段的记录仍使用原来的默认行为。</summary>
    private async Task<MinecraftLaunchSettings> LoadCoreAsync(string instancePath, CancellationToken cancellationToken)
    {
        var path = SettingsPath(instancePath);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (stream.Length > MaximumSettingsBytes) throw new InvalidDataException("启动设置文件过大。");
            var settings = await JsonSerializer.DeserializeAsync<MinecraftLaunchSettings>(stream, Options, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("启动设置文件为空。");
            MinecraftLaunchValidation.Validate(settings);
            return settings;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (JsonException ex) { throw new InvalidDataException("启动设置 JSON 损坏，请检查设置文件。", ex); }
    }

    /// <summary>显式保存决定下次默认选择，但沿用真实启动历史，不把表单中的历史内容当成已游玩事实。</summary>
    public async Task SaveAsync(string instancePath, MinecraftLaunchSettings settings, CancellationToken cancellationToken = default)
    {
        MinecraftLaunchValidation.Validate(settings);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await LoadCoreAsync(instancePath, cancellationToken).ConfigureAwait(false);
            await WriteCoreAsync(instancePath, settings with { LastLaunchIdentity = previous.LastLaunchIdentity }, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    /// <summary>进程创建后只更新实际使用的身份；不保存本次未确认的 JVM、Java、内存或日志编码草稿。</summary>
    public async Task RememberAsync(string instancePath, MinecraftLaunchIdentity identity, CancellationToken cancellationToken = default)
    {
        MinecraftLaunchValidation.ValidateIdentity(identity);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await LoadCoreAsync(instancePath, cancellationToken).ConfigureAwait(false);
            var updated = previous with
            {
                UseMicrosoftAccount = identity.UseMicrosoftAccount,
                PlayerName = identity.UseMicrosoftAccount ? previous.PlayerName : identity.PlayerName,
                PreferredMicrosoftAccount = identity.UseMicrosoftAccount
                    ? new MinecraftAccount(identity.PlayerId, identity.PlayerName) : previous.PreferredMicrosoftAccount,
                LastLaunchIdentity = identity
            };
            await WriteCoreAsync(instancePath, updated, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    /// <summary>在锁内验证并原子替换同一记录，确保读写限额一致且历史与下次默认身份同时提交。</summary>
    private async Task WriteCoreAsync(string instancePath, MinecraftLaunchSettings settings, CancellationToken cancellationToken)
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
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
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
