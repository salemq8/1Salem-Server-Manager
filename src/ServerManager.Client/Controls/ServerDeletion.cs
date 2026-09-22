using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Core;
using MessageBox = System.Windows.MessageBox;

namespace ServerManager.Client.Controls;

/// <summary>
/// "Delete Server" from a server's overflow menu (Servers and Server Detail). The Agent can
/// only remove a server from the manager — nothing in it deletes server files — so that is
/// exactly what the confirmation says: the folder, worlds and backup files stay on disk.
/// A running server is refused, never stopped, and the outcome shown is the Agent's answer.
/// </summary>
public static class ServerDeletion
{
    /// <summary>Raised once the Agent confirmed the removal, so the shell can close and refresh.</summary>
    public static event EventHandler<Guid>? ServerDeleted;

    public static bool IsRunningOrBusy(ServerCardViewModel card) =>
        card.Status is UiStatus.Running or UiStatus.Starting or UiStatus.Stopping or UiStatus.Busy ||
        card.Source?.State is ServerState.Starting or ServerState.Running or ServerState.Stopping or
            ServerState.Restarting or ServerState.Updating or ServerState.BackingUp or
            ServerState.Restoring;

    public static async Task RequestAsync(Window? owner, ServerCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        var name = card.Name;
        if (IsRunningOrBusy(card))
        {
            Inform(owner, LocalizationService.Get("Server.StopBeforeDelete"), LocalizationService.Get("Server.Running"));
            return;
        }

        using var client = AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromSeconds(30));
        // The folder that stays on disk is shown as-is (left-to-right) under the explanation.
        var location = await TryGetRootPathAsync(client, card.ServerId);
        var confirmed = ConfirmationDialog.Confirm(
            owner,
            LocalizationService.Get("Action.DeleteServer"),
            LocalizationService.Format("Confirm.DeleteServerTitle", name),
            LocalizationService.Format("Confirm.DeleteServerBody", name),
            location,
            LocalizationService.Get("Action.DeleteServer"));
        if (!confirmed)
        {
            return;
        }

        try
        {
            using var response = await client.DeleteAsync($"/api/v1/servers/{card.ServerId}");
            if (response.IsSuccessStatusCode)
            {
                NotificationService.Publish(
                    NotificationKind.Success,
                    LocalizationService.Get("Server.Deleted"),
                    LocalizationService.Format("Server.DeletedMessage", name));
                ServerDeleted?.Invoke(null, card.ServerId);
                return;
            }

            var result = await TryReadResultAsync(response);
            NotificationService.Publish(
                NotificationKind.Error,
                LocalizationService.Get("Server.DeleteFailed"),
                DescribeFailure(response.StatusCode, result?.ErrorCode));
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Already gone: bring the list in line with what the Agent has.
                await DashboardFeed.Shared.RefreshAsync();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            NotificationService.Publish(
                NotificationKind.Error,
                LocalizationService.Get("Server.DeleteFailed"),
                LocalizationService.Get("Error.ServiceUnavailable"));
        }
    }

    /// <summary>Why it did not work, in the person's language; the server entry is left intact.</summary>
    public static string DescribeFailure(HttpStatusCode status, string? errorCode) =>
        errorCode switch
        {
            "ServerRunning" => LocalizationService.Get("Server.StopBeforeDelete"),
            "ServerBusy" => LocalizationService.Get("Server.DeleteBusy"),
            "ServerNotFound" => LocalizationService.Get("Server.DeleteNotFound"),
            _ => status switch
            {
                HttpStatusCode.NotFound => LocalizationService.Get("Server.DeleteNotFound"),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    LocalizationService.Get("Error.ServiceUnavailable"),
                _ => LocalizationService.Get("Server.DeleteError")
            }
        };

    private static async Task<string?> TryGetRootPathAsync(HttpClient client, Guid serverId)
    {
        try
        {
            var servers = await client.GetFromJsonAsync<GameServerDefinition[]>("/api/v1/servers") ?? [];
            return servers.FirstOrDefault(server => server.Id == serverId)?.RootPath;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static async Task<OperationResult?> TryReadResultAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<OperationResult>();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void Inform(Window? owner, string message, string title)
    {
        if (owner is null)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
