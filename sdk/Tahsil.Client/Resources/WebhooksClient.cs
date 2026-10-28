using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Webhook endpoints, their deliveries and the event catalog. Requires <c>tahsil.webhooks:read</c>, or <c>tahsil.webhooks:write</c> to change them.</summary>
public sealed class WebhooksClient
{
    private readonly TahsilHttp _http;

    internal WebhooksClient(TahsilHttp http) => _http = http;

    /// <summary>List the event types.</summary>
    public Task<Page<EventTypeResponse>> ListEventTypesAsync(RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<EventTypeResponse>>(HttpMethod.Get, "webhooks/event-types", null, options, cancellationToken);

    /// <summary>List webhook endpoints.</summary>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<WebhookEndpointResponse>> ListEndpointsAsync(string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<WebhookEndpointResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("webhooks/endpoints", ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListEndpointsAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<WebhookEndpointResponse> ListAllEndpointsAsync(int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListEndpointsAsync(cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Create a webhook endpoint. Returns the signing secret once. Events are signed as Standard Webhooks describes; an empty list of event types means all.</summary>
    public Task<CreatedWebhookEndpointResponse> CreateEndpointAsync(CreateWebhookEndpointRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<CreatedWebhookEndpointResponse>(HttpMethod.Post, "webhooks/endpoints", request, options, cancellationToken);

    /// <summary>Get a webhook endpoint.</summary>
    public Task<WebhookEndpointResponse> GetEndpointAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<WebhookEndpointResponse>(HttpMethod.Get, $"webhooks/endpoints/{id}", null, options, cancellationToken);

    /// <summary>Update a webhook endpoint.</summary>
    public Task<WebhookEndpointResponse> UpdateEndpointAsync(Guid id, UpdateWebhookEndpointRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<WebhookEndpointResponse>(HttpMethod.Patch, $"webhooks/endpoints/{id}", request, options, cancellationToken);

    /// <summary>Delete a webhook endpoint. Deletes the endpoint with its delivery log; deliveries under way are dropped.</summary>
    public Task DeleteEndpointAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync(HttpMethod.Delete, $"webhooks/endpoints/{id}", null, options, cancellationToken);

    /// <summary>Rotate a webhook endpoint's secret. Returns the new secret once. Until <c>previousSecretExpiresAt</c>, deliveries carry a signature with each secret, so receivers can switch without losing events.</summary>
    public Task<WebhookSecretResponse> RotateSecretAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<WebhookSecretResponse>(HttpMethod.Post, $"webhooks/endpoints/{id}/secret", null, options, cancellationToken);

    /// <summary>Send a test event. Sends a <c>webhook.test</c> event to the endpoint, even when it is disabled.</summary>
    public Task<WebhookDeliveryResponse> SendTestEventAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<WebhookDeliveryResponse>(HttpMethod.Post, $"webhooks/endpoints/{id}/test", null, options, cancellationToken);

    /// <summary>List webhook deliveries. Newest first.</summary>
    /// <param name="endpoint">Only deliveries to this endpoint.</param>
    /// <param name="status"><c>pending</c>, <c>succeeded</c> or <c>failed</c>.</param>
    /// <param name="eventType">Only deliveries of this event type.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, 50 by default and at most 200.</param>
    /// <param name="options">The idempotency key or expected version of the call, and its response's version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Page<WebhookDeliveryResponse>> ListDeliveriesAsync(Guid? endpoint = null, WebhookDeliveryStatus? status = null, string? eventType = null, string? cursor = null, int? limit = null, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<Page<WebhookDeliveryResponse>>(HttpMethod.Get, TahsilHttp.WithQuery("webhooks/deliveries", ("endpoint", endpoint), ("status", status), ("eventType", eventType), ("cursor", cursor), ("limit", limit)), null, options, cancellationToken);

    /// <summary>Every item of <see cref="ListDeliveriesAsync"/>, following its pages.</summary>
    public IAsyncEnumerable<WebhookDeliveryResponse> ListAllDeliveriesAsync(Guid? endpoint = null, WebhookDeliveryStatus? status = null, string? eventType = null, int? limit = null, CancellationToken cancellationToken = default) =>
        TahsilHttp.AllAsync(cursor => ListDeliveriesAsync(endpoint, status, eventType, cursor, limit, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Get a webhook delivery.</summary>
    public Task<WebhookDeliveryResponse> GetDeliveryAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<WebhookDeliveryResponse>(HttpMethod.Get, $"webhooks/deliveries/{id}", null, options, cancellationToken);

    /// <summary>Send a delivery again. Sends the event again with the same event ID, as a fresh series of attempts.</summary>
    public Task<WebhookDeliveryResponse> RedeliverAsync(Guid id, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<WebhookDeliveryResponse>(HttpMethod.Post, $"webhooks/deliveries/{id}/redeliver", null, options, cancellationToken);
}
