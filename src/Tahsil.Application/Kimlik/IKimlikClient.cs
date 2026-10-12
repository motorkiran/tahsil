using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;

namespace Tahsil.Application.Kimlik;

public enum KimlikSubscriptionStatus
{
    Trialing,
    Active,

    /// <summary>In effect until its period ends, then expired.</summary>
    Canceled,

    Expired,
}

/// <summary>A subscription in Kimlik, as its Management API returns it.</summary>
public sealed record KimlikSubscription(
    Guid Id,
    KimlikSubscriberType SubscriberType,
    Guid SubscriberId,
    string Plan,
    KimlikSubscriptionStatus Status,
    DateTimeOffset CurrentPeriodStart,
    DateTimeOffset? CurrentPeriodEnd,
    DateTimeOffset? TrialEndsAt,
    DateTimeOffset? CanceledAt,
    string? ExternalReference)
{
    /// <summary>When it stops giving its plan, unless renewed first; none for one without an end.</summary>
    public DateTimeOffset? EndsAt => Status switch
    {
        KimlikSubscriptionStatus.Trialing => TrialEndsAt,
        KimlikSubscriptionStatus.Active or KimlikSubscriptionStatus.Canceled => CurrentPeriodEnd,
        _ => null,
    };

    /// <summary>Whether it still gives its plan at <paramref name="now"/>, as Kimlik decides it.</summary>
    public bool IsInEffectAt(DateTimeOffset now) => Status != KimlikSubscriptionStatus.Expired && (EndsAt is not { } end || now < end);
}

/// <summary>A new Kimlik subscription: a trial with its end, or a paid period with its end.</summary>
public sealed record KimlikSubscriptionDraft(
    KimlikSubscriber Subscriber, string Plan, DateTimeOffset? TrialEndsAt, DateTimeOffset? CurrentPeriodEnd, string ExternalReference);

/// <summary>
/// A change of a Kimlik subscription; only what is set is sent, since Kimlik reads an explicit <c>null</c> as "clear"
/// (KML-11), and Tahsil never clears a field.
/// </summary>
public sealed record KimlikSubscriptionChange
{
    public string? Plan { get; init; }

    /// <summary>Trialing or active; setting it resumes a canceled subscription.</summary>
    public KimlikSubscriptionStatus? Status { get; init; }

    public DateTimeOffset? TrialEndsAt { get; init; }

    public DateTimeOffset? CurrentPeriodEnd { get; init; }

    public string? ExternalReference { get; init; }

    public bool IsEmpty => Plan is null && Status is null && TrialEndsAt is null && CurrentPeriodEnd is null && ExternalReference is null;
}

public enum KimlikPlanKind
{
    Base,
    AddOn,
}

public sealed record KimlikPlan(string Key, KimlikPlanKind Kind, bool IsArchived);

public enum KimlikUserStatus
{
    Active,
    Suspended,
}

public sealed record KimlikUser(Guid Id, KimlikUserStatus Status, IReadOnlyList<string> Roles);

public sealed record KimlikOrganization(Guid Id, string Slug);

/// <summary>
/// Kimlik's Management API as the connector uses it (KML-11). Failures carry Kimlik's own error code, such as
/// <c>subscription.exists</c>, or <see cref="KimlikErrors.Unavailable"/> when Kimlik cannot be reached.
/// </summary>
public interface IKimlikClient
{
    /// <summary>The subscription, or a <see cref="ErrorType.NotFound"/> error when Kimlik has none with this ID.</summary>
    Task<Result<KimlikSubscription>> GetSubscriptionAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The subscriber's subscriptions, newest first, ended ones included.</summary>
    Task<Result<IReadOnlyList<KimlikSubscription>>> ListSubscriptionsAsync(KimlikSubscriber subscriber, CancellationToken cancellationToken);

    Task<Result<KimlikSubscription>> CreateSubscriptionAsync(KimlikSubscriptionDraft draft, CancellationToken cancellationToken);

    Task<Result<KimlikSubscription>> UpdateSubscriptionAsync(Guid id, KimlikSubscriptionChange change, CancellationToken cancellationToken);

    /// <summary>Cancels at the period's end; a subscription in trial or without an end expires at once.</summary>
    Task<Result<KimlikSubscription>> CancelSubscriptionAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Every plan, archived ones included.</summary>
    Task<Result<IReadOnlyList<KimlikPlan>>> ListPlansAsync(CancellationToken cancellationToken);

    Task<Result<KimlikUser>> GetUserAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<KimlikOrganization>> GetOrganizationAsync(Guid id, CancellationToken cancellationToken);
}

public static class KimlikErrors
{
    public static readonly Error Unavailable = Error.Unavailable("kimlik.unavailable", "Kimlik could not be reached or failed; the request will be tried again.");

    public static readonly Error Unauthorized = Error.Unauthorized("kimlik.unauthorized", "Kimlik did not accept Tahsil's client credentials.");
}
