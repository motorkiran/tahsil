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

public enum ConsentType
{
    PreInformation,
    DistanceSalesContract,
    SubscriptionContract,
    PrivacyNoticeShown,
    ImmediateStart,
    CardStorage,
    Marketing,
    Cancellation,
    Withdrawal,
    PlanChange,
    Custom,
}

/// <summary>
/// The evidence of what a customer was shown and accepted: a legal document (with its key, version and the SHA-256 of
/// what was rendered), or a consent such as the one to future charges of a saved card, with the exact text shown.
/// </summary>
/// <param name="Id">The record's ID.</param>
/// <param name="Type">What was accepted.</param>
/// <param name="DocumentKey">The legal document's key, for document consents.</param>
/// <param name="DocumentVersion">The document's version.</param>
/// <param name="DocumentHash">The SHA-256 of the rendered document, in hexadecimal.</param>
/// <param name="TextShown">The exact checkbox or button text.</param>
/// <param name="Context">What it was about: the price shown, amounts, the related subscription or card.</param>
/// <param name="CheckoutSessionId">The checkout session it was accepted in.</param>
/// <param name="PortalSessionId">The portal session it was accepted in.</param>
/// <param name="SubscriptionId">The subscription it concerns.</param>
/// <param name="IpAddress">The address it was accepted from.</param>
/// <param name="UserAgent">The browser it was accepted in.</param>
/// <param name="AcceptedAt">When it was accepted.</param>
/// <param name="Document">The rendered document as the customer saw it, as HTML; only when asked for.</param>
public sealed record ConsentRecordResponse(
    Guid Id,
    ConsentType Type,
    string? DocumentKey,
    string? DocumentVersion,
    string? DocumentHash,
    string TextShown,
    IReadOnlyDictionary<string, string> Context,
    Guid? CheckoutSessionId,
    Guid? PortalSessionId,
    Guid? SubscriptionId,
    string? IpAddress,
    string? UserAgent,
    DateTimeOffset AcceptedAt,
    string? Document);

/// <summary>An email Tahsil sent the customer, or skipped: its type, and why it was not sent.</summary>
public sealed record CustomerNotificationResponse(string Type, bool Sent, string? Reason, DateTimeOffset CreatedAt);

/// <summary>How much usage Tahsil keeps of the customer on a meter; the usage summaries give its quantities.</summary>
public sealed record CustomerUsageExport(MeterSummary Meter, long Events, DateTimeOffset First, DateTimeOffset Last);

/// <summary>Everything Tahsil holds about a customer, for data subject requests (KVKK).</summary>
public sealed record CustomerExportResponse(
    CustomerResponse Customer,
    IReadOnlyList<AuditEventResponse> AuditEvents,
    DateTimeOffset ExportedAt,
    IReadOnlyList<SubscriptionResponse> Subscriptions,
    IReadOnlyList<InvoiceResponse> Invoices,
    IReadOnlyList<PaymentResponse> Payments,
    IReadOnlyList<RefundResponse> Refunds,
    IReadOnlyList<PaymentMethodResponse> PaymentMethods,
    IReadOnlyList<BalanceTransactionResponse> BalanceTransactions,
    IReadOnlyList<CheckoutSessionResponse> CheckoutSessions,
    IReadOnlyList<ConsentRecordResponse> Consents,
    IReadOnlyList<CustomerNotificationResponse> Notifications,
    IReadOnlyList<CustomerUsageExport> Usage);
