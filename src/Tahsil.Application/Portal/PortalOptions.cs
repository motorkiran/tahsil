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

    /// <summary>The products, and of each the prices, a customer may switch among (PRT-5); none offers no other plan.</summary>
    public List<PortalProductOptions> Products { get; set; } = [];

    /// <summary>The quantities a customer may choose (PRT §2); the same minimum and maximum leaves quantities alone.</summary>
    public PortalQuantityOptions QuantityLimits { get; set; } = new();

    /// <summary>When a downgrade takes effect (PRT-6): <c>period_end</c>, or <c>now</c> with a credit for the unused time.</summary>
    public string DowngradeTiming { get; set; } = "period_end";

    internal bool DowngradesNow => DowngradeTiming == "now";

    public bool IsEnabled(string feature) => Features.TryGetValue(feature, out var enabled) ? enabled : PortalFeatures.Defaults[feature];

    internal bool HasValidLifetimes() =>
        SessionLifetime >= TimeSpan.FromMinutes(5) && SessionLifetime <= TimeSpan.FromDays(1)
        && EmailLinkLifetime >= TimeSpan.FromHours(1) && EmailLinkLifetime <= TimeSpan.FromDays(30);

    internal bool HasKnownFeatures() => Features.Keys.All(PortalFeatures.Defaults.ContainsKey);

    internal bool HasValidPlanChanges() =>
        DowngradeTiming is "period_end" or "now"
        && QuantityLimits is { Minimum: >= 1, Maximum: <= 10_000 } && QuantityLimits.Maximum >= QuantityLimits.Minimum
        && Products.All(product => !string.IsNullOrWhiteSpace(product.Product));
}

/// <summary>A product offered for switching, by key, with the keys of its prices on offer; all of its prices when none are named.</summary>
public sealed class PortalProductOptions
{
    public string Product { get; set; } = string.Empty;

    public List<string> Prices { get; set; } = [];
}

public sealed class PortalQuantityOptions
{
    public int Minimum { get; set; } = 1;

    public int Maximum { get; set; } = 1;
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
