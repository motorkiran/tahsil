using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Products of the catalog. Requires <c>tahsil.catalog:read</c>, or <c>tahsil.catalog:write</c> to change them.</summary>
public sealed class ProductsClient
{
    private readonly TahsilHttp _http;

    internal ProductsClient(TahsilHttp http) => _http = http;

    /// <summary>List products.</summary>
    /// <param name="archived">Only archived (true) or current (false) products.</param>
    /// <param name="search">Matches the key or name, ignoring case.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<ProductResponse>> ListAsync(bool? archived = null, string? search = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<ProductResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("products", ("archived", archived), ("q", search), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<ProductResponse> ListAllAsync(bool? archived = null, string? search = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(archived, search, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Create a product.</summary>
    public Task<ProductResponse> CreateAsync(CreateProductRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<ProductResponse>(HttpMethod.Post, "products", request, options, cancellationToken);

    /// <summary>Get a product.</summary>
    public Task<ProductResponse> GetAsync(string idOrKey, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<ProductResponse>(HttpMethod.Get, $"products/{TahsilHttp.Segment(idOrKey)}", null, options, cancellationToken);

    /// <summary>Update a product. JSON Merge Patch: omitted fields stay, null clears. Archiving stops new sales; existing subscriptions keep renewing.</summary>
    public Task<ProductResponse> UpdateAsync(string idOrKey, UpdateProductRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<ProductResponse>(HttpMethod.Patch, $"products/{TahsilHttp.Segment(idOrKey)}", request, options, cancellationToken);

    /// <summary>Delete a product. Deletes the product and its prices while none of them was ever used; otherwise archive it.</summary>
    public Task DeleteAsync(string idOrKey, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync(HttpMethod.Delete, $"products/{TahsilHttp.Segment(idOrKey)}", null, options, cancellationToken);
}
