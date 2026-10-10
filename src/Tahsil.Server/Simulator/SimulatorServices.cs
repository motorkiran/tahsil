using System.Text.Json;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.PaymentMethods;
using Tahsil.Infrastructure.Simulator;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Simulator;

namespace Tahsil.Server.Simulator;

/// <summary>The built-in simulator of Test mode (SIM-1): its adapter, its records and its card page.</summary>
internal static class SimulatorServices
{
    public static IServiceCollection AddSimulator(this IServiceCollection services)
    {
        // Live mode's provider registry leaves the simulator out, and its pages are not mapped.
        services.AddSingleton<SimulatorRecordStore>();
        services.AddSingleton<ISimulatorLedger, SimulatorLedger>();
        services.AddSingleton<SimulatorNotifier>();
        services.AddSingleton<ISimulatorNotifier>(provider => provider.GetRequiredService<SimulatorNotifier>());
        services.AddHostedService(provider => provider.GetRequiredService<SimulatorNotifier>());
        services.AddSingleton(provider => new SimulatorBank(
            provider.GetRequiredService<ISimulatorLedger>(), provider.GetRequiredService<BillingClock>(), provider.GetRequiredService<ISimulatorNotifier>()));
        services.AddSingleton<IPaymentProvider>(provider => new SimulatorProvider(
            provider.GetRequiredService<SimulatorBank>(), provider.GetRequiredService<IOptions<ServerOptions>>().Value.PublicUrl!));
        services.AddSingleton<ISimulatorCards, SimulatorCards>();
        services.AddSingleton<IConfigureOptions<RazorPagesOptions>, UnmapPagesInLiveMode>();
        return services;
    }

    private sealed class UnmapPagesInLiveMode(IOptions<ServerOptions> server) : IConfigureOptions<RazorPagesOptions>
    {
        public void Configure(RazorPagesOptions options)
        {
            if (!server.Value.IsTestMode)
            {
                options.Conventions.AddFolderRouteModelConvention("/Simulator", model => model.Selectors.Clear());
            }
        }
    }
}

/// <summary>Keeps the simulator's records in Tahsil's database.</summary>
internal sealed class SimulatorLedger(SimulatorRecordStore store) : ISimulatorLedger
{
    public async Task<SimulatorEntry?> FindAsync(Guid connectionId, SimulatorEntryKind kind, string reference, CancellationToken cancellationToken) =>
        ToEntry(await store.FindAsync(connectionId, Name(kind), reference, cancellationToken));

    public async Task<SimulatorEntry?> FindByTokenAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        ToEntry(await store.FindByTokenAsync(tokenHash, cancellationToken));

    public Task<bool> SaveAsync(SimulatorEntry entry, CancellationToken cancellationToken) => store.SaveAsync(
        new SimulatorRecord
        {
            Id = entry.Id,
            ConnectionId = entry.ConnectionId,
            Kind = Name(entry.Kind),
            Reference = entry.Reference,
            TokenHash = entry.TokenHash,
            State = entry.State,
            CreatedAt = entry.CreatedAt,
            Version = entry.Version,
        },
        isNew: entry.Version == 0,
        cancellationToken);

    private static SimulatorEntry? ToEntry(SimulatorRecord? record) => record is null
        ? null
        : new SimulatorEntry(
            record.Id,
            record.ConnectionId,
            Enum.Parse<SimulatorEntryKind>(record.Kind, ignoreCase: true),
            record.Reference,
            record.TokenHash,
            record.State,
            record.Version,
            record.CreatedAt);

    private static string Name(SimulatorEntryKind kind) => JsonNamingPolicy.SnakeCaseLower.ConvertName(kind.ToString());
}
