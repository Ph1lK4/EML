using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Contracts;
using Launcher.Download;
using Launcher.Metadata;
using Launcher.Runtime;

namespace Launcher.Forge;

/// <summary>一次安装的结果：版本 id、版本 JSON、universal jar 落在哪。</summary>
public sealed record ForgeInstallResult(
    string VersionId,
    string VersionJsonPath,
    string UniversalJarPath,
    string? BaseVersionJsonCopied);

/// <summary>
/// 1.7.10 的 Forge 安装器是个自解压 jar：里面是 install_profile.json + universal jar。
/// 这里不跑 java，直接解包 —— 装 Forge 不需要 JVM，只有启动游戏才需要。
/// </summary>
public sealed class ForgeInstaller
{
    public const string ForgeMaven = "https://maven.minecraftforge.net/";

    // 1.7.10 的官方版本 JSON（项目里 data/1.7.10.json 就是它）
    private const string LegacyBaseVersion = "1.7.10";
    private const string LegacyBaseJsonUrl =
        "https://piston-meta.mojang.com/v1/packages/ed5d8789ed29872ea2ef1c348302b0c55e3f3468/1.7.10.json";
    private const string LegacyBaseJsonSha1 = "ed5d8789ed29872ea2ef1c348302b0c55e3f3468";
    private const long LegacyBaseJsonSize = 14777;

    private readonly IDownloadService _downloader;

    public ForgeInstaller(IDownloadService? downloader = null) =>
        _downloader = downloader ?? new HttpFileDownloader();

    public async Task<ForgeInstallResult> InstallAsync(
        string installerJarPath,
        string instanceRoot,
        string? versionId = null,
        string? baseVersionJsonPath = null,
        CancellationToken ct = default)
    {
        var layout = new InstanceLayout(instanceRoot);
        using var archive = ZipFile.OpenRead(installerJarPath);

        var profile = ReadProfile(archive);
        var install = profile["install"]?.AsObject() ?? new JsonObject();
        var minecraft = install["minecraft"]?.GetValue<string>() ?? LegacyBaseVersion;
        var forgePath = install["path"]?.GetValue<string>();
        var target = install["target"]?.GetValue<string>();
        var label = install["version"]?.GetValue<string>();
        var filePath = install["filePath"]?.GetValue<string>();

        var id = versionId ?? DeriveVersionId(minecraft, label, target, forgePath);
        var libraryName = forgePath ?? target ?? $"net.minecraftforge:forge:{minecraft}";

        var copied = await EnsureBaseVersionAsync(layout, minecraft, baseVersionJsonPath, ct).ConfigureAwait(false);

        var relative = UniversalRelativePath(libraryName);
        var universalEntryName = filePath ?? relative[(relative.LastIndexOf('/') + 1)..];
        var entry = archive.GetEntry(universalEntryName)
                    ?? throw new InvalidDataException($"安装包里找不到 universal jar：{universalEntryName}");

        var universalPath = Path.Combine(layout.GameDirectory, "libraries", ToLocal(relative));
        Directory.CreateDirectory(Path.GetDirectoryName(universalPath)!);
        entry.ExtractToFile(universalPath, overwrite: true);

        var (sha1, size) = Hash(universalPath);
        var versionJson = PatchVersionInfo(profile, id, minecraft, relative, sha1, size);
        var versionJsonPath = layout.VersionJsonPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(versionJsonPath)!);
        await File.WriteAllTextAsync(versionJsonPath, versionJson, ct).ConfigureAwait(false);

