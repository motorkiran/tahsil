using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Pending invoice items, which join the customer's next invoice. Requires <c>tahsil.invoices:read</c>, or <c>tahsil.invoices:write</c>.</summary>
public sealed class InvoiceItemsClient
{
    private readonly TahsilHttp _http;

    internal InvoiceItemsClient(TahsilHttp http) => _http = http;

    /// <summary>List invoice items.</summary>
    /// <param name="customer">Only this customer's items.</param>
    /// <param name="pending">Only items no invoice has taken yet (true), or only invoiced ones (false).</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<InvoiceItemResponse>> ListAsync(Guid? customer = null, bool? pending = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<InvoiceItemResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("invoice-items", ("customer", customer), ("pending", pending), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<InvoiceItemResponse> ListAllAsync(Guid? customer = null, bool? pending = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(customer, pending, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Create a pending invoice item. A charge or credit that joins the customer's next manual invoice created with <c>includePendingItems</c>.</summary>
    public Task<InvoiceItemResponse> CreateAsync(CreateInvoiceItemRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<InvoiceItemResponse>(HttpMethod.Post, "invoice-items", request, options, cancellationToken);

    /// <summary>Get an invoice item.</summary>
    public Task<InvoiceItemResponse> GetAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<InvoiceItemResponse>(HttpMethod.Get, $"invoice-items/{id}", null, options, cancellationToken);

    /// <summary>Delete a pending invoice item. Only while no invoice includes it.</summary>
    public Task DeleteAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync(HttpMethod.Delete, $"invoice-items/{id}", null, options, cancellationToken);
}
