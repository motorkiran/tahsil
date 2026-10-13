using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tahsil.Application.Common;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Payments;

namespace Tahsil.Application.Connections;

/// <summary>What database preparation does for connections: the mode rule (DOM-8) and the Test mode simulator (PAY-2).</summary>
public sealed partial class ConnectionStartup(
    ITahsilDbContext context,
    ProviderRegistry registry,
    ConnectionSecrets secrets,
    IAuditLog audit,
    BillingClock clock,
    ILogger<ConnectionStartup> logger)
{
    public const string SimulatorKey = "simulator";

    private static readonly string[] SimulatorCurrencies = ["TRY", "USD", "EUR"];

    /// <summary>Stops startup when connections do not fit the installation's mode, such as sandbox connections in Live mode.</summary>
    /// <exception cref="InvalidOperationException">Connections break the rule; the message names them.</exception>
    public async Task CheckModeAsync(CancellationToken cancellationToken)
    {
        var connections = await context.ProviderConnections.AsNoTracking()
            .Select(connection => new { connection.Key, connection.Provider, connection.Environment })
            .ToListAsync(cancellationToken);
        var broken = connections.Where(connection => !ProviderRegistry.AllowsInMode(registry.Mode, connection.Provider, connection.Environment))
            .Select(connection => connection.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (broken.Count > 0)
        {
            throw new InvalidOperationException(
                $"The provider connections {string.Join(", ", broken)} are not allowed in {registry.Mode} mode. Test mode takes sandbox connections "
                + "and the simulator; Live mode takes production connections only. Delete or replace them, or change Tahsil:Server:Mode.");
        }
    }

    /// <summary>In Live mode, names every connection whose card fields are on Tahsil's pages, since it widens the merchant's PCI scope (ADP-13).</summary>
    public async Task WarnOfDirectFormsAsync(CancellationToken cancellationToken)
    {
        if (registry.Mode != TahsilMode.Live)
        {
            return;
        }

        var connections = await context.ProviderConnections.AsNoTracking().ToListAsync(cancellationToken);
        foreach (var connection in connections.Where(connection => connection.Settings.GetValueOrDefault(ConnectionFields.AllowDirectForm.Key) == "true"))
        {
            LogDirectForm(logger, connection.Key);
        }
    }

    /// <summary>
    /// Gives a Test mode installation without connections a simulator connection serving TRY, USD and EUR. An
    /// installation that has connections keeps what the operator chose.
    /// </summary>
    public async Task EnsureSimulatorAsync(CancellationToken cancellationToken)
    {
        if (registry.Mode != TahsilMode.Test
            || registry.Find(ProviderRegistry.Simulator) is not { } simulator
            || await context.ProviderConnections.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var settings = ConnectionFields.Settings(simulator.Descriptor, new Dictionary<string, string>(), null);
        var credentials = ConnectionFields.Credentials(simulator.Descriptor, new Dictionary<string, string>(), null);
        var connection = ProviderConnection.Create(
            SimulatorKey, ProviderRegistry.Simulator, "Simulator", ConnectionEnvironment.Sandbox, secrets.Encrypt(credentials.Value), settings.Value, isEnabled: true, now)
            .Value;
        context.ProviderConnections.Add(connection);

        var routed = await context.ConnectionCurrencies.Select(route => route.Currency).ToListAsync(cancellationToken);
        string[] currencies = [.. SimulatorCurrencies.Where(currency => simulator.Descriptor.Currencies.Contains(currency) && !routed.Contains(currency))];
        foreach (var currency in currencies)
        {
            context.ConnectionCurrencies.Add(ConnectionCurrency.Create(currency, connection.Id, now));
        }

        audit.Record(AuditActions.ConnectionCreated, AuditSubject.Of(AuditSubjects.Connection, connection.Id), new Dictionary<string, object?>
        {
            ["key"] = connection.Key,
            ["provider"] = connection.Provider,
            ["environment"] = "sandbox",
            ["enabled"] = true,
            ["currencies"] = currencies,
        });
        await context.SaveChangesAsync(cancellationToken);
        LogSimulatorCreated(logger, string.Join(", ", currencies));
    }

    [LoggerMessage(LogLevel.Warning, "The provider connection {Connection} shows card fields on Tahsil's pages (allowDirectForm): the merchant is in PCI DSS SAQ A-EP scope")]
    private static partial void LogDirectForm(ILogger logger, string connection);

    [LoggerMessage(LogLevel.Information, "Created the simulator connection for Test mode, serving {Currencies}")]
    private static partial void LogSimulatorCreated(ILogger logger, string currencies);
}
