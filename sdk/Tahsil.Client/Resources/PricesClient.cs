using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Prices of the catalog. Requires <c>tahsil.catalog:read</c>, or <c>tahsil.catalog:write</c> to change them.</summary>
public sealed class PricesClient
{
    private readonly TahsilHttp _http;

    internal PricesClient(TahsilHttp http) => _http = http;

    /// <summary>List prices.</summary>
    /// <param name="product">The product's ID or key.</param>
    /// <param name="currency">An ISO 4217 code, such as TRY.</param>
    /// <param name="type"><c>one_time</c> or <c>recurring</c>.</param>
    /// <param name="archived">Only archived (true) or current (false) prices.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<PriceResponse>> ListAsync(string? product = null, string? currency = null, PriceType? type = null, bool? archived = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<PriceResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("prices", ("product", product), ("currency", currency), ("type", type), ("archived", archived), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<PriceResponse> ListAllAsync(string? product = null, string? currency = null, PriceType? type = null, bool? archived = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(product, currency, type, archived, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Create a price. Amounts are integers in minor units (29900 is ₺299.00). Only the key, name, trial, archive flag and metadata change later: a new amount is a new price. With transferKey, the lookup key moves from the price that holds it.</summary>
    public Task<PriceResponse> CreateAsync(CreatePriceRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<PriceResponse>(HttpMethod.Post, "prices", request, options, cancellationToken);

    /// <summary>Get a price.</summary>
    public Task<PriceResponse> GetAsync(string idOrKey, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<PriceResponse>(HttpMethod.Get, $"prices/{TahsilHttp.Segment(idOrKey)}", null, options, cancellationToken);

    /// <summary>Update a price. Changes the lookup key, name, trial, archive flag or metadata; any other field answers price.immutable.</summary>
    public Task<PriceResponse> UpdateAsync(string idOrKey, UpdatePriceRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<PriceResponse>(HttpMethod.Patch, $"prices/{TahsilHttp.Segment(idOrKey)}", request, options, cancellationToken);
}
