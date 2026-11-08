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

internal interface IMinecraftAccountStore
{
    Task<StoredMinecraftAccount?> LoadAsync(CancellationToken token);
    Task SaveAsync(StoredMinecraftAccount account, CancellationToken token);
    Task DeleteAsync(CancellationToken token);
}

/// <summary>使用当前 Windows 用户的 DPAPI 加密，凭证放在 LocalAppData，和实例文件完全分离。</summary>
internal sealed class WindowsMinecraftAccountStore(string path) : IMinecraftAccountStore
{
    /// <summary>仅在当前用户可解密时加载；损坏数据保留，不以空账号覆盖。</summary>
    public async Task<StoredMinecraftAccount?> LoadAsync(CancellationToken token)
    {
        InstanceImportPaths.EnsureNoLinks(path);
        byte[] encrypted;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (stream.Length > 128 * 1024) throw new MinecraftAuthenticationException("本机账号凭证文件过大，请退出账号后重新登录。");
            encrypted = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(encrypted, token).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var plaintext = Protect(encrypted, decrypt: true);
        try
        {
            var account = JsonSerializer.Deserialize<StoredMinecraftAccount>(plaintext);
            if (account is null || account.SchemaVersion != 1 || account.Account is null ||
                !Guid.TryParse(account.ClientId, out _) || !Guid.TryParseExact(account.Account.Id, "N", out _) ||
                string.IsNullOrWhiteSpace(account.Account.Name) || account.Account.Name.Length > 16 ||
                account.Account.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')) ||
                string.IsNullOrWhiteSpace(account.RefreshToken) || account.RefreshToken.Length > 32768 || account.RefreshToken.Any(char.IsControl))
                throw new MinecraftAuthenticationException("本机账号凭证格式无效，请退出账号后重新登录。");
            return account;
        }
        catch (JsonException) { throw new MinecraftAuthenticationException("本机账号凭证损坏，请退出账号后重新登录。"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    /// <summary>先加密再创建临时文件，原子替换本服务的凭证；取消不留下明文或半写文件。</summary>
    public async Task SaveAsync(StoredMinecraftAccount account, CancellationToken token)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(account);
        byte[] encrypted;
        try { encrypted = Protect(plaintext, decrypt: false); }
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
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

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
            if (!ok || output.Length is <= 0 or > 131072)
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
