using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Portal;

/// <summary>
/// Deletes portal sessions a day after they expired, every hour: until then an expired link still leads to a page that
/// says so and sends the customer back (PRT-1).
/// </summary>
internal sealed class PortalSessionCleanup(
    IServiceScopeFactory scopes,
    BillingClock clock,
    IOptions<WorkerOptions> options,
    ILogger<PortalSessionCleanup> logger) : PeriodicJob(scopes, clock, options, logger)
{
    private readonly BillingClock _clock = clock;

    public override string Name => "portal-session-cleanup";

    protected override long LockKey => AdvisoryLockKeys.PortalSessionCleanup;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var expired = _clock.GetUtcNow().AddDays(-1);
        await services.GetRequiredService<TahsilDbContext>().PortalSessions
            .Where(session => session.ExpiresAt < expired)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
