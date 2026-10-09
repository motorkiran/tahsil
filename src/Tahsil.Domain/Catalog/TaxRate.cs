using System.Text.RegularExpressions;
using Tahsil.Domain.Common;

namespace Tahsil.Domain.Catalog;

/// <summary>
/// A tax that prices add or include, such as KDV 20%. The percentage and whether it is included never change
/// (DOM-2): to change a rate, create a new one and point new prices at it.
/// </summary>
public sealed partial class TaxRate : AggregateRoot
{
    public const int NameMaxLength = 50;
    public const int DescriptionMaxLength = 200;

    private TaxRate()
    {
    }

    public Guid Id { get; private init; }

    public string Key { get; private init; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public decimal Percentage { get; private init; }

    public bool IsInclusive { get; private init; }

    public string? Country { get; private init; }

    public bool IsArchived { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<TaxRate> Create(
        string key, string name, string? description, decimal percentage, bool isInclusive, string? country, DateTimeOffset now)
    {
        if (!CatalogKey.IsValid(key))
        {
            return CatalogKey.Invalid;
        }

        if (Validate(name, description) is { } invalid)
        {
            return invalid;
        }

        if (percentage is < 0 or > 100 || decimal.Round(percentage, 4) != percentage)
        {
            return TaxRateErrors.InvalidPercentage;
        }

        if (Text.Normalize(country) is { } code && !CountryPattern().IsMatch(code))
        {
            return TaxRateErrors.InvalidCountry;
        }

        return new TaxRate
        {
            Id = Guid.CreateVersion7(now),
            Key = key,
            Name = name.Trim(),
            Description = Text.Normalize(description),
            Percentage = percentage,
            IsInclusive = isInclusive,
            Country = Text.Normalize(country),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Changes what may change: the name, the description and whether the rate is archived.</summary>
    public Result Update(string name, string? description, bool isArchived, DateTimeOffset now)
    {
        if (Validate(name, description) is { } invalid)
        {
            return invalid;
        }

        Name = name.Trim();
        Description = Text.Normalize(description);
        IsArchived = isArchived;
        UpdatedAt = now;
        return Result.Success();
    }

    private static Error? Validate(string name, string? description) =>
        !Text.FitsRequired(name, NameMaxLength) ? TaxRateErrors.InvalidName
        : !Text.FitsOptional(description, DescriptionMaxLength) ? TaxRateErrors.InvalidDescription
        : null;

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryPattern();
}
