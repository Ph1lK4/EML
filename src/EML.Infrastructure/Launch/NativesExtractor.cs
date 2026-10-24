// ════════════════════════════════════════════════════════════════════
//  文件：NativesExtractor.cs
//  位置：src\EML.Infrastructure\Launch
//  作用：把 1.7.10 版本 JSON 里声明的本机库（.dll）解压到 natives 目录。
//
//  处理流程（三段）：
//    ① 读游戏目录里的版本 JSON，找 libraries 里带 natives.windows 的库；
//    ② 算出对应的 classifiers 分类器 jar 路径（${arch} 换成 64 或 32）；
//    ③ 把 jar 里的 .dll 解压到 versions\<版本>\<版本>-natives。
//
//  这个类不做的：下载（库没下就跳过，不在这里联网）、启动进程。
// ════════════════════════════════════════════════════════════════════

using System.IO.Compression;
using System.Text.Json;
using EML.Core.Launch;

namespace EML.Infrastructure.Launch;

/// <summary>
/// 从版本 JSON 推导并解压本机库。
/// </summary>
public sealed class NativesExtractor : INativesExtractor
{
    /// <summary>当前只处理 Windows；以后要做跨平台，把这里换成按系统判断。</summary>
    private const string OsName = "windows";

    /// <summary>
    /// 默认要跳过的条目前缀。
    /// natives jar 里的 META-INF 是给 jar 本身用的，解压出来没意义。
    /// 版本 JSON 里如果写了 extract.exclude，会在这个基础上追加。
    /// </summary>
    private static readonly string[] DefaultExcludes = ["META-INF/", "META-INF\\"];

    /// <inheritdoc />
    public string Extract(string gameDirectory, string versionName)
    {
        var versionJsonPath = Path.Combine(
            gameDirectory, "versions", versionName, versionName + ".json");

        if (!File.Exists(versionJsonPath))
        {
            throw new FileNotFoundException(
                $"缺 versions\\{versionName}\\{versionName}.json，无法解压本机库");
        }

        // 解压目标：versions\<版本>\<版本>-natives
        // 放在版本目录下而不是游戏根目录，是为了多版本共存时互不覆盖。
        var nativesDirectory = Path.Combine(
            gameDirectory, "versions", versionName, versionName + "-natives");

        using var document = JsonDocument.Parse(File.ReadAllText(versionJsonPath));

        if (!document.RootElement.TryGetProperty("libraries", out var libraries))
        {
            return string.Empty;
        }

        // 先清掉上一次留下的 dll，避免旧版本库残留导致加载错版本。
        if (Directory.Exists(nativesDirectory))
        {
            Directory.Delete(nativesDirectory, recursive: true);
        }

        Directory.CreateDirectory(nativesDirectory);

        var extractedCount = 0;

        foreach (var library in libraries.EnumerateArray())
        {
            var classifierPath = FindNativesClassifierPath(library);

            if (classifierPath is null)
            {
                continue;
            }

            var jarPath = Path.Combine(
                gameDirectory,
                "libraries",
                classifierPath.Replace('/', Path.DirectorySeparatorChar));

            // 库还没下载时跳过，而不是让整个启动失败：
            // 缺 dll 的症状是游戏里某个功能不可用，而不是命令行拼错。
            if (!File.Exists(jarPath))
            {
                continue;
            }

            var excludes = ReadExcludes(library);
            ExtractJar(jarPath, nativesDirectory, excludes);
            extractedCount++;
        }

        // 一个本机库都没有 -> 不需要 java.library.path
        return extractedCount == 0 ? string.Empty : nativesDirectory;
    }

    /// <summary>
    /// 在一个 library 节点里找本机库 jar 的相对路径（相对 libraries 目录）。
    ///
    /// 1.7.10 的写法有三种：
    ///   - <c>natives: { windows: "natives-windows" }</c>（lwjgl、jinput）
    ///   - <c>natives: { windows: "natives-windows-${arch}" }</c>（twitch）
    ///   - 只有 classifiers、没有 natives（不需要解压）
    /// </summary>
    private static string? FindNativesClassifierPath(JsonElement library)
    {
        if (!library.TryGetProperty("natives", out var natives) ||
            !natives.TryGetProperty(OsName, out var templateElement))
        {
            return null;
        }

        if (!library.TryGetProperty("downloads", out var downloads) ||
            !downloads.TryGetProperty("classifiers", out var classifiers))
        {
            return null;
        }

        var template = templateElement.GetString() ?? string.Empty;

        // ${arch}：64 位 Java / 64 位系统上用 64 位 dll
        var classifierKey = template.Replace(
            "${arch}", Environment.Is64BitProcess ? "64" : "32",
            StringComparison.Ordinal);

        // 先精确匹配模板算出来的 key
        if (classifiers.TryGetProperty(classifierKey, out var exact) &&
            exact.TryGetProperty("path", out var exactPath))
        {
            return exactPath.GetString();
        }

        // 再退一步：找任意 natives-windows 开头的分类器（兼容写法不规范的库）
        foreach (var classifier in classifiers.EnumerateObject())
        {
            if (classifier.Name.StartsWith("natives-" + OsName, StringComparison.OrdinalIgnoreCase) &&
                classifier.Value.TryGetProperty("path", out var fallbackPath))
            {
                return fallbackPath.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// 读 library.extract.exclude（没有就是空数组）。
    /// 这是版本 JSON 自带的排除规则，例如 ["META-INF/"]。
    /// </summary>
    private static IReadOnlyList<string> ReadExcludes(JsonElement library)
    {
        if (!library.TryGetProperty("extract", out var extract) ||
            !extract.TryGetProperty("exclude", out var exclude) ||
            exclude.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();

        foreach (var item in exclude.EnumerateArray())
        {
            var value = item.GetString();

            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        return result;
    }

    /// <summary>
    /// 把 jar 里的文件解压到目标目录。
    ///
    /// 只解压文件（目录项跳过），跳过排除前缀，
    /// 同名文件直接覆盖（旧版本残留已经在上一步清过）。
    /// </summary>
    private static void ExtractJar(
        string jarPath,
        string destinationDirectory,
        IReadOnlyList<string> excludes)
    {
        using var archive = ZipFile.OpenRead(jarPath);

        foreach (var entry in archive.Entries)
        {
            // 目录项（以 / 结尾、Name 为空）跳过，目录会按文件路径自动创建
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);

            if (IsExcluded(relativePath, excludes))
            {
                continue;
            }

            var destinationPath = Path.Combine(destinationDirectory, relativePath);

            // 防止 zip 里带 ..\ 跳出目标目录
            var fullDestination = Path.GetFullPath(destinationPath);
            var fullRoot = Path.GetFullPath(destinationDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!fullDestination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
            entry.ExtractToFile(fullDestination, overwrite: true);
        }
    }

    /// <summary>判断一个条目是否要跳过（默认前缀 + 版本 JSON 的 exclude）。</summary>
    private static bool IsExcluded(string relativePath, IReadOnlyList<string> excludes)
    {
        foreach (var prefix in DefaultExcludes)
        {
            if (relativePath.StartsWith(
                    prefix.Replace('/', Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var prefix in excludes)
        {
            if (relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
