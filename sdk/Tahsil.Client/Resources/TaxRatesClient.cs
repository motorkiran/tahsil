using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Tax rates of the catalog. Requires <c>tahsil.catalog:read</c>, or <c>tahsil.catalog:write</c> to change them.</summary>
public sealed class TaxRatesClient
{
    private readonly TahsilHttp _http;

    internal TaxRatesClient(TahsilHttp http) => _http = http;

    /// <summary>List tax rates.</summary>
    /// <param name="archived">Only archived (true) or current (false) rates.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<TaxRateResponse>> ListAsync(bool? archived = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<TaxRateResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("tax-rates", ("archived", archived), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<TaxRateResponse> ListAllAsync(bool? archived = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(archived, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Create a tax rate. The percentage and whether prices include the tax never change: to change a rate, create a new one.</summary>
    public Task<TaxRateResponse> CreateAsync(CreateTaxRateRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<TaxRateResponse>(HttpMethod.Post, "tax-rates", request, options, cancellationToken);

    /// <summary>Get a tax rate.</summary>
    public Task<TaxRateResponse> GetAsync(string idOrKey, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<TaxRateResponse>(HttpMethod.Get, $"tax-rates/{TahsilHttp.Segment(idOrKey)}", null, options, cancellationToken);

    /// <summary>Update a tax rate. Changes the name, description or archive flag. Archived rates cannot be given to new prices.</summary>
    public Task<TaxRateResponse> UpdateAsync(string idOrKey, UpdateTaxRateRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<TaxRateResponse>(HttpMethod.Patch, $"tax-rates/{TahsilHttp.Segment(idOrKey)}", request, options, cancellationToken);
}
