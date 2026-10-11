using System.Net.Mail;
using System.Text.RegularExpressions;
using Tahsil.Domain.Common;

namespace Tahsil.Domain.Customers;

public enum CustomerType
{
    Individual,
    Company,
}

public enum KimlikSubscriberType
{
    User,
    Organization,
}

/// <summary>The Kimlik user or organization a customer is.</summary>
public sealed record KimlikSubscriber(KimlikSubscriberType Type, Guid Id);

/// <summary>A billing address; the country is ISO 3166-1 alpha-2 and required when anything else is set.</summary>
public sealed record CustomerAddress(string? Line1, string? Line2, string? District, string? City, string? PostalCode, string Country);

/// <summary>The billing details of a customer, validated together.</summary>
public sealed record CustomerDetails
{
    public string? Email { get; init; }

    public string? Name { get; init; }

    public CustomerType Type { get; init; }

    public string? Phone { get; init; }

    /// <summary>The tax ID in clear text, only to validate it; customers keep it encrypted.</summary>
    public string? TaxId { get; init; }

    public string? TaxOffice { get; init; }

    public CustomerAddress? Address { get; init; }

    public required string Locale { get; init; }

    public bool IsTaxExempt { get; init; }

    public KimlikSubscriber? KimlikSubscriber { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Who pays: a person or a company, usually created by the application with its own ID (<see cref="ExternalId"/>).
/// Customers with billing history are anonymized instead of deleted.
/// </summary>
public sealed partial class Customer : AggregateRoot
{
    public const int ExternalIdMaxLength = 200;
    public const int EmailMaxLength = 254;
    public const int NameMaxLength = 200;
    public const int PhoneMaxLength = 32;
    public const int TaxOfficeMaxLength = 100;
    public const int AddressFieldMaxLength = 200;

    private static readonly string[] Locales = ["tr", "en"];

    private Customer()
    {
    }

    public Guid Id { get; private init; }

    public string? ExternalId { get; private set; }

    public string? Email { get; private set; }

    public string? Name { get; private set; }

    public CustomerType Type { get; private set; }

    public string? Phone { get; private set; }

    /// <summary>The tax ID, encrypted with the purpose <c>customers.tax_id</c> and bound to the customer.</summary>
    public byte[]? EncryptedTaxId { get; private set; }

    public string? TaxOffice { get; private set; }

    public CustomerAddress? Address { get; private set; }

    public string Locale { get; private set; } = "tr";

    public bool IsTaxExempt { get; private set; }

    public KimlikSubscriberType? KimlikSubscriberType { get; private set; }

    public Guid? KimlikSubscriberId { get; private set; }

    /// <summary>The Kimlik user or organization this customer is, when linked.</summary>
    public KimlikSubscriber? KimlikSubscriber =>
        KimlikSubscriberType is { } type && KimlikSubscriberId is { } id ? new KimlikSubscriber(type, id) : null;

    public IReadOnlyDictionary<string, string> Metadata { get; private set; } = Common.Metadata.Empty;

    /// <summary>The card charged when nothing more specific is set; an active payment method of this customer.</summary>
    public Guid? DefaultPaymentMethodId { get; private set; }

    public DateTimeOffset? AnonymizedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsAnonymized => AnonymizedAt is not null;

    /// <summary>A new customer; <paramref name="encryptTaxId"/> encrypts the tax ID for the new customer's ID.</summary>
    public static Result<Customer> Create(string? externalId, CustomerDetails details, Func<Guid, string, byte[]> encryptTaxId, DateTimeOffset now)
    {
        if (Text.Normalize(externalId) is { } external && external.Length > ExternalIdMaxLength)
        {
            return CustomerErrors.Invalid("externalId", "The external ID is at most 200 characters.");
        }

        var customer = new Customer { Id = Guid.CreateVersion7(now), ExternalId = Text.Normalize(externalId), CreatedAt = now };
        var update = customer.Update(details, encryptTaxId, now);
        return update.IsFailure ? update.Error : customer;
    }

    /// <summary>Replaces the billing details; the caller merges a partial update into <paramref name="details"/> first.</summary>
    public Result Update(CustomerDetails details, Func<Guid, string, byte[]> encryptTaxId, DateTimeOffset now)
    {
        if (IsAnonymized)
        {
            return CustomerErrors.Anonymized;
        }

        if (Validate(details) is { } invalid)
        {
            return invalid;
        }

        var metadata = Common.Metadata.Validate(details.Metadata);
        if (metadata.IsFailure)
        {
            return metadata.Error;
        }

        Email = Text.Normalize(details.Email);
        Name = Text.Normalize(details.Name);
        Type = details.Type;
        Phone = NormalizePhone(Text.Normalize(details.Phone), details.Address?.Country);
        EncryptedTaxId = Text.Normalize(details.TaxId) is { } taxId ? encryptTaxId(Id, taxId) : null;
        TaxOffice = Text.Normalize(details.TaxOffice);
        Address = NormalizeAddress(details.Address);
        Locale = details.Locale;
        IsTaxExempt = details.IsTaxExempt;
        KimlikSubscriberType = details.KimlikSubscriber?.Type;
        KimlikSubscriberId = details.KimlikSubscriber?.Id;
        Metadata = metadata.Value;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Gives the customer an external ID, as an upsert by external ID does for a customer it creates.</summary>
    public void SetExternalId(string externalId) => ExternalId = externalId;

    public void SetDefaultPaymentMethod(Guid? paymentMethodId, DateTimeOffset now)
    {
        DefaultPaymentMethodId = paymentMethodId;
        UpdatedAt = now;
    }

    /// <summary>
    /// Clears the personal data of a customer with billing history (CUS-8): email, name, phone, tax ID, tax office,
    /// address, external ID, Kimlik link and metadata. Finalized invoices keep their own snapshot.
    /// </summary>
    public void Anonymize(DateTimeOffset now)
    {
        ExternalId = null;
        Email = null;
        Name = null;
        Phone = null;
        EncryptedTaxId = null;
        TaxOffice = null;
        Address = null;
        KimlikSubscriberType = null;
        KimlikSubscriberId = null;
        Metadata = Common.Metadata.Empty;
        AnonymizedAt ??= now;
        UpdatedAt = now;
    }

    /// <summary>Checks billing details as <see cref="Create"/> and <see cref="Update"/> do, such as details to use later.</summary>
    public static Error? Validate(CustomerDetails details)
    {
        if (Text.Normalize(details.Email) is { } email && (email.Length > EmailMaxLength || !IsEmail(email)))
        {
            return CustomerErrors.Invalid("email", "The email address is not valid.");
        }

        if (!Text.FitsOptional(details.Name, NameMaxLength))
        {
            return CustomerErrors.Invalid("name", "The name is at most 200 characters.");
        }

        if (!Enum.IsDefined(details.Type))
        {
            return CustomerErrors.Invalid("type", "The type is individual or company.");
        }

        if (!Text.FitsOptional(details.Phone, PhoneMaxLength))
        {
            return CustomerErrors.Invalid("phone", "The phone number is at most 32 characters.");
        }

        if (!Text.FitsOptional(details.TaxOffice, TaxOfficeMaxLength))
        {
            return CustomerErrors.Invalid("taxOffice", "The tax office is at most 100 characters.");
        }

        if (details.Address is { } address && (!CountryPattern().IsMatch(address.Country ?? string.Empty)
            || new[] { address.Line1, address.Line2, address.District, address.City, address.PostalCode }.Any(field => !Text.FitsOptional(field, AddressFieldMaxLength))))
        {
            return CustomerErrors.Invalid("address", "An address has an ISO 3166-1 alpha-2 country, such as TR, and fields of at most 200 characters.");
        }

        if (Text.Normalize(details.TaxId) is { } taxId && !TaxIds.IsValid(taxId, details.Type, details.Address?.Country))
        {
            return CustomerErrors.InvalidTaxId;
        }

        if (!Locales.Contains(details.Locale))
        {
            return CustomerErrors.Invalid("locale", "The locale is tr or en.");
        }

        if (details.KimlikSubscriber is { } subscriber && (!Enum.IsDefined(subscriber.Type) || subscriber.Id == Guid.Empty))
        {
            return CustomerErrors.Invalid("kimlikSubscriber", "A Kimlik subscriber is a user or organization with its ID.");
        }

        return null;
    }

    private static bool IsEmail(string value) =>
        MailAddress.TryCreate(value, out var address) && address.Address == value && address.DisplayName.Length == 0;

    /// <summary>Phone numbers in E.164 when they read as one: <c>0532 123 45 67</c> in Turkey becomes <c>+905321234567</c>.</summary>
    private static string? NormalizePhone(string? phone, string? country)
    {
        if (phone is null)
        {
            return null;
        }

        var digits = new string([.. phone.Where(char.IsAsciiDigit)]);
        var separatorsOnly = phone.All(character => char.IsAsciiDigit(character) || character is ' ' or '-' or '(' or ')' or '.' or '+');
        if (!separatorsOnly)
        {
            return phone;
        }

        if (phone.StartsWith('+') && digits.Length is >= 8 and <= 15)
        {
            return "+" + digits;
        }

        if (country is null or "TR" && digits.Length == 11 && digits.StartsWith('0'))
        {
            return "+90" + digits[1..];
        }

        if (country is null or "TR" && digits.Length == 12 && digits.StartsWith("90", StringComparison.Ordinal))
        {
            return "+" + digits;
        }

        return phone;
    }

    private static CustomerAddress? NormalizeAddress(CustomerAddress? address) => address is null
        ? null
        : new CustomerAddress(
            Text.Normalize(address.Line1),
            Text.Normalize(address.Line2),
            Text.Normalize(address.District),
            Text.Normalize(address.City),
            Text.Normalize(address.PostalCode),
            address.Country);

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryPattern();
}

public static class CustomerErrors
{
    public static readonly Error NotFound = Error.NotFound("customer.not_found", "The customer does not exist.");

    public static readonly Error InvalidTaxId = Error.Validation(
        "customer.invalid_tax_id",
        "For Turkey, individuals have an 11-digit TCKN and companies a 10-digit VKN (or the owner's TCKN); elsewhere at most 32 characters.",
        "taxId");

    public static readonly Error KimlikSubscriberTaken = Error.Conflict("customer.kimlik_subscriber_taken", "Another customer is linked to this Kimlik subscriber.");

    public static readonly Error Anonymized = Error.Conflict("customer.anonymized", "The customer was anonymized and can no longer change or buy.");

    public static Error ExternalIdTaken(Guid existing) =>
        Error.Conflict("customer.external_id_taken", "Another customer has this external ID.") with
        {
            Extensions = new Dictionary<string, object?> { ["customerId"] = existing },
        };

    public static Error Invalid(string field, string message) => Error.Validation("customer.invalid", message, field);
}
