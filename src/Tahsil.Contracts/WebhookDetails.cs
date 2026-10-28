using System.Text.Json;
using Tahsil.Contracts.Serialization;

namespace Tahsil.Contracts;

// The details of each event type (EVT §2), for typed reading with WebhookEventDetails.As<T>. Events may carry more
// fields than these records; new fields are added without breaking them (EVT-6).
public sealed record CustomerCreatedDetails(string? ExternalId);

public sealed record CustomerUpdatedDetails(IReadOnlyList<string> Changed);

public sealed record CustomerDeletedDetails(bool Anonymized);

public sealed record PaymentMethodAttachedDetails(string? Brand, string? Last4, int? ExpMonth, int? ExpYear, Guid ConnectionId);

public sealed record PaymentMethodRemovedDetails(string? Brand, string? Last4);

public sealed record PaymentMethodWillExpireDetails(int ExpMonth, int ExpYear, IReadOnlyList<Guid> SubscriptionIds);

public sealed record CheckoutCompletedDetails(CheckoutMode Mode, string? ClientReferenceId, Guid? SubscriptionId, Guid? InvoiceId, Guid? PaymentMethodId);

public sealed record CheckoutExpiredDetails(CheckoutMode Mode, string? ClientReferenceId);

/// <summary>An item of a new subscription: the price's ID and the quantity, none for metered prices.</summary>
public sealed record SubscriptionEventItem(Guid Price, int? Quantity);

public sealed record SubscriptionCreatedDetails(SubscriptionStatus Status, IReadOnlyList<SubscriptionEventItem> Items, DateTimeOffset CurrentPeriodEnd, DateTimeOffset? TrialEnd);

public sealed record SubscriptionUpdatedDetails(IReadOnlyList<string> Changed, SubscriptionStatus Status, SubscriptionStatus? PreviousStatus);

public sealed record SubscriptionRenewedDetails(DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, Guid? InvoiceId);

public sealed record SubscriptionTrialWillEndDetails(DateTimeOffset TrialEnd);

public sealed record SubscriptionWillRenewDetails(DateTimeOffset RenewsAt);

public sealed record SubscriptionPastDueDetails(Guid? InvoiceId, string? FailureCode);

/// <param name="Reason">Why it ended.</param>
/// <param name="EndedAt">When it ended.</param>
/// <param name="Refund">What was refunded with the cancellation, in minor units, when a refund was asked for.</param>
public sealed record SubscriptionCanceledDetails(CancellationReason Reason, DateTimeOffset EndedAt, long? Refund = null);

/// <summary>
/// The details of <c>invoice.created</c>, <c>invoice.finalized</c>, <c>invoice.paid</c>, <c>invoice.voided</c> and
/// <c>invoice.marked_uncollectible</c>: the invoice's amounts as they are after the change.
/// </summary>
/// <param name="Number">The invoice's number; none for a draft.</param>
/// <param name="Total">The total, tax included.</param>
/// <param name="AmountDue">What is to be paid after the customer's balance.</param>
/// <param name="AmountPaid">What was paid.</param>
/// <param name="AmountRemaining">What is still to be paid.</param>
/// <param name="Currency">The currency of the amounts.</param>
/// <param name="DueDate">When an invoice sent to the customer is due.</param>
/// <param name="SubscriptionId">The subscription it bills.</param>
/// <param name="BillingReason">Why it was made.</param>
/// <param name="FinalizeAt">For <c>invoice.created</c>: when the draft finalizes itself.</param>
/// <param name="CheckoutSessionId">For <c>invoice.paid</c>: the checkout session that paid it.</param>
public sealed record InvoiceDetails(
    string? Number,
    long Total,
    long AmountDue,
    long AmountPaid,
    long AmountRemaining,
    string Currency,
    DateTimeOffset? DueDate,
    Guid? SubscriptionId,
    BillingReason BillingReason,
    DateTimeOffset? FinalizeAt = null,
    Guid? CheckoutSessionId = null);

public sealed record InvoicePaymentFailedDetails(
    string? Number, long AmountDue, string Currency, int Attempt, string? FailureCode, string? FailureCategory, DateTimeOffset? NextAttemptAt);

public sealed record InvoiceActionRequiredDetails(string? Number, Uri? HostedUrl);

public sealed record InvoiceOverdueDetails(string? Number, DateTimeOffset? DueDate, long AmountRemaining);

