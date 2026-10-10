using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Domain.Webhooks;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> builder)
    {
        builder.Property(endpoint => endpoint.Id).ValueGeneratedNever();
        builder.Property(endpoint => endpoint.Key).HasMaxLength(Domain.Catalog.CatalogKey.MaxLength);
        builder.HasIndex(endpoint => endpoint.Key).IsUnique();
        builder.Property(endpoint => endpoint.Url).HasMaxLength(WebhookEndpoint.UrlMaxLength);
        builder.Property(endpoint => endpoint.Description).HasMaxLength(WebhookEndpoint.DescriptionMaxLength);
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.Property(delivery => delivery.Id).ValueGeneratedNever();
        builder.Property(delivery => delivery.EventType).HasMaxLength(64);
        builder.Property(delivery => delivery.Payload).HasColumnType("jsonb");
        builder.Property(delivery => delivery.Status).HasMaxLength(16);
        builder.Property(delivery => delivery.ResponseBody).HasMaxLength(WebhookDelivery.ResponseBodyMaxLength);
        builder.Property(delivery => delivery.Error).HasMaxLength(WebhookDelivery.ErrorMaxLength);
        builder.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(delivery => delivery.EndpointId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(delivery => new { delivery.EventId, delivery.EndpointId }).IsUnique();
        builder.HasIndex(delivery => delivery.NextAttemptAt).HasFilter("status = 'pending'");
        builder.HasIndex(delivery => new { delivery.EndpointId, delivery.Id });
    }
}
