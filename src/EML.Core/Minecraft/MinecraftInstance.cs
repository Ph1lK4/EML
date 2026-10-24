namespace EML.Core.Minecraft;

public sealed record MinecraftInstance(
    string Name,
    string InstancePath,
    string GameDirectory,
    IReadOnlyList<string> VersionIds);