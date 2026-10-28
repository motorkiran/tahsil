using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Payments. Requires <c>tahsil.payments:read</c>.</summary>
public sealed class PaymentsClient
{
    private readonly TahsilHttp _http;

    internal PaymentsClient(TahsilHttp http) => _http = http;

    /// <summary>List payments. Newest first.</summary>
    /// <param name="customer">Only this customer's payments.</param>
    /// <param name="invoice">Only payments of this invoice.</param>
    /// <param name="status"><c>processing</c>, <c>requires_action</c>, <c>succeeded</c>, <c>failed</c> or <c>canceled</c>.</param>
    /// <param name="connection">Only payments through this provider connection.</param>
    /// <param name="failureCode">Only payments that failed with this code, such as <c>insufficient_funds</c>.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<PaymentResponse>> ListAsync(Guid? customer = null, Guid? invoice = null, PaymentStatus? status = null, Guid? connection = null, string? failureCode = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<PaymentResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("payments", ("customer", customer), ("invoice", invoice), ("status", status), ("connection", connection), ("failureCode", failureCode), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<PaymentResponse> ListAllAsync(Guid? customer = null, Guid? invoice = null, PaymentStatus? status = null, Guid? connection = null, string? failureCode = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(customer, invoice, status, connection, failureCode, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Get a payment.</summary>
    public Task<PaymentResponse> GetAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<PaymentResponse>(HttpMethod.Get, $"payments/{id}", null, options, cancellationToken);
}
