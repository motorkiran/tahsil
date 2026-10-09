namespace Tahsil.Infrastructure.Outbox;

/// <summary>Delivery settings from the <c>Tahsil:Outbox</c> configuration section.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Tahsil:Outbox";

    /// <summary>
    /// How often pending messages are looked for. Messages written by this instance are picked up as soon as their
    /// transaction commits; polling covers other instances and retries.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int BatchSize { get; set; } = 20;

    /// <summary>Attempts before a message is marked as failed. Retries back off exponentially, up to an hour apart.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>How long delivered messages are kept before they are deleted.</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);

    internal bool IsValid() =>
        PollingInterval > TimeSpan.Zero && BatchSize is > 0 and <= 1000 && MaxAttempts > 0 && RetentionPeriod > TimeSpan.Zero;
}
