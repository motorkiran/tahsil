using Microsoft.Extensions.Logging.Abstractions;
using Stripe;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Stripe;

namespace Tahsil.Providers.Tests.Conformance;

/// <summary>
/// The conformance suite against a Stripe sandbox (STR §8), when <c>TAHSIL_SANDBOX_STRIPE_SECRET_KEY</c> is set. The
/// fixture, never the adapter, saves Stripe's test payment methods through confirmed off-session SetupIntents.
/// </summary>
public sealed class StripeConformance : ConformanceSuite, IDisposable
{
    private static readonly string? SecretKey = Environment.GetEnvironmentVariable("TAHSIL_SANDBOX_STRIPE_SECRET_KEY");
    private readonly HttpClient _http = new();

    protected override IPaymentProvider Provider { get; } = new StripeProvider();

    protected override string? Unavailable =>
        string.IsNullOrEmpty(SecretKey) ? "Set TAHSIL_SANDBOX_STRIPE_SECRET_KEY to run against a Stripe sandbox." : null;

    protected override Amount Amount => new(1_000, "USD", 2);

    public void Dispose() => _http.Dispose();

    protected override ProviderContext Context() => new()
    {
        ConnectionId = Guid.CreateVersion7(),
        Environment = ProviderEnvironment.Sandbox,
        Credentials = new Dictionary<string, string>
        {
            ["secretKey"] = SecretKey!,
            ["webhookSigningSecret"] = Environment.GetEnvironmentVariable("TAHSIL_SANDBOX_STRIPE_WEBHOOK_SECRET") ?? "whsec_unused",
        },
        Settings = new Dictionary<string, string>(),
        Http = _http,
        Time = TimeProvider.System,
        Logger = NullLogger.Instance,
        NotificationUrl = new Uri("https://billing.example.com/provider-notifications/conformance"),
    };

    /// <summary>Stripe declines charges of saved cards only generically; insufficient funds is refused when the card is saved.</summary>
    protected override bool Covers(TestCardScenario scenario) => scenario != TestCardScenario.InsufficientFunds;

    protected override async Task<SavedCardReference> SaveCardAsync(TestCardScenario scenario)
    {
        var client = new StripeClient(SecretKey!);
        var customer = await client.V1.Customers.CreateAsync(new CustomerCreateOptions { Email = "ayse@example.com", Name = "Ayşe Yılmaz" }, cancellationToken: CancellationToken);
        var setup = await client.V1.SetupIntents.CreateAsync(new SetupIntentCreateOptions
        {
            Customer = customer.Id,
            PaymentMethod = scenario == TestCardScenario.Declined ? "pm_card_chargeCustomerFail" : "pm_card_visa",
            Usage = "off_session",
            Confirm = true,
            AllowedPaymentMethodTypes = ["card"],
        }, cancellationToken: CancellationToken);
        setup.Status.ShouldBe("succeeded");
        return new SavedCardReference(customer.Id, $$"""{"paymentMethodId":"{{setup.PaymentMethodId}}"}""");
    }
}
