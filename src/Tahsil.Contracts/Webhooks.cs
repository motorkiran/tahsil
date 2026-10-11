using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tahsil.Contracts;

/// <summary>
/// The body of every webhook (EVT-1): what happened, to what, by whom, with the details a consumer needs to route it.
/// Events are thin: consumers that act on state fetch the subject through the API (EVT-2).
/// </summary>
public sealed record WebhookEvent(string Type, DateTimeOffset Timestamp, string Mode, WebhookEventData Data);

public sealed record WebhookEventData(string SubjectType, string SubjectId, Guid? CustomerId, WebhookEventActor Actor, JsonElement? Details = null);

/// <summary>Who caused an event: <c>api_key</c>, <c>admin</c>, <c>customer</c> (on a hosted page) or <c>system</c> (EVT-3).</summary>
public sealed record WebhookEventActor(string Type, string? Id);

/// <summary>The events Tahsil sends to webhook endpoints (EVT-5), named <c>resource.past_tense_verb</c>.</summary>
public static class TahsilEventTypes
{
    public const string CustomerCreated = "customer.created";
    public const string CustomerUpdated = "customer.updated";
    public const string CustomerDeleted = "customer.deleted";
    public const string PaymentMethodAttached = "payment_method.attached";
    public const string PaymentMethodRemoved = "payment_method.removed";
    public const string PaymentMethodWillExpire = "payment_method.will_expire";
    public const string CheckoutCompleted = "checkout.completed";
    public const string CheckoutExpired = "checkout.expired";
    public const string InvoiceCreated = "invoice.created";
    public const string InvoiceFinalized = "invoice.finalized";
    public const string InvoicePaid = "invoice.paid";
    public const string InvoicePaymentFailed = "invoice.payment_failed";
    public const string InvoiceActionRequired = "invoice.action_required";
    public const string InvoiceOverdue = "invoice.overdue";
    public const string InvoiceVoided = "invoice.voided";
    public const string InvoiceMarkedUncollectible = "invoice.marked_uncollectible";
    public const string PaymentSucceeded = "payment.succeeded";
    public const string PaymentFailed = "payment.failed";
    public const string RefundSucceeded = "refund.succeeded";
    public const string RefundFailed = "refund.failed";

    /// <summary>Sent only on request, to try an endpoint out; endpoints need not subscribe to it.</summary>
    public const string Test = "webhook.test";

    /// <summary>Every event type endpoints can subscribe to, with what it means.</summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [CustomerCreated] = "A customer was created.",
        [CustomerUpdated] = "A customer's contact or billing details, locale, Kimlik link or metadata changed; `details.changed` names the fields.",
        [CustomerDeleted] = "A customer was deleted, or anonymized when billing history keeps it (`details.anonymized`).",
        [PaymentMethodAttached] = "A card was saved for a customer.",
        [PaymentMethodRemoved] = "A saved card was removed.",
        [PaymentMethodWillExpire] = "A default card of a running subscription expires soon.",
        [CheckoutCompleted] = "A checkout session was paid: `details` names what it created (the invoice, the saved card).",
        [CheckoutExpired] = "A checkout session ended unpaid: it ran out of time or the application expired it.",
        [InvoiceCreated] = "A draft with a draft period was created; it is finalized at `details.finalizeAt`.",
        [InvoiceFinalized] = "An invoice was finalized: it has a number and is due.",
        [InvoicePaid] = "An invoice was paid in full.",
        [InvoicePaymentFailed] = "An automatic payment of an invoice failed.",
        [InvoiceActionRequired] = "The customer must authenticate or pay an invoice on its hosted page.",
        [InvoiceOverdue] = "An invoice sent for payment passed its due date unpaid.",
        [InvoiceVoided] = "An invoice was voided.",
        [InvoiceMarkedUncollectible] = "An invoice was marked uncollectible; it can still be paid.",
        [PaymentSucceeded] = "A payment succeeded, including late successes.",
        [PaymentFailed] = "A payment failed.",
        [RefundSucceeded] = "A refund succeeded.",
        [RefundFailed] = "A refund failed at the provider.",
    };

    public static readonly IReadOnlySet<string> All = new HashSet<string>(Descriptions.Keys, StringComparer.Ordinal);
}

public sealed record EventTypeResponse(string Type, string Description);

public enum WebhookDeliveryStatus
{
    Pending,
    Succeeded,
    Failed,
}

public sealed record CreateWebhookEndpointRequest
{
    /// <summary>What provisioning documents know the endpoint by; optional.</summary>
    public string? Key { get; init; }

    /// <summary>An HTTPS URL; plain HTTP only for this machine in Test mode.</summary>
    [Required]
    public required string Url { get; init; }

    public string? Description { get; init; }

    /// <summary>The event types it receives; none means all.</summary>
    public IReadOnlyList<string>? EventTypes { get; init; }

    /// <summary>The signing secret (<c>whsec_</c> and base64); one is generated when omitted.</summary>
    public string? Secret { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>JSON Merge Patch: omitted fields stay, <c>null</c> clears the description, and an empty list of event types means all.</summary>
public sealed record UpdateWebhookEndpointRequest
{
    public string? Url { get; init; }

    public string? Description
    {
        get;
        init
        {
            field = value;
            HasDescription = true;
        }
    }

    public IReadOnlyList<string>? EventTypes { get; init; }

    public bool? Enabled { get; init; }

    [JsonIgnore]
    public bool HasDescription { get; private init; }
}

public sealed record WebhookEndpointResponse(
    Guid Id,
    string? Key,
    string Url,
    string? Description,
    IReadOnlyList<string> EventTypes,
    bool Enabled,
    DateTimeOffset? FailingSince,
    DateTimeOffset? PreviousSecretExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A new endpoint and its signing secret, which is shown only now.</summary>
public sealed record CreatedWebhookEndpointResponse(WebhookEndpointResponse Endpoint, string Secret);

/// <summary>The new signing secret, shown only now; the previous one keeps signing until <c>previousSecretExpiresAt</c> (EVT-8).</summary>
public sealed record WebhookSecretResponse(string Secret, DateTimeOffset PreviousSecretExpiresAt);

public sealed record WebhookDeliveryResponse(
    Guid Id,
    Guid EndpointId,
    Guid EventId,
    string EventType,
    WebhookDeliveryStatus Status,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextAttemptAt,
    int? ResponseStatusCode,
    string? ResponseBody,
    string? Error,
    JsonElement Payload);
