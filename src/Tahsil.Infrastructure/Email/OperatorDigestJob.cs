using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Notifications;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Email;

/// <summary>Looks every hour whether the day's digest of open attention items is due (NTF-7).</summary>
internal sealed class OperatorDigestJob(IServiceScopeFactory scopes, BillingClock clock, IOptions<WorkerOptions> options, ILogger<OperatorDigestJob> logger)
    : PeriodicJob(scopes, clock, options, logger)
{
    public override string Name => "operator-digest";

    protected override long LockKey => AdvisoryLockKeys.OperatorDigest;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<OperatorDigest>().QueueIfDueAsync(cancellationToken);
}
