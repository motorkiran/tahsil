using Microsoft.Extensions.DependencyInjection;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Server.Tests.Payments;

/// <summary>Where the process dies during a saved-card charge, as if it were killed.</summary>
public enum ProcessDeath
{
    None,

    /// <summary>After the payment was written ahead, before the provider was called.</summary>
    BeforeTheCharge,

    /// <summary>After the provider charged, before Tahsil recorded the outcome.</summary>
    AfterTheCharge,
}

/// <summary>The faults the failure injection tests turn on.</summary>
internal sealed class Faults
{
    public ProcessDeath Death { get; set; }

    /// <summary>Added to the amounts the provider reports as charged, as a provider that charged another amount.</summary>
    public long ChargedAmountOffset { get; set; }
}

/// <summary>
/// The real simulator with faults injected around it (PAY §13): the process dying around a charge, or answers with
/// another charged amount. Without faults it is the simulator.
/// </summary>
internal sealed class FaultySimulator(IPaymentProvider simulator, Faults faults) : IPaymentProvider
{
    public ProviderDescriptor Descriptor => simulator.Descriptor;

    /// <summary>Replaces the simulator of <paramref name="services"/> with one that has <paramref name="faults"/>.</summary>
    public static void Inject(IServiceCollection services, Faults faults)
    {
        var registration = services.Single(service => service.ServiceType == typeof(IPaymentProvider) && service.ImplementationFactory is not null);
        services.Remove(registration);
        services.AddSingleton<IPaymentProvider>(provider => new FaultySimulator((IPaymentProvider)registration.ImplementationFactory!(provider), faults));
    }

    public ProviderCapabilities CapabilitiesFor(IReadOnlyDictionary<string, string> settings) => simulator.CapabilitiesFor(settings);

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderContext context, CancellationToken cancellationToken) =>
        simulator.TestConnectionAsync(context, cancellationToken);

    public Task<StartResult> StartAsync(ProviderContext context, StartRequest request, CancellationToken cancellationToken) =>
        simulator.StartAsync(context, request, cancellationToken);

    public async Task<PaymentOutcome> CompleteAsync(ProviderContext context, CompleteRequest request, CancellationToken cancellationToken) =>
        Altered(await simulator.CompleteAsync(context, request, cancellationToken));

    public async Task<PaymentOutcome> ChargeSavedCardAsync(ProviderContext context, ChargeRequest request, CancellationToken cancellationToken)
    {
        if (faults.Death == ProcessDeath.BeforeTheCharge)
        {
            throw new ProcessKilledException();
        }

        var outcome = await simulator.ChargeSavedCardAsync(context, request, cancellationToken);
        return faults.Death == ProcessDeath.AfterTheCharge ? throw new ProcessKilledException() : Altered(outcome);
    }

    public async Task<PaymentLookup> FindPaymentAsync(ProviderContext context, PaymentQuery query, CancellationToken cancellationToken)
    {
        var lookup = await simulator.FindPaymentAsync(context, query, cancellationToken);
        return lookup is PaymentLookup.Found found ? new PaymentLookup.Found(Altered(found.Outcome)) : lookup;
    }

    public Task<RefundOutcome> RefundAsync(ProviderContext context, RefundRequest request, CancellationToken cancellationToken) =>
        simulator.RefundAsync(context, request, cancellationToken);

    public Task<RefundLookup> FindRefundAsync(ProviderContext context, RefundQuery query, CancellationToken cancellationToken) =>
        simulator.FindRefundAsync(context, query, cancellationToken);

    public Task<RemoveCardResult> RemoveSavedCardAsync(ProviderContext context, SavedCardReference card, CancellationToken cancellationToken) =>
        simulator.RemoveSavedCardAsync(context, card, cancellationToken);

    public Task<NotificationResult> HandleNotificationAsync(ProviderContext context, IncomingNotification notification, CancellationToken cancellationToken) =>
        simulator.HandleNotificationAsync(context, notification, cancellationToken);

    private PaymentOutcome Altered(PaymentOutcome outcome) => faults.ChargedAmountOffset != 0 && outcome.ChargedAmount is { } charged
        ? outcome with { ChargedAmount = charged with { MinorUnits = charged.MinorUnits + faults.ChargedAmountOffset } }
        : outcome;

    /// <summary>Stands for the process dying: nothing after it runs.</summary>
    private sealed class ProcessKilledException() : Exception("The process was killed.");
}
