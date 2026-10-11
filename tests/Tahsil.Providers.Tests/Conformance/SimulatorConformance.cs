using Microsoft.Extensions.Logging.Abstractions;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Simulator;
using Tahsil.Providers.Tests.Simulator;

namespace Tahsil.Providers.Tests.Conformance;

/// <summary>The conformance suite against the simulator, in every build.</summary>
public sealed class SimulatorConformance : ConformanceSuite
{
    private readonly Guid _connection = Guid.CreateVersion7();
    private readonly SimulatorBank _bank = new(new InMemoryLedger(), TimeProvider.System);

    public SimulatorConformance() => Provider = new SimulatorProvider(_bank, new Uri("https://billing.example.com/"));

    protected override IPaymentProvider Provider { get; }

    protected override string? Unavailable => null;

    protected override ProviderContext Context() => new()
    {
        ConnectionId = _connection,
        Environment = ProviderEnvironment.Sandbox,
        Credentials = new Dictionary<string, string>(),
        Settings = new Dictionary<string, string>(),
        Http = new HttpClient(),
        Time = TimeProvider.System,
        Logger = NullLogger.Instance,
        NotificationUrl = new Uri($"https://billing.example.com/provider-notifications/{_connection}"),
    };

    protected override async Task<SavedCardReference> SaveCardAsync(TestCardScenario scenario)
    {
        var number = scenario switch
        {
            TestCardScenario.InsufficientFunds => "4000000000009995",
            TestCardScenario.Declined => "4000000000000341",
            _ => "4242424242424242",
        };
        var card = await _bank.SaveCardAsync(_connection, number, 12, 2030, CancellationToken) ?? throw new InvalidOperationException("Not a test card.");
        return new SavedCardReference(card.ProviderCustomer, card.Token);
    }
}
