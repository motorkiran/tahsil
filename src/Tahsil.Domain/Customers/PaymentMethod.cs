using Tahsil.Domain.Common;

namespace Tahsil.Domain.Customers;

public enum PaymentMethodStatus
{
    Active,
    Expired,
    Removed,
}

/// <summary>What may be shown about a saved card; never card data (ADP-2).</summary>
public sealed record CardDetails(string Brand, string Funding, string? Bin, string Last4, int? ExpMonth, int? ExpYear, string? Issuer, string? CardFamily, string? Country)
{
    private static readonly HashSet<string> Brands = ["visa", "mastercard", "amex", "troy", "unionpay", "discover", "jcb"];
    private static readonly HashSet<string> Fundings = ["credit", "debit", "prepaid"];

    /// <summary>Brings a provider's card details into the stored form: known brands and fundings, bounded lengths.</summary>
    public static CardDetails Normalize(
        string? brand, string? funding, string? bin, string? last4, int? expMonth, int? expYear, string? issuer, string? cardFamily, string? country)
    {
        var normalizedBrand = brand?.Trim().ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal) switch
        {
            "americanexpress" => "amex",
            "master" or "mastercard" => "mastercard",
            var known when known is not null && Brands.Contains(known) => known,
            _ => "other",
        };
        var normalizedFunding = funding?.Trim().ToLowerInvariant() is { } value && Fundings.Contains(value) ? value : "unknown";
        return new CardDetails(
            normalizedBrand,
            normalizedFunding,
            bin is { Length: >= 6 and <= 8 } && bin.All(char.IsAsciiDigit) ? bin : null,
            last4 is { Length: 4 } && last4.All(char.IsAsciiDigit) ? last4 : "0000",
            expMonth is >= 1 and <= 12 ? expMonth : null,
            expYear is >= 2000 and <= 2200 ? expYear : null,
            Truncate(issuer, 100),
            Truncate(cardFamily, 50),
            country is { Length: 2 } ? country.ToUpperInvariant() : null);
    }

    private static string? Truncate(string? value, int length) => Text.Normalize(value) is { } text ? text[..Math.Min(text.Length, length)] : null;
}

/// <summary>
/// A card a provider saved for a customer (CUS-10), charged only through the connection that saved it and only with
/// the customer's recorded consent (CUS-11). Its token is encrypted; its fingerprint keeps one record per token (CUS-12).
/// </summary>
public sealed class PaymentMethod : AggregateRoot
{
    private PaymentMethod()
    {
    }

    public Guid Id { get; private init; }

    public Guid CustomerId { get; private init; }

    public Guid ConnectionId { get; private init; }

    public PaymentMethodStatus Status { get; private set; }

    /// <summary>The provider's token fields as encrypted JSON, bound to the payment method.</summary>
    public byte[] Token { get; private set; } = [];

    /// <summary>The SHA-256 of the canonical token JSON.</summary>
    public byte[] TokenFingerprint { get; private init; } = [];

    public string Brand { get; private set; } = "other";

    public string Funding { get; private set; } = "unknown";

    public string? Bin { get; private set; }

    public string Last4 { get; private set; } = string.Empty;

    public int? ExpMonth { get; private set; }

    public int? ExpYear { get; private set; }

    /// <summary>The end of the expiry month in the billing time zone.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public string? Issuer { get; private set; }

    public string? CardFamily { get; private set; }

    public string? Country { get; private set; }

    public Guid ConsentRecordId { get; private set; }

    public DateTimeOffset? RemovedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsChargeable => Status == PaymentMethodStatus.Active;

    public static PaymentMethod Create(
        Guid customerId,
        Guid connectionId,
        CardDetails card,
        DateTimeOffset? expiresAt,
        Func<Guid, byte[]> encryptToken,
        byte[] tokenFingerprint,
        Guid consentRecordId,
        DateTimeOffset now)
    {
        var id = Guid.CreateVersion7(now);
        var method = new PaymentMethod
        {
            Id = id,
            CustomerId = customerId,
            ConnectionId = connectionId,
            Status = PaymentMethodStatus.Active,
            Token = encryptToken(id),
            TokenFingerprint = tokenFingerprint,
            ConsentRecordId = consentRecordId,
            CreatedAt = now,
        };
        method.Show(card, expiresAt, now);
        return method;
    }

    /// <summary>The provider returned this token again: its details are refreshed and it is active again (CUS-12).</summary>
    public void Refresh(CardDetails card, DateTimeOffset? expiresAt, Guid consentRecordId, DateTimeOffset now)
    {
        Status = PaymentMethodStatus.Active;
        RemovedAt = null;
        ConsentRecordId = consentRecordId;
        Show(card, expiresAt, now);
    }

    /// <summary>Removed by the customer, an operator or the API (CUS-16); it is never charged again.</summary>
    public bool Remove(DateTimeOffset now)
    {
        if (Status == PaymentMethodStatus.Removed)
        {
            return false;
        }

        Status = PaymentMethodStatus.Removed;
        RemovedAt = now;
        UpdatedAt = now;
        return true;
    }

    /// <summary>The expiry worker passed the card's expiry (CUS-14).</summary>
    public void Expire(DateTimeOffset now)
    {
        if (Status == PaymentMethodStatus.Active && ExpiresAt <= now)
        {
            Status = PaymentMethodStatus.Expired;
            UpdatedAt = now;
        }
    }

    private void Show(CardDetails card, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        Brand = card.Brand;
        Funding = card.Funding;
        Bin = card.Bin;
        Last4 = card.Last4;
        ExpMonth = card.ExpMonth;
        ExpYear = card.ExpYear;
        ExpiresAt = expiresAt;
        Issuer = card.Issuer;
        CardFamily = card.CardFamily;
        Country = card.Country;
        UpdatedAt = now;
    }
}
