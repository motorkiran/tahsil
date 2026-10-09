using System.Text.Json;

namespace Tahsil.Contracts.Serialization;

/// <summary>The JSON conventions of the Tahsil API: camelCase properties and snake_case enum values.</summary>
public static class TahsilJson
{
    /// <summary>Applies the conventions to options that start from <see cref="JsonSerializerDefaults.Web"/>.</summary>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.Converters.Add(new SnakeCaseEnumConverter());
        return options;
    }
}
