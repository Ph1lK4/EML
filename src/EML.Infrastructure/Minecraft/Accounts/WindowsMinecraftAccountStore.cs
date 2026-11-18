using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EML.Core.Minecraft.Accounts;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.Accounts;

/// <summary>仅供加密存储序列化；不包含短期 access token，不属于实例备份。</summary>
internal sealed class StoredMinecraftAccount
{
    public int SchemaVersion { get; init; } = 1;
    public required string ClientId { get; init; }
    public required MinecraftAccount Account { get; init; }
    public required string RefreshToken { get; init; }
    public override string ToString() => "Stored Minecraft account [redacted]";
}

/// <summary>新版凭证集合仍整体加密；默认账号可为空，移除账号不会自动选择另一身份。</summary>
internal sealed class StoredMinecraftAccounts
{
    public int SchemaVersion { get; init; } = 2;
    public IReadOnlyList<StoredMinecraftAccount> Accounts { get; init; } = [];
    public string? ActiveAccountId { get; init; }
    public override string ToString() => "Stored Minecraft accounts [redacted]";
}

internal interface IMinecraftAccountStore
{
    Task<StoredMinecraftAccounts?> LoadAsync(CancellationToken token);
    Task SaveAsync(StoredMinecraftAccounts accounts, CancellationToken token);
    Task DeleteAsync(CancellationToken token);
}

/// <summary>使用当前 Windows 用户的 DPAPI 加密，凭证放在 LocalAppData，和实例文件完全分离。</summary>
internal sealed class WindowsMinecraftAccountStore(string path) : IMinecraftAccountStore
{
    internal const int MaximumAccounts = 32;
    private const int MaximumBytes = 4 * 1024 * 1024;

    /// <summary>仅在当前用户可解密时加载；损坏数据保留，不以空账号覆盖。</summary>
    public async Task<StoredMinecraftAccounts?> LoadAsync(CancellationToken token)
    {
        InstanceImportPaths.EnsureNoLinks(path);
        byte[] encrypted;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (stream.Length is <= 0 or > MaximumBytes) throw new MinecraftAuthenticationException("本机账号凭证文件大小无效，请检查凭证文件，不要覆盖原文件。");
            encrypted = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(encrypted, token).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var plaintext = Protect(encrypted, decrypt: true);
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("SchemaVersion", out var version) || !version.TryGetInt32(out var schema))
                throw InvalidStore();
            StoredMinecraftAccounts accounts;
            if (schema == 1)
            {
                // 旧文件只转换内存模型；确认新的账号操作成功后才原子写入新版密文。
                var legacy = JsonSerializer.Deserialize<StoredMinecraftAccount>(plaintext) ?? throw InvalidStore();
                accounts = new() { Accounts = [legacy], ActiveAccountId = legacy.Account?.Id };
            }
            else if (schema == 2)
                accounts = JsonSerializer.Deserialize<StoredMinecraftAccounts>(plaintext) ?? throw InvalidStore();
            else throw InvalidStore();
            Validate(accounts);
            return accounts;
        }
        catch (JsonException) { throw new MinecraftAuthenticationException("本机账号凭证损坏，请检查凭证文件；原文件已保留。"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    /// <summary>先加密再创建临时文件，原子替换本服务的凭证；取消不留下明文或半写文件。</summary>
    public async Task SaveAsync(StoredMinecraftAccounts accounts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Validate(accounts);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(accounts);
        byte[] encrypted;
        try
        {
            if (plaintext.Length > MaximumBytes - 4096) throw InvalidStore();
            encrypted = Protect(plaintext, decrypt: false);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        InstanceImportPaths.EnsureNoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await output.WriteAsync(encrypted, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            InstanceImportPaths.EnsureNoLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            InstanceImportPaths.EnsureNoLinks(temporary);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>整份集合一次验证，拒绝重复 UUID、悬空默认选择或非法令牌，不能只丢弃坏条目继续覆盖。</summary>
    internal static void Validate(StoredMinecraftAccounts accounts)
    {
        if (accounts.SchemaVersion != 2 || accounts.Accounts is null || accounts.Accounts.Count > MaximumAccounts)
            throw InvalidStore();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts.Accounts)
        {
            if (account is null || account.SchemaVersion != 1 || account.Account is null ||
                !Guid.TryParse(account.ClientId, out var clientId) || clientId == Guid.Empty ||
                !Guid.TryParseExact(account.Account.Id, "N", out var playerId) || playerId == Guid.Empty ||
                string.IsNullOrWhiteSpace(account.Account.Name) || account.Account.Name.Length > 16 ||
                account.Account.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')) ||
                string.IsNullOrWhiteSpace(account.RefreshToken) || account.RefreshToken.Length > 32768 || account.RefreshToken.Any(char.IsControl) ||
                !ids.Add(account.Account.Id))
                throw InvalidStore();
        }
        if (accounts.ActiveAccountId is not null && !ids.Contains(accounts.ActiveAccountId)) throw InvalidStore();
    }

    /// <summary>诊断不附带反序列化内容或令牌，避免错误上报泄露凭证。</summary>
    private static MinecraftAuthenticationException InvalidStore() =>
        new("本机账号凭证格式无效或账号数量超过限制，请检查凭证文件；原文件不会被覆盖。");

    /// <summary>退出只删除 EML 自己的加密凭证，微软浏览器会话和其他启动器账号不受影响。</summary>
    public Task DeleteAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        InstanceImportPaths.EnsureNoLinks(path);
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { }
        return Task.CompletedTask;
    }

    /// <summary>DPAPI 使用 CurrentUser 范围，禁止系统交互；原生和托管明文缓冲释放前清零。</summary>
    private static byte[] Protect(byte[] bytes, bool decrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("账号安全存储需要 Windows。");
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = decrypt
                ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok || output.Length is <= 0 or > MaximumBytes)
                throw new MinecraftAuthenticationException("无法读取或保存 Windows 加密凭证，请使用原 Windows 用户重新登录。");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob data, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob data, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
