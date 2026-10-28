using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Payment provider connections and currency routing. Requires <c>tahsil.connections:read</c>, or <c>tahsil.connections:write</c> to change them.</summary>
public sealed class ProviderConnectionsClient
{
    private readonly TahsilHttp _http;

    internal ProviderConnectionsClient(TahsilHttp http) => _http = http;

    /// <summary>List payment providers. The providers this installation has adapters for, with the credentials and settings their connections take and what they can do.</summary>
    public Task<Page<ProviderResponse>> ListProvidersAsync(RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<ProviderResponse>>(HttpMethod.Get, "providers", null, options, cancellationToken);

    /// <summary>Get the connection of each currency. Currency codes and the keys of the connections that take customer-present payments in them.</summary>
    public Task<IReadOnlyDictionary<string, string>> GetRoutingAsync(RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<IReadOnlyDictionary<string, string>>(HttpMethod.Get, "provider-connections/routing", null, options, cancellationToken);

    /// <summary>Set the connection of each currency. Replaces the routing, such as <c>{ "TRY": "iyzico-main", "USD": "stripe-main" }</c>. A connection serves only currencies its provider supports, and only while enabled; saved cards are always charged through the connection that saved them.</summary>
    public Task<IReadOnlyDictionary<string, string>> UpdateRoutingAsync(IReadOnlyDictionary<string, string> request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<IReadOnlyDictionary<string, string>>(HttpMethod.Put, "provider-connections/routing", request, options, cancellationToken);

    /// <summary>List provider connections.</summary>
    /// <param name="provider">Only connections to this provider.</param>
    /// <param name="enabled">Only enabled (true) or disabled (false) connections.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<ConnectionResponse>> ListAsync(string? provider = null, bool? enabled = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<ConnectionResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("provider-connections", ("provider", provider), ("enabled", enabled), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<ConnectionResponse> ListAllAsync(string? provider = null, bool? enabled = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(provider, enabled, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Create a provider connection. Credentials are validated against the provider's fields, stored encrypted and never returned. Test mode takes sandbox connections, Live mode production ones.</summary>
    public Task<ConnectionResponse> CreateAsync(CreateConnectionRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<ConnectionResponse>(HttpMethod.Post, "provider-connections", request, options, cancellationToken);

    /// <summary>Get a provider connection.</summary>
    public Task<ConnectionResponse> GetAsync(string idOrKey, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<ConnectionResponse>(HttpMethod.Get, $"provider-connections/{TahsilHttp.Segment(idOrKey)}", null, options, cancellationToken);

    /// <summary>Update a provider connection. Credentials and settings merge with the current ones. A disabled connection takes no new payments, and renewals of its cards fail until it is enabled again.</summary>
    public Task<ConnectionResponse> UpdateAsync(string idOrKey, UpdateConnectionRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<ConnectionResponse>(HttpMethod.Patch, $"provider-connections/{TahsilHttp.Segment(idOrKey)}", request, options, cancellationToken);

    /// <summary>Delete a provider connection. Only while the connection has no payments or payment methods; otherwise disable it.</summary>
    public Task DeleteAsync(string idOrKey, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync(HttpMethod.Delete, $"provider-connections/{TahsilHttp.Segment(idOrKey)}", null, options, cancellationToken);

    /// <summary>Test a provider connection. Asks the provider whether the credentials work and records the result as <c>lastTest</c>.</summary>
    public Task<ConnectionResponse> TestAsync(string idOrKey, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<ConnectionResponse>(HttpMethod.Post, $"provider-connections/{TahsilHttp.Segment(idOrKey)}/test", null, options, cancellationToken);
}
