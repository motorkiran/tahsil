using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Subscriptions;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.Property(subscription => subscription.Id).ValueGeneratedNever();
        builder.Property(subscription => subscription.Status).HasMaxLength(24);
        builder.Property(subscription => subscription.Currency).HasMaxLength(3);
        builder.Property(subscription => subscription.Interval).HasMaxLength(8);
        builder.Property(subscription => subscription.IntervalCount).HasColumnType("smallint");
        builder.Property(subscription => subscription.CollectionMethod).HasMaxLength(24);
        builder.Property(subscription => subscription.DaysUntilDue).HasColumnType("smallint");
        builder.Property(subscription => subscription.TrialEndBehavior).HasMaxLength(24);
        builder.Property(subscription => subscription.CancellationReason).HasMaxLength(32);
        builder.Property(subscription => subscription.CancellationFeedback).HasMaxLength(32);
        builder.Property(subscription => subscription.CancellationComment).HasMaxLength(Subscription.CommentMaxLength);
        builder.Property(subscription => subscription.ScheduledChange)
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<ScheduledChange?>(), JsonValueConverter<ScheduledChange?>.Comparer);
        builder.Property(subscription => subscription.Metadata).HasMetadataColumn();
        builder.Ignore(subscription => subscription.TrialEnded);
        builder.Ignore(subscription => subscription.IsTerminal);

        builder.HasMany(subscription => subscription.Items).WithOne().HasForeignKey(item => item.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(subscription => subscription.Items).HasField("_items").UsePropertyAccessMode(PropertyAccessMode.Field);
        // latest_invoice_id has no foreign key: invoices reference their subscription, and a cycle would keep EF Core from
        // inserting a subscription and its first invoice together.
        builder.HasOne<Customer>().WithMany().HasForeignKey(subscription => subscription.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PaymentMethod>().WithMany().HasForeignKey(subscription => subscription.DefaultPaymentMethodId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Checkout.CheckoutSession>().WithMany().HasForeignKey(subscription => subscription.CheckoutSessionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(subscription => subscription.NextCycleAt).HasFilter("next_cycle_at IS NOT NULL");
        builder.HasIndex(subscription => new { subscription.CustomerId, subscription.Id });
        builder.HasIndex(subscription => subscription.CheckoutSessionId).IsUnique().HasFilter("checkout_session_id IS NOT NULL");
    }
}

internal sealed class SubscriptionItemConfiguration : IEntityTypeConfiguration<SubscriptionItem>
{
    public void Configure(EntityTypeBuilder<SubscriptionItem> builder)
    {
        builder.ToTable("subscription_items");
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.HasOne<Price>().WithMany().HasForeignKey(item => item.PriceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(item => new { item.SubscriptionId, item.PriceId }).IsUnique();
        builder.HasIndex(item => item.PriceId);
    }
}

internal sealed class KimlikMirrorConfiguration : IEntityTypeConfiguration<KimlikMirror>
{
    public void Configure(EntityTypeBuilder<KimlikMirror> builder)
    {
        builder.HasKey(mirror => mirror.SubscriptionId);
        builder.Property(mirror => mirror.Error).HasMaxLength(KimlikMirror.ErrorMaxLength);
        builder.HasOne<Subscription>().WithOne().HasForeignKey<KimlikMirror>(mirror => mirror.SubscriptionId).OnDelete(DeleteBehavior.Cascade);

        // Kimlik's webhooks name its subscriptions (KML-21).
        builder.HasIndex(mirror => mirror.KimlikSubscriptionId).IsUnique().HasFilter("kimlik_subscription_id IS NOT NULL");
    }
}
