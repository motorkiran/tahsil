using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Customer portal sessions. Requires <c>tahsil.checkout:write</c>.</summary>
public sealed class PortalSessionsClient
{
    private readonly TahsilHttp _http;

    internal PortalSessionsClient(TahsilHttp http) => _http = http;

    /// <summary>Create a portal session. For a customer the application has authenticated: redirect them to <c>url</c>, where they manage their subscriptions, card, invoices and billing details. The session works for that customer alone, as often as they like until <c>expiresAt</c> (<c>Tahsil:Portal:SessionLifetime</c>, an hour by default). <c>url</c> is given now only; create another session when it is lost. <c>flow</c> opens the portal on one task.</summary>
    public Task<PortalSessionResponse> CreateAsync(CreatePortalSessionRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<PortalSessionResponse>(HttpMethod.Post, "portal-sessions", request, options, cancellationToken);
}
