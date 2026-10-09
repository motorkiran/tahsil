using System.Text.RegularExpressions;
using Tahsil.Domain.Common;

namespace Tahsil.Domain.Catalog;

/// <summary>The key of a catalog object: a lowercase letter, then up to 63 lowercase letters, digits, <c>_</c> or <c>-</c>.</summary>
public static partial class CatalogKey
{
    public const int MaxLength = 64;

    public static readonly Error Invalid = Error.Validation(
        "catalog.invalid_key", "A key starts with a lowercase letter and has up to 64 lowercase letters, digits, '_' or '-'.", "key");

    public static bool IsValid(string? key) => key is not null && Pattern().IsMatch(key);

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,63}$")]
    private static partial Regex Pattern();
}
