using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Payments;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class ProviderConnectionConfiguration : IEntityTypeConfiguration<ProviderConnection>
{
    public void Configure(EntityTypeBuilder<ProviderConnection> builder)
    {
        builder.Property(connection => connection.Id).ValueGeneratedNever();
        builder.Property(connection => connection.Key).HasMaxLength(CatalogKey.MaxLength);
        builder.Property(connection => connection.Provider).HasMaxLength(ProviderConnection.ProviderMaxLength);
        builder.Property(connection => connection.Name).HasMaxLength(ProviderConnection.NameMaxLength);
        builder.Property(connection => connection.Environment).HasMaxLength(16);
        builder.Property(connection => connection.Settings).HasMetadataColumn();
        builder.Property(connection => connection.LastTestStatus).HasMaxLength(16);
        builder.Property(connection => connection.LastTestMessage).HasMaxLength(ProviderConnection.TestMessageMaxLength);
        builder.HasIndex(connection => connection.Key).IsUnique();
    }
}

internal sealed class ConnectionCurrencyConfiguration : IEntityTypeConfiguration<ConnectionCurrency>
{
    public void Configure(EntityTypeBuilder<ConnectionCurrency> builder)
    {
        builder.HasKey(route => route.Currency);
        builder.Property(route => route.Currency).HasMaxLength(3);
        builder.HasOne<ProviderConnection>().WithMany().HasForeignKey(route => route.ConnectionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProviderCustomerConfiguration : IEntityTypeConfiguration<ProviderCustomer>
{
    public void Configure(EntityTypeBuilder<ProviderCustomer> builder)
    {
        builder.HasKey(customer => new { customer.CustomerId, customer.ConnectionId });
        builder.Property(customer => customer.Reference).HasMaxLength(ProviderCustomer.ReferenceMaxLength);
        builder.Property(customer => customer.Data).HasMetadataColumn();
        builder.HasIndex(customer => new { customer.ConnectionId, customer.Reference }).IsUnique();
        builder.HasOne<Customer>().WithMany().HasForeignKey(customer => customer.CustomerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ProviderConnection>().WithMany().HasForeignKey(customer => customer.ConnectionId).OnDelete(DeleteBehavior.Cascade);
    }
}
