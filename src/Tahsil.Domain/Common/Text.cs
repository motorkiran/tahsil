namespace Tahsil.Domain.Common;

/// <summary>Text normalization of the domain model: trimmed, and empty means absent.</summary>
public static class Text
{
    public static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Whether optional text fits: absent, or at most <paramref name="maxLength"/> characters once trimmed.</summary>
    public static bool FitsOptional(string? value, int maxLength) => Normalize(value) is not { } text || text.Length <= maxLength;

    /// <summary>Whether required text is present and at most <paramref name="maxLength"/> characters once trimmed.</summary>
    public static bool FitsRequired(string? value, int maxLength) => Normalize(value) is { } text && text.Length <= maxLength;
}
