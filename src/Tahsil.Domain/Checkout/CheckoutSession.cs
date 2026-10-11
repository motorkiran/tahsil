using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;

namespace Tahsil.Domain.Checkout;

public enum CheckoutMode
{
    /// <summary>Sells one-time prices.</summary>
    Payment,

    /// <summary>Saves a card without selling anything.</summary>
    Setup,
}

public enum CheckoutStatus
{
    Open,
    Complete,
    Expired,
}

public enum BillingDetailsCollectionMode
{
    /// <summary>What the provider and invoices need.</summary>
    Auto,

    /// <summary>Always the full address and tax ID.</summary>
    Required,
}

/// <summary>An item the session sells.</summary>
public sealed record CheckoutItem(Guid PriceId, int Quantity);

/// <summary>A customer the session creates when its form is submitted, with the details given at creation (CHK-2).</summary>
public sealed record CheckoutCustomer(
    string? ExternalId, string? Email, string? Name, CustomerType Type, string? Phone, CustomerAddress? Address, string? Locale, KimlikSubscriber? KimlikSubscriber);

/// <summary>
/// A hosted page that sells something to one customer (DOM §6.1): what is bought, at the quote computed at creation,
/// on the terms of the page; open until it completes with a verified payment or expires (DOM-10).
/// </summary>
public sealed class CheckoutSession : AggregateRoot
{
    public const int UrlMaxLength = 2000;
    public const int ClientReferenceIdMaxLength = 200;
    public const int MaxItems = 20;
    public const string SessionIdPlaceholder = "{CHECKOUT_SESSION_ID}";

    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);

    private CheckoutSession()
    {
    }

    public Guid Id { get; private init; }

    /// <summary>SHA-256 of the page's URL token, which finds the session (CHK-31).</summary>
    public byte[] TokenHash { get; private init; } = [];

    /// <summary>The token, encrypted for the session, so the API can give the page's URL again while it is open.</summary>
    public byte[] Token { get; private init; } = [];

    public CheckoutMode Mode { get; private init; }

    public CheckoutStatus Status { get; private set; }

    /// <summary>Known at creation, or set when the customer submits the form.</summary>
    public Guid? CustomerId { get; private set; }

    public CheckoutCustomer? CustomerDetails { get; private init; }

    public string Currency { get; private init; } = string.Empty;

    public List<CheckoutItem> Items { get; private init; } = [];

    public bool AllowPromotionCodes { get; private init; }

    public BillingDetailsCollectionMode BillingDetailsCollection { get; private init; }

    public CheckoutQuote Quote { get; private set; } = CheckoutQuote.Empty;

    /// <summary>Overrides the customer's language on the page.</summary>
    public string? Locale { get; private init; }

    public string SuccessUrl { get; private init; } = string.Empty;

    public string? CancelUrl { get; private init; }

    public string? ClientReferenceId { get; private init; }

    public IReadOnlyDictionary<string, string> Metadata { get; private init; } = Common.Metadata.Empty;

    /// <summary>Payment attempts started, limited against card testing (CHK-21).</summary>
    public int PaymentAttempts { get; private set; }

    public DateTimeOffset ExpiresAt { get; private init; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? ExpiredAt { get; private set; }

    public Guid? InvoiceId { get; private set; }

    public Guid? PaymentMethodId { get; private set; }

    public Guid? PaymentId { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>A new open session; the caller resolved the items, the customer and the routing.</summary>
    /// <param name="mode">What the session does.</param>
    /// <param name="tokenHash">The hash of the page's token.</param>
    /// <param name="encryptToken">Encrypts the token for the session's ID.</param>
    /// <param name="customerId">An existing customer, or <see langword="null"/> with <paramref name="customerDetails"/>.</param>
    /// <param name="customerDetails">The customer to create on submission.</param>
    /// <param name="currency">The currency of the items, or of the card to save.</param>
    /// <param name="items">What is sold; empty in setup mode.</param>
    /// <param name="quote">The computed first invoice.</param>
    /// <param name="terms">The page's terms and the application's references.</param>
    /// <param name="now">The time.</param>
    public static Result<CheckoutSession> Create(
        CheckoutMode mode,
        byte[] tokenHash,
        Func<Guid, byte[]> encryptToken,
        Guid? customerId,
        CheckoutCustomer? customerDetails,
        string currency,
        IReadOnlyList<CheckoutItem> items,
        CheckoutQuote quote,
        CheckoutTerms terms,
        DateTimeOffset now)
    {
        if (terms.Lifetime < MinimumLifetime || terms.Lifetime > MaximumLifetime)
        {
            return CheckoutErrors.InvalidLifetime;
        }

        if (!IsValidUrl(terms.SuccessUrl, terms.AllowLoopbackHttp))
        {
            return CheckoutErrors.InvalidUrl("successUrl");
        }

        if (terms.CancelUrl is { } cancelUrl && !IsValidUrl(cancelUrl, terms.AllowLoopbackHttp))
        {
            return CheckoutErrors.InvalidUrl("cancelUrl");
        }

        if (!Text.FitsOptional(terms.ClientReferenceId, ClientReferenceIdMaxLength))
        {
            return CheckoutErrors.Invalid("clientReferenceId", "The client reference ID is at most 200 characters.");
        }

        var metadata = Common.Metadata.Validate(terms.Metadata);
        if (metadata.IsFailure)
        {
            return metadata.Error;
        }

        var id = Guid.CreateVersion7(now);
        return new CheckoutSession
        {
            Id = id,
            TokenHash = tokenHash,
            Token = encryptToken(id),
            Mode = mode,
            Status = CheckoutStatus.Open,
            CustomerId = customerId,
            CustomerDetails = customerDetails,
            Currency = currency,
            Items = [.. items],
            AllowPromotionCodes = terms.AllowPromotionCodes,
            BillingDetailsCollection = terms.BillingDetailsCollection,
            Quote = quote,
            Locale = terms.Locale,
            SuccessUrl = terms.SuccessUrl,
            CancelUrl = terms.CancelUrl,
            ClientReferenceId = Text.Normalize(terms.ClientReferenceId),
            Metadata = metadata.Value,
            ExpiresAt = now + terms.Lifetime,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Ends an open session that was not paid (DOM-10); the caller reconciled any payment in flight first.</summary>
    public Result Expire(DateTimeOffset now)
    {
        if (Status != CheckoutStatus.Open)
        {
            return CheckoutErrors.NotOpen;
        }

        Status = CheckoutStatus.Expired;
        ExpiredAt = now;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Absolute HTTPS without credentials or a fragment, at most <see cref="UrlMaxLength"/> characters; plain HTTP for
    /// this machine only where allowed (CHK-5).
    /// </summary>
    public static bool IsValidUrl(string? url, bool allowLoopbackHttp) =>
        url is { Length: > 0 and <= UrlMaxLength }
        && Uri.TryCreate(url.Replace(SessionIdPlaceholder, "cs", StringComparison.Ordinal), UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && (uri.Scheme == Uri.UriSchemeHttps || (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}

/// <summary>The terms of a session's page and the application's references, validated by <see cref="CheckoutSession.Create"/>.</summary>
public sealed record CheckoutTerms(
    string SuccessUrl,
    string? CancelUrl,
    TimeSpan Lifetime,
    bool AllowPromotionCodes,
    BillingDetailsCollectionMode BillingDetailsCollection,
    string? Locale,
    string? ClientReferenceId,
    IReadOnlyDictionary<string, string>? Metadata,
    bool AllowLoopbackHttp);