        return new ForgeInstallResult(id, versionJsonPath, universalPath, copied);
    }

    /// <summary>只读安装包，打印一下里面是什么版本（`forge info` 用）。</summary>
    public static ForgeInstallProfile ReadProfile(string installerJarPath)
    {
        using var archive = ZipFile.OpenRead(installerJarPath);
        var profile = ReadProfile(archive);
        var install = profile["install"]?.AsObject();
        return new ForgeInstallProfile(
            install?["minecraft"]?.GetValue<string>(),
            install?["version"]?.GetValue<string>(),
            install?["path"]?.GetValue<string>(),
            install?["target"]?.GetValue<string>());
    }

    private static JsonObject ReadProfile(ZipArchive archive)
    {
        var entry = archive.GetEntry("install_profile.json")
                    ?? throw new InvalidDataException("安装包里没有 install_profile.json（不是 Forge 安装包？）");
        using var stream = entry.Open();
        return JsonNode.Parse(stream)?.AsObject()
               ?? throw new InvalidDataException("install_profile.json 解析失败");
    }

    private async Task<string?> EnsureBaseVersionAsync(
        InstanceLayout layout, string minecraft, string? baseVersionJsonPath, CancellationToken ct)
    {
        var destination = layout.VersionJsonPath(minecraft);
        if (File.Exists(destination)) return null;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (baseVersionJsonPath is not null && File.Exists(baseVersionJsonPath))
        {
            File.Copy(baseVersionJsonPath, destination, overwrite: true);
            return baseVersionJsonPath;
        }

        if (minecraft != LegacyBaseVersion)
            throw new InvalidDataException(
                $"实例里没有基础版本 {minecraft}.json；请用 --base 指定，或先把这个版本的 JSON 放进去");

        var task = new DownloadTask(
            LegacyBaseJsonUrl, destination, LegacyBaseJsonSha1, LegacyBaseJsonSize, $"{minecraft}.json", "version-json");
        await _downloader.DownloadAsync(new[] { task }, progress: null, ct).ConfigureAwait(false);
        return null;
    }

    private static string PatchVersionInfo(
        JsonObject profile, string id, string minecraft, string universalRelative, string sha1, long size)
    {
        var versionInfo = profile["versionInfo"]?.AsObject()
                          ?? throw new InvalidDataException("install_profile.json 里没有 versionInfo");

        versionInfo["id"] = id;
        versionInfo["inheritsFrom"] = minecraft;
        versionInfo["jar"] = minecraft;

        var libraries = versionInfo["libraries"]?.AsArray();
        if (libraries is null)
        {
            libraries = new JsonArray();
            versionInfo["libraries"] = libraries;
        }

        var forgeLibrary = libraries.OfType<JsonObject>()
            .FirstOrDefault(lib => lib["name"]?.GetValue<string>()?.StartsWith("net.minecraftforge:forge:", StringComparison.Ordinal) == true);

        if (forgeLibrary is null)
        {
            forgeLibrary = new JsonObject();
            libraries.Add(forgeLibrary);
        }

        var baseUrl = (forgeLibrary["url"]?.GetValue<string>() ?? ForgeMaven).TrimEnd('/') + "/";
        forgeLibrary["downloads"] = new JsonObject
        {
            ["artifact"] = new JsonObject
            {
                ["path"] = universalRelative,
                ["url"] = baseUrl + universalRelative,
                ["sha1"] = sha1,
                ["size"] = size,
            },
        };

        return versionInfo.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10 → libraries/.../forge-…-universal.jar 的相对路径。</summary>
    private static string UniversalRelativePath(string libraryName)
    {
        var parts = libraryName.Split(':');
        if (parts.Length < 3) throw new InvalidDataException($"Forge 库坐标不完整：{libraryName}");

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        return $"{group}/{artifact}/{version}/{artifact}-{version}-universal.jar";
    }

    private static string DeriveVersionId(string minecraft, string? label, string? target, string? forgePath)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            var cleaned = label.Replace(" ", "", StringComparison.Ordinal);
            if (cleaned.StartsWith("Forge", StringComparison.OrdinalIgnoreCase))
                cleaned = "Forge" + cleaned["Forge".Length..];
            if (cleaned.EndsWith("-" + minecraft, StringComparison.Ordinal))
                cleaned = cleaned[..^(minecraft.Length + 1)];
            return minecraft + "-" + cleaned;
        }

        if (!string.IsNullOrWhiteSpace(forgePath))
        {
            var version = forgePath.Split(':').ElementAtOrDefault(2) ?? "";
            if (version.EndsWith("-" + minecraft, StringComparison.Ordinal))
                version = version[..^(minecraft.Length + 1)];
            if (version.StartsWith(minecraft + "-", StringComparison.Ordinal))
                version = version[(minecraft.Length + 1)..];
            return minecraft + "-Forge" + version;
        }

        return minecraft + "-Forge";
    }

    private static (string Sha1, long Size) Hash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA1.HashData(stream);
        return (Convert.ToHexString(hash).ToLowerInvariant(), stream.Length);
    }

    private static string ToLocal(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);
}

/// <summary>安装包自述（只读，给人看）。</summary>
public sealed record ForgeInstallProfile(
    string? Minecraft,
    string? Version,
    string? ForgePath,
    string? Target);
