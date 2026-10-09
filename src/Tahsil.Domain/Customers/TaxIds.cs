namespace Tahsil.Domain.Customers;

/// <summary>Turkish identity (TCKN) and tax (VKN) numbers, as CUS-4 validates them.</summary>
public static class TaxIds
{
    public const int ForeignMaxLength = 32;

    /// <summary>
    /// A TCKN: 11 digits, the first not 0; digit 10 is ((d1 + d3 + d5 + d7 + d9) × 7 − (d2 + d4 + d6 + d8)) mod 10, and
    /// digit 11 is the sum of the first ten mod 10.
    /// </summary>
    public static bool IsValidTckn(string value)
    {
        if (value.Length != 11 || !value.All(char.IsAsciiDigit) || value[0] == '0')
        {
            return false;
        }

        var d = value.Select(character => character - '0').ToArray();
        var tenth = (((d[0] + d[2] + d[4] + d[6] + d[8]) * 7) - (d[1] + d[3] + d[5] + d[7])) % 10;
        if (tenth < 0)
        {
            tenth += 10;
        }

        return d[9] == tenth && d[10] == d.Take(10).Sum() % 10;
    }

    /// <summary>A VKN: 10 digits.</summary>
    public static bool IsValidVkn(string value) => value.Length == 10 && value.All(char.IsAsciiDigit);

    /// <summary>
    /// Whether <paramref name="taxId"/> fits the customer: for Turkey (or no country), a TCKN for individuals and a VKN or
    /// TCKN for companies; elsewhere, free text up to 32 characters.
    /// </summary>
    public static bool IsValid(string taxId, CustomerType type, string? country) =>
        country is null or "TR"
            ? type == CustomerType.Individual ? IsValidTckn(taxId) : IsValidVkn(taxId) || IsValidTckn(taxId)
            : taxId.Length <= ForeignMaxLength;
}
