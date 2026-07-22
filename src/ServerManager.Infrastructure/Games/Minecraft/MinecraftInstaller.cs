using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Minecraft;

public sealed class MinecraftInstaller(
    HttpClient httpClient,
    IMinecraftVersionCatalog versionCatalog,
    IJavaRuntimeLocator javaRuntimeLocator,
    IGameServerStore gameServerStore,
    IJavaRuntimeInstaller? javaRuntimeInstaller = null) : IMinecraftInstaller
{
    public async Task<MinecraftInstallResult> InstallAsync(
        MinecraftInstallRequest request,
        CancellationToken cancellationToken = default) =>
        await InstallInternalAsync(request, null, cancellationToken);

    public async Task<MinecraftInstallResult> InstallWithProgressAsync(
        MinecraftInstallRequest request,
        IProgress<MinecraftInstallStep> progress,
        CancellationToken cancellationToken = default) =>
        await InstallInternalAsync(request, progress, cancellationToken);

    private async Task<MinecraftInstallResult> InstallInternalAsync(
        MinecraftInstallRequest request,
        IProgress<MinecraftInstallStep>? progress,
        CancellationToken cancellationToken)
    {
        Report(
            progress,
            MinecraftCreationStage.ValidatingDestination,
            "Validating the destination, port, EULA, and memory settings.",
            5);
        ValidateRequest(request);

        var destination = Path.GetFullPath(request.DestinationPath);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException(
                "The Minecraft destination must not already exist. Import existing data instead.");
        }

        var parent = Directory.GetParent(destination)
            ?? throw new DirectoryNotFoundException("The destination parent folder is invalid.");
        Directory.CreateDirectory(parent.FullName);
        var staging = Path.Combine(
            parent.FullName,
            $".1salem-minecraft-staging-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(staging);
            Report(
                progress,
                MinecraftCreationStage.ResolvingVersion,
                "Resolving official Minecraft release metadata.",
                10);
            var version = await versionCatalog.GetVersionAsync(request.Version, cancellationToken);
            Report(
                progress,
                MinecraftCreationStage.DetectingJava,
                $"Detecting Java {version.RequiredJavaMajor} or newer.",
                18);
            var java = !string.IsNullOrWhiteSpace(request.JavaExecutablePath)
                ? await javaRuntimeLocator.InspectAsync(
                    request.JavaExecutablePath,
                    cancellationToken)
                : await javaRuntimeLocator.FindAsync(
                    version.RequiredJavaMajor,
                    cancellationToken);
            if (java is not null && java.MajorVersion < version.RequiredJavaMajor)
            {
                throw new InvalidOperationException(
                    $"Configured Java {java.MajorVersion} is incompatible; " +
                    $"Minecraft {version.Id} requires Java {version.RequiredJavaMajor} or newer.");
            }

            if (java is null &&
                request.InstallJavaIfMissing &&
                javaRuntimeInstaller is not null)
            {
                Report(
                    progress,
                    MinecraftCreationStage.InstallingJava,
                    $"Installing a verified Java {version.RequiredJavaMajor} runtime.",
                    24);
                java = await javaRuntimeInstaller.InstallAsync(
                    version.RequiredJavaMajor,
                    cancellationToken);
            }

            if (java is null)
            {
                throw new InvalidOperationException(
                    $"Java {version.RequiredJavaMajor} or newer is required. " +
                    "Choose a compatible java.exe or approve automatic Java installation.");
            }

            var jarPath = Path.Combine(staging, "server.jar");
            Report(
                progress,
                MinecraftCreationStage.DownloadingServer,
                $"Downloading the official Minecraft {version.Id} server.",
                32);
            await DownloadAndVerifyAsync(
                version,
                jarPath,
                progress,
                cancellationToken);
            await GenerateFilesAsync(
                staging,
                request,
                version,
                java,
                progress,
                cancellationToken);

            Directory.Move(staging, destination);
            var serverId = Guid.NewGuid();
            Report(
                progress,
                MinecraftCreationStage.RegisteringServer,
                "Registering the server in the Agent database.",
                72);
            await gameServerStore.UpsertAsync(
                new GameServerDefinition(
                    serverId,
                    GameType.Minecraft,
                    request.Name,
                    destination,
                    request.Port,
                    version.Id,
                    DateTimeOffset.UtcNow,
                    ServerState.Stopped,
                    request.MinimumMemoryMb,
                    request.MaximumMemoryMb,
                    java.ExecutablePath,
                    request.PreferredAdapterId,
                    request.AutoStart,
                    request.AutoRestart),
                ServerState.Stopped,
                cancellationToken);
            return new MinecraftInstallResult(
                serverId,
                destination,
                version.Id,
                version.Sha1,
                DateTimeOffset.UtcNow,
                java.ExecutablePath,
                java.MajorVersion);
        }
        catch
        {
            DeleteOwnedStagingDirectory(staging, parent.FullName);
            throw;
        }
    }

    private async Task DownloadAndVerifyAsync(
        MinecraftVersionDescriptor version,
        string destination,
        IProgress<MinecraftInstallStep>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            version.ServerDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = new FileStream(
                         destination,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        Report(
            progress,
            MinecraftCreationStage.VerifyingDownload,
            "Verifying the official server JAR hash and size.",
            42);
        await using var file = File.OpenRead(destination);
        var hash = Convert.ToHexString(await SHA1.HashDataAsync(file, cancellationToken))
            .ToLowerInvariant();
        if (!hash.Equals(version.Sha1, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Minecraft server JAR hash mismatch. Expected {version.Sha1}, received {hash}.");
        }

        if (version.SizeBytes > 0 && new FileInfo(destination).Length != version.SizeBytes)
        {
            throw new InvalidDataException("Minecraft server JAR size did not match official metadata.");
        }
    }

    private static async Task GenerateFilesAsync(
        string staging,
        MinecraftInstallRequest request,
        MinecraftVersionDescriptor version,
        JavaRuntimeInfo java,
        IProgress<MinecraftInstallStep>? progress,
        CancellationToken cancellationToken)
    {
        Report(
            progress,
            MinecraftCreationStage.CreatingFolders,
            "Creating the managed server folder structure.",
            48);
        Directory.CreateDirectory(Path.Combine(staging, "logs"));
        Directory.CreateDirectory(Path.Combine(staging, "backups"));
        Directory.CreateDirectory(Path.Combine(staging, ".1salem"));

        Report(
            progress,
            MinecraftCreationStage.WritingProperties,
            "Writing validated server.properties.",
            54);
        await File.WriteAllTextAsync(
            Path.Combine(staging, "server.properties"),
            MinecraftPropertiesSerializer.Serialize(request.Settings, request.Port),
            new UTF8Encoding(false),
            cancellationToken);
        Report(
            progress,
            MinecraftCreationStage.WritingMemoryArguments,
            "Writing the selected Xms and Xmx memory settings.",
            59);
        await File.WriteAllTextAsync(
            Path.Combine(staging, "user_jvm_args.txt"),
            $"-Xms{request.MinimumMemoryMb}M{Environment.NewLine}-Xmx{request.MaximumMemoryMb}M{Environment.NewLine}",
            new UTF8Encoding(false),
            cancellationToken);
        Report(
            progress,
            MinecraftCreationStage.AcceptingEula,
            "Recording the explicit Minecraft EULA acceptance.",
            63);
        await File.WriteAllTextAsync(
            Path.Combine(staging, "eula.txt"),
            $"# Accepted explicitly through 1Salem Server Manager{Environment.NewLine}eula=true{Environment.NewLine}",
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(staging, "start-server.cmd"),
            $"@echo off{Environment.NewLine}\"{java.ExecutablePath}\" @user_jvm_args.txt -jar server.jar nogui{Environment.NewLine}",
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(staging, ".1salem", "java-path.txt"),
            java.ExecutablePath,
            new UTF8Encoding(false),
            cancellationToken);

        var metadata = new MinecraftServerMetadata(
            "MinecraftJavaVanilla",
            version.Id,
            "server.jar",
            version.Sha1,
            version.RequiredJavaMajor,
            DateTimeOffset.UtcNow);
        await using var metadataStream = File.Create(
            Path.Combine(staging, ".1salem", "metadata.json"));
        await JsonSerializer.SerializeAsync(
            metadataStream,
            metadata,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true },
            cancellationToken);
    }

    private static void Report(
        IProgress<MinecraftInstallStep>? progress,
        MinecraftCreationStage stage,
        string message,
        int percent) =>
        progress?.Report(new MinecraftInstallStep(stage, message, percent));

    private static void ValidateRequest(MinecraftInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.EulaAccepted)
        {
            throw new InvalidOperationException(
                "Explicit Minecraft EULA acceptance is required before installation.");
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80)
        {
            throw new ArgumentException("The server name must contain 1 to 80 characters.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Version))
        {
            throw new ArgumentException("A Minecraft release version is required.", nameof(request));
        }

        if (request.Port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The port must be between 1 and 65535.");
        }

        if (request.MinimumMemoryMb < 512 ||
            request.MaximumMemoryMb < request.MinimumMemoryMb ||
            request.MaximumMemoryMb > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Minecraft memory must be at least 512 MB and Xmx must be greater than or equal to Xms.");
        }

        _ = MinecraftPropertiesSerializer.Serialize(request.Settings, request.Port);
    }

    private static void DeleteOwnedStagingDirectory(string staging, string parent)
    {
        if (!Directory.Exists(staging) ||
            !SafePathPolicy.IsWithinRoot(staging, parent) ||
            !Path.GetFileName(staging).StartsWith(
                ".1salem-minecraft-staging-",
                StringComparison.Ordinal))
        {
            return;
        }

        Directory.Delete(staging, true);
    }
}
