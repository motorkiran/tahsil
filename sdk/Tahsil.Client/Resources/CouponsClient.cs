using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Coupons. Requires <c>tahsil.coupons:read</c>, or <c>tahsil.coupons:write</c> to change them.</summary>
public sealed class CouponsClient
{
    private readonly TahsilHttp _http;

    internal CouponsClient(TahsilHttp http) => _http = http;

    /// <summary>List coupons.</summary>
    /// <param name="archived">Only archived (true) or current (false) coupons.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<CouponResponse>> ListAsync(bool? archived = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<CouponResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("coupons", ("archived", archived), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<CouponResponse> ListAllAsync(bool? archived = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(archived, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Create a coupon. A percentage or an amount off, once, for a number of cycles or forever. The discount, duration and products never change.</summary>
    public Task<CouponResponse> CreateAsync(CreateCouponRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CouponResponse>(HttpMethod.Post, "coupons", request, options, cancellationToken);

    /// <summary>Get a coupon.</summary>
    public Task<CouponResponse> GetAsync(string idOrKey, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CouponResponse>(HttpMethod.Get, $"coupons/{TahsilHttp.Segment(idOrKey)}", null, options, cancellationToken);

    /// <summary>Update a coupon. JSON Merge Patch of the name, limits, archive flag and metadata. Archived coupons and their codes redeem no more; existing discounts continue.</summary>
    public Task<CouponResponse> UpdateAsync(string idOrKey, UpdateCouponRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CouponResponse>(HttpMethod.Patch, $"coupons/{TahsilHttp.Segment(idOrKey)}", request, options, cancellationToken);
}
