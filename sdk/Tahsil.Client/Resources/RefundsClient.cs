using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Refunds of payments. Requires <c>tahsil.payments:read</c>, or <c>tahsil.refunds:write</c> to refund.</summary>
public sealed class RefundsClient
{
    private readonly TahsilHttp _http;

    internal RefundsClient(TahsilHttp http) => _http = http;

    /// <summary>Refund a payment. Refunds what is left of a succeeded payment, or part of it. The provider may answer later: <c>processing</c> refunds settle by themselves. Refunds never change the status of the payment or its invoice.</summary>
    public Task<RefundResponse> CreateAsync(CreateRefundRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<RefundResponse>(HttpMethod.Post, "refunds", request, options, cancellationToken);

    /// <summary>List refunds. Newest first.</summary>
    /// <param name="payment">Only refunds of this payment.</param>
    /// <param name="status"><c>processing</c>, <c>succeeded</c> or <c>failed</c>.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<RefundResponse>> ListAsync(Guid? payment = null, RefundStatus? status = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<RefundResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("refunds", ("payment", payment), ("status", status), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<RefundResponse> ListAllAsync(Guid? payment = null, RefundStatus? status = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(payment, status, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Get a refund.</summary>
    public Task<RefundResponse> GetAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<RefundResponse>(HttpMethod.Get, $"refunds/{id}", null, options, cancellationToken);
}
