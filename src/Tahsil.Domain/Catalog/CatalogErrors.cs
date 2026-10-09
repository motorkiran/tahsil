using Tahsil.Domain.Common;

namespace Tahsil.Domain.Catalog;

public static class TaxRateErrors
{
    public static readonly Error NotFound = Error.NotFound("tax_rate.not_found", "The tax rate does not exist.");
    public static readonly Error KeyTaken = Error.Conflict("tax_rate.key_taken", "Another tax rate has this key.");
    public static readonly Error InvalidName = Error.Validation("tax_rate.invalid", "A name is required and is at most 50 characters.", "name");
    public static readonly Error InvalidDescription = Error.Validation("tax_rate.invalid", "The description is at most 200 characters.", "description");
    public static readonly Error InvalidPercentage = Error.Validation("tax_rate.invalid", "The percentage is between 0 and 100, with at most four decimals.", "percentage");
    public static readonly Error InvalidCountry = Error.Validation("tax_rate.invalid", "The country is an ISO 3166-1 alpha-2 code, such as TR.", "country");
}

public static class MeterErrors
{
    public static readonly Error NotFound = Error.NotFound("meter.not_found", "The meter does not exist.");
    public static readonly Error KeyTaken = Error.Conflict("meter.key_taken", "Another meter has this key.");
    public static readonly Error InvalidName = Error.Validation("meter.invalid", "A name is required and is at most 100 characters.", "name");
    public static readonly Error InvalidUniqueProperty = Error.Validation(
        "meter.invalid", "A unique_count meter names the event property it counts (at most 40 characters); other meters have none.", "uniqueProperty");
}

public static class ProductErrors
{
    public static readonly Error NotFound = Error.NotFound("product.not_found", "The product does not exist.");
    public static readonly Error KeyTaken = Error.Conflict("product.key_taken", "Another product has this key.");
    public static readonly Error InvalidName = Error.Validation("product.invalid", "A name is required and is at most 100 characters.", "name");
    public static readonly Error InvalidDescription = Error.Validation("product.invalid", "The description is at most 500 characters.", "description");
    public static readonly Error InvalidKimlikPlan = Error.Validation("product.invalid", "The Kimlik plan is a plan key of at most 64 characters.", "kimlikPlan");
    public static readonly Error Archived = Error.Validation("product.archived", "The product is archived and sells no more.");
    public static readonly Error InUse = Error.Conflict("product.in_use", "The product's prices have been used; archive the product instead.");
}

public static class PriceErrors
{
    public static readonly Error NotFound = Error.NotFound("price.not_found", "The price does not exist.");
    public static readonly Error KeyTaken = Error.Conflict("price.key_taken", "Another price has this lookup key; set transferKey to move it to this price.");
    public static readonly Error Archived = Error.Validation("price.archived", "The price is archived and sells no more.");
    public static readonly Error UnsupportedCurrency = Error.Validation("currency.unsupported", "The currency is not a supported ISO 4217 code.", "currency");

    public static Error Invalid(string field, string message) => Error.Validation("price.invalid", message, field);

    public static Error Immutable(string field) =>
        Error.Validation("price.immutable", $"'{field}' cannot change once a price exists; create a new price instead.", field);

    public static Error InvalidReference(string field, string message) => Error.Validation("price.invalid_reference", message, field);
}
