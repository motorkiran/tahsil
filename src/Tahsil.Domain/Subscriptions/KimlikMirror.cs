namespace Tahsil.Domain.Subscriptions;

/// <summary>
/// The Kimlik subscription a Tahsil subscription is mirrored to, and how its last sync went (KML-17, KML-20). Kept
/// apart from the subscription, so that syncs, which run on their own, do not change the subscription's version.
/// </summary>
public sealed class KimlikMirror
{
    public const int ErrorMaxLength = 500;

    private KimlikMirror()
    {
    }

    public Guid SubscriptionId { get; private init; }

    /// <summary>The Kimlik subscription, while one gives the subscription's plan.</summary>
    public Guid? KimlikSubscriptionId { get; private set; }

    public DateTimeOffset? SyncedAt { get; private set; }

    /// <summary>The last failure, cleared by a success.</summary>
    public string? Error { get; private set; }

    /// <summary>Since when syncs have failed without a success in between.</summary>
    public DateTimeOffset? FailingSince { get; private set; }

    public static KimlikMirror For(Guid subscriptionId) => new() { SubscriptionId = subscriptionId };

    public void Succeeded(Guid? kimlikSubscriptionId, DateTimeOffset now)
    {
        KimlikSubscriptionId = kimlikSubscriptionId;
        SyncedAt = now;
        Error = null;
        FailingSince = null;
    }

    public void Failed(string error, DateTimeOffset now)
    {
        Error = error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
        FailingSince ??= now;
    }
}
