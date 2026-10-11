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
        "subscription.invalid", "Invoices sent to the customer are due in 1 to 365 days.", "daysUntilDue");

    public static readonly Error PaymentMethodRequired = Error.Validation(
        "subscription.payment_method_required",
        "Charging automatically without a trial needs a card: give defaultPaymentMethod, or save a default card for the customer with a connection that takes the currency.",
        "defaultPaymentMethod");

    public static readonly Error PaymentMethodUnusable = Error.Validation(
        "subscription.payment_method_unusable",
        "The card is not active, not the customer's, or its connection is disabled, cannot charge saved cards or does not take the subscription's currency.",
        "defaultPaymentMethod");

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
