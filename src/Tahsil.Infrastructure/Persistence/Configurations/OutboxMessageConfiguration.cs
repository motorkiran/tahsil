using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Infrastructure.Outbox;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.Property(message => message.Id).ValueGeneratedNever();
        builder.Property(message => message.Type).HasMaxLength(OutboxMessage.TypeMaxLength);
        builder.Property(message => message.Payload).HasColumnType("jsonb");

        // The dispatcher only ever looks for pending messages that are due.
        builder.HasIndex(message => message.NextAttemptAt)
            .HasFilter($"status = '{DbEnum.Value(OutboxMessageStatus.Pending)}'");
    }
}
