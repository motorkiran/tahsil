using Tahsil.Application.PaymentMethods;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Simulator;

namespace Tahsil.Server.Simulator;

/// <summary>The test API's way into the simulated bank (SIM-7, SIM-8).</summary>
internal sealed class SimulatorCards(SimulatorBank bank) : ISimulatorCards
{
    public Task<SavedCard?> SaveAsync(Guid connectionId, string number, int expMonth, int expYear, CancellationToken cancellationToken) =>
        bank.SaveCardAsync(connectionId, number, expMonth, expYear, cancellationToken);

    public async Task<bool> SetBehaviorAsync(Guid connectionId, string token, string behavior, CancellationToken cancellationToken) =>
        CardBehavior.Parse(behavior) is { } parsed && await bank.SetCardBehaviorAsync(connectionId, token, parsed, cancellationToken);
}
