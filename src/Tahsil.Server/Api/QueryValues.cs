using System.Text.Json;

namespace Tahsil.Server.Api;

internal static class QueryValues
{
    /// <summary>Reads an optional enum query value by its API name (snake_case); absent values are valid.</summary>
    public static bool TryParseEnum<TEnum>(string? value, out TEnum? result)
        where TEnum : struct, Enum
    {
        result = null;
        if (value is null)
        {
            return true;
        }

        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(JsonNamingPolicy.SnakeCaseLower.ConvertName(candidate.ToString()), value, StringComparison.Ordinal))
            {
                result = candidate;
                return true;
            }
        }

        return false;
    }
}
