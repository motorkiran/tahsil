using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Outbox;

/// <summary>Deletes delivered outbox messages once they are older than the retention period, every hour.</summary>
internal sealed class OutboxCleanup(
    IServiceScopeFactory scopes,
    BillingClock clock,
    IOptions<WorkerOptions> workerOptions,
    IOptions<OutboxOptions> outboxOptions,
    ILogger<OutboxCleanup> logger) : PeriodicJob(scopes, clock, workerOptions, logger)
{
    public override string Name => "outbox-cleanup";

    protected override long LockKey => AdvisoryLockKeys.OutboxCleanup;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var cutoff = Clock.GetUtcNow() - outboxOptions.Value.RetentionPeriod;
        await services.GetRequiredService<TahsilDbContext>().OutboxMessages
            .Where(message => message.Status == OutboxMessageStatus.Processed && message.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
