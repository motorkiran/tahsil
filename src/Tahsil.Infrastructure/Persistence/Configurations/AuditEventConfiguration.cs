using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Domain.Auditing;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.Property(auditEvent => auditEvent.Id).ValueGeneratedNever();
        builder.Property(auditEvent => auditEvent.Action).HasMaxLength(AuditEvent.ActionMaxLength);
        builder.Property(auditEvent => auditEvent.ActorId).HasMaxLength(AuditEvent.ReferenceMaxLength);
        builder.Property(auditEvent => auditEvent.SubjectType).HasMaxLength(AuditEvent.ReferenceMaxLength);
        builder.Property(auditEvent => auditEvent.SubjectId).HasMaxLength(AuditEvent.ReferenceMaxLength);
        builder.Property(auditEvent => auditEvent.UserAgent).HasMaxLength(AuditEvent.UserAgentMaxLength);
        builder.Property(auditEvent => auditEvent.CorrelationId).HasMaxLength(AuditEvent.ReferenceMaxLength);
        builder.Property(auditEvent => auditEvent.Data).HasColumnType("jsonb");

        builder.HasIndex(auditEvent => auditEvent.OccurredAt);
        builder.HasIndex(auditEvent => new { auditEvent.SubjectType, auditEvent.SubjectId });
        builder.HasIndex(auditEvent => auditEvent.ActorId);
        builder.HasIndex(auditEvent => auditEvent.CustomerId);
    }
}
