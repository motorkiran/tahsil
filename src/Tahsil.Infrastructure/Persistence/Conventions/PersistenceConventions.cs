using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Tahsil.Domain.Common;

namespace Tahsil.Infrastructure.Persistence.Conventions;

/// <summary>The mapping rules every Tahsil table follows, applied by convention rather than per entity.</summary>
internal static class PersistenceConventions
{
    /// <summary>Applies the type-wide conventions; call from <c>ConfigureConventions</c>.</summary>
    public static void Configure(ModelConfigurationBuilder configuration)
    {
        configuration.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    /// <summary>Applies the model conventions; call at the end of <c>OnModelCreating</c>.</summary>
    public static void Apply(ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            if (entity.ClrType.IsAssignableTo(typeof(AggregateRoot)))
            {
                builder.Entity(entity.ClrType).Property(nameof(AggregateRoot.Version)).IsRowVersion();
            }

            foreach (var property in entity.GetProperties())
            {
                StoreEnumAsSnakeCase(property);
            }
        }
    }

    private static void StoreEnumAsSnakeCase(IMutableProperty property)
    {
        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if (!type.IsEnum || property.GetValueConverter() is not null)
        {
            return;
        }

        property.SetValueConverter((ValueConverter)Activator.CreateInstance(typeof(SnakeCaseEnumConverter<>).MakeGenericType(type))!);
        property.SetMaxLength(property.GetMaxLength() ?? DbEnum.MaxLength);
    }
}
