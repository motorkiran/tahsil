namespace Tahsil.Infrastructure.Workers;

/// <summary>Background worker settings from the <c>Tahsil:Workers</c> configuration section.</summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Tahsil:Workers";

    /// <summary>
    /// Runs the workers and periodic jobs on this instance. Any number of instances may run them; turn them off on
    /// instances that should only serve requests.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often workers look for due items. Items this instance makes due are picked up at once; polling covers
    /// other instances and scheduled times.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The most items a worker processes before it looks at the time again.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// How long a worker that calls other services owns an item. If the instance dies, the item is due again once
    /// the lease runs out.
    /// </summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The most provider calls for payments that run at once, per instance and connection.</summary>
    public int PaymentConcurrency { get; set; } = 4;

    internal bool IsValid() =>
        PollingInterval > TimeSpan.Zero && BatchSize is > 0 and <= 1000 && Lease >= TimeSpan.FromSeconds(30) && PaymentConcurrency is > 0 and <= 64;
}
