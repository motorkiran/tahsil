using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tahsil.Contracts.Serialization;

/// <summary>
/// Enums as lowercase snake_case strings (<c>past_due</c>), the same values Tahsil stores and uses in event names
/// and error codes. Numbers are rejected: they would tie clients to declaration order.
/// </summary>
public sealed class SnakeCaseEnumConverter() : JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false);
