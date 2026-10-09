using System.Text.RegularExpressions;

namespace Tahsil.Domain.Common;

/// <summary>
/// Free-form string pairs that applications attach to records; Tahsil stores and returns them and never interprets
/// them. At most 50 keys of up to 40 characters (letters, digits, <c>_</c>, <c>.</c> and <c>-</c>), values up to 500.
/// </summary>
public static partial class Metadata
{
    public const int MaxKeys = 50;
    public const int MaxKeyLength = 40;
    public const int MaxValueLength = 500;

    public static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(StringComparer.Ordinal);

    public static readonly Error Invalid = Error.Validation(
        "metadata.invalid",
        $"Metadata has at most {MaxKeys} keys of up to {MaxKeyLength} letters, digits, '_', '.' or '-', with values of up to {MaxValueLength} characters.",
        "metadata");

    public static Result<IReadOnlyDictionary<string, string>> Validate(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            return Result.Success<IReadOnlyDictionary<string, string>>(Empty);
        }

        if (metadata.Count > MaxKeys || metadata.Any(pair => !KeyPattern().IsMatch(pair.Key) || pair.Value is null || pair.Value.Length > MaxValueLength))
        {
            return Invalid;
        }

        return Result.Success<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(metadata, StringComparer.Ordinal));
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,40}$")]
    private static partial Regex KeyPattern();
}
