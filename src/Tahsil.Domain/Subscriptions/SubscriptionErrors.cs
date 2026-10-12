using Tahsil.Domain.Common;

namespace Tahsil.Domain.Subscriptions;

public static class SubscriptionErrors
{
    public static readonly Error NotFound = Error.NotFound("subscription.not_found", "The subscription does not exist.");

    public static readonly Error InvalidCancelAt = Error.Validation(
        "subscription.invalid_cancel_at", "The cancellation time is in the future and at most the current period's end.", "at");

    public static readonly Error NotResumable = Error.Conflict(
        "subscription.not_resumable", "Only a subscription with a scheduled cancellation can be resumed, before it ends.");

    public static readonly Error InvalidDaysUntilDue = Error.Validation(
        "subscription.invalid", "Invoices sent to the customer are due in 0 to 365 days.", "daysUntilDue");

    public static readonly Error PaymentMethodRequired = Error.Validation(
        "subscription.payment_method_required",
        "Charging automatically without a trial needs a card: give defaultPaymentMethod, or save a default card for the customer with a connection that takes the currency.",
        "defaultPaymentMethod");

    public static readonly Error PaymentMethodUnusable = Error.Validation(
        "subscription.payment_method_unusable",
        "The card is not active, not the customer's, or its connection is disabled, cannot charge saved cards or does not take the subscription's currency.",
        "defaultPaymentMethod");

    public static readonly Error CurrencyMismatch = Error.Validation(
        "subscription.currency_mismatch", "Every price of a subscription is in the subscription's currency.", "items");

    public static readonly Error IntervalChangeRequiresNow = Error.Validation(
        "subscription.interval_change_requires_now", "Prices with another interval replace the items now; the period restarts at the change.", "effective");

    public static readonly Error InvalidEffectiveAt = Error.Validation(
        "subscription.invalid", "A scheduled change takes effect at a renewal of the subscription, before any scheduled cancellation.", "effectiveAt");

    public static readonly Error PriceIncreaseNoticeRequired = Error.Validation(
        "subscription.price_increase_notice_required",
        "A higher price for an individual customer takes effect at a renewal at least the notice period away (Tahsil:Subscriptions:PriceIncreaseNotice); only company customers can skip the notice.",
        "effective");

    public static readonly Error InvalidComment = Error.Validation("subscription.invalid", "The comment is at most 500 characters.", "comment");

    public static Error InvalidItems(string field, string message) => Error.Validation("subscription.invalid_items", message, field);

    public static Error InvalidState(SubscriptionStatus status, string action) =>
        Error.Conflict("subscription.invalid_state", $"A subscription that is {Wire(status)} cannot be {action}.");

    public static Error Invalid(string field, string message) => Error.Validation("subscription.invalid", message, field);

    private static string Wire(SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.IncompleteExpired => "incomplete_expired",
        SubscriptionStatus.PastDue => "past_due",
        _ => status.ToString().ToLowerInvariant(),
    };
}
