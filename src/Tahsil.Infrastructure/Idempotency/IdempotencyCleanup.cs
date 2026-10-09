using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Idempotency;

/// <summary>Deletes idempotency records past their 24 hours, every hour.</summary>
internal sealed class IdempotencyCleanup(
    IServiceScopeFactory scopes,
    BillingClock clock,
    TimeProvider time,
    IOptions<WorkerOptions> options,
    ILogger<IdempotencyCleanup> logger) : PeriodicJob(scopes, clock, options, logger)
{
    public override string Name => "idempotency-cleanup";

    protected override long LockKey => AdvisoryLockKeys.IdempotencyCleanup;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        // Records expire in real time, like the requests they answer.
        var now = time.GetUtcNow();
        await services.GetRequiredService<TahsilDbContext>().IdempotencyRecords
            .Where(record => record.ExpiresAt < now)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
