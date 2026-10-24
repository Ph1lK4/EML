namespace Launcher.Download;

/// <summary>
/// 官方地址 → BMCLAPI 镜像。只映射已经实测通的两类资源（库、assets）；
/// 拿不准的返回 null，让下载器回退官方地址，别把不确定的映射写成默认。
/// </summary>
public static class Mirror
{
    private const string Bmcl = "https://bmclapi2.bangbang93.com";

    private static readonly (string From, string To)[] Rules =
    {
        ("https://libraries.minecraft.net/", Bmcl + "/libraries/"),
        ("http://libraries.minecraft.net/", Bmcl + "/libraries/"),
        ("https://resources.download.minecraft.net/", Bmcl + "/assets/"),
        ("http://resources.download.minecraft.net/", Bmcl + "/assets/"),
    };

    /// <summary>能镜像就返回镜像地址；不能镜像返回 null（调用方直接走原地址）。</summary>
    public static string? ToMirror(string url)
    {
        foreach (var (from, to) in Rules)
        {
            if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                return to + url[from.Length..];
        }

        return null;
    }
}
