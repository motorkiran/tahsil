using System.Globalization;
using System.Text.Json;

namespace Tahsil.Providers.Abstractions;

/// <summary>
/// Reads provider JSON that is not always typed as documented: numbers sent as strings, booleans as <c>"true"</c> or
/// <c>1</c>, and property names in varying case. A missing or unreadable value is <see langword="null"/>.
/// </summary>
public static class LenientJson
{
    /// <summary>Parses a body, or returns <see langword="null"/> when it is not JSON (an unparseable answer is <c>Unknown</c>, ADP-4).</summary>
    public static JsonDocument? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Finds a property by a path of names, matching each name exactly first and then ignoring case.</summary>
    public static JsonElement? PropertyAt(this JsonElement element, params ReadOnlySpan<string> path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (current.TryGetProperty(name, out var exact))
            {
                current = exact;
                continue;
            }

            JsonElement? found = null;
            foreach (var property in current.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    found = property.Value;
                    break;
                }
            }

            if (found is not { } next)
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : current;
    }

    public static string? TextAt(this JsonElement element, params ReadOnlySpan<string> path) => element.PropertyAt(path) switch
    {
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } value => value.GetRawText(),
        _ => null,
    };

    public static long? Int64At(this JsonElement element, params ReadOnlySpan<string> path) => element.PropertyAt(path) switch
    {
        { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out var number) => number,
        { ValueKind: JsonValueKind.String } value when long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };

    /// <summary>Reads a decimal exactly, never through <see cref="double"/>.</summary>
    public static decimal? DecimalAt(this JsonElement element, params ReadOnlySpan<string> path) => element.PropertyAt(path) switch
    {
        { ValueKind: JsonValueKind.Number } value when value.TryGetDecimal(out var number) => number,
        { ValueKind: JsonValueKind.String } value when decimal.TryParse(value.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };

    public static bool? BooleanAt(this JsonElement element, params ReadOnlySpan<string> path) => element.PropertyAt(path) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out var number) && number is 0 or 1 => number == 1,
        { ValueKind: JsonValueKind.String } value => value.GetString()?.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "1" or "YES" => true,
            "FALSE" or "0" or "NO" => false,
            _ => null,
        },
        _ => null,
    };

    public static DateTimeOffset? DateTimeAt(this JsonElement element, params ReadOnlySpan<string> path) => element.PropertyAt(path) switch
    {
        { ValueKind: JsonValueKind.String } value when DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) => time,
        { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out var seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds),
        _ => null,
    };

    public static IEnumerable<JsonElement> ArrayAt(this JsonElement element, params ReadOnlySpan<string> path) =>
        element.PropertyAt(path) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
}
