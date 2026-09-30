using System.Text;
using ServerManager.Core;
using ServerManager.Core.Minecraft;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Agent;

/// <summary>
/// The Gameplay page's server.properties writer. It keeps a restore point like every settings
/// change, writes atomically and reads the result back; unlike the full settings editor it never
/// restarts anything, and a failed write puts the previous file back as it was.
/// </summary>
public sealed partial class ServerConfigurationService : IMinecraftPropertiesWriter
{
    public async Task<OperationResult> WriteAsync(
        Guid serverId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var server = await GetMinecraftServerAsync(serverId, cancellationToken);
        foreach (var (key, value) in values)
        {
            if (!MinecraftGameplayPropertyPolicy.TryNormalize(key, value, out var normalized, out var error) ||
                !string.Equals(normalized, value, StringComparison.Ordinal))
            {
                return OperationResult.Fail("InvalidValue", error ?? $"'{key}' must be written in its normalized form.");
            }
        }

        var propertiesPath = Path.Combine(server.RootPath, "server.properties");
        var original = File.Exists(propertiesPath)
            ? await File.ReadAllTextAsync(propertiesPath, cancellationToken)
            : null;
        var restorePoint = await restorePoints.CreateAsync(
            server,
            "minecraft-gameplay",
            ["server.properties"],
            values,
            cancellationToken);
        try
        {
            await WriteManagedFileAsync(
                server.RootPath,
                "server.properties",
                MinecraftPropertiesSerializer.SetValues(original ?? string.Empty, values),
                cancellationToken);
            var written = MinecraftPropertiesSerializer.Parse(await File.ReadAllTextAsync(propertiesPath, cancellationToken));
            foreach (var (key, value) in values)
            {
                if (!written.TryGetValue(key, out var readBack) || !string.Equals(readBack, value, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"server.properties did not read back {key}={value}.");
                }
            }

            await restorePoints.CompleteAsync(server.Id, restorePoint.Id, markKnownWorking: false, cancellationToken);
            return OperationResult.Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try
            {
                if (original is not null)
                {
                    await File.WriteAllTextAsync(propertiesPath, original, new UTF8Encoding(false), CancellationToken.None);
                }
            }
            catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException)
            {
                return OperationResult.Fail(
                    "MinecraftGameplayApplyFailed",
                    $"Saving failed ({exception.Message}) and the previous server.properties could not be put back " +
                    $"({restoreException.Message}). Restore point {restorePoint.Id} holds it.");
            }

            return OperationResult.Fail(
                "MinecraftGameplayApplyFailed",
                $"Saving failed and server.properties was left as it was: {exception.Message}");
        }
    }
}
