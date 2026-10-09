using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tahsil.Domain.ApiKeys;
using Tahsil.Domain.Common;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Persistence.Configurations;

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.Property(key => key.Id).ValueGeneratedNever();
        builder.Property(key => key.Name).HasMaxLength(ApiKey.NameMaxLength);
        builder.Property(key => key.DisplayPrefix).HasMaxLength(ApiKey.DisplayPrefixLength);
        builder.Property(key => key.Actor)
            .HasColumnType("jsonb")
            .HasConversion(new JsonValueConverter<Actor>(), JsonValueConverter<Actor>.Comparer);

        builder.HasIndex(key => key.SecretHash).IsUnique();
    }
}
