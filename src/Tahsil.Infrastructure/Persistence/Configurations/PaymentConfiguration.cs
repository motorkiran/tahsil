using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Payments;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable(table =>
        {
            // DOM-16: a payment collects an invoice or a checkout.
            table.HasCheckConstraint("ck_payments_subject", "invoice_id IS NOT NULL OR checkout_session_id IS NOT NULL");
            table.HasCheckConstraint("ck_payments_amount", "amount > 0 OR (purpose = 'setup' AND amount = 0)");
        });
        builder.Property(payment => payment.Id).ValueGeneratedNever();
        builder.Property(payment => payment.Reference).HasMaxLength(64);
        builder.Property(payment => payment.Currency).HasMaxLength(3);
        builder.Property(payment => payment.Purpose).HasMaxLength(16);
        builder.Property(payment => payment.Status).HasMaxLength(24);
        builder.Property(payment => payment.Initiator).HasMaxLength(16);
        builder.Property(payment => payment.Method).HasMaxLength(16);
        builder.Property(payment => payment.ProviderPaymentId).HasMaxLength(200);
        builder.Property(payment => payment.ProviderData).HasMetadataColumn();
        builder.Property(payment => payment.FailureCode).HasMaxLength(32);
        builder.Property(payment => payment.FailureCategory).HasMaxLength(32);
        builder.Property(payment => payment.FailureMessage).HasMaxLength(Payment.FailureMessageMaxLength);
        builder.Property(payment => payment.ProviderFailureCode).HasMaxLength(100);
        builder.Property(payment => payment.ProviderFailureMessage).HasMaxLength(Payment.FailureMessageMaxLength);
        builder.Property(payment => payment.ReconcileAttempts).HasColumnType("smallint");
        builder.Property(payment => payment.Actor)
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<Actor>(), JsonValueConverter<Actor>.Comparer);
        builder.Ignore(payment => payment.IsInFlight);

        builder.HasOne<Customer>().WithMany().HasForeignKey(payment => payment.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Invoice>().WithMany().HasForeignKey(payment => payment.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProviderConnection>().WithMany().HasForeignKey(payment => payment.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PaymentMethod>().WithMany().HasForeignKey(payment => payment.PaymentMethodId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ConsentRecord>().WithMany().HasForeignKey(payment => payment.ConsentRecordId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Checkout.CheckoutSession>().WithMany().HasForeignKey(payment => payment.CheckoutSessionId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(payment => payment.ClientNetwork).HasMaxLength(64);

        // Attempts per network in the last hour (CHK-21).
        builder.HasIndex(payment => new { payment.ClientNetwork, payment.CreatedAt }).HasFilter("client_network IS NOT NULL");

        builder.HasIndex(payment => payment.Reference).IsUnique();
        builder.HasIndex(payment => new { payment.ConnectionId, payment.ProviderPaymentId }).IsUnique().HasFilter("provider_payment_id IS NOT NULL");

        // DOM-17: one payment in flight per invoice.
        builder.HasIndex(payment => payment.InvoiceId).IsUnique().HasFilter("status IN ('processing', 'requires_action')").HasDatabaseName("ix_payments_invoice_in_flight");
        builder.HasIndex(payment => payment.ReconcileAt).HasFilter("status IN ('processing', 'requires_action')");
        builder.HasIndex(payment => new { payment.CustomerId, payment.Id });
    }
}

internal sealed class PaymentMethodConfiguration : IEntityTypeConfiguration<PaymentMethod>
{
    public void Configure(EntityTypeBuilder<PaymentMethod> builder)
    {
        builder.Property(method => method.Id).ValueGeneratedNever();
        builder.Property(method => method.Status).HasMaxLength(16);
        builder.Property(method => method.Brand).HasMaxLength(16);
        builder.Property(method => method.Funding).HasMaxLength(16);
        builder.Property(method => method.Bin).HasMaxLength(8);
        builder.Property(method => method.Last4).HasMaxLength(4);
        builder.Property(method => method.ExpMonth).HasColumnType("smallint");
        builder.Property(method => method.ExpYear).HasColumnType("smallint");
        builder.Property(method => method.Issuer).HasMaxLength(100);
        builder.Property(method => method.CardFamily).HasMaxLength(50);
        builder.Property(method => method.Country).HasMaxLength(2);
        builder.Ignore(method => method.IsChargeable);

        builder.HasOne<Customer>().WithMany().HasForeignKey(method => method.CustomerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ProviderConnection>().WithMany().HasForeignKey(method => method.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ConsentRecord>().WithMany().HasForeignKey(method => method.ConsentRecordId).OnDelete(DeleteBehavior.Restrict);

        // CUS-12: a token is stored once per connection.
        builder.HasIndex(method => new { method.ConnectionId, method.TokenFingerprint }).IsUnique();
        builder.HasIndex(method => new { method.CustomerId, method.Status });
        builder.HasIndex(method => method.ExpiresAt).HasFilter("status = 'active'");
    }
}

internal sealed class ConsentRecordConfiguration : IEntityTypeConfiguration<ConsentRecord>
{
    public void Configure(EntityTypeBuilder<ConsentRecord> builder)
    {
        builder.Property(record => record.Id).ValueGeneratedNever();
        builder.Property(record => record.Type).HasMaxLength(32);
        builder.Property(record => record.DocumentKey).HasMaxLength(64);
        builder.Property(record => record.DocumentVersion).HasMaxLength(32);
        builder.Property(record => record.TextShown).HasMaxLength(ConsentRecord.TextMaxLength);
        builder.Property(record => record.Context).HasMetadataColumn();
        builder.Property(record => record.UserAgent).HasMaxLength(ConsentRecord.UserAgentMaxLength);

        // Legal evidence outlives the customer's anonymization (DOM-20), so the customer cannot be deleted under it.
        builder.HasOne<Customer>().WithMany().HasForeignKey(record => record.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Checkout.CheckoutSession>().WithMany().HasForeignKey(record => record.CheckoutSessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(record => new { record.CustomerId, record.Id });
    }
}

internal sealed class AttentionItemConfiguration : IEntityTypeConfiguration<AttentionItem>
{
    public void Configure(EntityTypeBuilder<AttentionItem> builder)
    {
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.Kind).HasMaxLength(32);
        builder.Property(item => item.SubjectType).HasMaxLength(32);
        builder.Property(item => item.Details).HasMetadataColumn();
        builder.Property(item => item.Resolution).HasMaxLength(AttentionItem.ResolutionMaxLength);
        builder.Property(item => item.ResolvedBy)
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<Actor?>(), JsonValueConverter<Actor?>.Comparer);
        builder.Ignore(item => item.IsOpen);

        builder.HasIndex(item => new { item.Kind, item.SubjectId }).IsUnique().HasFilter("resolved_at IS NULL");
        builder.HasIndex(item => item.RaisedAt).HasFilter("resolved_at IS NULL");
    }
}

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable(table => table.HasCheckConstraint("ck_refunds_amount", "amount > 0"));
        builder.Property(refund => refund.Id).ValueGeneratedNever();
        builder.Property(refund => refund.Reference).HasMaxLength(64);
        builder.Property(refund => refund.Currency).HasMaxLength(3);
        builder.Property(refund => refund.Reason).HasMaxLength(32);
        builder.Property(refund => refund.Kind).HasMaxLength(16);
        builder.Property(refund => refund.Status).HasMaxLength(16);
        builder.Property(refund => refund.ProviderRefundId).HasMaxLength(200);
        builder.Property(refund => refund.FailureCode).HasMaxLength(32);
        builder.Property(refund => refund.FailureMessage).HasMaxLength(500);
        builder.Property(refund => refund.ProviderFailureCode).HasMaxLength(100);
        builder.Property(refund => refund.ReconcileAttempts).HasColumnType("smallint");
        builder.Property(refund => refund.Note).HasMaxLength(Refund.NoteMaxLength);
        builder.Property(refund => refund.Actor)
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<Actor>(), JsonValueConverter<Actor>.Comparer);

        builder.HasOne<Payment>().WithMany().HasForeignKey(refund => refund.PaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(refund => refund.Reference).IsUnique();
        builder.HasIndex(refund => new { refund.PaymentId, refund.Id });
        builder.HasIndex(refund => refund.ReconcileAt).HasFilter("status = 'processing'");
    }
}

internal sealed class ProviderNotificationConfiguration : IEntityTypeConfiguration<ProviderNotification>
{
    public void Configure(EntityTypeBuilder<ProviderNotification> builder)
    {
        builder.Property(notification => notification.Id).ValueGeneratedNever();
        builder.Property(notification => notification.ProviderEventId).HasMaxLength(ProviderNotification.EventIdMaxLength);
        builder.Property(notification => notification.Type).HasMaxLength(ProviderNotification.TypeMaxLength);
        builder.Property(notification => notification.Payload).HasColumnType("jsonb");
        builder.Property(notification => notification.Status).HasMaxLength(16);
        builder.Property(notification => notification.Attempts).HasColumnType("smallint");
        builder.Property(notification => notification.LastError).HasMaxLength(ProviderNotification.LastErrorMaxLength);

        builder.HasOne<ProviderConnection>().WithMany().HasForeignKey(notification => notification.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(notification => new { notification.ConnectionId, notification.ProviderEventId }).IsUnique().HasFilter("provider_event_id IS NOT NULL");
        builder.HasIndex(notification => notification.NextAttemptAt).HasFilter("status = 'pending'");
        builder.HasIndex(notification => notification.ReceivedAt).HasFilter("raw_body IS NOT NULL");
    }
}
