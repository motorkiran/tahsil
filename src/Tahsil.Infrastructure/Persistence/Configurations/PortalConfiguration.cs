using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Domain.Checkout;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Portal;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class PortalSessionConfiguration : IEntityTypeConfiguration<PortalSession>
{
    public void Configure(EntityTypeBuilder<PortalSession> builder)
    {
        builder.Property(session => session.Id).ValueGeneratedNever();
        builder.Property(session => session.ReturnUrl).HasMaxLength(CheckoutSession.UrlMaxLength);
        builder.Property(session => session.Locale).HasMaxLength(PortalSession.LocaleMaxLength);
        builder.Property(session => session.Flow).HasMaxLength(32);
        builder.HasIndex(session => session.TokenHash).IsUnique();
        builder.HasIndex(session => session.ExpiresAt);

        // Sessions are passes, not records: they go with the customer and with the subscription a flow names.
        builder.HasOne<Customer>().WithMany().HasForeignKey(session => session.CustomerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Subscription>().WithMany().HasForeignKey(session => session.FlowSubscriptionId).OnDelete(DeleteBehavior.Cascade);
    }
}
