namespace Tahsil.Providers.Abstractions;

/// <summary>An amount in minor units, with the currency's number of minor-unit digits for format conversions (ADP-8).</summary>
public sealed record Amount(long MinorUnits, string Currency, int Exponent);

public enum PaymentPurpose
{
    Charge,

    /// <summary>Saves a card without charging it.</summary>
    Setup,

    /// <summary>A small charge to save a card, voided or refunded at once (ADP-15).</summary>
    VerificationCharge,
}

/// <summary>The customer as a provider may need to see them.</summary>
public sealed record CustomerInfo
{
    public required Guid Id { get; init; }

    public string? Name { get; init; }

    public string? Email { get; init; }

    public string? Phone { get; init; }

    public bool IsCompany { get; init; }

    public string? TaxId { get; init; }

    public string? AddressLine { get; init; }

    public string? City { get; init; }

    public string? PostalCode { get; init; }

    public string? Country { get; init; }

    public string Locale { get; init; } = "tr";

    public string? IpAddress { get; init; }
}

/// <summary>A line of the basket, for providers that need one.</summary>
public sealed record LineItem(string Description, decimal Quantity, long AmountMinorUnits);

/// <summary>A card the provider keeps, as Tahsil refers to it.</summary>
public sealed record SavedCardReference(string? ProviderCustomer, string Token);

public sealed record StartRequest
{
    public required Guid PaymentId { get; init; }

    /// <summary>Tahsil's reference, sent as the provider's order, conversation or reference ID (ADP-6).</summary>
    public required string Reference { get; init; }

    public required Amount Amount { get; init; }

    public required PaymentPurpose Purpose { get; init; }

    public bool SaveCard { get; init; }

    public string? ProviderCustomer { get; init; }

    public required CustomerInfo Customer { get; init; }

    public IReadOnlyList<LineItem> Lines { get; init; } = [];

    public string? Description { get; init; }

    public required Uri ReturnUrl { get; init; }

    public required Uri CancelUrl { get; init; }

    public string Locale { get; init; } = "tr";
}

/// <summary>How the customer pays: on the provider's page, inside Tahsil's, or in a form that posts to the provider.</summary>
public abstract record Presentation
{
    public sealed record Redirect(Uri Url) : Presentation;

    /// <summary>HTML and scripts rendered in Tahsil's page with its CSP nonce, which replaces <c>{nonce}</c> in the HTML for inline scripts.</summary>
    public sealed record Embed(string Html, IReadOnlyList<Uri> Scripts) : Presentation;

    public sealed record DirectForm(Uri Action, IReadOnlyDictionary<string, string> HiddenFields, IReadOnlyList<string> CardFields) : Presentation;
}

public sealed record StartResult
{
    /// <summary>How the customer pays; none when the start <see cref="Failure"/>.</summary>
    public Presentation? Presentation { get; init; }

    /// <summary>
    /// The provider refused to start the payment, such as with wrong keys; nothing was charged. The payment fails with
    /// this outcome.
    /// </summary>
    public PaymentOutcome? Failure { get; init; }

    /// <summary>Opaque, non-secret values Tahsil keeps with the payment and gives back, such as a provider session ID.</summary>
    public IReadOnlyDictionary<string, string> ProviderData { get; init; } = new Dictionary<string, string>();

    /// <summary>A provider-side customer created during start; Tahsil stores it at once.</summary>
    public string? ProviderCustomer { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public static StartResult Failed(PaymentOutcome failure) => new() { Failure = failure };
}

public sealed record CompleteRequest
{
    public required Guid PaymentId { get; init; }

    public required string Reference { get; init; }

    public required Amount Amount { get; init; }

    public IReadOnlyDictionary<string, string> ProviderData { get; init; } = new Dictionary<string, string>();

    public bool SaveCard { get; init; }

    public string? ProviderCustomer { get; init; }

    /// <summary>The form and query values the provider sent with the customer's return.</summary>
    public IReadOnlyDictionary<string, string> ReturnValues { get; init; } = new Dictionary<string, string>();
}

public sealed record ChargeRequest
{
    public required Guid PaymentId { get; init; }

    public required string Reference { get; init; }

    public required Amount Amount { get; init; }

    public required SavedCardReference SavedCard { get; init; }

    public required CustomerInfo Customer { get; init; }

