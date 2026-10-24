// ════════════════════════════════════════════════════════════════════
//  文件：VanillaLaunchPlanner.cs
//  位置：src\EML.Infrastructure\Launch
//  作用：把「用哪个 Java、游戏目录在哪、玩家叫什么」变成一条完整的
//        1.7.10 原版启动命令（只生成，不启动）。
//
//  为什么要拆成这几步？
//    1) 1.7.10 的版本 JSON 里，库和客户端 jar 是分开列的，
//       必须按顺序拼成 classpath，顺序错了游戏起不来；
//    2) 启动参数是一段带 ${...} 占位符的老式模板，
//       必须逐段替换成真实值；
//    3) 离线模式没有正版账号，UUID 要按 Java 的规则自己算。
//    这三件事互不影响，所以拆成独立方法，方便单独读、单独测。
//
//  这个类不做的：下载、解压、启动进程、联网。
// ════════════════════════════════════════════════════════════════════

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EML.Core.Launch;

namespace EML.Infrastructure.Launch;

/// <summary>
/// 原版 1.7.10 的启动计划器实现。
///
/// 输出的一条命令大致长这样：
/// <code>
/// "java.exe" -Xms1024M -Xmx2048M -cp "库1;库2;…;versions\1.7.10\1.7.10.jar"
///            net.minecraft.client.main.Main
///            --username Steve --version 1.7.10 --gameDir … --assetIndex 1.7.10
///            --uuid … --userType legacy
/// </code>
/// </summary>
public sealed class VanillaLaunchPlanner : ILaunchPlanner
{
    /// <summary>
    /// 这一步只支持 1.7.10 原版。
    /// 以后要支持别的版本，把它从常量改成参数即可，其它逻辑不用动。
    /// </summary>
    private const string VersionId = "1.7.10";

    /// <summary>
    /// 入口：生成启动计划。
    ///
    /// 流程分四段（代码里也用分隔线标了出来）：
    ///   ① 读版本 JSON，拿主类、参数模板、资源索引；
    ///   ② 拼 classpath：所有库 + 客户端 jar；
    ///   ③ 准备占位符的值（玩家、目录、UUID……）；
    ///   ④ 按顺序拼出最终参数。
    /// </summary>
    /// <param name="request">这次启动的输入。</param>
    /// <returns>可直接显示/执行的启动计划。</returns>
    /// <exception cref="FileNotFoundException">
    /// 游戏目录里没有 versions\1.7.10\1.7.10.json 时抛出。
    /// </exception>
    public LaunchPlan Build(LaunchRequest request)
    {
        // ────────────────────────────────────────────────────────────
        // ① 读版本 JSON
        //    JSON 是 Mojang 官方元数据，里面有主类、启动模板、库列表。
        //    只认游戏目录里的那份；没有就报错，不内置任何文件。
        // ────────────────────────────────────────────────────────────
        var root = LoadVersionJson(request.GameDirectory).RootElement;

        // 主类：1.7.10 是 net.minecraft.client.main.Main。
        var mainClass = root.GetProperty("mainClass").GetString() ?? string.Empty;

        // 启动参数模板：老版本用 minecraftArguments（一整串字符串），
        // 新版本才用 arguments 数组。1.7.10 走的是前者。
        var template = root.GetProperty("minecraftArguments").GetString() ?? string.Empty;

        // 资源索引 id：1.7.10 就是 "1.7.10"。
        // 它决定游戏去 assets\indexes\<id>.json 读资源清单。
        var assetIndex = root.GetProperty("assetIndex").GetProperty("id").GetString() ?? VersionId;

        // ────────────────────────────────────────────────────────────
        // ② 拼 classpath
        //    顺序：libraries 里带 artifact 的库 → 客户端 jar。
        //    客户端 jar 必须放最后，这是 Mojang 启动器的惯例。
        // ────────────────────────────────────────────────────────────
        var libraries = BuildClasspath(root, request.GameDirectory);
        var clientJar = Path.Combine(
            request.GameDirectory, "versions", VersionId, VersionId + ".jar");
        var classpath = string.Join(';', libraries.Append(clientJar));

        // ────────────────────────────────────────────────────────────
        // ③ 准备占位符的值
        //    模板里的 ${xxx} 会被替换成这里对应的值。
        //    键名必须和 Mojang 模板完全一致，改一个字母就替换不上。
        // ────────────────────────────────────────────────────────────
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = request.PlayerName,
            ["version_name"] = request.VersionName,
            ["game_directory"] = request.GameDirectory,

            // 资源目录固定是 <游戏目录>\assets
            ["assets_root"] = Path.Combine(request.GameDirectory, "assets"),
            ["assets_index_name"] = assetIndex,

            // 离线模式的 UUID：按玩家名算，同名永远同一个 UUID
            ["auth_uuid"] = OfflineUuid(request.PlayerName),

            // 离线模式没有令牌，给个 "0" 即可；1.7.10 不校验它
            ["auth_access_token"] = "0",

            // 正版启动器会填用户属性，离线模式给空对象
            ["user_properties"] = "{}",

            // 1.7.10 认 legacy 这种账号类型
            ["user_type"] = "legacy"
        };

