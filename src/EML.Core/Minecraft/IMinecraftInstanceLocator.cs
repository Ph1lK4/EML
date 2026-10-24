namespace EML.Core.Minecraft;

public interface IMinecraftInstanceLocator
{
    Task<IReadOnlyList<MinecraftInstance>> FindAllAsync(
        CancellationToken cancellationToken = default);
}