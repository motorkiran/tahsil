using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Domain.Usage;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class UsageEventConfiguration : IEntityTypeConfiguration<UsageEvent>
{
    public void Configure(EntityTypeBuilder<UsageEvent> builder)
    {
        builder.Property(usage => usage.Id).ValueGeneratedNever();
        builder.Property(usage => usage.Identifier).HasMaxLength(UsageEvent.IdentifierMaxLength);
        builder.Property(usage => usage.Value).HasPrecision(28, UsageEvent.ValueScale);
        builder.Property(usage => usage.Properties).HasColumnType("jsonb");

        // No foreign keys: meters and customers are never deleted, and this table takes the installation's highest write volume.
        // Deduplication (USG-2).
        builder.HasIndex(usage => usage.Identifier).IsUnique();

        // Aggregation of a customer's usage on a meter over a period (USG-8).
        builder.HasIndex(usage => new { usage.MeterId, usage.CustomerId, usage.OccurredAt });
    }
}
