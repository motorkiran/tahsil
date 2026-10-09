using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Infrastructure.Idempotency;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.Property(record => record.Id).ValueGeneratedNever();
        builder.Property(record => record.Key).HasMaxLength(IdempotencyRecord.KeyMaxLength);
        builder.Property(record => record.Method).HasMaxLength(8);
        builder.Property(record => record.Path).HasMaxLength(512);
        builder.Property(record => record.ResourceType).HasMaxLength(IdempotencyRecord.ResourceTypeMaxLength);
        builder.Property(record => record.ResponseHeaders).HasColumnType("jsonb");

        builder.HasIndex(record => new { record.ApiKeyId, record.Key }).IsUnique();
        builder.HasIndex(record => record.ExpiresAt);
    }
}
