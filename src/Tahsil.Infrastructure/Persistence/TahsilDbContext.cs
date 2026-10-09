using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Tahsil.Application.Common;
using Tahsil.Domain.ApiKeys;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Clock;
using Tahsil.Infrastructure.Outbox;
using Tahsil.Infrastructure.Persistence.Conventions;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Persistence;

public sealed class TahsilDbContext(DbContextOptions<TahsilDbContext> options)
    : DbContext(options), ITahsilDbContext, IDataProtectionKeyContext
{
    public const string Schema = "tahsil";

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    IQueryable<AuditEvent> ITahsilDbContext.AuditEvents => AuditEvents;

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<TestClock> TestClocks => Set<TestClock>();

    public DbSet<WorkerFailure> WorkerFailures => Set<WorkerFailure>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>
    /// Starts a transaction, or joins the one in progress: a use case that runs as part of a larger one, such as
    /// applying a provisioning file, then commits or rolls back with it.
    /// </summary>
    public async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        Database.CurrentTransaction is { } current ? new JoinedTransaction(current) : await Database.BeginTransactionAsync(cancellationToken);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        PersistenceConventions.Configure(configurationBuilder);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TahsilDbContext).Assembly);
        PersistenceConventions.Apply(modelBuilder);
    }
}
