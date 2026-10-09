using System.Text.RegularExpressions;

namespace Tahsil.Application.Common;

/// <summary>
/// Masks personal data in text that goes to diagnostic logs, such as provider error messages: email addresses keep
/// their first letter and domain (<c>a***@example.com</c>), and identity and tax numbers (10 or 11 digits, TCKN and
/// VKN) keep their last two digits.
/// </summary>
public static partial class Redaction
{
    public static string Email(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : $"{email[0]}***{email[at..]}";
    }

    public static string IdentityNumber(string number) =>
        number.Length <= 2 ? new string('*', number.Length) : new string('*', number.Length - 2) + number[^2..];

    /// <summary>Masks every email address and identity number in <paramref name="text"/>.</summary>
    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var masked = EmailPattern().Replace(text, match => Email(match.Value));
        return IdentityNumberPattern().Replace(masked, match => IdentityNumber(match.Value));
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    /// <summary>Ten or eleven digits standing alone; longer runs (card numbers, references) are not identity numbers.</summary>
    [GeneratedRegex(@"(?<!\d)\d{10,11}(?!\d)")]
    private static partial Regex IdentityNumberPattern();
}
