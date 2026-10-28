using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>API keys. Requires <c>tahsil.api_keys:read</c>, or <c>tahsil.api_keys:write</c> to change them.</summary>
public sealed class ApiKeysClient
{
    private readonly TahsilHttp _http;

    internal ApiKeysClient(TahsilHttp http) => _http = http;

    /// <summary>List API keys. Lists API keys in creation order, revoked and expired ones included.</summary>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<ApiKeyResponse>> ListAsync(string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<ApiKeyResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("api-keys", ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<ApiKeyResponse> ListAllAsync(int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Create an API key. Creates a key with permissions the caller holds itself. The response is the only time the key's secret is shown.</summary>
    public Task<CreatedApiKeyResponse> CreateAsync(CreateApiKeyRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CreatedApiKeyResponse>(HttpMethod.Post, "api-keys", request, options, cancellationToken);

    /// <summary>Get an API key.</summary>
    public Task<ApiKeyResponse> GetAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<ApiKeyResponse>(HttpMethod.Get, $"api-keys/{id}", null, options, cancellationToken);

    /// <summary>Revoke an API key. The key stops working at once, and stays listed with the time it was revoked.</summary>
    public Task<ApiKeyResponse> RevokeAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<ApiKeyResponse>(HttpMethod.Post, $"api-keys/{id}/revoke", null, options, cancellationToken);
}
