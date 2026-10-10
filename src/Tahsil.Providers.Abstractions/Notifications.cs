using System.Net;

namespace Tahsil.Providers.Abstractions;

/// <summary>A request a provider sent to the connection's notification URL, as it arrived.</summary>
public sealed record IncomingNotification
{
    public required string Method { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public required byte[] Body { get; init; }

    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();

    public IPAddress? RemoteAddress { get; init; }
}

/// <summary>How to answer the provider, such as PayTR's plain <c>OK</c>.</summary>
public sealed record NotificationResponse(int Status, string ContentType, string Body)
{
    public static NotificationResponse Ok { get; } = new(200, "text/plain", string.Empty);
}

/// <summary>What a notification says has happened.</summary>
public abstract record NotificationUpdate
{
    public sealed record Payment(string Reference, PaymentOutcome Outcome) : NotificationUpdate;

    public sealed record Refund(string Reference, RefundOutcome Outcome) : NotificationUpdate;

    /// <summary>The notification says too little to trust; Tahsil asks the provider about the payment.</summary>
    public sealed record CheckPayment(string Reference) : NotificationUpdate;

    public sealed record CheckRefund(string Reference) : NotificationUpdate;

    /// <summary>The provider's card updater changed a saved card's expiry or last four digits.</summary>
    public sealed record CardUpdated(string Token, SavedCard Details) : NotificationUpdate;
}

public sealed record NotificationResult
{
    /// <summary>Whether the notification's signature or origin was verified; unverified notifications change nothing.</summary>
    public required bool Verified { get; init; }

    /// <summary>The provider's ID of the event, to apply each notification once.</summary>
    public string? ProviderEventId { get; init; }

    public required NotificationResponse Response { get; init; }

    public IReadOnlyList<NotificationUpdate> Updates { get; init; } = [];

    public static NotificationResult Rejected(NotificationResponse response) => new() { Verified = false, Response = response };
}
