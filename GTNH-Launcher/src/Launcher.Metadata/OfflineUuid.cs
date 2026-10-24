using System.Security.Cryptography;
using System.Text;

namespace Launcher.Metadata;

/// <summary>
/// Java 版离线模式的 UUID：MD5("OfflinePlayer:" + 名字) 再按 UUID v3 改位。
/// 和原版服务端算出来的一致，服务器才不会把同一个人当成两个玩家。
/// </summary>
public static class OfflineUuid
{
    public static string ForName(string playerName)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // variant RFC 4122
        return new Guid(hash, bigEndian: true).ToString();
    }

    /// <summary>参数的 --uuid 要 32 位不带连字符。</summary>
    public static string ForNameNoDashes(string playerName) => ForName(playerName).Replace("-", "");
}
