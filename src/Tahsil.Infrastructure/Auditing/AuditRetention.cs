using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Auditing;

/// <summary>Deletes audit events older than the retention period, every hour, in batches.</summary>
internal sealed partial class AuditRetention(
    IServiceScopeFactory scopes,
    BillingClock clock,
    IOptions<WorkerOptions> workerOptions,
    IOptions<AuditOptions> auditOptions,
    ILogger<AuditRetention> logger) : PeriodicJob(scopes, clock, workerOptions, logger)
{
    public const int BatchSize = 1000;

    public override string Name => "audit-retention";

    protected override long LockKey => AdvisoryLockKeys.AuditRetention;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var database = services.GetRequiredService<TahsilDbContext>().Database;
        var cutoff = Clock.GetUtcNow() - auditOptions.Value.RetentionPeriod;
        int deleted;
        do
        {
            deleted = await database.ExecuteSqlAsync($"""
                DELETE FROM tahsil.audit_events
                WHERE id IN (SELECT id FROM tahsil.audit_events WHERE occurred_at < {cutoff} LIMIT {BatchSize})
                """, cancellationToken);
            if (deleted > 0)
            {
                LogDeleted(logger, deleted);
            }
        }
        while (deleted == BatchSize);
    }

    [LoggerMessage(LogLevel.Information, "Deleted {Count} audit events past the retention period")]
    private static partial void LogDeleted(ILogger logger, int count);
}
