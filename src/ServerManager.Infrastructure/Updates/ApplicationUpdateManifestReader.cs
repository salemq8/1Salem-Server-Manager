using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Updates;

public static class ApplicationUpdateManifestReader
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

    public static ApplicationUpdateManifest Read(
        string json,
        ApplicationUpdateChannel channel,
        bool allowLoopbackTestSources = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var manifest = JsonSerializer.Deserialize<ApplicationUpdateManifest>(
            json,
            SerializerOptions)
            ?? throw new InvalidDataException("The update manifest is empty.");
        ApplicationUpdateManifestPolicy.Validate(
            manifest,
            channel,
            allowLoopbackTestSources);
        return manifest;
    }
}