    public IReadOnlyList<LineItem> Lines { get; init; } = [];

    public string? Description { get; init; }
}

public enum PaymentStatus
{
    Succeeded,
    Failed,
    Pending,
    RequiresAction,

    /// <summary>The call may have changed state, but its result is not known; Tahsil reconciles (ADP-4).</summary>
    Unknown,
}

public enum ThreeDSecureResult
{
    NotAttempted,
    Authenticated,
    Failed,
}

/// <summary>A card a provider saved, with what may be shown about it. Never card data (ADP-2).</summary>
public sealed record SavedCard
{
    public string? ProviderCustomer { get; init; }

    public required string Token { get; init; }

    public string? Brand { get; init; }

    public string? Funding { get; init; }

    public string? Bin { get; init; }

    public string? Last4 { get; init; }

    public int? ExpMonth { get; init; }

    public int? ExpYear { get; init; }

    public string? Issuer { get; init; }

    public string? CardFamily { get; init; }

    public string? Country { get; init; }
}

public sealed record PaymentOutcome
{
    public required PaymentStatus Status { get; init; }

    public string? ProviderPaymentId { get; init; }

    /// <summary>What the provider says it charged, compared with the payment (PAY-9).</summary>
    public Amount? ChargedAmount { get; init; }

    public FailureCode? FailureCode { get; init; }

    public string? ProviderFailureCode { get; init; }

    public string? ProviderFailureMessage { get; init; }

    public ThreeDSecureResult ThreeDSecure { get; init; } = ThreeDSecureResult.NotAttempted;

    public SavedCard? SavedCard { get; init; }

    public IReadOnlyDictionary<string, string> ProviderData { get; init; } = new Dictionary<string, string>();

    public static PaymentOutcome Unknown(string? message = null) => new() { Status = PaymentStatus.Unknown, ProviderFailureMessage = message };

    public static PaymentOutcome Failure(FailureCode code, string? providerCode = null, string? providerMessage = null) =>
        new() { Status = PaymentStatus.Failed, FailureCode = code, ProviderFailureCode = providerCode, ProviderFailureMessage = providerMessage };
}

public sealed record PaymentQuery(string Reference, string? ProviderPaymentId, DateTimeOffset CreatedAt)
{
    /// <summary>What the adapter kept with the payment, such as a provider session ID.</summary>
    public IReadOnlyDictionary<string, string> ProviderData { get; init; } = new Dictionary<string, string>();

    /// <summary>The provider's customer of the payment's customer on this connection, for providers that list payments by customer.</summary>
    public string? ProviderCustomer { get; init; }
}

public abstract record PaymentLookup
{
    public sealed record Found(PaymentOutcome Outcome) : PaymentLookup;

    public sealed record NotFound : PaymentLookup;

    /// <summary>The provider could not be asked now; try again later.</summary>
    public sealed record Unavailable(string? Message = null) : PaymentLookup;
}

public sealed record RefundRequest
{
    public required Guid RefundId { get; init; }

    public required string Reference { get; init; }

    public required string PaymentReference { get; init; }

    public string? ProviderPaymentId { get; init; }

    public required Amount PaymentAmount { get; init; }

    public required DateTimeOffset PaymentSucceededAt { get; init; }

    public required Amount Amount { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyDictionary<string, string> PaymentProviderData { get; init; } = new Dictionary<string, string>();
}

public enum RefundStatus
{
    Succeeded,
    Pending,
    Failed,
    Unknown,
}

public enum RefundKind
{
    Refund,
    Void,
}

public sealed record RefundOutcome
{
    public required RefundStatus Status { get; init; }

    public RefundKind Kind { get; init; } = RefundKind.Refund;

    public string? ProviderRefundId { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureMessage { get; init; }

    public static RefundOutcome Unknown(string? message = null) => new() { Status = RefundStatus.Unknown, FailureMessage = message };
}

public sealed record RefundQuery(string Reference, string PaymentReference, string? ProviderRefundId, string? ProviderPaymentId, DateTimeOffset CreatedAt);

public abstract record RefundLookup
{
    public sealed record Found(RefundOutcome Outcome) : RefundLookup;

    public sealed record NotFound : RefundLookup;

    public sealed record Unavailable(string? Message = null) : RefundLookup;
}

public sealed record RemoveCardResult(bool Removed, string? Message = null);
