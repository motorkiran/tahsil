using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tahsil.Infrastructure.Persistence.Conventions;

/// <summary>
/// The JSON that Tahsil stores (JSON columns, outbox payloads, audit data): camelCase properties and snake_case
/// enum values, set explicitly so the stored form never depends on serializer defaults.
/// </summary>
internal static class StorageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };
}
