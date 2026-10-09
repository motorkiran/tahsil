using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Tahsil.Infrastructure.Persistence.Conventions;

/// <summary>
/// Stores a record as JSON with explicit options (camelCase properties, snake_case enums), so the stored form does
/// not depend on serializer defaults.
/// </summary>
internal sealed class JsonValueConverter<T>() : ValueConverter<T, string>(
    value => JsonSerializer.Serialize(value, Options),
    json => JsonSerializer.Deserialize<T>(json, Options)!)
{
    private static JsonSerializerOptions Options => StorageJson.Options;

    /// <summary>Compares values by their JSON, so records are change-tracked by value.</summary>
    public static ValueComparer<T> Comparer { get; } = new(
        (left, right) => JsonSerializer.Serialize(left, Options) == JsonSerializer.Serialize(right, Options),
        value => JsonSerializer.Serialize(value, Options).GetHashCode(StringComparison.Ordinal),
        value => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!);
}