/// <summary>The details of <c>payment.succeeded</c> and <c>payment.failed</c>.</summary>
/// <param name="InvoiceId">The invoice it paid.</param>
/// <param name="Amount">The amount, in minor units.</param>
/// <param name="Currency">The currency.</param>
/// <param name="Method">How it was paid: <c>card</c>, or out of band such as <c>bank_transfer</c>.</param>
/// <param name="ConnectionId">The provider connection, for card payments.</param>
/// <param name="FailureCode">Why it failed.</param>
/// <param name="FailureCategory">What a failure means for retries.</param>
public sealed record PaymentDetails(Guid? InvoiceId, long Amount, string Currency, string? Method, Guid? ConnectionId, string? FailureCode, string? FailureCategory);

/// <summary>The details of <c>refund.succeeded</c> and <c>refund.failed</c>.</summary>
/// <param name="PaymentId">The payment refunded.</param>
/// <param name="InvoiceId">The payment's invoice.</param>
/// <param name="Amount">The amount, in minor units.</param>
/// <param name="Currency">The currency.</param>
/// <param name="Reason">Why it was refunded.</param>
/// <param name="FailureCode">Why it failed.</param>
public sealed record RefundDetails(Guid PaymentId, Guid? InvoiceId, long Amount, string Currency, RefundReason Reason, string? FailureCode);

/// <summary>Reading an event's details as their record.</summary>
public static class WebhookEventDetails
{
    private static readonly JsonSerializerOptions Options = TahsilJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static readonly Dictionary<string, Type> Types = new(StringComparer.Ordinal)
    {
        [TahsilEventTypes.CustomerCreated] = typeof(CustomerCreatedDetails),
        [TahsilEventTypes.CustomerUpdated] = typeof(CustomerUpdatedDetails),
        [TahsilEventTypes.CustomerDeleted] = typeof(CustomerDeletedDetails),
        [TahsilEventTypes.PaymentMethodAttached] = typeof(PaymentMethodAttachedDetails),
        [TahsilEventTypes.PaymentMethodRemoved] = typeof(PaymentMethodRemovedDetails),
        [TahsilEventTypes.PaymentMethodWillExpire] = typeof(PaymentMethodWillExpireDetails),
        [TahsilEventTypes.CheckoutCompleted] = typeof(CheckoutCompletedDetails),
        [TahsilEventTypes.CheckoutExpired] = typeof(CheckoutExpiredDetails),
        [TahsilEventTypes.SubscriptionCreated] = typeof(SubscriptionCreatedDetails),
        [TahsilEventTypes.SubscriptionUpdated] = typeof(SubscriptionUpdatedDetails),
        [TahsilEventTypes.SubscriptionRenewed] = typeof(SubscriptionRenewedDetails),
        [TahsilEventTypes.SubscriptionTrialWillEnd] = typeof(SubscriptionTrialWillEndDetails),
        [TahsilEventTypes.SubscriptionWillRenew] = typeof(SubscriptionWillRenewDetails),
        [TahsilEventTypes.SubscriptionPastDue] = typeof(SubscriptionPastDueDetails),
        [TahsilEventTypes.SubscriptionCanceled] = typeof(SubscriptionCanceledDetails),
        [TahsilEventTypes.InvoiceCreated] = typeof(InvoiceDetails),
        [TahsilEventTypes.InvoiceFinalized] = typeof(InvoiceDetails),
        [TahsilEventTypes.InvoicePaid] = typeof(InvoiceDetails),
        [TahsilEventTypes.InvoiceVoided] = typeof(InvoiceDetails),
        [TahsilEventTypes.InvoiceMarkedUncollectible] = typeof(InvoiceDetails),
        [TahsilEventTypes.InvoicePaymentFailed] = typeof(InvoicePaymentFailedDetails),
        [TahsilEventTypes.InvoiceActionRequired] = typeof(InvoiceActionRequiredDetails),
        [TahsilEventTypes.InvoiceOverdue] = typeof(InvoiceOverdueDetails),
        [TahsilEventTypes.PaymentSucceeded] = typeof(PaymentDetails),
        [TahsilEventTypes.PaymentFailed] = typeof(PaymentDetails),
        [TahsilEventTypes.RefundSucceeded] = typeof(RefundDetails),
        [TahsilEventTypes.RefundFailed] = typeof(RefundDetails),
    };

    /// <summary>The record of an event type's details; none for an event without details, such as <c>webhook.test</c>.</summary>
    public static Type? TypeOf(string type) => Types.GetValueOrDefault(type);

    /// <summary>The details as <typeparamref name="T"/>, such as <see cref="InvoiceDetails"/> for <c>invoice.paid</c>.</summary>
    public static T? As<T>(this WebhookEventData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Details is { } details ? details.Deserialize<T>(Options) : default;
    }
}
