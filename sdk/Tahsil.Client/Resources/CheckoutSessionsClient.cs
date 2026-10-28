using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Hosted checkout sessions. Requires <c>tahsil.checkout:write</c>.</summary>
public sealed class CheckoutSessionsClient
{
    private readonly TahsilHttp _http;

    internal CheckoutSessionsClient(TahsilHttp http) => _http = http;

    /// <summary>Create a checkout session. Prices and the amount due are computed now and shown on the page as the <c>quote</c>; send the customer to <c>url</c>. The session expires unpaid after <c>expiresIn</c> (24 hours by default).</summary>
    public Task<CheckoutSessionResponse> CreateAsync(CreateCheckoutSessionRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CheckoutSessionResponse>(HttpMethod.Post, "checkout-sessions", request, options, cancellationToken);

    /// <summary>List checkout sessions. Newest first.</summary>
    /// <param name="status"><c>open</c>, <c>complete</c> or <c>expired</c>.</param>
    /// <param name="customer">Only sessions of this customer.</param>
    /// <param name="clientReferenceId">Only sessions with this client reference ID.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<CheckoutSessionResponse>> ListAsync(CheckoutStatus? status = null, Guid? customer = null, string? clientReferenceId = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<CheckoutSessionResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("checkout-sessions", ("status", status), ("customer", customer), ("clientReferenceId", clientReferenceId), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<CheckoutSessionResponse> ListAllAsync(CheckoutStatus? status = null, Guid? customer = null, string? clientReferenceId = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(status, customer, clientReferenceId, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Get a checkout session. <c>url</c> is given while the session is open.</summary>
    public Task<CheckoutSessionResponse> GetAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CheckoutSessionResponse>(HttpMethod.Get, $"checkout-sessions/{id}", null, options, cancellationToken);

    /// <summary>Expire a checkout session. Ends an open session so it can no longer be paid; <c>checkout.expired</c> is sent.</summary>
    public Task<CheckoutSessionResponse> ExpireAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CheckoutSessionResponse>(HttpMethod.Post, $"checkout-sessions/{id}/expire", null, options, cancellationToken);
}
