namespace Tahsil.Infrastructure.Workers;

/// <summary>
/// The consecutive failures of one worker item, shared by every instance. Deleted when the item succeeds; with
/// <see cref="AttentionRaisedAt"/> set, the item is on the attention list.
/// </summary>
public sealed class WorkerFailure
{
    public const int WorkerMaxLength = 64;

    /// <summary>Failures after which the item is reported as needing attention.</summary>
    public const int AttentionThreshold = 10;

    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60),
    ];

    public required string Worker { get; init; }

    public required Guid ItemId { get; init; }

    public int FailureCount { get; set; }

    public DateTimeOffset FirstFailedAt { get; set; }

    public DateTimeOffset LastFailedAt { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? AttentionRaisedAt { get; set; }

    /// <summary>How long an item waits after its <paramref name="failureCount"/>th consecutive failure: 1, 5, 15 and 60 minutes, then hourly.</summary>
    public static TimeSpan BackoffAfter(int failureCount) => Backoff[Math.Clamp(failureCount, 1, Backoff.Length) - 1];
}
