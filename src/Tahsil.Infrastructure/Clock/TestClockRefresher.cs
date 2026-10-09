using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Clock;

/// <summary>Re-reads the test clock offset every second in Test mode; does nothing in Live mode.</summary>
internal sealed partial class TestClockRefresher(
    IServiceScopeFactory scopes,
    TestClockOffset offset,
    IOptions<ServerOptions> server,
    TimeProvider system,
    ILogger<TestClockRefresher> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!server.Value.IsTestMode)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, system);
        var failing = false;
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await offset.RefreshAsync(scope.ServiceProvider.GetRequiredService<TahsilDbContext>(), stoppingToken);
                failing = false;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // Once per outage, not every second.
                if (!failing)
                {
                    LogRefreshFailed(logger, exception);
                }

                failing = true;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(LogLevel.Warning, "Could not read the test clock; the billing clock keeps its last offset")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);
}
