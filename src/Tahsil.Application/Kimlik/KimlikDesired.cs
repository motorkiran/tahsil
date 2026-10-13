using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Kimlik;

public enum KimlikDesiredKind
{
    /// <summary>No Kimlik subscription: nothing is paid yet.</summary>
    None,

    /// <summary>Trialing until <see cref="KimlikDesired.At"/>.</summary>
    Trialing,

    /// <summary>Active until <see cref="KimlikDesired.At"/>.</summary>
    Active,

    /// <summary>Canceled, in effect until <see cref="KimlikDesired.At"/>.</summary>
    CancelsAt,

    /// <summary>Out of effect now.</summary>
    Ended,
}

/// <summary>What Kimlik should show for a Tahsil subscription (KML-13).</summary>
public sealed record KimlikDesired(KimlikDesiredKind Kind, string? Plan = null, DateTimeOffset? At = null)
{
    public static readonly KimlikDesired None = new(KimlikDesiredKind.None);

    public static readonly KimlikDesired Ended = new(KimlikDesiredKind.Ended);

    /// <summary>Whether Kimlik should give a plan.</summary>
    public bool GivesPlan => Kind is KimlikDesiredKind.Trialing or KimlikDesiredKind.Active or KimlikDesiredKind.CancelsAt;
}

/// <summary>The facts of a Tahsil subscription that its Kimlik state follows from.</summary>
public sealed record KimlikFacts(
    SubscriptionStatus Status,
    DateTimeOffset? TrialEnd,
    DateTimeOffset CurrentPeriodEnd,
    DateTimeOffset? CancelAt,
    string? Plan,
    bool Linked,
    DateTimeOffset? FinalActionAt);

public static class KimlikStates
{
    /// <summary>
    /// The desired Kimlik state (KML-13), a pure function of the subscription and the settings: ends written to Kimlik
    /// carry the buffer of KML-14, except a cancellation's, which ends access when Tahsil's ends.
    /// </summary>
    public static KimlikDesired Desired(KimlikFacts facts, KimlikOptions options)
    {
        if (facts.Plan is not { } plan || !facts.Linked)
        {
            // Not mirrored, or not any more.
            return KimlikDesired.Ended;
        }

        var buffer = options.SyncBuffer;
        return facts.Status switch
        {
            SubscriptionStatus.Incomplete => KimlikDesired.None,

            // A Kimlik trial cannot be canceled to end later: it ends at its trial end, which a cancellation brings forward.
            SubscriptionStatus.Trialing => new KimlikDesired(KimlikDesiredKind.Trialing, plan, facts.CancelAt ?? facts.TrialEnd!.Value + buffer),
            SubscriptionStatus.Active or SubscriptionStatus.PastDue when facts.CancelAt is { } cancelAt => new KimlikDesired(KimlikDesiredKind.CancelsAt, plan, cancelAt),
            SubscriptionStatus.Active => new KimlikDesired(KimlikDesiredKind.Active, plan, facts.CurrentPeriodEnd + buffer),
            SubscriptionStatus.PastDue when options.PastDueAccess == KimlikOptions.EndImmediately => KimlikDesired.Ended,
            SubscriptionStatus.PastDue => new KimlikDesired(
                KimlikDesiredKind.Active, plan, (facts.FinalActionAt is { } final && final > facts.CurrentPeriodEnd ? final : facts.CurrentPeriodEnd) + buffer),
            _ => KimlikDesired.Ended,
        };
    }
}
