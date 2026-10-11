using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Domain.Checkout;
using Tahsil.Domain.Customers;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class NotificationLogConfiguration : IEntityTypeConfiguration<Domain.Notifications.NotificationLog>
{
    public void Configure(EntityTypeBuilder<Domain.Notifications.NotificationLog> builder)
    {
        builder.ToTable("notification_log");
        builder.Property(log => log.Id).ValueGeneratedNever();
        builder.Property(log => log.Type).HasMaxLength(64);
        builder.Property(log => log.OccurrenceKey).HasMaxLength(Domain.Notifications.NotificationLog.OccurrenceKeyMaxLength);
        builder.Property(log => log.Status).HasMaxLength(16);
        builder.Property(log => log.Reason).HasMaxLength(64);
        builder.HasIndex(log => log.OccurrenceKey).IsUnique();
        builder.HasIndex(log => new { log.CustomerId, log.Id });
    }
}

internal sealed class LegalDocumentVersionConfiguration : IEntityTypeConfiguration<LegalDocumentVersion>
{
    public void Configure(EntityTypeBuilder<LegalDocumentVersion> builder)
    {
        builder.HasKey(version => new { version.Key, version.Version, version.Locale });
        builder.Property(version => version.Key).HasMaxLength(64);
        builder.Property(version => version.Version).HasMaxLength(32);
        builder.Property(version => version.Locale).HasMaxLength(8);
    }
}

internal sealed class CheckoutSessionConfiguration : IEntityTypeConfiguration<CheckoutSession>
{
    public void Configure(EntityTypeBuilder<CheckoutSession> builder)
    {
        builder.Property(session => session.Id).ValueGeneratedNever();
        builder.Property(session => session.Mode).HasMaxLength(16);
        builder.Property(session => session.Status).HasMaxLength(16);
        builder.Property(session => session.Currency).HasMaxLength(3);
        builder.Property(session => session.CustomerDetails)
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<CheckoutCustomer?>(), JsonValueConverter<CheckoutCustomer?>.Comparer);
        builder.Property(session => session.Items)
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<List<CheckoutItem>>(), JsonValueConverter<List<CheckoutItem>>.Comparer);
        builder.Property(session => session.Quote)
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<CheckoutQuote>(), JsonValueConverter<CheckoutQuote>.Comparer);
        builder.Property(session => session.Documents)
            .HasColumnName("consents")
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<List<CheckoutDocument>>(), JsonValueConverter<List<CheckoutDocument>>.Comparer);
        builder.Property(session => session.BillingDetailsCollection).HasMaxLength(16);
        builder.Property(session => session.Locale).HasMaxLength(8);
        builder.Property(session => session.SuccessUrl).HasMaxLength(CheckoutSession.UrlMaxLength);
        builder.Property(session => session.CancelUrl).HasMaxLength(CheckoutSession.UrlMaxLength);
        builder.Property(session => session.ClientReferenceId).HasMaxLength(CheckoutSession.ClientReferenceIdMaxLength);
        builder.Property(session => session.Metadata).HasMetadataColumn();
        builder.Property(session => session.PaymentAttempts).HasColumnType("smallint");

        builder.HasOne<Customer>().WithMany().HasForeignKey(session => session.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(session => session.TokenHash).IsUnique();
        builder.HasIndex(session => session.ExpiresAt).HasFilter("status = 'open'");
        builder.HasIndex(session => new { session.CustomerId, session.Id });
        builder.HasIndex(session => session.ClientReferenceId).HasFilter("client_reference_id IS NOT NULL");
    }
}
