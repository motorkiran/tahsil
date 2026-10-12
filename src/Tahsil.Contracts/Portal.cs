using System.ComponentModel.DataAnnotations;

namespace Tahsil.Contracts;

/// <summary>A task a portal session opens on; the customer returns to <c>returnUrl</c> after it.</summary>
public enum PortalFlow
{
    PaymentMethodUpdate,
    SubscriptionUpdate,
    SubscriptionCancel,
}

/// <summary>A portal session for a customer the application has authenticated.</summary>
public sealed record CreatePortalSessionRequest
{
    public required Guid Customer { get; init; }

    /// <summary>Where the portal's "Return" link leads, such as the application's billing page.</summary>
    [Required]
    public required string ReturnUrl { get; init; }

    /// <summary><c>tr</c> or <c>en</c>; the customer's language when omitted.</summary>
    public string? Locale { get; init; }

    public PortalFlow? Flow { get; init; }

    /// <summary>For <c>subscription_update</c> and <c>subscription_cancel</c>: the customer's subscription.</summary>
    public Guid? FlowSubscription { get; init; }
}

/// <summary>A portal session. <c>url</c> is given once, when the session is created: Tahsil keeps only its hash.</summary>
public sealed record PortalSessionResponse(
    Guid Id, Uri Url, Guid CustomerId, string? ReturnUrl, string? Locale, PortalFlow? Flow, Guid? FlowSubscription, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);
