using System.Text.Json.Serialization;

namespace Tahsil.Contracts;

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

/// <summary>A billing address; <c>country</c> is ISO 3166-1 alpha-2, such as <c>TR</c>.</summary>
public sealed record CustomerAddress(
    string? Line1 = null, string? Line2 = null, string? District = null, string? City = null, string? PostalCode = null, string Country = "TR");

public sealed record CustomerResponse(
    Guid Id,
    string? ExternalId,
    string? Email,
    string? Name,
    CustomerType Type,
    string? Phone,
    string? TaxId,
    string? TaxOffice,
    CustomerAddress? Address,
    string Locale,
    bool IsTaxExempt,
    KimlikSubscriber? KimlikSubscriber,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset? AnonymizedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CustomerBalanceResponse> Balances);

public sealed record CreateCustomerRequest
{
    /// <summary>The application's own ID for the customer, unique; most applications address customers by it.</summary>
    public string? ExternalId { get; init; }

    public string? Email { get; init; }

    /// <summary>A person's full name or a company's legal name.</summary>
    public string? Name { get; init; }

    public CustomerType Type { get; init; } = CustomerType.Individual;

    /// <summary>Stored in E.164 when it reads as a phone number.</summary>
    public string? Phone { get; init; }

    /// <summary>For Turkey: a TCKN for individuals, a VKN (or the owner's TCKN) for companies. Stored encrypted.</summary>
    public string? TaxId { get; init; }

    public string? TaxOffice { get; init; }

    public CustomerAddress? Address { get; init; }

    /// <summary><c>tr</c> or <c>en</c>; the installation's default when omitted.</summary>
    public string? Locale { get; init; }

    public bool IsTaxExempt { get; init; }

    public KimlikSubscriber? KimlikSubscriber { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>JSON Merge Patch: omitted fields stay as they are, <c>null</c> clears.</summary>
public sealed record UpdateCustomerRequest
{
    public string? Email { get => field; init { field = value; HasEmail = true; } }

    public string? Name { get => field; init { field = value; HasName = true; } }

    public CustomerType? Type { get; init; }

    public string? Phone { get => field; init { field = value; HasPhone = true; } }

    public string? TaxId { get => field; init { field = value; HasTaxId = true; } }

    public string? TaxOffice { get => field; init { field = value; HasTaxOffice = true; } }

    public CustomerAddress? Address { get => field; init { field = value; HasAddress = true; } }

    public string? Locale { get; init; }

    public bool? IsTaxExempt { get; init; }

    public KimlikSubscriber? KimlikSubscriber { get => field; init { field = value; HasKimlikSubscriber = true; } }

    public IReadOnlyDictionary<string, string>? Metadata { get => field; init { field = value; HasMetadata = true; } }

    [JsonIgnore]
    public bool HasEmail { get; private init; }

    [JsonIgnore]
    public bool HasName { get; private init; }

    [JsonIgnore]
    public bool HasPhone { get; private init; }

    [JsonIgnore]
    public bool HasTaxId { get; private init; }

    [JsonIgnore]
    public bool HasTaxOffice { get; private init; }

    [JsonIgnore]
    public bool HasAddress { get; private init; }

    [JsonIgnore]
    public bool HasKimlikSubscriber { get; private init; }

    [JsonIgnore]
    public bool HasMetadata { get; private init; }
}

/// <summary>Everything Tahsil holds about a customer, for data subject requests (KVKK).</summary>
public sealed record CustomerExportResponse(CustomerResponse Customer, IReadOnlyList<AuditEventResponse> AuditEvents, DateTimeOffset ExportedAt);