        // ────────────────────────────────────────────────────────────
        // ④ 拼最终参数
        //    顺序：内存 → classpath → 主类 → 游戏参数。
        //    -Xms 给 1 GB，-Xmx 用界面上填的值。
        // ────────────────────────────────────────────────────────────
        var arguments = "-Xms1024M " +
                        $"-Xmx{request.MaxMemoryMb}M " +
                        $"-cp \"{classpath}\" " +
                        mainClass + " " +
                        Expand(template, values);

        return new LaunchPlan(request.JavaPath, arguments, request.GameDirectory);
    }

    /// <summary>
    /// 读游戏目录里的版本 JSON。
    ///
    /// 只认 <c>&lt;游戏目录&gt;\versions\1.7.10\1.7.10.json</c>。
    /// 找不到时抛 <see cref="FileNotFoundException"/>，消息是给用户看的，
    /// 会原样显示在窗口状态栏里。
    /// </summary>
    private static JsonDocument LoadVersionJson(string gameDirectory)
    {
        var localPath = Path.Combine(
            gameDirectory, "versions", VersionId, VersionId + ".json");

        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException(
                $"缺 versions\\{VersionId}\\{VersionId}.json，请先把 1.7.10 原版装进游戏目录");
        }

        return JsonDocument.Parse(File.ReadAllText(localPath));
    }

    /// <summary>
    /// 从版本 JSON 的 libraries 里拼出 classpath 列表。
    ///
    /// 规则：
    ///   - 只收带 <c>downloads.artifact.path</c> 的库（这些才是要进 classpath 的 jar）；
    ///   - 只声明了 natives、没有 artifact 的库（比如 lwjgl-platform）跳过，
    ///     它们的 dll 是解压到 natives 目录的，不进 classpath；
    ///   - 用完整路径去重，防止同一个 jar 进两次。
    /// </summary>
    private static List<string> BuildClasspath(JsonElement root, string gameDirectory)
    {
        var librariesDirectory = Path.Combine(gameDirectory, "libraries");
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in root.GetProperty("libraries").EnumerateArray())
        {
            // 没有 downloads.artifact.path 的库直接跳过（natives 库属于这种）
            if (!library.TryGetProperty("downloads", out var downloads) ||
                !downloads.TryGetProperty("artifact", out var artifact) ||
                !artifact.TryGetProperty("path", out var pathElement))
            {
                continue;
            }

            var relativePath = pathElement.GetString();

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            // Mojang 的 path 用 / 分隔，Windows 上要换成 \
            var fullPath = Path.Combine(
                librariesDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (seen.Add(fullPath))
            {
                result.Add(fullPath);
            }
        }

        return result;
    }

    /// <summary>
    /// 把参数模板按空格切开，逐段替换 ${...}。
    ///
    /// 为什么不直接 string.Replace？
    ///   因为要保证「没被识别的占位符原样保留」，
    ///   而且逐段处理方便以后加参数（比如某些参数要单独加引号）。
    /// </summary>
    private static string Expand(
        string template,
        IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder(template.Length + 128);

        foreach (var token in template.Split(
                     ' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(ReplaceToken(token, values));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 替换单个 token 里的 ${key}。
    ///
    /// 只替换第一个占位符（1.7.10 的模板里每个 token 最多一个）；
    /// 找不到对应值时原样返回，方便从命令行看出是哪个没填。
    /// </summary>
    private static string ReplaceToken(
        string token,
        IReadOnlyDictionary<string, string> values)
    {
        var start = token.IndexOf("${", StringComparison.Ordinal);

        if (start < 0)
        {
            return token;
        }

        var end = token.IndexOf('}', start);

        if (end < 0)
        {
            return token;
        }

        var key = token.Substring(start + 2, end - start - 2);

        return values.TryGetValue(key, out var value)
            ? token[..start] + value + token[(end + 1)..]
            : token;
    }

    /// <summary>
    /// 离线模式的 UUID：<c>MD5("OfflinePlayer:" + 名字)</c>，再按 Java 的规则
    /// 打上版本位与变体位，最后格式化成带连字符的标准 UUID。
    ///
    /// 为什么是这个算法？
    ///   这是 Java 的 <c>UUID.nameUUIDFromBytes</c> 的行为，Minecraft 离线模式
    ///   沿用了它，所以同名玩家在任何启动器里 UUID 都一样。
    ///
    /// 举例：Steve → 5627dd98-e6be-3c21-b8a8-e92344183641
    /// </summary>
    private static string OfflineUuid(string playerName)
    {
        var hash = MD5.HashData(
            Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));

        // 把第 7 个字节的高 4 位改成 3（表示 UUID 版本 3，MD5 派生）
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);

        // 把第 9 个字节的高 2 位改成 10（RFC 4122 变体）
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        var hex = Convert.ToHexString(hash).ToLowerInvariant();

        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }
}
