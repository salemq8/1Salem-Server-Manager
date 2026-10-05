using System.Net.Http;
using System.Net.Http.Json;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Client.Transport;

/// <summary>Typed client for the Agent's loopback-only Connect owner API.</summary>
public sealed class ConnectOwnerClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public ConnectOwnerClient(TimeSpan? timeout = null) =>
        _httpClient = AgentTransportDefaults.CreateLoopbackHttpClient(
            timeout ?? TimeSpan.FromSeconds(30));

    public Task<ConnectStatusResponse?> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _httpClient.GetFromJsonAsync<ConnectStatusResponse>("/api/v1/connect/status", cancellationToken);

    public Task<ConnectStatusResponse> SaveCredentialAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken = default) =>
        SendAsync<ConnectStatusResponse>(
            HttpMethod.Put,
            "/api/v1/connect/credential",
            new ConnectCredentialRequest(clientId, clientSecret),
            cancellationToken);

    public Task<ConnectStatusResponse> CheckAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ConnectStatusResponse>(HttpMethod.Post, "/api/v1/connect/check", null, cancellationToken);

    public Task TurnOffAsync(CancellationToken cancellationToken = default) =>
        SendAsync<OperationResult>(HttpMethod.Delete, "/api/v1/connect/credential", null, cancellationToken);

    /// <summary>A server's Connect state; a failed check throws with the Agent's own reason.</summary>
    public async Task<ServerConnectResponse?> GetServerAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"/api/v1/servers/{serverId}/connect", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ApiErrorResponse? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(cancellationToken);
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
            {
            }

            throw new ConnectOwnerClientException(
                error?.Code,
                error?.WhatFailed ?? $"The local 1Salem service answered {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<ServerConnectResponse>(cancellationToken);
    }

    public Task EnableAsync(Guid serverId, CancellationToken cancellationToken = default) =>
        SendAsync<OperationResult>(HttpMethod.Post, $"/api/v1/servers/{serverId}/connect/enable", null, cancellationToken);

    public Task DisableAsync(Guid serverId, CancellationToken cancellationToken = default) =>
        SendAsync<OperationResult>(HttpMethod.Post, $"/api/v1/servers/{serverId}/connect/disable", null, cancellationToken);

    public Task<ConnectInviteCreated> CreateInviteAsync(
        Guid serverId,
        int ttlSeconds,
        CancellationToken cancellationToken = default) =>
        SendAsync<ConnectInviteCreated>(
            HttpMethod.Post,
            $"/api/v1/servers/{serverId}/connect/invites",
            new ConnectInviteRequest(ttlSeconds),
            cancellationToken);

    public Task RevokeInviteAsync(string inviteId, CancellationToken cancellationToken = default) =>
        SendAsync<OperationResult>(HttpMethod.Post, $"/api/v1/connect/invites/{Uri.EscapeDataString(inviteId)}/revoke", null, cancellationToken);

    public Task ApproveAsync(string membershipId, CancellationToken cancellationToken = default) =>
        MembershipActionAsync(membershipId, "approve", null, cancellationToken);

    public Task RejectAsync(string membershipId, CancellationToken cancellationToken = default) =>
        MembershipActionAsync(membershipId, "reject", null, cancellationToken);

    public Task<ConnectRevokeResult> RevokeAsync(string membershipId, CancellationToken cancellationToken = default) =>
        SendAsync<ConnectRevokeResult>(HttpMethod.Post, MembershipUrl(membershipId, "revoke"), null, cancellationToken);

    public Task SetNicknameAsync(
        string membershipId,
        string? nickname,
        CancellationToken cancellationToken = default) =>
        MembershipActionAsync(
            membershipId,
            "nickname",
            new ConnectNicknameRequest(nickname),
            cancellationToken,
            HttpMethod.Put);

    public void Dispose() => _httpClient.Dispose();

    private Task MembershipActionAsync(
        string membershipId,
        string action,
        object? body,
        CancellationToken cancellationToken,
        HttpMethod? method = null) =>
        SendAsync<OperationResult>(method ?? HttpMethod.Post, MembershipUrl(membershipId, action), body, cancellationToken);

    private static string MembershipUrl(string membershipId, string action) =>
        $"/api/v1/connect/memberships/{Uri.EscapeDataString(membershipId)}/{action}";

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string url,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await response.Content.ReadFromJsonAsync<OperationResult>(cancellationToken);
            throw new ConnectOwnerClientException(
                failure?.ErrorCode,
                failure?.Message ?? $"Connect request failed ({(int)response.StatusCode}).");
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new ConnectOwnerClientException(null, "The local service returned an empty response.");
    }
}

public sealed class ConnectOwnerClientException(string? errorCode, string message) : Exception(message)
{
    public string? ErrorCode { get; } = errorCode;
}
