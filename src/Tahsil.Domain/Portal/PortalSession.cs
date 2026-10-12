using Tahsil.Domain.Checkout;
using Tahsil.Domain.Common;

namespace Tahsil.Domain.Portal;

/// <summary>A task a portal session opens on (PRT-2).</summary>
public enum PortalFlow
{
    PaymentMethodUpdate,
    SubscriptionUpdate,
    SubscriptionCancel,
}

/// <summary>
/// A customer's way into the portal (DOM §6.3, PRT-1): the application authenticated them and sends them to the
/// session's page, which works for them alone, as often as they like until it expires.
/// </summary>
public sealed class PortalSession
{
    public const int LocaleMaxLength = 8;

    private PortalSession()
    {
    }

    public Guid Id { get; private init; }

    /// <summary>SHA-256 of the page's URL token, which finds the session.</summary>
    public byte[] TokenHash { get; private init; } = [];

    public Guid CustomerId { get; private init; }

    /// <summary>Where the customer goes back to; none for a session Tahsil made for an email's link (PRT-4).</summary>
    public string? ReturnUrl { get; private init; }

    /// <summary>Overrides the customer's language on the page.</summary>
    public string? Locale { get; private init; }

    public PortalFlow? Flow { get; private init; }

    /// <summary>The subscription <see cref="Flow"/> is about.</summary>
    public Guid? FlowSubscriptionId { get; private init; }

    public DateTimeOffset ExpiresAt { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static Result<PortalSession> Create(
        byte[] tokenHash,
        Guid customerId,
        string? returnUrl,
        bool allowLoopbackHttp,
        string? locale,
        PortalFlow? flow,
        Guid? flowSubscriptionId,
        TimeSpan lifetime,
        DateTimeOffset now)
    {
        if (returnUrl is not null && !CheckoutSession.IsValidUrl(returnUrl, allowLoopbackHttp))
        {
            return PortalErrors.InvalidUrl;
        }

        if ((flow is PortalFlow.SubscriptionUpdate or PortalFlow.SubscriptionCancel) != (flowSubscriptionId is not null))
        {
            return PortalErrors.Invalid("flowSubscription", "The subscription flows name their subscription; other sessions name none.");
        }

        return new PortalSession
        {
            Id = Guid.CreateVersion7(now),
            TokenHash = tokenHash,
            CustomerId = customerId,
            ReturnUrl = returnUrl,
            Locale = locale,
            Flow = flow,
            FlowSubscriptionId = flowSubscriptionId,
            ExpiresAt = now + lifetime,
            CreatedAt = now,
        };
    }

    public bool IsOpen(DateTimeOffset now) => now < ExpiresAt;
}

public static class PortalErrors
{
    public static readonly Error InvalidUrl = Error.Validation(
        "portal.invalid_url", "The return URL is an absolute HTTPS URL of at most 2000 characters, without credentials or a fragment.", "returnUrl");

    public static readonly Error SubscriptionNotFound = Error.Validation(
        "portal.invalid", "The flow's subscription is not one of the customer's that is still running.", "flowSubscription");

    public static Error Invalid(string field, string message) => Error.Validation("portal.invalid", message, field);
}
