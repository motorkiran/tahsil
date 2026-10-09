using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Domain.Customers;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    /// <summary>
    /// Email search ignores case through an expression index, which EF Core cannot declare; the migration creates it with
    /// this statement.
    /// </summary>
    public const string LowerEmailIndex = "CREATE INDEX ix_customers_lower_email ON tahsil.customers (lower(email) text_pattern_ops)";

    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.Property(customer => customer.Id).ValueGeneratedNever();
        builder.Property(customer => customer.ExternalId).HasMaxLength(Customer.ExternalIdMaxLength);
        builder.Property(customer => customer.Email).HasMaxLength(Customer.EmailMaxLength);
        builder.Property(customer => customer.Name).HasMaxLength(Customer.NameMaxLength);
        builder.Property(customer => customer.Phone).HasMaxLength(Customer.PhoneMaxLength);
        builder.Property(customer => customer.EncryptedTaxId).HasColumnName("tax_id");
        builder.Property(customer => customer.TaxOffice).HasMaxLength(Customer.TaxOfficeMaxLength);
        builder.Property(customer => customer.Locale).HasMaxLength(8);
        builder.Property(customer => customer.KimlikSubscriberType).HasMaxLength(16);
        builder.Property(customer => customer.Address)
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<CustomerAddress?>(), JsonValueConverter<CustomerAddress?>.Comparer);
        builder.Property(customer => customer.Metadata).HasMetadataColumn();
        builder.Ignore(customer => customer.KimlikSubscriber);

        builder.HasIndex(customer => customer.ExternalId).IsUnique().HasFilter("external_id IS NOT NULL");
        builder.HasIndex(customer => new { customer.KimlikSubscriberType, customer.KimlikSubscriberId }).IsUnique().HasFilter("kimlik_subscriber_id IS NOT NULL");
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_customers_kimlik_subscriber", "(kimlik_subscriber_type IS NULL) = (kimlik_subscriber_id IS NULL)"));
    }
}
