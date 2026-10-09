using Microsoft.EntityFrameworkCore;

namespace Tahsil.Infrastructure.Persistence;

/// <summary>
/// Brings the database up to date. EF Core holds a database lock while migrating, and an advisory lock covers the
/// steps that follow, so instances that start at the same time take turns. Any failure stops startup: it means the
/// configuration is wrong.
/// </summary>
internal sealed class DatabasePreparation(TahsilDbContext context)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await context.Database.MigrateAsync(cancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKeys.DatabasePreparation})", cancellationToken);

        // Later milestones add the provisioning file and the Test mode bootstrap here.

        await transaction.CommitAsync(cancellationToken);
    }
}
