using Tahsil.Domain.Common;

namespace Tahsil.Domain.Catalog;

/// <summary>
/// Something the application sells, with prices in one or more currencies and intervals. Archiving stops new sales;
/// existing subscriptions keep renewing.
/// </summary>
public sealed class Product : AggregateRoot
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;
    public const int KimlikPlanMaxLength = 64;

    private Product()
    {
    }

    public Guid Id { get; private init; }

    public string Key { get; private init; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>The key of the Kimlik plan a subscription to this product grants.</summary>
    public string? KimlikPlan { get; private set; }

    public bool IsArchived { get; private set; }

    public IReadOnlyDictionary<string, string> Metadata { get; private set; } = Common.Metadata.Empty;

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<Product> Create(
        string key, string name, string? description, string? kimlikPlan, IReadOnlyDictionary<string, string>? metadata, DateTimeOffset now)
    {
        if (!CatalogKey.IsValid(key))
        {
            return CatalogKey.Invalid;
        }

        var product = new Product { Id = Guid.CreateVersion7(now), Key = key, CreatedAt = now };
        var update = product.Update(name, description, kimlikPlan, isArchived: false, metadata, now);
        return update.IsFailure ? update.Error : product;
    }

    public Result Update(string name, string? description, string? kimlikPlan, bool isArchived, IReadOnlyDictionary<string, string>? metadata, DateTimeOffset now)
    {
        if (!Text.FitsRequired(name, NameMaxLength))
        {
            return ProductErrors.InvalidName;
        }

        if (!Text.FitsOptional(description, DescriptionMaxLength))
        {
            return ProductErrors.InvalidDescription;
        }

        if (Text.Normalize(kimlikPlan) is { } plan && (plan.Length > KimlikPlanMaxLength || plan.Any(char.IsWhiteSpace)))
        {
            return ProductErrors.InvalidKimlikPlan;
        }

        var validMetadata = Common.Metadata.Validate(metadata);
        if (validMetadata.IsFailure)
        {
            return validMetadata.Error;
        }

        Name = name.Trim();
        Description = Text.Normalize(description);
        KimlikPlan = Text.Normalize(kimlikPlan);
        IsArchived = isArchived;
        Metadata = validMetadata.Value;
        UpdatedAt = now;
        return Result.Success();
    }
}
