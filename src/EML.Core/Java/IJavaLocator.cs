namespace EML.Core.Java;

public interface IJavaLocator
{
    Task<IReadOnlyList<JavaInstallation>> FindAllAsync(
        CancellationToken cancellationToken = default);
}