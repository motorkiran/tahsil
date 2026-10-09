using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Domain.Auditing;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Auditing;

/// <summary>
/// Deletes audit events older than the retention period, every hour, in batches, and records what it deleted (the
/// destruction log of KVKK). Destruction events themselves are kept at least three years, whatever the retention.
/// Retention runs in real time: Test mode's time travel does not age the trail.
/// </summary>
internal sealed partial class AuditRetention(
    IServiceScopeFactory scopes,
    BillingClock clock,
    TimeProvider time,
    IOptions<WorkerOptions> workerOptions,
    IOptions<AuditOptions> auditOptions,
    ILogger<AuditRetention> logger) : PeriodicJob(scopes, clock, workerOptions, logger)
{
    public const int BatchSize = 1000;

    /// <summary>The least time a destruction event is kept.</summary>
    public static readonly TimeSpan DestructionLogRetention = TimeSpan.FromDays(3 * 365);

    public override string Name => "audit-retention";

    protected override long LockKey => AdvisoryLockKeys.AuditRetention;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<TahsilDbContext>();
        var now = time.GetUtcNow();
        var cutoff = now - auditOptions.Value.RetentionPeriod;
        var destructionCutoff = now - DestructionLogRetention;
        var destroyed = AuditActions.DataDestroyed;

        var total = 0;
        int deleted;
        do
        {
            deleted = await context.Database.ExecuteSqlAsync($"""
                DELETE FROM tahsil.audit_events
                WHERE id IN (
                    SELECT id FROM tahsil.audit_events
                    WHERE occurred_at < {cutoff} AND (action <> {destroyed} OR occurred_at < {destructionCutoff})
                    LIMIT {BatchSize})
                """, cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize);

        if (total > 0)
        {
            services.GetRequiredService<IAuditLog>().Record(
                AuditActions.DataDestroyed,
                data: new Dictionary<string, object?> { ["category"] = "audit_events", ["count"] = total, ["olderThan"] = cutoff });
            await context.SaveChangesAsync(cancellationToken);
            LogDeleted(logger, total);
        }
    }

    [LoggerMessage(LogLevel.Information, "Deleted {Count} audit events past the retention period")]
    private static partial void LogDeleted(ILogger logger, int count);
}
