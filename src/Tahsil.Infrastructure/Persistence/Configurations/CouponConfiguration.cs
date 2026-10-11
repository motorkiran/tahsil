using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Coupons;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Subscriptions;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class CouponConfiguration : IEntityTypeConfiguration<Coupon>
{
    public void Configure(EntityTypeBuilder<Coupon> builder)
    {
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("ck_coupons_discount", "(percent_off IS NULL) <> (amount_off IS NULL) AND (amount_off IS NULL) = (currency IS NULL)");
            table.HasCheckConstraint("ck_coupons_percent_off", "percent_off > 0 AND percent_off <= 100");
            table.HasCheckConstraint("ck_coupons_amount_off", "amount_off > 0");
            table.HasCheckConstraint("ck_coupons_redemptions", "times_redeemed >= 0 AND (max_redemptions IS NULL OR times_redeemed <= max_redemptions)");
        });
        builder.Property(coupon => coupon.Id).ValueGeneratedNever();
        builder.Property(coupon => coupon.Key).HasMaxLength(CatalogKey.MaxLength);
        builder.Property(coupon => coupon.Name).HasMaxLength(Coupon.NameMaxLength);
        builder.Property(coupon => coupon.PercentOff).HasPrecision(5, 2);
        builder.Property(coupon => coupon.Currency).HasMaxLength(3);
        builder.Property(coupon => coupon.Duration).HasMaxLength(16);
        builder.Property(coupon => coupon.Metadata).HasMetadataColumn();
        builder.HasIndex(coupon => coupon.Key).IsUnique();
    }
}

internal sealed class DiscountConfiguration : IEntityTypeConfiguration<Discount>
{
    public void Configure(EntityTypeBuilder<Discount> builder)
    {
        builder.ToTable(table => table.HasCheckConstraint("ck_discounts_target", "num_nonnulls(subscription_id, invoice_id, checkout_session_id) = 1"));
        builder.Property(discount => discount.Id).ValueGeneratedNever();
        builder.HasOne<Coupon>().WithMany().HasForeignKey(discount => discount.CouponId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PromotionCode>().WithMany().HasForeignKey(discount => discount.PromotionCodeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Customer>().WithMany().HasForeignKey(discount => discount.CustomerId).OnDelete(DeleteBehavior.Restrict);

        // Subscriptions point back at their discount without a foreign key, so that neither row waits for the other.
        builder.HasOne<Subscription>().WithMany().HasForeignKey(discount => discount.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Invoice>().WithMany().HasForeignKey(discount => discount.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(discount => discount.SubscriptionId);
        builder.HasIndex(discount => discount.InvoiceId);
    }
}

internal sealed class PromotionCodeConfiguration : IEntityTypeConfiguration<PromotionCode>
{
    public void Configure(EntityTypeBuilder<PromotionCode> builder)
    {
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("ck_promotion_codes_code", "code ~ '^[A-Z0-9_-]{3,40}$'");
            table.HasCheckConstraint("ck_promotion_codes_redemptions", "times_redeemed >= 0 AND (max_redemptions IS NULL OR times_redeemed <= max_redemptions)");
        });
        builder.Property(code => code.Id).ValueGeneratedNever();
        builder.Property(code => code.Code).HasMaxLength(PromotionCode.CodeMaxLength);
        builder.Property(code => code.MinimumAmountCurrency).HasMaxLength(3);
        builder.HasIndex(code => code.Code).IsUnique();
        builder.HasIndex(code => code.CouponId);
        builder.HasOne<Coupon>().WithMany().HasForeignKey(code => code.CouponId).OnDelete(DeleteBehavior.Restrict);

        // A code for one customer goes with them when they are deleted.
        builder.HasOne<Customer>().WithMany().HasForeignKey(code => code.CustomerId).OnDelete(DeleteBehavior.Cascade);
    }
}
