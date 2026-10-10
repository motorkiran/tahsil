using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Payments;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Simulator;

namespace Tahsil.Server.Simulator;

/// <summary>
/// Delivers the simulator's signed notifications to Tahsil's notification handling, in the process, as the connection's
/// settings say: at once or after a delay, and optionally twice (SIM-9). Provider-side time is real time.
/// </summary>
internal sealed partial class SimulatorNotifier(IServiceProvider services, IServiceScopeFactory scopes, ILogger<SimulatorNotifier> logger)
    : BackgroundService, ISimulatorNotifier
{
    private readonly Channel<(Guid ConnectionId, string Type, string Reference)> _queue = Channel.CreateUnbounded<(Guid, string, string)>();

    public void Notify(Guid connectionId, string type, string reference) => _queue.Writer.TryWrite((connectionId, type, reference));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (connectionId, type, reference) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            // Each notification waits for its own delay without holding up the others.
            _ = SendAsync(connectionId, type, reference, stoppingToken);
        }
    }

    private async Task SendAsync(Guid connectionId, string type, string reference, CancellationToken cancellationToken)
    {
        try
        {
            Dictionary<string, string> settings;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var connection = await scope.ServiceProvider.GetRequiredService<ITahsilDbContext>().ProviderConnections.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.Id == connectionId, cancellationToken);
                if (connection is null)
                {
                    return;
                }

                settings = new Dictionary<string, string>(connection.Settings);
            }

            if (settings.GetValueOrDefault("notifications") == "false")
            {
                return;
            }

            if (decimal.TryParse(settings.GetValueOrDefault("notificationDelaySeconds"), NumberStyles.Number, CultureInfo.InvariantCulture, out var delay) && delay > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds((double)delay), TimeProvider.System, cancellationToken);
            }

            var time = TimeProvider.System;
            var body = JsonSerializer.SerializeToUtf8Bytes(new { id = $"evt_{Guid.CreateVersion7(time.GetUtcNow()):N}", type, reference });
            var signature = await services.GetRequiredService<SimulatorBank>().SignAsync(connectionId, time.GetUtcNow().ToUnixTimeSeconds(), body, cancellationToken);
            var deliveries = settings.GetValueOrDefault("notificationDuplicate") == "true" ? 2 : 1;
            for (var delivery = 0; delivery < deliveries; delivery++)
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ProviderNotifications>().ReceiveAsync(connectionId, new IncomingNotification
                {
                    Method = HttpMethods.Post,
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [SimulatorBank.SignatureHeader] = signature,
                        ["Content-Type"] = "application/json",
                    },
                    Body = body,
                }, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSendFailed(logger, exception, type, reference);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the payment or refund is reconciled instead.
        }
    }

    [LoggerMessage(LogLevel.Warning, "The simulator could not deliver its {Type} notification for {Reference}")]
    private static partial void LogSendFailed(ILogger logger, Exception exception, string type, string reference);
}
