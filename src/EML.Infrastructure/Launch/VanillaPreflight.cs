// ════════════════════════════════════════════════════════════════════
//  文件：VanillaPreflight.cs
//  位置：src\EML.Infrastructure\Launch
//  作用：1.7.10 原版的启动前检查实现 —— 把版本 JSON 里声明的文件逐个点一遍。
//
//  检查范围（顺序也对应用户能感知的顺序）：
//    一、Java 可执行文件
//    二、版本 JSON（后面所有检查都要读它，所以它排第二）
//    三、客户端 jar（versions\1.7.10\1.7.10.jar）
//    四、库（libraries）和 Windows 的 natives 分类器
//    五、资源索引（assets\indexes\1.7.10.json）和资源对象（assets\objects\xx\hash）
//
//  它不负责：
//    - 拼命令行（ILaunchPlanner）；
//    - 解压 natives（INativesExtractor，启动时才会写磁盘）；
//    - 修文件（那是下载模块的事，这里只报告，不动手）。
//
//  为什么只查「本机 Windows 的 natives」：
//    NativesExtractor 只解压 windows 那一份，别的平台的分类器根本用不上，
//    查了反而会误报「缺文件」。
// ════════════════════════════════════════════════════════════════════

using System.Text.Json;
using EML.Core.Launch;

namespace EML.Infrastructure.Launch;

/// <summary>
/// 1.7.10 原版的启动前检查。
/// 实现要点：所有「缺文件」都收集起来，不因为第一个缺失就提前返回 ——
/// 用户一次看到所有问题，比修一个重启一次快得多。
/// </summary>
public sealed class VanillaPreflight : ILaunchPreflight
{
    /// <summary>这一步只支持 1.7.10 原版；以后支持别的版本时改成参数。</summary>
    private const string GameVersion = "1.7.10";

    /// <summary>版本 JSON 里 natives 的键名（Windows）。和 NativesExtractor 保持一致。</summary>
    private const string OsName = "windows";

    /// <summary>最多报告多少个缺失文件，避免一次刷出几百行。</summary>
    private const int MaxMissingReported = 200;

    /// <summary>
    /// 开始检查。
    /// </summary>
    public LaunchPreflightResult Check(LaunchRequest request)
    {
        var missing = new List<MissingFile>();
        var checkedCount = 0;

        // ────────────────────────────────────────────────────────────
        // 一、Java 可执行文件
        // ────────────────────────────────────────────────────────────
        checkedCount++;
        if (!File.Exists(request.JavaPath))
        {
            missing.Add(new MissingFile("java", "java.exe", request.JavaPath));
        }

        // ────────────────────────────────────────────────────────────
        // 二、版本 JSON
        //    读不到它，后面的库和资源都不知道该查什么，直接返回。
        // ────────────────────────────────────────────────────────────
        var versionDirectory = Path.Combine(
            request.GameDirectory, "versions", GameVersion);
        var versionJsonPath = Path.Combine(
            versionDirectory, GameVersion + ".json");

        checkedCount++;
        if (!File.Exists(versionJsonPath))
        {
            missing.Add(new MissingFile("version", "版本 JSON", versionJsonPath));
            return new LaunchPreflightResult(checkedCount, missing);
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(File.ReadAllText(versionJsonPath));
        }
        catch (JsonException)
        {
            // 文件在、内容坏了，也算「缺」——启动时照样跑不起来。
            missing.Add(new MissingFile("version", "版本 JSON（内容损坏）", versionJsonPath));
            return new LaunchPreflightResult(checkedCount, missing);
        }

        using (document)
        {
            var root = document.RootElement;

            // ────────────────────────────────────────────────────────
            // 三、客户端 jar
            // ────────────────────────────────────────────────────────
            var clientJarPath = Path.Combine(
                versionDirectory, GameVersion + ".jar");

            checkedCount++;
            if (!File.Exists(clientJarPath))
            {
                missing.Add(new MissingFile("client", "客户端 jar", clientJarPath));
            }

            // ────────────────────────────────────────────────────────
            // 四、库 + Windows 的 natives 分类器
            // ────────────────────────────────────────────────────────
            var librariesDirectory = Path.Combine(
                request.GameDirectory, "libraries");

            if (root.TryGetProperty("libraries", out var libraries))
            {
                foreach (var library in libraries.EnumerateArray())
                {
                    CollectLibraryFiles(
                        library, librariesDirectory, missing, ref checkedCount);
                }
            }

            // ────────────────────────────────────────────────────────
            // 五、资源索引 + 资源对象
            // ────────────────────────────────────────────────────────
            var assetIndexId = root.TryGetProperty("assetIndex", out var assetIndex)
                && assetIndex.TryGetProperty("id", out var idElement)
                    ? idElement.GetString()
                    : null;

            if (!string.IsNullOrWhiteSpace(assetIndexId))
            {
                CheckAssets(
                    request.GameDirectory, assetIndexId!, missing, ref checkedCount);
            }
        }

        return new LaunchPreflightResult(checkedCount, missing);
    }

