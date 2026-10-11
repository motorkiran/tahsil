using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Invoices;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class InvoiceLinkKeyConfiguration : IEntityTypeConfiguration<InvoiceLinkKey>
{
    public void Configure(EntityTypeBuilder<InvoiceLinkKey> builder)
    {
        builder.Property(key => key.Id).ValueGeneratedNever();
        builder.HasIndex(key => key.Version).IsUnique();
    }
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.Property(invoice => invoice.Id).ValueGeneratedNever();
        builder.Property(invoice => invoice.Number).HasMaxLength(Invoice.NumberMaxLength);
        builder.Property(invoice => invoice.Status).HasMaxLength(16);
        builder.Property(invoice => invoice.CollectionMethod).HasMaxLength(24);
        builder.Property(invoice => invoice.Currency).HasMaxLength(3);
        builder.Property(invoice => invoice.DaysUntilDue).HasColumnType("smallint");
        builder.Property(invoice => invoice.AttemptCount).HasColumnType("smallint");
        builder.Property(invoice => invoice.CustomerSnapshot)
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<CustomerSnapshot?>(), JsonValueConverter<CustomerSnapshot?>.Comparer);
        builder.Property(invoice => invoice.Discount)
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<InvoiceDiscount?>(), JsonValueConverter<InvoiceDiscount?>.Comparer);
        builder.Property(invoice => invoice.LegalInvoiceNumber).HasMaxLength(64);
        builder.Property(invoice => invoice.LegalInvoiceUrl).HasMaxLength(2000);
        builder.Property(invoice => invoice.Memo).HasMaxLength(Invoice.MemoMaxLength);
        builder.Property(invoice => invoice.Metadata).HasMetadataColumn();
        builder.Ignore(invoice => invoice.LegalInvoice);
        builder.Ignore(invoice => invoice.IsDraft);

        builder.HasMany(invoice => invoice.Lines).WithOne().HasForeignKey(line => line.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(invoice => invoice.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasOne<Customer>().WithMany().HasForeignKey(invoice => invoice.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Subscriptions.Subscription>().WithMany().HasForeignKey(invoice => invoice.SubscriptionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(invoice => invoice.Number).IsUnique().HasFilter("number IS NOT NULL");
        builder.HasIndex(invoice => new { invoice.CustomerId, invoice.Id });

        // A period is billed once (DOM-13).
        builder.HasIndex(invoice => new { invoice.SubscriptionId, invoice.BillingReason, invoice.PeriodStart })
            .IsUnique()
            .HasFilter("billing_reason IN ('subscription_create', 'subscription_cycle')");
        builder.HasIndex(invoice => invoice.FinalizeAt).HasFilter("status = 'draft' AND auto_advance");
        builder.HasIndex(invoice => invoice.NextPaymentAttemptAt).HasFilter("status = 'open'");
        builder.HasIndex(invoice => invoice.DunningDeadlineAt).HasFilter("status = 'open' AND dunning_ended_at IS NULL");
        builder.HasIndex(invoice => invoice.NextReminderAt).HasFilter("status = 'open'");
    }
}

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        builder.ToTable("invoice_lines");
        builder.Property(line => line.Id).ValueGeneratedNever();
        builder.Property(line => line.Type).HasMaxLength(16);
        builder.Property(line => line.Description).HasMaxLength(InvoiceLine.DescriptionMaxLength);
        builder.Property(line => line.Quantity).HasPrecision(28, 8);
        builder.Property(line => line.UnitAmountDecimal).HasPrecision(28, 12);
        builder.Property(line => line.TaxPercentage).HasPrecision(7, 4);
        builder.Property(line => line.SortOrder).HasColumnType("smallint");
        builder.HasOne<Price>().WithMany().HasForeignKey(line => line.PriceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TaxRate>().WithMany().HasForeignKey(line => line.TaxRateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PendingInvoiceItem>().WithMany().HasForeignKey(line => line.PendingItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PendingInvoiceItemConfiguration : IEntityTypeConfiguration<PendingInvoiceItem>
{
    public void Configure(EntityTypeBuilder<PendingInvoiceItem> builder)
    {
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.Currency).HasMaxLength(3);
        builder.Property(item => item.Description).HasMaxLength(PendingInvoiceItem.DescriptionMaxLength);
        builder.Property(item => item.Quantity).HasPrecision(28, 8);
        builder.Ignore(item => item.IsPending);
        builder.HasOne<Customer>().WithMany().HasForeignKey(item => item.CustomerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Price>().WithMany().HasForeignKey(item => item.PriceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TaxRate>().WithMany().HasForeignKey(item => item.TaxRateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Invoice>().WithMany().HasForeignKey(item => item.InvoiceId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Domain.Subscriptions.Subscription>().WithMany().HasForeignKey(item => item.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(item => new { item.CustomerId, item.Currency }).HasFilter("invoice_id IS NULL");
    }
}

internal sealed class CustomerBalanceConfiguration : IEntityTypeConfiguration<CustomerBalance>
{
    public void Configure(EntityTypeBuilder<CustomerBalance> builder)
    {
        builder.HasKey(balance => new { balance.CustomerId, balance.Currency });
        builder.Property(balance => balance.Currency).HasMaxLength(3);
        builder.HasOne<Customer>().WithMany().HasForeignKey(balance => balance.CustomerId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BalanceTransactionConfiguration : IEntityTypeConfiguration<BalanceTransaction>
{
    public void Configure(EntityTypeBuilder<BalanceTransaction> builder)
    {
        builder.Property(transaction => transaction.Id).ValueGeneratedNever();
        builder.Property(transaction => transaction.Currency).HasMaxLength(3);
        builder.Property(transaction => transaction.Description).HasMaxLength(CustomerBalance.DescriptionMaxLength);
        builder.Property(transaction => transaction.Actor)
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<Actor>(), JsonValueConverter<Actor>.Comparer);
        builder.HasOne<Customer>().WithMany().HasForeignKey(transaction => transaction.CustomerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Invoice>().WithMany().HasForeignKey(transaction => transaction.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(transaction => new { transaction.CustomerId, transaction.Id });
    }
}

/// <summary>The next invoice number of each year (INV-9), in the billing time zone.</summary>
internal sealed class InvoiceNumberSequence
{
    public short Year { get; init; }

    public long NextValue { get; init; }
}

internal sealed class InvoiceNumberSequenceConfiguration : IEntityTypeConfiguration<InvoiceNumberSequence>
{
    public void Configure(EntityTypeBuilder<InvoiceNumberSequence> builder)
    {
        builder.ToTable("invoice_number_sequences");
        builder.HasKey(sequence => sequence.Year);
        builder.Property(sequence => sequence.Year).ValueGeneratedNever();
    }
}
