using System.Text.Json;

namespace Tahsil.Infrastructure.Persistence.Conventions;

/// <summary>
/// Enum values as stored in the database: lowercase snake_case names (<c>PastDue</c> is <c>past_due</c>), the same
/// as their API values. Raw SQL and index filters use these instead of spelling values by hand.
/// </summary>
internal static class DbEnum
{
    public const int MaxLength = 32;

    public static string Value<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        Name(value.ToString());

    public static string Name(string memberName) => JsonNamingPolicy.SnakeCaseLower.ConvertName(memberName);
}