    // ────────────────────────────────────────────────────────────────
    // 辅助一：单个库要查的文件（库本体 + natives 分类器）
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 收集一个库对应的文件：库本体（artifact）和 Windows 的 natives 分类器。
    /// 没有 downloads 字段的库（例如只声明坐标的）跳过，不误报。
    /// </summary>
    private static void CollectLibraryFiles(
        JsonElement library,
        string librariesDirectory,
        List<MissingFile> missing,
        ref int checkedCount)
    {
        if (!library.TryGetProperty("downloads", out var downloads))
        {
            return;
        }

        // 库本体
        if (downloads.TryGetProperty("artifact", out var artifact)
            && TryGetPath(artifact, out var artifactPath))
        {
            CheckLibraryFile(
                librariesDirectory, artifactPath, missing, ref checkedCount);
        }

        // natives 分类器：只查 Windows 那一份
        var classifierPath = FindNativesClassifierPath(library);

        if (classifierPath is not null)
        {
            CheckLibraryFile(
                librariesDirectory, classifierPath, missing, ref checkedCount);
        }
    }

    /// <summary>
    /// 找这个库在 Windows 上的 natives 分类器路径。
    /// 逻辑要和 NativesExtractor.FindNativesClassifierPath 一致，
    /// 否则会出现「检查说齐全、解压却失败」。
    /// </summary>
    private static string? FindNativesClassifierPath(JsonElement library)
    {
        if (!library.TryGetProperty("natives", out var natives)
            || !natives.TryGetProperty(OsName, out var templateElement)
            || !library.TryGetProperty("downloads", out var downloads)
            || !downloads.TryGetProperty("classifiers", out var classifiers))
        {
            return null;
        }

        var template = templateElement.GetString();

        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        // ${arch}：64 位进程用 64 位 dll（twitch 的写法是 natives-windows-${arch}）
        var classifierKey = template.Replace(
            "${arch}", Environment.Is64BitProcess ? "64" : "32",
            StringComparison.Ordinal);

        if (classifiers.TryGetProperty(classifierKey, out var exact)
            && TryGetPath(exact, out var exactPath))
        {
            return exactPath;
        }

        // 兼容写法不规范的库：退一步找任意 natives-windows 开头的分类器
        foreach (var classifier in classifiers.EnumerateObject())
        {
            if (classifier.Name.StartsWith(
                    "natives-" + OsName, StringComparison.OrdinalIgnoreCase)
                && TryGetPath(classifier.Value, out var fallbackPath))
            {
                return fallbackPath;
            }
        }

        return null;
    }

    // ────────────────────────────────────────────────────────────────
    // 辅助二：资源索引和资源对象
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 检查资源：先看索引在不在，再按索引里的 hash 逐个点 objects。
    /// 资源对象是「内容寻址」的：路径由 hash 决定（前两位做子目录），
    /// 只要文件在，内容就一定对（hash 就是它的名字）。
    /// </summary>
    private static void CheckAssets(
        string gameDirectory,
        string assetIndexId,
        List<MissingFile> missing,
        ref int checkedCount)
    {
        var assetsDirectory = Path.Combine(gameDirectory, "assets");
        var indexPath = Path.Combine(
            assetsDirectory, "indexes", assetIndexId + ".json");

        checkedCount++;
        if (!File.Exists(indexPath))
        {
            missing.Add(new MissingFile("assets", "资源索引", indexPath));
            return;
        }

        JsonDocument index;

        try
        {
            index = JsonDocument.Parse(File.ReadAllText(indexPath));
        }
        catch (JsonException)
        {
            missing.Add(new MissingFile("assets", "资源索引（内容损坏）", indexPath));
            return;
        }

        using (index)
        {
            if (!index.RootElement.TryGetProperty("objects", out var objects))
            {
                return;
            }

            foreach (var pair in objects.EnumerateObject())
            {
                var hash = pair.Value.TryGetProperty("hash", out var hashElement)
                    ? hashElement.GetString()
                    : null;

                // hash 一定至少两位，否则拼不出子目录，跳过（坏数据）
                if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2)
                {
                    continue;
                }

                var objectPath = Path.Combine(
                    assetsDirectory, "objects", hash[..2], hash);

                checkedCount++;
                if (!File.Exists(objectPath))
                {
                    missing.Add(new MissingFile("assets", pair.Name, objectPath));
                }

                // 缺太多就不继续点了：用户看到前 200 个已经够动手修
                if (missing.Count >= MaxMissingReported)
                {
                    return;
                }
            }
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 辅助三：从 downloads 节点里取 path
    // ────────────────────────────────────────────────────────────────

    /// <summary>取 <c>path</c> 字段；没有或为空返回 false。</summary>
    private static bool TryGetPath(JsonElement element, out string? path)
    {
        path = element.TryGetProperty("path", out var value)
            ? value.GetString()
            : null;

        return !string.IsNullOrWhiteSpace(path);
    }

    /// <summary>
    /// 把一个库文件的相对路径转成完整路径并检查。
    /// 版本 JSON 里是正斜杠，Windows 要用反斜杠。
    /// </summary>
    private static void CheckLibraryFile(
        string librariesDirectory,
        string? relativePath,
        List<MissingFile> missing,
        ref int checkedCount)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var fullPath = Path.Combine(
            librariesDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        checkedCount++;
        if (!File.Exists(fullPath))
        {
            missing.Add(new MissingFile(
                "library", Path.GetFileName(fullPath), fullPath));
        }
    }
}
