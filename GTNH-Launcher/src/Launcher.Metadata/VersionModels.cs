using System.Text.Json.Serialization;
using Launcher.Contracts;

namespace Launcher.Metadata;

// 只覆盖 1.7.10 及旧版 Forge 需要的字段。
// 1.13+ 的 arguments 数组、1.8+ 的继承关系等，等真要支持时再加（写在 README 的 TODO）。

public sealed class VersionJson
{
    public string? Id { get; set; }
    public string? InheritsFrom { get; set; }
    public string? MainClass { get; set; }
    public string? MinecraftArguments { get; set; }
    public string? Assets { get; set; }
    public AssetIndexRef? AssetIndex { get; set; }
    public VersionDownloads? Downloads { get; set; }
    public List<LibraryJson>? Libraries { get; set; }
    public LoggingSection? Logging { get; set; }
    public JavaVersionJson? JavaVersion { get; set; }
    public string? Type { get; set; }
    public string? ReleaseTime { get; set; }
    public int? MinimumLauncherVersion { get; set; }
}

public sealed class AssetIndexRef
{
    public string? Id { get; set; }
    public string? Sha1 { get; set; }
    public long? Size { get; set; }
    public long? TotalSize { get; set; }
    public string? Url { get; set; }
}

public sealed class VersionDownloads
{
    public DownloadFile? Client { get; set; }
    public DownloadFile? Server { get; set; }
}

public sealed class DownloadFile
{
    public string? Id { get; set; }
    public string? Path { get; set; }
    public string? Sha1 { get; set; }
    public long? Size { get; set; }
    public string? Url { get; set; }
}

public sealed class LibraryJson
{
    public string? Name { get; set; }
    public string? Url { get; set; }
    public LibraryDownloads? Downloads { get; set; }
    public Dictionary<string, string>? Natives { get; set; }
    public ExtractJson? Extract { get; set; }
    public List<RuleJson>? Rules { get; set; }
}

public sealed class LibraryDownloads
{
    public DownloadFile? Artifact { get; set; }
    public Dictionary<string, DownloadFile>? Classifiers { get; set; }
}

public sealed class ExtractJson
{
    public List<string>? Exclude { get; set; }
}

public sealed class RuleJson
{
    public string? Action { get; set; }
    public OsRule? Os { get; set; }
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsRule
{
    public string? Name { get; set; }
    public string? Arch { get; set; }
    public string? Version { get; set; }
}

public sealed class LoggingSection
{
    public LoggingClient? Client { get; set; }
}

public sealed class LoggingClient
{
    public string? Argument { get; set; }
    public DownloadFile? File { get; set; }
    public string? Type { get; set; }
}

public sealed class JavaVersionJson
{
    public string? Component { get; set; }
    public int MajorVersion { get; set; }
}

/// <summary>合并继承链之后的版本（Forge 的 json 只有差异，要跟父版本拼起来）。</summary>
public sealed class ResolvedVersion
{
    public required string Id { get; init; }
    /// <summary>客户端 jar 用哪个版本目录，1.7.10 + Forge 时是 1.7.10。</summary>
    public required string ClientJarVersionId { get; init; }
    public string? MainClass { get; init; }
    public string? MinecraftArguments { get; init; }
    public AssetIndexRef? AssetIndex { get; init; }
    public VersionDownloads? Downloads { get; init; }
    public LoggingSection? Logging { get; init; }
    public JavaVersionJson? JavaVersion { get; init; }
    public required IReadOnlyList<LibraryJson> Libraries { get; init; }

    [JsonIgnore]
    public JavaRequirement? Java =>
        JavaVersion is { Component: not null } jv ? new JavaRequirement(jv.Component, jv.MajorVersion) : null;
}
