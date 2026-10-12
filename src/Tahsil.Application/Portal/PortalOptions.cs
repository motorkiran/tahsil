namespace Tahsil.Application.Portal;

/// <summary>From the <c>Tahsil:Portal</c> section (PRT §1, §2).</summary>
public sealed class PortalOptions
{
    public const string SectionName = "Tahsil:Portal";

    /// <summary>How long a session of the API lasts (PRT-1).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long the portal link of an email works (PRT-4).</summary>
    public TimeSpan EmailLinkLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Turns features on or off by name; those not named keep their defaults (<see cref="PortalFeatures"/>).</summary>
    public Dictionary<string, bool> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled(string feature) => Features.TryGetValue(feature, out var enabled) ? enabled : PortalFeatures.Defaults[feature];

    internal bool HasValidLifetimes() =>
        SessionLifetime >= TimeSpan.FromMinutes(5) && SessionLifetime <= TimeSpan.FromDays(1)
        && EmailLinkLifetime >= TimeSpan.FromHours(1) && EmailLinkLifetime <= TimeSpan.FromDays(30);

    internal bool HasKnownFeatures() => Features.Keys.All(PortalFeatures.Defaults.ContainsKey);
}

/// <summary>What customers can do in the portal (PRT §2), with whether it is on unless configured otherwise.</summary>
public static class PortalFeatures
{
    public const string Subscriptions = "subscriptions";
    public const string PlanChange = "plan_change";
    public const string Cancel = "cancel";
    public const string Resume = "resume";
    public const string PaymentMethod = "payment_method";
    public const string Invoices = "invoices";
    public const string BillingDetails = "billing_details";
    public const string PromotionCodes = "promotion_codes";
    public const string Withdrawal = "withdrawal";

    public static readonly IReadOnlyDictionary<string, bool> Defaults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
    {
        [Subscriptions] = true,
        [PlanChange] = true,
        [Cancel] = true,
        [Resume] = true,
        [PaymentMethod] = true,
        [Invoices] = true,
        [BillingDetails] = true,
        [PromotionCodes] = false,
        [Withdrawal] = false,
    };
}
