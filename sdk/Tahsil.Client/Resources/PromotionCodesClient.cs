using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Promotion codes of coupons. Requires <c>tahsil.coupons:read</c>, or <c>tahsil.coupons:write</c> to change them.</summary>
public sealed class PromotionCodesClient
{
    private readonly TahsilHttp _http;

    internal PromotionCodesClient(TahsilHttp http) => _http = http;

    /// <summary>List promotion codes.</summary>
    /// <param name="coupon">The coupon's ID or key.</param>
    /// <param name="active">Only active (true) or inactive (false) codes.</param>
    /// <param name="code">A code, ignoring case.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<PromotionCodeResponse>> ListAsync(string? coupon = null, bool? active = null, string? code = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<PromotionCodeResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("promotion-codes", ("coupon", coupon), ("active", active), ("code", code), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<PromotionCodeResponse> ListAllAsync(string? coupon = null, bool? active = null, string? code = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(coupon, active, code, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Create a promotion code. Codes are stored in uppercase and unique ignoring case across all coupons. Restrictions never change; deactivate a code and create another.</summary>
    public Task<PromotionCodeResponse> CreateAsync(CreatePromotionCodeRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<PromotionCodeResponse>(HttpMethod.Post, "promotion-codes", request, options, cancellationToken);

    /// <summary>Get a promotion code.</summary>
    public Task<PromotionCodeResponse> GetAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<PromotionCodeResponse>(HttpMethod.Get, $"promotion-codes/{id}", null, options, cancellationToken);

    /// <summary>Activate or deactivate a promotion code.</summary>
    public Task<PromotionCodeResponse> UpdateAsync(Guid id, UpdatePromotionCodeRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<PromotionCodeResponse>(HttpMethod.Patch, $"promotion-codes/{id}", request, options, cancellationToken);
}
