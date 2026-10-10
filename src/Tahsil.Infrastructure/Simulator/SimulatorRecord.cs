using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Common;
using Tahsil.Domain.Payments;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Simulator;

/// <summary>
/// A record of the simulated provider (SIM-10): its payments, refunds and saved cards, with the simulator's state as
/// JSON. Only Test mode writes them.
/// </summary>
internal sealed class SimulatorRecord
{
    public const int ReferenceMaxLength = 100;

    public Guid Id { get; init; }

    public Guid ConnectionId { get; init; }

    public string Kind { get; init; } = string.Empty;

    public string Reference { get; init; } = string.Empty;

    public byte[]? TokenHash { get; set; }

    public string State { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; init; }

    public uint Version { get; init; }
}

internal sealed class SimulatorRecordConfiguration : IEntityTypeConfiguration<SimulatorRecord>
{
    public void Configure(EntityTypeBuilder<SimulatorRecord> builder)
    {
        builder.ToTable("simulator_transactions");
        builder.Property(record => record.Id).ValueGeneratedNever();
        builder.Property(record => record.Kind).HasMaxLength(16);
        builder.Property(record => record.Reference).HasMaxLength(SimulatorRecord.ReferenceMaxLength);
        builder.Property(record => record.State).HasColumnType("jsonb");
        builder.Property(record => record.Version).IsRowVersion();
        builder.HasIndex(record => new { record.ConnectionId, record.Kind, record.Reference }).IsUnique();
        builder.HasIndex(record => record.TokenHash).IsUnique().HasFilter("token_hash IS NOT NULL");
        builder.HasOne<ProviderConnection>().WithMany().HasForeignKey(record => record.ConnectionId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Reads and writes simulator records in a scope of their own, so they commit on their own, as a real provider's
/// records do, whatever happens to the caller's transaction.
/// </summary>
internal sealed class SimulatorRecordStore(IServiceScopeFactory scopes)
{
    public async Task<SimulatorRecord?> FindAsync(Guid connectionId, string kind, string reference, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await Records(scope).AsNoTracking()
            .SingleOrDefaultAsync(record => record.ConnectionId == connectionId && record.Kind == kind && record.Reference == reference, cancellationToken);
    }

    public async Task<SimulatorRecord?> FindByTokenAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await Records(scope).AsNoTracking().SingleOrDefaultAsync(record => record.TokenHash == tokenHash, cancellationToken);
    }

    /// <summary>Inserts a new record or updates the stored version; <see langword="false"/> when another write got there first.</summary>
    public async Task<bool> SaveAsync(SimulatorRecord record, bool isNew, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TahsilDbContext>();
        if (isNew)
        {
            context.Add(record);
        }
        else
        {
            var entry = context.Attach(record);
            entry.Property(stored => stored.State).IsModified = true;
            entry.Property(stored => stored.TokenHash).IsModified = true;
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return false;
        }
    }

    private static DbSet<SimulatorRecord> Records(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<TahsilDbContext>().Set<SimulatorRecord>();
}
