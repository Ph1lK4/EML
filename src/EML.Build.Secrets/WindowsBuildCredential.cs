using System;
using System.Runtime.InteropServices;
using System.Text;

namespace EML.Build.Secrets;

/// <summary>仅供编译器读取当前 Windows 用户的 DPAPI 凭证，不向启动器引入运行时依赖。</summary>
internal static class WindowsBuildCredential
{
    private const int UiForbidden = 0x1;
    private const int MaximumInputLength = 16 * 1024;
    private const int MaximumPlaintextBytes = 1280;

    /// <summary>将有界的 Base64 密文解为 UTF-8 密钥，清理托管及原生缓冲区，不弹出系统提示。</summary>
    internal static string? Unprotect(string encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > MaximumInputLength) return null;
        byte[]? encrypted = null;
        byte[]? plaintext = null;
        var input = new DataBlob();
        var output = new DataBlob();
        try
        {
            encrypted = Convert.FromBase64String(encoded);
            if (encrypted.Length == 0) return null;
            input.Length = encrypted.Length;
            input.Data = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(encrypted, 0, input.Data, input.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                UiForbidden, out output)) return null;
            if (output.Data == IntPtr.Zero || output.Length <= 0 || output.Length > MaximumPlaintextBytes) return null;
            plaintext = new byte[output.Length];
            Marshal.Copy(output.Data, plaintext, 0, plaintext.Length);
            // 不允许损坏的 UTF-8 被替换字符掩盖；生成器随后再校验可见 ASCII 格式。
            return new UTF8Encoding(false, true).GetString(plaintext);
        }
        finally
        {
            if (plaintext != null) Array.Clear(plaintext, 0, plaintext.Length);
            if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
            if (input.Data != IntPtr.Zero)
            {
                ClearNative(input.Data, input.Length);
                Marshal.FreeHGlobal(input.Data);
            }
            if (output.Data != IntPtr.Zero)
            {
                ClearNative(output.Data, output.Length);
                LocalFree(output.Data);
            }
        }
    }

    /// <summary>释放前显式覆盖原生缓冲区，避免明文留在可复用的本机内存中。</summary>
    private static void ClearNative(IntPtr address, int length)
    {
        for (var index = 0; index < length; index++) Marshal.WriteByte(address, index, 0);
    }

    /// <summary>与 Windows DATA_BLOB 保持相同的长度和指针布局。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        internal int Length;
        internal IntPtr Data;
    }

    /// <summary>通过 Windows DPAPI 解密，不提供可交互提示、额外熵或描述输出。</summary>
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    /// <summary>释放 DPAPI 分配的返回缓冲区。</summary>
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
