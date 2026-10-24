using System.Globalization;
using System.Text.Json;
using Launcher.Metadata;

namespace Launcher.Prism;

/// <summary>
/// 只读地打开一个 Prism / MultiMC 实例：不下载、不写盘，只用实例里已有的文件。
/// 目录约定（Prism 与我们的「自包含实例」不同，库和资源是全局共享的）：
///   &lt;PrismRoot&gt;/libraries     所有实例共享的库
///   &lt;PrismRoot&gt;/assets       所有实例共享的资源
///   &lt;实例&gt;/.minecraft         游戏目录（mods/config/saves 都在这里）
///   &lt;实例&gt;/natives            Prism 已经解压好的 native dll
///   &lt;实例&gt;/patches/*.json     组件补丁，启动参数就从这里来
/// </summary>
public sealed class PrismInstance
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public required string InstanceDirectory { get; init; }
    public required string PrismRoot { get; init; }
    public required string Name { get; init; }
    public required string GameDirectory { get; init; }
    public required string LibrariesDirectory { get; init; }
    /// <summary>实例自带的库目录（Prism 会把 tweak 用的 jar 放在这里，比如 lwjgl3ify 的 forgePatches）。</summary>
    public required string InstanceLibrariesDirectory { get; init; }
    public required string AssetsDirectory { get; init; }
    public required string NativesDirectory { get; init; }
    public string? JavaPath { get; init; }
    /// <summary>实例配置里写的 Java 主版本（instance.cfg 的 JavaVersion），读不到就是 0。</summary>
    public int JavaMajor { get; init; }
    public int MinMemoryMb { get; init; }
    public int MaxMemoryMb { get; init; }
    public string? MainClass { get; init; }
    public required string MinecraftArguments { get; init; }
    public required string AssetIndexId { get; init; }
    public required string MainJarName { get; init; }
    public required IReadOnlyList<LibraryJson> Libraries { get; init; }
    public required IReadOnlyList<string> ExtraJvmArgs { get; init; }
    public required IReadOnlyList<string> Tweakers { get; init; }

    /// <param name="instanceDirectory">例如 ...\PrismLauncher\instances\GT_New_Horizons_2.9.0-beta-1_Java_17-25 (2)</param>
    /// <param name="prismRoot">Prism 根目录；不传就按 instances 的上一级推断。</param>
    public static PrismInstance Open(string instanceDirectory, string? prismRoot = null)
    {
        var directory = Path.GetFullPath(instanceDirectory);
        var root = prismRoot is null
            ? Path.GetFullPath(Path.Combine(directory, "..", ".."))
            : Path.GetFullPath(prismRoot);

        var instanceConfig = ReadIni(Path.Combine(directory, "instance.cfg"));
        var globalConfig = ReadIni(Path.Combine(root, "prismlauncher.cfg"));

        var patches = LoadPatches(directory);
        if (patches.Count == 0)
            throw new InvalidDataException($"实例里没有 patches/*.json，不像 Prism 实例：{directory}");

        var ascending = patches.OrderBy(p => p.Order ?? 0).ToList();

        // order 大的优先级高：主类、资源索引取最后一个声明；库则整条链合并。
        var mainClass = ascending.LastOrDefault(p => !string.IsNullOrWhiteSpace(p.MainClass))?.MainClass;
        var arguments = ascending.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.MinecraftArguments))
            ?.MinecraftArguments ?? "";
        var assetIndexId = ascending.LastOrDefault(p => p.AssetIndex?.Id is not null)?.AssetIndex?.Id
            ?? "1.7.10";
        var mainJarName = ascending.Select(p => p.MainJar?.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? $"com.mojang:minecraft:{assetIndexId}:client";

        var libraries = new List<LibraryJson>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = ascending.Count - 1; i >= 0; i--) // 高优先级在前，classpath 与 Prism 的覆盖顺序一致
        {
            foreach (var library in ascending[i].Libraries ?? Enumerable.Empty<LibraryJson>())
            {
                if (string.IsNullOrWhiteSpace(library.Name)) continue;
                if (seen.Add(LibraryKey(library.Name!))) libraries.Add(library);
            }
        }

        var overrideMemory = string.Equals(
            instanceConfig.GetValueOrDefault("OverrideMemory"), "true", StringComparison.OrdinalIgnoreCase);
        var memorySource = overrideMemory ? instanceConfig : globalConfig;
        var javaVersion = instanceConfig.GetValueOrDefault("JavaVersion")
                          ?? globalConfig.GetValueOrDefault("JavaVersion");

        return new PrismInstance
        {
            InstanceDirectory = directory,
            PrismRoot = root,
            Name = instanceConfig.GetValueOrDefault("name") ?? Path.GetFileName(directory),
            GameDirectory = Path.Combine(directory, ".minecraft"),
            LibrariesDirectory = Path.Combine(root, "libraries"),
            InstanceLibrariesDirectory = Path.Combine(directory, "libraries"),
            AssetsDirectory = Path.Combine(root, "assets"),
            NativesDirectory = Path.Combine(directory, "natives"),
            JavaPath = instanceConfig.GetValueOrDefault("JavaPath") ?? globalConfig.GetValueOrDefault("JavaPath"),
            JavaMajor = ParseMajor(javaVersion),
            MinMemoryMb = ParseInt(memorySource.GetValueOrDefault("MinMemAlloc"), 512),
            MaxMemoryMb = ParseInt(memorySource.GetValueOrDefault("MaxMemAlloc"), 4096),
            MainClass = mainClass,
            MinecraftArguments = arguments,
            AssetIndexId = assetIndexId,
            MainJarName = mainJarName,
            Libraries = libraries,
            ExtraJvmArgs = ascending.SelectMany(p => p.JvmArgs ?? Enumerable.Empty<string>()).ToList(),
            Tweakers = ascending.SelectMany(p => p.Tweakers ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>把实例里配置的 javaw.exe 换成 java.exe（要抓控制台输出，也要能把退出码读回来）。</summary>
    public string? ResolveConsoleJava()
    {
        if (JavaPath is not { } path) return null;
        if (!path.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase)) return path;

        var console = Path.Combine(Path.GetDirectoryName(path) ?? "", "java.exe");
        return File.Exists(console) ? console : path;
    }

    private static List<PrismPatch> LoadPatches(string directory)
    {
        var patchesDirectory = Path.Combine(directory, "patches");
        if (!Directory.Exists(patchesDirectory)) return new List<PrismPatch>();

        var patches = new List<PrismPatch>();
        foreach (var file in Directory.GetFiles(patchesDirectory, "*.json")
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var patch = JsonSerializer.Deserialize<PrismPatch>(File.ReadAllText(file), JsonOptions);
            if (patch is not null) patches.Add(patch);
        }

        return patches;
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return values;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '[' or ';' or '#') continue;

            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        return values;
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    /// <summary>"25.0.1" → 25；"1.8.0_202" → 1（老式 1.x 版本号，Java 8 的配置一般也写 8）。</summary>
    private static int ParseMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var head = version.Split('.', '-', '_', '+')[0];
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? major : 0;
    }

    /// <summary>去重键：group:artifact[:classifier]，同一库被多个补丁声明时只留优先级最高的那份。</summary>
    private static string LibraryKey(string name)
    {
        var parts = name.Split(':');
        return parts.Length >= 4 ? $"{parts[0]}:{parts[1]}:{parts[3]}" : $"{parts[0]}:{parts[1]}";
    }
}
