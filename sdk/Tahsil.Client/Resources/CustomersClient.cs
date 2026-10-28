using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Customers, with their cards, balances, consents and usage. Requires <c>tahsil.customers:read</c>, or <c>tahsil.customers:write</c> to change them.</summary>
public sealed class CustomersClient
{
    private readonly TahsilHttp _http;

    internal CustomersClient(TahsilHttp http) => _http = http;

    /// <summary>List and search customers. Newest first. <c>email</c> and <c>externalId</c> match exactly (the email ignoring case); <c>q</c> matches the beginning of the email, name or external ID.</summary>
    /// <param name="search">Matches the beginning of the email, name or external ID, ignoring case.</param>
    /// <param name="email">An email address, ignoring case.</param>
    /// <param name="externalId">The application's ID for the customer.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<CustomerResponse>> ListAsync(string? search = null, string? email = null, string? externalId = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<CustomerResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("customers", ("q", search), ("email", email), ("externalId", externalId), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<CustomerResponse> ListAllAsync(string? search = null, string? email = null, string? externalId = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(search, email, externalId, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Create a customer. An external ID that another customer has answers 409 customer.external_id_taken with that customer's ID in <c>customerId</c>.</summary>
    public Task<CustomerResponse> CreateAsync(CreateCustomerRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CustomerResponse>(HttpMethod.Post, "customers", request, options, cancellationToken);

    /// <summary>Get a customer.</summary>
    public Task<CustomerResponse> GetAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CustomerResponse>(HttpMethod.Get, $"customers/{id}", null, options, cancellationToken);

    /// <summary>Update a customer. JSON Merge Patch: omitted fields stay, null clears.</summary>
    public Task<CustomerResponse> UpdateAsync(Guid id, UpdateCustomerRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CustomerResponse>(HttpMethod.Patch, $"customers/{id}", request, options, cancellationToken);

    /// <summary>Delete or anonymize a customer. Deletes a customer without billing history (204). A customer with history is anonymized instead, and returned (200).</summary>
    /// <returns>The anonymized customer when they had billing history; <see langword="null"/> when they were deleted.</returns>
    public Task<CustomerResponse?> DeleteAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendOptionalAsync<CustomerResponse>(HttpMethod.Delete, $"customers/{id}", null, options, cancellationToken);

    /// <summary>Export a customer's data. Everything Tahsil holds about the customer, for data subject requests under KVKK.</summary>
    public Task<CustomerExportResponse> ExportAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CustomerExportResponse>(HttpMethod.Get, $"customers/{id}/export", null, options, cancellationToken);

    /// <summary>List a customer's consent records. The evidence of what the customer was shown and accepted: legal documents, the consent to future charges of a saved card, cancellations and withdrawals. Newest first. Records are kept when a customer is anonymized.</summary>
    /// <param name="id">The ID.</param>
    /// <param name="includeDocuments">Whether each record carries the rendered document the customer saw, as HTML.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<ConsentRecordResponse>> ListConsentsAsync(Guid id, bool? includeDocuments = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<ConsentRecordResponse>>(HttpMethod.Get, TahsilHttp.WithQuery($"customers/{id}/consents", ("includeDocuments", includeDocuments)), null, options, cancellationToken);

    /// <summary>Get a customer by external ID.</summary>
    public Task<CustomerResponse> GetByExternalIdAsync(string externalId, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CustomerResponse>(HttpMethod.Get, $"customers/by-external-id/{TahsilHttp.Segment(externalId)}", null, options, cancellationToken);

    /// <summary>Create or update a customer by external ID. Creates the customer (201) or updates it as a JSON Merge Patch (200), for applications that sync customers on every sign-in.</summary>
    public Task<CustomerResponse> UpsertByExternalIdAsync(string externalId, UpdateCustomerRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CustomerResponse>(HttpMethod.Put, $"customers/by-external-id/{TahsilHttp.Segment(externalId)}", request, options, cancellationToken);

    /// <summary>List a customer's saved cards. Active cards first.</summary>
    /// <param name="id">The ID.</param>
    /// <param name="status"><c>active</c>, <c>expired</c> or <c>removed</c>.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<PaymentMethodResponse>> ListPaymentMethodsAsync(Guid id, PaymentMethodStatus? status = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<PaymentMethodResponse>>(HttpMethod.Get, TahsilHttp.WithQuery($"customers/{id}/payment-methods", ("status", status)), null, options, cancellationToken);

    /// <summary>Set a customer's default card.</summary>
    public Task<PaymentMethodResponse> SetDefaultPaymentMethodAsync(Guid id, SetDefaultPaymentMethodRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<PaymentMethodResponse>(HttpMethod.Post, $"customers/{id}/default-payment-method", request, options, cancellationToken);

    /// <summary>Get a saved card.</summary>
    public Task<PaymentMethodResponse> GetPaymentMethodAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<PaymentMethodResponse>(HttpMethod.Get, $"payment-methods/{id}", null, options, cancellationToken);

    /// <summary>Remove a saved card. The card is never charged again and stops being the default; the provider is asked to delete its token.</summary>
    public Task RemovePaymentMethodAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync(HttpMethod.Delete, $"payment-methods/{id}", null, options, cancellationToken);

    /// <summary>Get a customer's usage on a meter. The quantity of the range and of each day or month in it, each aggregated the meter's way, whether a subscription bills the usage or not. At most 400 intervals.</summary>
    /// <param name="id">The ID.</param>
    /// <param name="meter">The meter, by ID or key.</param>
    /// <param name="from">The start of the range; 30 days before <c>to</c> by default, or 12 months for months.</param>
    /// <param name="to">The end of the range, which is not part of it; now by default.</param>
    /// <param name="interval"><c>day</c> (the default) or <c>month</c>, in the billing time zone.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<CustomerUsageResponse> GetUsageAsync(Guid id, string? meter = null, DateTimeOffset? from = null, DateTimeOffset? to = null, UsageInterval? interval = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CustomerUsageResponse>(HttpMethod.Get, TahsilHttp.WithQuery($"customers/{id}/usage", ("meter", meter), ("from", from), ("to", to), ("interval", interval)), null, options, cancellationToken);

    /// <summary>List a customer's balance history. Newest first.</summary>
    /// <param name="customerId">The customer's ID.</param>
    /// <param name="currency">Only this currency.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<BalanceTransactionResponse>> ListBalanceTransactionsAsync(Guid customerId, string? currency = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<BalanceTransactionResponse>>(HttpMethod.Get, TahsilHttp.WithQuery($"customers/{customerId}/balance-transactions", ("currency", currency), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListBalanceTransactionsAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<BalanceTransactionResponse> ListAllBalanceTransactionsAsync(Guid customerId, string? currency = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListBalanceTransactionsAsync(customerId, currency, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Adjust a customer's balance. Positive amounts give credit that the next invoices use; negative amounts are added to the next invoice. Balances never turn into refunds.</summary>
    public Task<BalanceTransactionResponse> AdjustBalanceAsync(Guid customerId, CreateBalanceTransactionRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<BalanceTransactionResponse>(HttpMethod.Post, $"customers/{customerId}/balance-transactions", request, options, cancellationToken);

    /// <summary>Get a balance transaction.</summary>
    public Task<BalanceTransactionResponse> GetBalanceTransactionAsync(Guid customerId, Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<BalanceTransactionResponse>(HttpMethod.Get, $"customers/{customerId}/balance-transactions/{id}", null, options, cancellationToken);
}
