namespace EML.Core.Java;

public sealed record JavaInstallation(
    string ExecutablePath,
    string Version,
    string Architecture);