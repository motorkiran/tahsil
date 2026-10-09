namespace Tahsil.Infrastructure.Auditing;

/// <summary>Audit trail settings from the <c>Tahsil:Audit</c> configuration section.</summary>
public sealed class AuditOptions
{
    public const string SectionName = "Tahsil:Audit";

    /// <summary>How long audit events are kept; ten years by default, the longest Turkish record-keeping period.</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(3650);

    internal bool IsValid() => RetentionPeriod >= TimeSpan.FromDays(1);
}
