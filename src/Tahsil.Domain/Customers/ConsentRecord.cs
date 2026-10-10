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
    Custom,
}

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

    public static ConsentRecord Create(
        Guid customerId, ConsentType type, string textShown, IReadOnlyDictionary<string, string> context, IPAddress? ipAddress, string? userAgent, DateTimeOffset acceptedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(textShown);
        return new ConsentRecord
        {
            Id = Guid.CreateVersion7(acceptedAt),
            CustomerId = customerId,
            Type = type,
            TextShown = textShown.Length > TextMaxLength ? textShown[..TextMaxLength] : textShown,
            Context = context,
            IpAddress = ipAddress,
            UserAgent = userAgent is { Length: > UserAgentMaxLength } ? userAgent[..UserAgentMaxLength] : userAgent,
            AcceptedAt = acceptedAt,
        };
    }
}
