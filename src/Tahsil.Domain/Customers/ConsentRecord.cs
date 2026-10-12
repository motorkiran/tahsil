using System.Net;
using Tahsil.Domain.Common;

namespace Tahsil.Domain.Customers;

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

    /// <summary>A plan change confirmed in the portal, with the amounts shown (PRT-7).</summary>
    PlanChange,
    Custom,
}

/// <summary>A legal document as the customer was shown it.</summary>
public sealed record DocumentShown(string Key, string Version, string Html);

/// <summary>
/// The append-only evidence of what a customer was shown and accepted, such as the consent to future charges of a
/// saved card (CUS-11). Never updated or deleted before its retention ends, also when the customer is anonymized (DOM-20).
/// </summary>
public sealed class ConsentRecord
{
    public const int TextMaxLength = 1000;
    public const int UserAgentMaxLength = 512;

    private ConsentRecord()
    {
    }

    public Guid Id { get; private init; }

    public Guid CustomerId { get; private init; }

    public ConsentType Type { get; private init; }

    public string? DocumentKey { get; private init; }

    public string? DocumentVersion { get; private init; }

    public byte[]? DocumentHash { get; private init; }

    public byte[]? RenderedDocument { get; private init; }

    /// <summary>The exact checkbox or button text.</summary>
    public string TextShown { get; private init; } = string.Empty;

    /// <summary>What the customer saw with it: amounts, the billing period, the related invoice or payment.</summary>
    public IReadOnlyDictionary<string, string> Context { get; private init; } = Metadata.Empty;

    public Guid? CheckoutSessionId { get; private init; }

    public Guid? PortalSessionId { get; private init; }

    public Guid? SubscriptionId { get; private init; }

    public IPAddress? IpAddress { get; private init; }

    public string? UserAgent { get; private init; }

    public DateTimeOffset AcceptedAt { get; private init; }

    /// <summary>
    /// The acceptance of a legal document (CHK-16): the rendered HTML as the customer saw it, compressed, with its hash
    /// and the exact checkbox text.
    /// </summary>
    public static ConsentRecord ForDocument(
        Guid customerId,
        ConsentType type,
        DocumentShown document,
        string textShown,
        IReadOnlyDictionary<string, string> context,
        Guid? checkoutSessionId,
        IPAddress? ipAddress,
        string? userAgent,
        DateTimeOffset acceptedAt)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(document.Html);
        using var compressed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize))
        {
            gzip.Write(bytes);
        }

        var record = Create(customerId, type, textShown, context, ipAddress, userAgent, acceptedAt, checkoutSessionId);
        return new ConsentRecord
        {
            Id = record.Id,
            CustomerId = record.CustomerId,
            Type = record.Type,
            DocumentKey = document.Key,
            DocumentVersion = document.Version,
            DocumentHash = System.Security.Cryptography.SHA256.HashData(bytes),
            RenderedDocument = compressed.ToArray(),
            TextShown = record.TextShown,
            Context = record.Context,
            CheckoutSessionId = checkoutSessionId,
            IpAddress = record.IpAddress,
            UserAgent = record.UserAgent,
            AcceptedAt = record.AcceptedAt,
        };
    }

    /// <summary>The rendered document of a document consent, as the customer saw it.</summary>
    public string? ReadDocument()
    {
        if (RenderedDocument is null)
        {
            return null;
        }

        using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(RenderedDocument), System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>What a customer confirmed in the portal: the button or text as shown, and what it was about.</summary>
    public static ConsentRecord ForPortal(
        Guid customerId,
        ConsentType type,
        string textShown,
        IReadOnlyDictionary<string, string> context,
        Guid portalSessionId,
        Guid? subscriptionId,
        IPAddress? ipAddress,
        string? userAgent,
        DateTimeOffset acceptedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(textShown);
        return new ConsentRecord
        {
            Id = Guid.CreateVersion7(acceptedAt),
            CustomerId = customerId,
            Type = type,
            TextShown = textShown,
            Context = context,
            PortalSessionId = portalSessionId,
            SubscriptionId = subscriptionId,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            AcceptedAt = acceptedAt,
        };
    }

    public static ConsentRecord Create(
        Guid customerId,
        ConsentType type,
        string textShown,
        IReadOnlyDictionary<string, string> context,
        IPAddress? ipAddress,
        string? userAgent,
        DateTimeOffset acceptedAt,
        Guid? checkoutSessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(textShown);
        return new ConsentRecord
        {
            Id = Guid.CreateVersion7(acceptedAt),
            CustomerId = customerId,
            Type = type,
            TextShown = textShown.Length > TextMaxLength ? textShown[..TextMaxLength] : textShown,
            Context = context,
            CheckoutSessionId = checkoutSessionId,
            IpAddress = ipAddress,
            UserAgent = userAgent is { Length: > UserAgentMaxLength } ? userAgent[..UserAgentMaxLength] : userAgent,
            AcceptedAt = acceptedAt,
        };
    }
}
