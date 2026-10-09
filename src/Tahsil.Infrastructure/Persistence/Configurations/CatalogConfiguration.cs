using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Domain.Catalog;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class TaxRateConfiguration : IEntityTypeConfiguration<TaxRate>
{
    public void Configure(EntityTypeBuilder<TaxRate> builder)
    {
        builder.ToTable(table => table.HasCheckConstraint("ck_tax_rates_percentage", "percentage >= 0 AND percentage <= 100"));
        builder.Property(rate => rate.Id).ValueGeneratedNever();
        builder.Property(rate => rate.Key).HasMaxLength(CatalogKey.MaxLength);
        builder.Property(rate => rate.Name).HasMaxLength(TaxRate.NameMaxLength);
        builder.Property(rate => rate.Description).HasMaxLength(TaxRate.DescriptionMaxLength);
        builder.Property(rate => rate.Percentage).HasPrecision(7, 4);
        builder.Property(rate => rate.Country).HasMaxLength(2);
        builder.HasIndex(rate => rate.Key).IsUnique();
    }
}

internal sealed class MeterConfiguration : IEntityTypeConfiguration<Meter>
{
    public void Configure(EntityTypeBuilder<Meter> builder)
    {
        builder.Property(meter => meter.Id).ValueGeneratedNever();
        builder.Property(meter => meter.Key).HasMaxLength(CatalogKey.MaxLength);
        builder.Property(meter => meter.Name).HasMaxLength(Meter.NameMaxLength);
        builder.Property(meter => meter.Aggregation).HasMaxLength(16);
        builder.Property(meter => meter.UniqueProperty).HasMaxLength(Meter.UniquePropertyMaxLength);
        builder.HasIndex(meter => meter.Key).IsUnique();
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.Property(product => product.Id).ValueGeneratedNever();
        builder.Property(product => product.Key).HasMaxLength(CatalogKey.MaxLength);
        builder.Property(product => product.Name).HasMaxLength(Product.NameMaxLength);
        builder.Property(product => product.Description).HasMaxLength(Product.DescriptionMaxLength);
        builder.Property(product => product.KimlikPlan).HasMaxLength(Product.KimlikPlanMaxLength);
        builder.Property(product => product.Metadata).HasMetadataColumn();
        builder.HasIndex(product => product.Key).IsUnique();
    }
}

internal sealed class PriceConfiguration : IEntityTypeConfiguration<Price>
{
    public void Configure(EntityTypeBuilder<Price> builder)
    {
        builder.Property(price => price.Id).ValueGeneratedNever();
        builder.Property(price => price.Key).HasMaxLength(CatalogKey.MaxLength);
        builder.Property(price => price.Name).HasMaxLength(Price.NameMaxLength);
        builder.Property(price => price.Currency).HasMaxLength(3).IsFixedLength(false);
        builder.Property(price => price.Type).HasMaxLength(16);
        builder.Property(price => price.Interval).HasMaxLength(8);
        builder.Property(price => price.UsageType).HasMaxLength(16);
        builder.Property(price => price.BillingScheme).HasMaxLength(16);
        builder.Property(price => price.PackageRounding).HasMaxLength(8);
        builder.Property(price => price.TiersMode).HasMaxLength(16);
        builder.Property(price => price.UnitAmountDecimal).HasPrecision(28, Price.UnitAmountDecimalScale);
        builder.Property(price => price.Tiers)
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<IReadOnlyList<PriceTier>?>(), JsonValueConverter<IReadOnlyList<PriceTier>?>.Comparer);
        builder.Property(price => price.Metadata).HasMetadataColumn();

        builder.HasIndex(price => price.Key).IsUnique().HasFilter("key IS NOT NULL");
        builder.HasOne<Product>().WithMany().HasForeignKey(price => price.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Meter>().WithMany().HasForeignKey(price => price.MeterId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TaxRate>().WithMany().HasForeignKey(price => price.TaxRateId).OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(table => table.HasCheckConstraint("ck_prices_currency", "currency ~ '^[A-Z]{3}$'"));
    }
}
