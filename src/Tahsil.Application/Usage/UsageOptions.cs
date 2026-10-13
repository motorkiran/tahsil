namespace Tahsil.Application.Usage;

/// <summary>Usage reporting settings from the <c>Tahsil:Usage</c> configuration section.</summary>
public sealed class UsageOptions
{
    public const string SectionName = "Tahsil:Usage";

    /// <summary>The most events one request reports (USG-1).</summary>
    public int MaxBatchSize { get; set; } = 1000;

    /// <summary>How far ahead of the billing clock an event's timestamp may be (USG-4).</summary>
    public TimeSpan FutureTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long events are kept after they happened, once their period is invoiced (USG-13).</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(400);

    internal bool IsValid() => MaxBatchSize is >= 1 and <= 10_000 && FutureTolerance >= TimeSpan.Zero && RetentionPeriod >= TimeSpan.FromDays(1);
}
