using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Agent;

public sealed class PalworldControlCenterService(
    IGameServerStore serverStore,
    IProcessSupervisor processSupervisor,
    INetworkService networkService,
    ISecretStore secretStore,
    ISettingsStore settingsStore,
    IAuditLogStore auditLogStore,
    GameServerOrchestrator orchestrator,
    PalworldRestClient restClient,
    PalworldManagementCache managementCache,
    ConfigurationRestorePointService restorePoints)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<PalworldRestEnableResponse> EnableManagementAsync(
        Guid serverId,
        PalworldRestEnableRequest request,
        CancellationToken cancellationToken = default)
    {
        var stage = PalworldManagementActivationStage.ResolvingServer;
        GameServerDefinition? server = null;
        PalworldServerMetadata? originalMetadata = null;
        string? safetyBackup = null;
        ConfigurationRestorePointItem? restorePoint = null;
        var configurationWritten = false;
        var metadataWritten = false;
        var wasRunning = false;
        try
        {
            if (!request.ConfirmRestart)
            {
                throw new ActivationException(
                    "RestartConfirmationRequired",
                    "Confirmation is required before Palworld configuration is changed or a running server is restarted.",
                    stage);
            }

            server = await GetServerAsync(serverId, cancellationToken);
            stage = PalworldManagementActivationStage.ValidatingConfiguration;
            var document = await PalworldConfigurationFile.ReadAsync(
                server.RootPath,
                cancellationToken);
            var currentValues = PalworldSettingsSerializer.ParseValues(
                document.Content);
            originalMetadata = await ReadMetadataAsync(
                server.RootPath,
                cancellationToken);
            wasRunning = await IsRunningAsync(server.Id, cancellationToken);
            restorePoint = await restorePoints.CreateAsync(
                server,
                "palworld-management-enable",
                [
                    PalworldConfigurationFile.RelativePath,
                    Path.Combine(".1salem", "metadata.json")
                ],
                new Dictionary<string, string>
                {
                    ["REST API"] = "enabled",
                    ["REST API bind"] = "127.0.0.1"
                },
                cancellationToken);

            stage = PalworldManagementActivationStage.CreatingSafetyBackup;
            safetyBackup = await PalworldConfigurationFile.CreateSafetyBackupAsync(
                server.RootPath,
                "management-enable",
                cancellationToken);

            stage = PalworldManagementActivationStage.SelectingLocalPort;
            var currentIniEnabled = ReadBoolean(
                currentValues,
                "RESTAPIEnabled",
                false);
            var currentIniPort = ReadInteger(
                currentValues,
                "RESTAPIPort",
                originalMetadata.Settings.RestApiPort);
            var requestedPort = request.PreferredPort ??
                                (currentIniPort is > 0 and <= 65_535
                                    ? currentIniPort
                                    : originalMetadata.Settings.RestApiPort);
            var restPort = await SelectRestPortAsync(
                requestedPort is > 0 and <= 65_535 ? requestedPort : 8212,
                request.PreferredPort is not null,
                currentIniEnabled,
                cancellationToken);

            stage = PalworldManagementActivationStage.ProtectingCredentials;
            var (adminPassword, protectedAdminPassword) =
                ResolveOrCreateAdminPassword(originalMetadata);

            stage = PalworldManagementActivationStage.WritingConfiguration;
            var rendered = PalworldSettingsSerializer.MergeValues(
                document.Content,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["RESTAPIEnabled"] = "true",
                    ["RESTAPIPort"] =
                        restPort.ToString(CultureInfo.InvariantCulture),
                    ["AdminPassword"] =
                        PalworldSettingsSerializer.RenderText(adminPassword)
                });
            var verificationValues = PalworldSettingsSerializer.ParseValues(rendered);
            if (!ReadBoolean(verificationValues, "RESTAPIEnabled", false) ||
                ReadInteger(verificationValues, "RESTAPIPort", 0) != restPort ||
                string.IsNullOrWhiteSpace(PalworldSettingsSerializer.ReadText(
                    verificationValues["AdminPassword"])))
            {
                throw new ActivationException(
                    "ConfigurationValidationFailed",
                    "The temporary Palworld configuration did not contain the required REST settings.",
                    stage);
            }

            await PalworldConfigurationFile.WriteAtomicAsync(
                document,
                rendered,
                cancellationToken);
            configurationWritten = true;

            var updatedMetadata = originalMetadata with
            {
                ProtectedAdminPassword = protectedAdminPassword,
                Settings = originalMetadata.Settings with
                {
                    RestApiEnabled = true,
                    RestApiPort = restPort
                }
            };
            await WriteMetadataAtomicAsync(
                server.RootPath,
                updatedMetadata,
                cancellationToken);
            metadataWritten = true;

            if (!wasRunning)
            {
                var waiting = new PalworldManagementSnapshot(
                    server.Id,
                    PalworldManagementState.Connecting,
                    true,
                    restPort,
                    "Management is enabled. Start Palworld to verify the localhost connection.",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    [],
                    DateTimeOffset.UtcNow);
                managementCache.Set(waiting);
                await WriteAuditAsync(
                    server,
                    "PalworldManagementEnabled",
                    true,
                    $"RESTAPIPort={restPort}; Restarted=False",
                    cancellationToken);
                await restorePoints.CompleteAsync(
                    server.Id,
                    restorePoint.Id,
                    markKnownWorking: false,
                    cancellationToken);
                return new PalworldRestEnableResponse(
                    true,
                    waiting.StatusMessage,
                    false,
                    false,
                    waiting,
                    PalworldManagementActivationStage.Connected,
                    ConfigurationBackupPath: safetyBackup);
            }

            stage = PalworldManagementActivationStage.RestartingServer;
            await orchestrator.RestartAsync(server.Id, cancellationToken);
            stage = PalworldManagementActivationStage.WaitingForGameProcess;
            await WaitForGameProcessAsync(server.Id, cancellationToken);
            stage = PalworldManagementActivationStage.TestingRestApi;
            var connected = await WaitForRestAsync(server, cancellationToken);
            managementCache.Set(connected);
            await WriteAuditAsync(
                server,
                "PalworldManagementEnabled",
                true,
                $"RESTAPIPort={restPort}; Restarted=True; Verified=True",
                cancellationToken);
            await restorePoints.CompleteAsync(
                server.Id,
                restorePoint.Id,
                markKnownWorking: true,
                cancellationToken);
            return new PalworldRestEnableResponse(
                true,
                "Palworld management is connected through localhost and remained enabled after restart.",
                true,
                true,
                connected,
                PalworldManagementActivationStage.Connected,
                ConfigurationBackupPath: safetyBackup);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var errorCode = exception is ActivationException activation
                ? activation.Code
                : MapErrorCode(exception);
            var errorStage = exception is ActivationException stageException
                ? stageException.Stage
                : stage;
            var rolledBack = false;
            if (server is not null &&
                originalMetadata is not null &&
                safetyBackup is not null &&
                (configurationWritten || metadataWritten))
            {
                try
                {
                    stage = PalworldManagementActivationStage.RollingBack;
                    await PalworldConfigurationFile.RestoreAtomicAsync(
                        server.RootPath,
                        safetyBackup,
                        cancellationToken);
                    await WriteMetadataAtomicAsync(
                        server.RootPath,
                        originalMetadata,
                        cancellationToken);
                    if (wasRunning)
                    {
                        await orchestrator.RestartAsync(server.Id, cancellationToken);
                    }

                    rolledBack = true;
                }
                catch (Exception rollbackException)
                {
                    exception = new AggregateException(
                        exception,
                        new InvalidOperationException(
                            $"Rollback failed: {rollbackException.Message}",
                            rollbackException));
                }
            }

            var status = PalworldRestClient.Unavailable(
                serverId,
                originalMetadata?.Settings.RestApiPort ?? 8212,
                $"Management activation failed during {errorStage}: {exception.Message}");
            managementCache.Set(status);
            if (server is not null)
            {
                await WriteAuditAsync(
                    server,
                    "PalworldManagementEnabled",
                    false,
                    $"Stage={errorStage}; Error={errorCode}; RolledBack={rolledBack}; Detail={exception.Message}",
                    CancellationToken.None);
            }

            return new PalworldRestEnableResponse(
                false,
                status.StatusMessage,
                wasRunning,
                false,
                status,
                PalworldManagementActivationStage.Failed,
                errorCode,
                safetyBackup,
                rolledBack);
        }
    }

    public async Task<PalworldRestEnableResponse> DisableManagementAsync(
        Guid serverId,
        PalworldManagementActionRequest request,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        if (!request.ConfirmRestart)
        {
            var current = managementCache.Get(serverId) ??
                          await restClient.GetSnapshotAsync(server, cancellationToken);
            return new PalworldRestEnableResponse(
                false,
                "Confirmation is required before local management is disabled.",
                false,
                false,
                current,
                PalworldManagementActivationStage.None,
                "RestartConfirmationRequired");
        }

        var document = await PalworldConfigurationFile.ReadAsync(
            server.RootPath,
            cancellationToken);
        var metadata = await ReadMetadataAsync(server.RootPath, cancellationToken);
        var backup = await PalworldConfigurationFile.CreateSafetyBackupAsync(
            server.RootPath,
            "management-disable",
            cancellationToken);
        var restorePoint = await restorePoints.CreateAsync(
            server,
            "palworld-management-disable",
            [
                PalworldConfigurationFile.RelativePath,
                Path.Combine(".1salem", "metadata.json")
            ],
            new Dictionary<string, string>
            {
                ["REST API"] = "disabled"
            },
            cancellationToken);
        var wasRunning = await IsRunningAsync(server.Id, cancellationToken);
        try
        {
            var updated = PalworldSettingsSerializer.MergeValues(
                document.Content,
                new Dictionary<string, string>
                {
                    ["RESTAPIEnabled"] = "false"
                });
            await PalworldConfigurationFile.WriteAtomicAsync(
                document,
                updated,
                cancellationToken);
            await WriteMetadataAtomicAsync(
                server.RootPath,
                metadata with
                {
                    Settings = metadata.Settings with { RestApiEnabled = false }
                },
                cancellationToken);
            if (wasRunning)
            {
                await orchestrator.RestartAsync(server.Id, cancellationToken);
            }

            var disabled = PalworldRestClient.Disabled(
                server.Id,
                metadata.Settings.RestApiPort,
                "Palworld local management is disabled.");
            managementCache.Set(disabled);
            await WriteAuditAsync(
                server,
                "PalworldManagementDisabled",
                true,
                $"Restarted={wasRunning}",
                cancellationToken);
            await restorePoints.CompleteAsync(
                server.Id,
                restorePoint.Id,
                markKnownWorking: wasRunning,
                cancellationToken);
            return new PalworldRestEnableResponse(
                true,
                disabled.StatusMessage,
                wasRunning,
                true,
                disabled,
                PalworldManagementActivationStage.Disabled,
                ConfigurationBackupPath: backup);
        }
        catch (Exception exception)
        {
            var rolledBack = false;
            try
            {
                await PalworldConfigurationFile.RestoreAtomicAsync(
                    server.RootPath,
                    backup,
                    CancellationToken.None);
                await WriteMetadataAtomicAsync(
                    server.RootPath,
                    metadata,
                    CancellationToken.None);
                if (wasRunning)
                {
                    await orchestrator.RestartAsync(
                        server.Id,
                        CancellationToken.None);
                }

                rolledBack = true;
            }
            catch (Exception rollbackException)
            {
                exception = new AggregateException(
                    exception,
                    new InvalidOperationException(
                        $"Rollback failed: {rollbackException.Message}",
                        rollbackException));
            }

            return new PalworldRestEnableResponse(
                false,
                rolledBack
                    ? $"Disabling management failed and the previous configuration was restored: {exception.Message}"
                    : $"Disabling management failed and rollback also failed: {exception.Message}",
                wasRunning,
                false,
                managementCache.Get(server.Id) ??
                PalworldRestClient.Unavailable(
                    server.Id,
                    metadata.Settings.RestApiPort,
                    exception.Message),
                PalworldManagementActivationStage.Failed,
                MapErrorCode(exception),
                backup,
                rolledBack);
        }
    }

    public async Task<PalworldManagementSnapshot> RetryManagementAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        var snapshot = await restClient.GetSnapshotAsync(server, cancellationToken);
        managementCache.Set(snapshot);
        return snapshot;
    }

    public async Task<PalworldRestOperationResult> TestManagementAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        return await restClient.TestConnectionAsync(server, cancellationToken);
    }

    public async Task<PalworldRestOperationResult> SaveWorldNowAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        var result = await restClient.SaveWorldAsync(server, cancellationToken);
        if (result.Success)
        {
            var key = $"backup.center.{server.Id:N}";
            var settings = await settingsStore.GetAsync<BackupCenterSettings>(
                key,
                cancellationToken) ??
                new BackupCenterSettings(
                    server.Id,
                    false,
                    "@daily",
                    BackupDestinationPolicy.GetDefaultRoot(server),
                    20,
                    30,
                    100L * 1024 * 1024 * 1024,
                    true,
                    true,
                    null,
                    null,
                    null);
            await settingsStore.SetAsync(
                key,
                settings with
                {
                    LastSuccessfulWorldSaveAtUtc =
                        result.CompletedAtUtc
                },
                cancellationToken);
        }

        await WriteAuditAsync(
            server,
            "PalworldSaveWorld",
            result.Success,
            $"Code={result.Code}; Message={result.Message}",
            cancellationToken);
        return result;
    }

    public async Task<PalworldRestOperationResult> AnnounceAsync(
        Guid serverId,
        PalworldAnnouncementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var server = await GetServerAsync(serverId, cancellationToken);
        var result = await restClient.AnnounceAsync(
            server,
            request.Message,
            cancellationToken);
        await WriteAuditAsync(
            server,
            "PalworldAnnouncement",
            result.Success,
            $"Code={result.Code}; Length={request.Message.Length}",
            cancellationToken);
        return result;
    }

    public async Task<PalworldWorldSettingsResponse> GetWorldSettingsAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        var document = await PalworldConfigurationFile.ReadAsync(
            server.RootPath,
            cancellationToken);
        var current = PalworldSettingsSerializer.ParseValues(document.Content);
        var defaults = await ReadOfficialDefaultsAsync(
            server.RootPath,
            cancellationToken);
        return new PalworldWorldSettingsResponse(
            server.Id,
            document.Path,
            PalworldWorldSettingsCatalog.Describe(current, defaults),
            PalworldWorldSettingsCatalog.Categories,
            PalworldWorldSettingsCatalog.Presets,
            UnknownSettings: current
                .Where(item =>
                    !PalworldWorldSettingsCatalog.ManagedNames.Contains(
                        item.Key))
                .ToDictionary(
                    item => item.Key,
                    item => MaskSecret(item.Key, item.Value),
                    StringComparer.OrdinalIgnoreCase));
    }

    public async Task<PalworldWorldSettingsPresetResponse> GetWorldSettingsPresetAsync(
        Guid serverId,
        string preset,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        var defaults = await ReadOfficialDefaultsAsync(
            server.RootPath,
            cancellationToken);
        return new PalworldWorldSettingsPresetResponse(
            preset,
            PalworldWorldSettingsCatalog.GetPreset(preset, defaults));
    }

    public async Task<PalworldWorldSettingsUpdateResponse> UpdateWorldSettingsAsync(
        Guid serverId,
        PalworldWorldSettingsUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        if (request.ApplyAndRestart && !request.ConfirmRestart)
        {
            return WorldFailure(
                "RestartConfirmationRequired",
                "Confirm the restart before applying Palworld world settings.");
        }

        IReadOnlyDictionary<string, string> rendered;
        try
        {
            rendered = PalworldWorldSettingsCatalog.ValidateAndRender(
                request.Changes);
        }
        catch (ArgumentException exception)
        {
            return WorldFailure("InvalidWorldSetting", exception.Message);
        }

        if (rendered.Count == 0)
        {
            return WorldFailure(
                "NoChanges",
                "No Palworld world setting changes were supplied.");
        }

        var management = managementCache.Get(server.Id);
        var players = management?.PlayersOnline ?? 0;
        var document = await PalworldConfigurationFile.ReadAsync(
            server.RootPath,
            cancellationToken);
        var originalMetadata = await ReadMetadataAsync(
            server.RootPath,
            cancellationToken);
        var backup = await PalworldConfigurationFile.CreateSafetyBackupAsync(
            server.RootPath,
            "world-settings",
            cancellationToken);
        var restorePoint = await restorePoints.CreateAsync(
            server,
            "palworld-world-settings",
            [
                PalworldConfigurationFile.RelativePath,
                Path.Combine(".1salem", "metadata.json")
            ],
            request.Changes,
            cancellationToken);
        var wasRunning = await IsRunningAsync(server.Id, cancellationToken);
        var restarted = false;
        try
        {
            var updatedContent = PalworldSettingsSerializer.MergeValues(
                document.Content,
                rendered);
            var parsedUpdated = PalworldSettingsSerializer.ParseValues(updatedContent);
            VerifyRenderedValues(parsedUpdated, rendered);
            await PalworldConfigurationFile.WriteAtomicAsync(
                document,
                updatedContent,
                cancellationToken);

            var updatedMetadata = ApplyMetadataChanges(
                originalMetadata,
                rendered);
            await WriteMetadataAtomicAsync(
                server.RootPath,
                updatedMetadata,
                cancellationToken);
            if (!updatedMetadata.Settings.ServerName.Equals(
                    server.Name,
                    StringComparison.Ordinal))
            {
                await serverStore.UpsertAsync(
                    server with { Name = updatedMetadata.Settings.ServerName },
                    server.State,
                    cancellationToken);
            }

            if (request.ApplyAndRestart && wasRunning)
            {
                if (request.AnnouncementSeconds > 0 && players > 0)
                {
                    var message = string.IsNullOrWhiteSpace(
                        request.AnnouncementMessage)
                        ? $"Server settings will be applied in {request.AnnouncementSeconds} seconds."
                        : request.AnnouncementMessage;
                    var announcement = await restClient.AnnounceAsync(
                        server,
                        message,
                        cancellationToken);
                    if (!announcement.Success)
                    {
                        throw new InvalidOperationException(
                            $"Player announcement failed: {announcement.Message}");
                    }

                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Clamp(
                            request.AnnouncementSeconds,
                            1,
                            120)),
                        cancellationToken);
                }

                var saved = await restClient.SaveWorldAsync(
                    server,
                    cancellationToken);
                if (!saved.Success)
                {
                    throw new InvalidOperationException(
                        $"World save failed: {saved.Message}");
                }

                await orchestrator.RestartAsync(server.Id, cancellationToken);
                restarted = true;
                await WaitForGameProcessAsync(server.Id, cancellationToken);
            }

            var verifyDocument = await PalworldConfigurationFile.ReadAsync(
                server.RootPath,
                cancellationToken);
            VerifyRenderedValues(
                PalworldSettingsSerializer.ParseValues(verifyDocument.Content),
                rendered);
            var restVerified = true;
            if (restarted &&
                originalMetadata.Settings.RestApiEnabled)
            {
                var restSettings = await WaitForSettingsAsync(
                    server,
                    cancellationToken);
                restVerified = restSettings.Success;
                if (!restVerified)
                {
                    throw new InvalidOperationException(restSettings.Message);
                }

                VerifyActiveRestValues(restSettings.Settings, rendered);
            }

            foreach (var (name, value) in rendered)
            {
                var old = PalworldSettingsSerializer.ParseValues(document.Content)
                    .GetValueOrDefault(name, "(not set)");
                await auditLogStore.WriteAsync(
                    "LocalAdministrator",
                    "PalworldWorldSettingChanged",
                    $"{server.Id}:{name}",
                    true,
                    $"Old={MaskSecret(name, old)}; New={MaskSecret(name, value)}; Restarted={restarted}; Verified={restVerified}",
                    cancellationToken);
            }

            await restorePoints.CompleteAsync(
                server.Id,
                restorePoint.Id,
                restarted && restVerified,
                cancellationToken);
            return new PalworldWorldSettingsUpdateResponse(
                true,
                restarted
                    ? "Palworld settings were saved, the world was saved, the server restarted, and the configuration was verified."
                    : "Palworld settings were saved for the next restart.",
                MaskChanges(rendered),
                backup,
                restarted,
                true,
                false,
                players);
        }
        catch (Exception exception)
        {
            var rolledBack = false;
            try
            {
                await PalworldConfigurationFile.RestoreAtomicAsync(
                    server.RootPath,
                    backup,
                    CancellationToken.None);
                await WriteMetadataAtomicAsync(
                    server.RootPath,
                    originalMetadata,
                    CancellationToken.None);
                if (!originalMetadata.Settings.ServerName.Equals(
                        server.Name,
                        StringComparison.Ordinal))
                {
                    await serverStore.UpsertAsync(
                        server with
                        {
                            Name = originalMetadata.Settings.ServerName
                        },
                        server.State,
                        CancellationToken.None);
                }
                if (wasRunning)
                {
                    await orchestrator.RestartAsync(
                        server.Id,
                        CancellationToken.None);
                }

                rolledBack = true;
            }
            catch
            {
                // The exact primary error is returned; audit records rollback failure.
            }

            await auditLogStore.WriteAsync(
                "LocalAdministrator",
                "PalworldWorldSettingsChanged",
                server.Id.ToString(),
                false,
                $"Error={exception.Message}; Restarted={restarted}; RolledBack={rolledBack}",
                CancellationToken.None);
            return new PalworldWorldSettingsUpdateResponse(
                false,
                $"Applying Palworld settings failed: {exception.Message}",
                MaskChanges(rendered),
                backup,
                restarted,
                false,
                rolledBack,
                players,
                "WorldSettingsApplyFailed");
        }
    }

    public async Task<IReadOnlyList<PalworldConfigurationHistoryItem>>
        GetConfigurationHistoryAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        return PalworldConfigurationFile.ListHistory(server.RootPath)
            .Select(path =>
            {
                var info = new FileInfo(path);
                var id = Path.GetFileNameWithoutExtension(path);
                var parts = id.Split('-', 4);
                var reason = parts.Length == 4 ? parts[3] : "configuration";
                return new PalworldConfigurationHistoryItem(
                    id,
                    new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                    reason,
                    path,
                    info.Length);
            })
            .ToArray();
    }

    public async Task<OperationResult> RestoreConfigurationAsync(
        Guid serverId,
        PalworldRestoreConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.ConfirmRestart)
        {
            return OperationResult.Fail(
                "RestartConfirmationRequired",
                "Confirmation is required before restoring configuration.");
        }

        var server = await GetServerAsync(serverId, cancellationToken);
        var selected = PalworldConfigurationFile.ListHistory(server.RootPath)
            .FirstOrDefault(path =>
                Path.GetFileNameWithoutExtension(path).Equals(
                    request.HistoryId,
                    StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return OperationResult.Fail(
                "ConfigurationHistoryNotFound",
                "The selected configuration history item was not found.");
        }

        var originalMetadata = await ReadMetadataAsync(
            server.RootPath,
            cancellationToken);
        var safetyBackup =
            await PalworldConfigurationFile.CreateSafetyBackupAsync(
            server.RootPath,
            "before-history-restore",
            cancellationToken);
        var wasRunning = await IsRunningAsync(server.Id, cancellationToken);
        try
        {
            await PalworldConfigurationFile.RestoreAtomicAsync(
                server.RootPath,
                selected,
                cancellationToken);
            var restoredDocument = await PalworldConfigurationFile.ReadAsync(
                server.RootPath,
                cancellationToken);
            var restoredValues = PalworldSettingsSerializer.ParseValues(
                restoredDocument.Content);
            var restoredMetadata = ApplyMetadataChanges(
                originalMetadata,
                restoredValues);
            var restoredSettings = restoredMetadata.Settings with
            {
                Port = ReadInteger(
                    restoredValues,
                    "PublicPort",
                    restoredMetadata.Settings.Port),
                RconEnabled = ReadBoolean(
                    restoredValues,
                    "RCONEnabled",
                    restoredMetadata.Settings.RconEnabled),
                RconPort = ReadInteger(
                    restoredValues,
                    "RCONPort",
                    restoredMetadata.Settings.RconPort),
                RestApiEnabled = ReadBoolean(
                    restoredValues,
                    "RESTAPIEnabled",
                    restoredMetadata.Settings.RestApiEnabled),
                RestApiPort = ReadInteger(
                    restoredValues,
                    "RESTAPIPort",
                    restoredMetadata.Settings.RestApiPort)
            };
            var protectedAdminPassword =
                restoredValues.TryGetValue("AdminPassword", out var admin)
                    ? secretStore.Protect(
                        PalworldSettingsSerializer.ReadText(admin))
                    : restoredMetadata.ProtectedAdminPassword;
            restoredMetadata = restoredMetadata with
            {
                Settings = restoredSettings,
                ProtectedAdminPassword = protectedAdminPassword
            };
            await WriteMetadataAtomicAsync(
                server.RootPath,
                restoredMetadata,
                cancellationToken);
            await serverStore.UpsertAsync(
                server with
                {
                    Name = restoredSettings.ServerName,
                    Port = restoredSettings.Port
                },
                server.State,
                cancellationToken);

            if (wasRunning)
            {
                await orchestrator.RestartAsync(server.Id, cancellationToken);
                await WaitForGameProcessAsync(server.Id, cancellationToken);
                if (restoredSettings.RestApiEnabled)
                {
                    managementCache.Set(
                        await WaitForRestAsync(server, cancellationToken));
                }
                else
                {
                    managementCache.Set(PalworldRestClient.Disabled(
                        server.Id,
                        restoredSettings.RestApiPort,
                        "Palworld local management is disabled by the restored configuration."));
                }
            }

            await WriteAuditAsync(
                server,
                "PalworldConfigurationRestored",
                true,
                $"History={request.HistoryId}; Restarted={wasRunning}; Verified=True",
                cancellationToken);
            return OperationResult.Ok();
        }
        catch (Exception exception)
        {
            var rolledBack = false;
            try
            {
                await PalworldConfigurationFile.RestoreAtomicAsync(
                    server.RootPath,
                    safetyBackup,
                    CancellationToken.None);
                await WriteMetadataAtomicAsync(
                    server.RootPath,
                    originalMetadata,
                    CancellationToken.None);
                await serverStore.UpsertAsync(
                    server,
                    server.State,
                    CancellationToken.None);
                if (wasRunning)
                {
                    await orchestrator.RestartAsync(
                        server.Id,
                        CancellationToken.None);
                }

                rolledBack = true;
            }
            catch
            {
                // The audit entry and operation result retain the primary failure.
            }

            await WriteAuditAsync(
                server,
                "PalworldConfigurationRestored",
                false,
                $"History={request.HistoryId}; Error={exception.Message}; RolledBack={rolledBack}",
                CancellationToken.None);
            return OperationResult.Fail(
                "ConfigurationRestoreFailed",
                rolledBack
                    ? $"Configuration restore failed and the previous configuration was restored: {exception.Message}"
                    : $"Configuration restore and rollback failed: {exception.Message}");
        }
    }

    private async Task<GameServerDefinition> GetServerAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        var server = await serverStore.GetAsync(serverId, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Palworld server {serverId} is not registered.");
        if (server.Game != GameType.Palworld)
        {
            throw new ArgumentException(
                "The registered server is not Palworld.",
                nameof(serverId));
        }

        var resolvedRoot = Path.GetFullPath(server.RootPath);
        if (!Directory.Exists(resolvedRoot) ||
            !File.Exists(Path.Combine(resolvedRoot, "PalServer.exe")))
        {
            throw new DirectoryNotFoundException(
                "The registered Palworld root is missing or does not contain PalServer.exe.");
        }

        return server with { RootPath = resolvedRoot };
    }

    private async Task<int> SelectRestPortAsync(
        int requested,
        bool explicitPort,
        bool alreadyEnabled,
        CancellationToken cancellationToken)
    {
        if (alreadyEnabled)
        {
            return requested;
        }

        for (var port = requested; port <= Math.Min(65_535, requested + 20); port++)
        {
            var result = await networkService.TestPortAsync(
                port,
                "TCP",
                cancellationToken);
            if (result.IsAvailable)
            {
                return port;
            }

            if (explicitPort)
            {
                throw new ActivationException(
                    "RestPortConflict",
                    $"REST port {port} is already in use{(string.IsNullOrWhiteSpace(result.OwningProcess) ? "." : $" by {result.OwningProcess}.")}",
                    PalworldManagementActivationStage.SelectingLocalPort);
            }
        }

        throw new ActivationException(
            "RestPortConflict",
            $"No unused localhost management port was found from {requested} through {Math.Min(65_535, requested + 20)}.",
            PalworldManagementActivationStage.SelectingLocalPort);
    }

    private (string Plaintext, string Protected) ResolveOrCreateAdminPassword(
        PalworldServerMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.ProtectedAdminPassword))
        {
            try
            {
                var existing = secretStore.Unprotect(
                    metadata.ProtectedAdminPassword);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return (existing, metadata.ProtectedAdminPassword);
                }
            }
            catch (Exception exception) when (
                exception is CryptographicException or FormatException)
            {
                throw new ActivationException(
                    "AdminPasswordProtectionInvalid",
                    "The protected Palworld admin password could not be read. Enter a new admin password before repair.",
                    PalworldManagementActivationStage.ProtectingCredentials);
            }
        }

        var generated = Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return (generated, secretStore.Protect(generated));
    }

    private async Task WaitForGameProcessAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await processSupervisor.GetSnapshotAsync(
                serverId,
                cancellationToken);
            if (snapshot is
                {
                    State: ServerState.Running,
                    GameProcessId: not null
                })
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new ActivationException(
            "GameProcessStartupTimeout",
            "The real Palworld game child process did not become ready within 120 seconds.",
            PalworldManagementActivationStage.WaitingForGameProcess);
    }

    private async Task<PalworldManagementSnapshot> WaitForRestAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120);
        PalworldRestOperationResult? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await restClient.TestConnectionAsync(server, cancellationToken);
            if (last.Success)
            {
                var snapshot = await restClient.GetSnapshotAsync(
                    server,
                    cancellationToken);
                if (snapshot.State == PalworldManagementState.Online)
                {
                    return snapshot;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new ActivationException(
            last?.Code ?? "RestStartupTimeout",
            $"Palworld restarted, but the localhost REST API did not become ready within 120 seconds. {last?.Message}",
            PalworldManagementActivationStage.TestingRestApi);
    }

    private async Task<PalworldRestSettingsResult> WaitForSettingsAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120);
        PalworldRestSettingsResult? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await restClient.GetSettingsAsync(server, cancellationToken);
            if (last.Success)
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return last ?? new PalworldRestSettingsResult(
            false,
            "RestStartupTimeout",
            "Palworld settings verification timed out.",
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);
    }

    private async Task<bool> IsRunningAsync(
        Guid serverId,
        CancellationToken cancellationToken) =>
        await processSupervisor.GetSnapshotAsync(serverId, cancellationToken)
        is { State: ServerState.Running };

    private static async Task<PalworldServerMetadata> ReadMetadataAsync(
        string serverRoot,
        CancellationToken cancellationToken)
    {
        var path = SafePathPolicy.ResolveWithinRoot(
            serverRoot,
            Path.Combine(".1salem", "metadata.json"));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Managed Palworld metadata was not found.",
                path);
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PalworldServerMetadata>(
            stream,
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidDataException(
                "Managed Palworld metadata is invalid.");
    }

    private static async Task WriteMetadataAtomicAsync(
        string serverRoot,
        PalworldServerMetadata metadata,
        CancellationToken cancellationToken)
    {
        var path = SafePathPolicy.ResolveWithinRoot(
            serverRoot,
            Path.Combine(".1salem", "metadata.json"));
        var temporary = $"{path}.1salem-{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(metadata, JsonOptions),
                new UTF8Encoding(false),
                cancellationToken);
            await using (var verifyStream = File.OpenRead(temporary))
            {
                _ = await JsonSerializer.DeserializeAsync<PalworldServerMetadata>(
                        verifyStream,
                        JsonOptions,
                        cancellationToken)
                    ?? throw new InvalidDataException(
                        "Temporary Palworld metadata validation failed.");
            }

            if (OperatingSystem.IsWindows())
            {
                File.Replace(temporary, path, null, true);
            }
            else
            {
                File.Move(temporary, path, true);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>?>
        ReadOfficialDefaultsAsync(
            string serverRoot,
            CancellationToken cancellationToken)
    {
        var path = SafePathPolicy.ResolveWithinRoot(
            serverRoot,
            "DefaultPalWorldSettings.ini");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            return PalworldSettingsSerializer.ParseValues(content);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private PalworldServerMetadata ApplyMetadataChanges(
        PalworldServerMetadata metadata,
        IReadOnlyDictionary<string, string> rendered)
    {
        var settings = metadata.Settings;
        if (rendered.TryGetValue("ServerName", out var serverName))
        {
            settings = settings with
            {
                ServerName = PalworldSettingsSerializer.ReadText(serverName)
            };
        }

        if (rendered.TryGetValue("ServerDescription", out var description))
        {
            settings = settings with
            {
                Description = PalworldSettingsSerializer.ReadText(description)
            };
        }

        if (rendered.TryGetValue("ServerPlayerMaxNum", out var maxPlayers) &&
            int.TryParse(
                maxPlayers,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedMax))
        {
            settings = settings with { MaxPlayers = parsedMax };
        }

        var protectedServerPassword = metadata.ProtectedServerPassword;
        if (rendered.TryGetValue("ServerPassword", out var password))
        {
            protectedServerPassword = secretStore.Protect(
                PalworldSettingsSerializer.ReadText(password));
        }

        return metadata with
        {
            Settings = settings,
            ProtectedServerPassword = protectedServerPassword
        };
    }

    private async Task WriteAuditAsync(
        GameServerDefinition server,
        string action,
        bool succeeded,
        string detail,
        CancellationToken cancellationToken) =>
        await auditLogStore.WriteAsync(
            "LocalAdministrator",
            action,
            server.Id.ToString(),
            succeeded,
            detail,
            cancellationToken);

    private static void VerifyRenderedValues(
        IReadOnlyDictionary<string, string> actual,
        IReadOnlyDictionary<string, string> expected)
    {
        foreach (var (name, value) in expected)
        {
            if (!actual.TryGetValue(name, out var actualValue) ||
                !actualValue.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Palworld setting '{name}' did not pass write verification.");
            }
        }
    }

    private static void VerifyActiveRestValues(
        IReadOnlyDictionary<string, string> actual,
        IReadOnlyDictionary<string, string> expected)
    {
        foreach (var (name, rawExpected) in expected)
        {
            if (!actual.TryGetValue(name, out var rawActual))
            {
                continue;
            }

            var expectedValue = PalworldSettingsSerializer.ReadText(rawExpected)
                .Trim();
            var actualValue = PalworldSettingsSerializer.ReadText(rawActual)
                .Trim();
            if (double.TryParse(
                    expectedValue,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var expectedNumber) &&
                double.TryParse(
                    actualValue,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var actualNumber))
            {
                if (Math.Abs(expectedNumber - actualNumber) <= 0.000001)
                {
                    continue;
                }
            }
            else if (actualValue.Equals(
                         expectedValue,
                         StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            throw new InvalidDataException(
                $"Palworld reported a different active value for '{name}'.");
        }
    }

    private static bool ReadBoolean(
        IReadOnlyDictionary<string, string> values,
        string name,
        bool fallback) =>
        values.TryGetValue(name, out var value) &&
        bool.TryParse(value, out var result)
            ? result
            : fallback;

    private static int ReadInteger(
        IReadOnlyDictionary<string, string> values,
        string name,
        int fallback) =>
        values.TryGetValue(name, out var value) &&
        int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : fallback;

    private static string MapErrorCode(Exception exception) =>
        exception switch
        {
            FileNotFoundException => "ConfigurationFileNotFound",
            DirectoryNotFoundException => "WrongConfigurationPath",
            UnauthorizedAccessException => "ConfigurationNotWritable",
            InvalidDataException => "InvalidIniSyntax",
            IOException => "ConfigurationWriteFailed",
            TimeoutException => "ServerRestartFailure",
            _ => "ManagementActivationFailed"
        };

    private static PalworldWorldSettingsUpdateResponse WorldFailure(
        string code,
        string message) =>
        new(
            false,
            message,
            new Dictionary<string, string>(),
            null,
            false,
            false,
            false,
            0,
            code);

    private static IReadOnlyDictionary<string, string> MaskChanges(
        IReadOnlyDictionary<string, string> changes) =>
        changes.ToDictionary(
            pair => pair.Key,
            pair => MaskSecret(pair.Key, pair.Value),
            StringComparer.OrdinalIgnoreCase);

    private static string MaskSecret(string name, string value) =>
        name.Equals("ServerPassword", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AdminPassword", StringComparison.OrdinalIgnoreCase)
            ? "(protected)"
            : value;

    private sealed class ActivationException(
        string code,
        string message,
        PalworldManagementActivationStage stage) : Exception(message)
    {
        public string Code { get; } = code;

        public PalworldManagementActivationStage Stage { get; } = stage;
    }
}
