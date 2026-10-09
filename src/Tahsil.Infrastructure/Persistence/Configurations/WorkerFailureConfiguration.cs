using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class WorkerFailureConfiguration : IEntityTypeConfiguration<WorkerFailure>
{
    public void Configure(EntityTypeBuilder<WorkerFailure> builder)
    {
        builder.HasKey(failure => new { failure.Worker, failure.ItemId });
        builder.Property(failure => failure.Worker).HasMaxLength(WorkerFailure.WorkerMaxLength);

        builder.HasIndex(failure => failure.AttentionRaisedAt).HasFilter("attention_raised_at IS NOT NULL");
    }
}
