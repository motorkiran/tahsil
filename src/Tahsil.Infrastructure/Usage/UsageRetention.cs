using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Usage;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Usage;

/// <summary>
/// Deletes usage events past the retention period, every hour, in batches (USG-13): by when they happened on the
/// billing clock, and never while a subscription that is not over still has to invoice them. Invoices keep the
/// quantities they billed, so nothing that was invoiced changes.
/// </summary>
internal sealed partial class UsageRetention(
    IServiceScopeFactory scopes,
    BillingClock clock,
    IOptions<WorkerOptions> workerOptions,
    IOptions<UsageOptions> usageOptions,
    ILogger<UsageRetention> logger) : PeriodicJob(scopes, clock, workerOptions, logger)
{
    public const int BatchSize = 5000;

    private readonly BillingClock _clock = clock;

    public override string Name => "usage-retention";

    protected override long LockKey => AdvisoryLockKeys.UsageRetention;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<TahsilDbContext>();
        var cutoff = _clock.GetUtcNow() - usageOptions.Value.RetentionPeriod;

        var total = 0;
        int deleted;
        do
        {
            deleted = await context.Database.ExecuteSqlAsync($"""
                DELETE FROM tahsil.usage_events
                WHERE id IN (
                    SELECT e.id FROM tahsil.usage_events e
                    WHERE e.occurred_at < {cutoff}
                        AND NOT EXISTS (
                            SELECT 1 FROM tahsil.subscription_items i
                            JOIN tahsil.subscriptions s ON s.id = i.subscription_id
                            JOIN tahsil.prices p ON p.id = i.price_id
                            WHERE s.customer_id = e.customer_id AND p.meter_id = e.meter_id
                                AND s.status NOT IN ('canceled', 'incomplete_expired')
                                AND e.occurred_at >= COALESCE(i.usage_billed_until, i.created_at))
                    LIMIT {BatchSize})
                """, cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize);

        if (total > 0)
        {
            LogDeleted(logger, total);
        }
    }

    [LoggerMessage(LogLevel.Information, "Deleted {Count} usage events past the retention period")]
    private static partial void LogDeleted(ILogger logger, int count);
}
