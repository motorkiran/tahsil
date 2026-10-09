using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Tahsil.Infrastructure.Persistence.Conventions;

internal static class MetadataColumn
{
    /// <summary>Metadata as a <c>jsonb</c> object of strings, never null, <c>{}</c> by default.</summary>
    public static PropertyBuilder<IReadOnlyDictionary<string, string>> HasMetadataColumn(this PropertyBuilder<IReadOnlyDictionary<string, string>> property) =>
        property
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .HasConversion(new JsonValueConverter<IReadOnlyDictionary<string, string>>(), JsonValueConverter<IReadOnlyDictionary<string, string>>.Comparer);
}
