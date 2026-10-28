using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Meters of metered prices. Requires <c>tahsil.catalog:read</c>, or <c>tahsil.catalog:write</c> to change them.</summary>
public sealed class MetersClient
{
    private readonly TahsilHttp _http;

    internal MetersClient(TahsilHttp http) => _http = http;

    /// <summary>List meters.</summary>
    /// <param name="archived">Only archived (true) or current (false) meters.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<MeterResponse>> ListAsync(bool? archived = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<MeterResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("meters", ("archived", archived), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<MeterResponse> ListAllAsync(bool? archived = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(archived, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Create a meter. The key is the name usage events report; it, the aggregation and the counted property never change.</summary>
    public Task<MeterResponse> CreateAsync(CreateMeterRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<MeterResponse>(HttpMethod.Post, "meters", request, options, cancellationToken);

    /// <summary>Get a meter.</summary>
    public Task<MeterResponse> GetAsync(string idOrKey, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<MeterResponse>(HttpMethod.Get, $"meters/{TahsilHttp.Segment(idOrKey)}", null, options, cancellationToken);

    /// <summary>Update a meter.</summary>
    public Task<MeterResponse> UpdateAsync(string idOrKey, UpdateMeterRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<MeterResponse>(HttpMethod.Patch, $"meters/{TahsilHttp.Segment(idOrKey)}", request, options, cancellationToken);
}
