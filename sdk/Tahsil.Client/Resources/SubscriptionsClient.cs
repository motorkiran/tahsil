using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Subscriptions. Requires <c>tahsil.subscriptions:read</c>, or <c>tahsil.subscriptions:write</c> to change them.</summary>
public sealed class SubscriptionsClient
{
    private readonly TahsilHttp _http;

    internal SubscriptionsClient(TahsilHttp http) => _http = http;

    /// <summary>Get a subscription's usage in its current period. For each metered item: the quantity used in the current period so far and the amount it would bill now, before discounts and tax that is added.</summary>
    public Task<SubscriptionUsageResponse> GetUsageAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<SubscriptionUsageResponse>(HttpMethod.Get, $"subscriptions/{id}/usage", null, options, cancellationToken);

    /// <summary>Create a subscription. With a trial, the subscription starts <c>trialing</c> and nothing is charged. Otherwise its first invoice is finalized at once: charged to the card during the request with <c>charge_automatically</c> (<c>active</c> when paid; <c>incomplete</c> when declined, with <c>latestInvoice.hostedUrl</c> for the customer to pay within a day), or sent with <c>send_invoice</c> (<c>active</c> at once).</summary>
    public Task<SubscriptionResponse> CreateAsync(CreateSubscriptionRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<SubscriptionResponse>(HttpMethod.Post, "subscriptions", request, options, cancellationToken);

    /// <summary>List subscriptions. Newest first.</summary>
    /// <param name="customer">Only subscriptions of this customer.</param>
    /// <param name="status"><c>incomplete</c>, <c>incomplete_expired</c>, <c>trialing</c>, <c>active</c>, <c>past_due</c> or <c>canceled</c>.</param>
    /// <param name="price">Only subscriptions with this price, by ID or lookup key.</param>
    /// <param name="product">Only subscriptions with a price of this product, by ID or key.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<SubscriptionResponse>> ListAsync(Guid? customer = null, SubscriptionStatus? status = null, string? price = null, string? product = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<SubscriptionResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("subscriptions", ("customer", customer), ("status", status), ("price", price), ("product", product), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<SubscriptionResponse> ListAllAsync(Guid? customer = null, SubscriptionStatus? status = null, string? price = null, string? product = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListAsync(customer, status, price, product, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Get a subscription.</summary>
    public Task<SubscriptionResponse> GetAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<SubscriptionResponse>(HttpMethod.Get, $"subscriptions/{id}", null, options, cancellationToken);

    /// <summary>Change a subscription. <c>items</c> replaces the item set. Changed <c>now</c> (the default), it is prorated by <c>prorationBehavior</c>: <c>always_invoice</c> bills the difference at once and charges it like a first invoice (a credit goes to the customer's balance), <c>create_prorations</c> leaves it for the next renewal, <c>none</c> bills nothing until then. With <c>effective: period_end</c> (and optionally a later renewal as <c>effectiveAt</c>) it is stored as <c>scheduledChange</c> and applied at that renewal. Prices with another interval restart the period now. A higher price for an individual customer must be scheduled at least <c>Tahsil:Subscriptions:PriceIncreaseNotice</c> ahead.</summary>
    public Task<SubscriptionResponse> UpdateAsync(Guid id, UpdateSubscriptionRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<SubscriptionResponse>(HttpMethod.Patch, $"subscriptions/{id}", request, options, cancellationToken);

    /// <summary>Cancel a subscription. <c>at: period_end</c> (the default) or a time within the current period schedules the end, which <c>resume</c> takes back; <c>at: now</c> ends it at once, bills its pending items on a final invoice unless <c>invoiceNow</c> is false, and may refund the unused share of the period (<c>prorated</c>) or the last payment in full (<c>last_payment</c>). A refund that fails does not undo the cancellation; it becomes an attention item or a <c>refund.failed</c> event.</summary>
    public Task<SubscriptionResponse> CancelAsync(Guid id, CancelSubscriptionRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<SubscriptionResponse>(HttpMethod.Post, $"subscriptions/{id}/cancel", request, options, cancellationToken);

    /// <summary>Take back a scheduled cancellation.</summary>
    public Task<SubscriptionResponse> ResumeAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<SubscriptionResponse>(HttpMethod.Post, $"subscriptions/{id}/resume", null, options, cancellationToken);

    /// <summary>Remove a subscription's discount. Invoices created afterwards are not discounted; a draft still open drops the discount when it is finalized. The same as <c>PATCH</c> with <c>coupon: null</c>.</summary>
    public Task<SubscriptionResponse> RemoveDiscountAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<SubscriptionResponse>(HttpMethod.Delete, $"subscriptions/{id}/discount", null, options, cancellationToken);

    /// <summary>Preview a change of a subscription. Takes the body of a change of <c>items</c>, with a <c>coupon</c> or <c>promotionCode</c> to discount it by, and returns the invoice it would create now (lines, totals, tax and the balance it would apply), the lines it would leave for the next renewal, and the next renewal, without changing or redeeming anything.</summary>
    public Task<SubscriptionPreviewResponse> PreviewChangeAsync(Guid id, UpdateSubscriptionRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<SubscriptionPreviewResponse>(HttpMethod.Post, $"subscriptions/{id}/preview", request, options, cancellationToken);
}
