namespace Tahsil.Application.Subscriptions;

public sealed class SubscriptionOptions
{
    public const string SectionName = "Tahsil:Subscriptions";

    /// <summary>How long an incomplete subscription waits for its first invoice to be paid (SUB-6).</summary>
    public TimeSpan IncompleteExpiry { get; set; } = TimeSpan.FromHours(23);
}
