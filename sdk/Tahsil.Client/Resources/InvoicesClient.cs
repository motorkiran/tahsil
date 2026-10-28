using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Invoices. Requires <c>tahsil.invoices:read</c>, or <c>tahsil.invoices:write</c> to change them.</summary>
public sealed class InvoicesClient
{
    private readonly TahsilHttp _http;

    internal InvoicesClient(TahsilHttp http) => _http = http;

    /// <summary>Pay an invoice with a saved card. Charges one of the customer's saved cards now, off session, whatever the invoice's schedule. The payment's status says how it ended; <c>processing</c> means the provider's answer is still being confirmed.</summary>
    public Task<PaymentResponse> PayAsync(Guid id, PayInvoiceRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<PaymentResponse>(HttpMethod.Post, $"invoices/{id}/pay", request, options, cancellationToken);

    /// <summary>List invoices. Newest first.</summary>
    /// <param name="customer">Only this customer's invoices.</param>
    /// <param name="status"><c>draft</c>, <c>open</c>, <c>paid</c>, <c>void</c> or <c>uncollectible</c>.</param>
    /// <param name="number">An invoice number, such as <c>INV-2026-000123</c>.</param>
    /// <param name="subscription">Only this subscription's invoices.</param>
    /// <param name="dunning">Only unpaid invoices in dunning: after a failed automatic payment or a passed due date, before the final action.</param>
    /// <param name="overdue">Only invoices sent for payment whose due date passed unpaid.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<InvoiceResponse>> ListAsync(Guid? customer = null, InvoiceStatus? status = null, string? number = null, Guid? subscription = null, bool? dunning = null, bool? overdue = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<InvoiceResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("invoices", ("customer", customer), ("status", status), ("number", number), ("subscription", subscription), ("dunning", dunning), ("overdue", overdue), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<InvoiceResponse> ListAllAsync(Guid? customer = null, InvoiceStatus? status = null, string? number = null, Guid? subscription = null, bool? dunning = null, bool? overdue = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(customer, status, number, subscription, dunning, overdue, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Create a draft invoice. Lines are one-time prices in the invoice's currency, or descriptions with unit amounts (negative for credits). The draft changes freely until it is finalized.</summary>
    public Task<InvoiceResponse> CreateAsync(CreateInvoiceRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<InvoiceResponse>(HttpMethod.Post, "invoices", request, options, cancellationToken);

    /// <summary>Preview a subscription's next renewal invoice. The invoice the next renewal would create as things stand, with a scheduled change that takes effect then, the metered usage so far and the subscription's pending items; nothing is created.</summary>
    /// <param name="subscription">The subscription whose next renewal to preview.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<InvoicePreviewResponse> GetUpcomingAsync(Guid subscription, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<InvoicePreviewResponse>(HttpMethod.Get, TahsilHttp.WithQuery("invoices/upcoming", ("subscription", subscription)), null, options, cancellationToken);

    /// <summary>Get an invoice.</summary>
    public Task<InvoiceResponse> GetAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<InvoiceResponse>(HttpMethod.Get, $"invoices/{id}", null, options, cancellationToken);

    /// <summary>Update an invoice. Drafts: the collection method, days until due, memo and metadata. Finalized invoices: the legal invoice the application issued, and metadata.</summary>
    public Task<InvoiceResponse> UpdateAsync(Guid id, UpdateInvoiceRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<InvoiceResponse>(HttpMethod.Patch, $"invoices/{id}", request, options, cancellationToken);

    /// <summary>Delete a draft invoice. Its pending items wait for another invoice. Finalized invoices are voided instead.</summary>
    public Task DeleteAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync(HttpMethod.Delete, $"invoices/{id}", null, options, cancellationToken);

    /// <summary>Email an invoice again. Emails a finalized, unpaid invoice sent for payment to its customer again, with its hosted page link; at most once every ten minutes.</summary>
    public Task<InvoiceResponse> SendAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<InvoiceResponse>(HttpMethod.Post, $"invoices/{id}/send", null, options, cancellationToken);

    /// <summary>Add lines to a draft invoice.</summary>
    public Task<InvoiceResponse> AddLinesAsync(Guid id, AddInvoiceLinesRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<InvoiceResponse>(HttpMethod.Post, $"invoices/{id}/lines", request, options, cancellationToken);

    /// <summary>Remove a line from a draft invoice.</summary>
    public Task<InvoiceResponse> RemoveLineAsync(Guid id, Guid lineId, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<InvoiceResponse>(HttpMethod.Delete, $"invoices/{id}/lines/{lineId}", null, options, cancellationToken);

    /// <summary>Finalize a draft invoice. Assigns the number, takes the customer's billing details and applies the customer's balance. An invoice with nothing left to pay is paid at once.</summary>
    public Task<InvoiceResponse> FinalizeAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<InvoiceResponse>(HttpMethod.Post, $"invoices/{id}/finalize", null, options, cancellationToken);

    /// <summary>Void an invoice. For open and uncollectible invoices without payments. The customer's balance gets back what finalization took from it or added to it.</summary>
    public Task<InvoiceResponse> VoidAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<InvoiceResponse>(HttpMethod.Post, $"invoices/{id}/void", null, options, cancellationToken);

    /// <summary>Mark an invoice uncollectible. Stops collection; the customer can still pay it.</summary>
    public Task<InvoiceResponse> MarkUncollectibleAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<InvoiceResponse>(HttpMethod.Post, $"invoices/{id}/mark-uncollectible", null, options, cancellationToken);
}
