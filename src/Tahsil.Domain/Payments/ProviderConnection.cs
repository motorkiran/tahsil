using Tahsil.Domain.Catalog;
using Tahsil.Domain.Common;

namespace Tahsil.Domain.Payments;

public enum ConnectionEnvironment
{
    Sandbox,
    Production,
}

public enum ConnectionTestStatus
{
    Succeeded,
    Failed,
}

/// <summary>
/// An account at a payment provider that Tahsil charges through, with its encrypted credentials. A saved card is always
/// charged through the connection that saved it (PAY-5); a connection with payment methods or payments is disabled,
/// never deleted (DOM-9).
/// </summary>
public sealed class ProviderConnection : AggregateRoot
{
    public const int NameMaxLength = 100;
    public const int ProviderMaxLength = 32;
    public const int TestMessageMaxLength = 500;

    private ProviderConnection()
    {
    }

    public Guid Id { get; private init; }

    public string Key { get; private init; } = string.Empty;

    /// <summary>The adapter's key, such as <c>iyzico</c>.</summary>
    public string Provider { get; private init; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public ConnectionEnvironment Environment { get; private init; }

    /// <summary>The credentials as encrypted JSON, bound to the connection.</summary>
    public byte[] Credentials { get; private set; } = [];

    public IReadOnlyDictionary<string, string> Settings { get; private set; } = new Dictionary<string, string>();

    public bool IsEnabled { get; private set; }

    /// <summary>Since when an operator has paused new checkouts on the connection, such as during card testing (CHK-22).</summary>
    public DateTimeOffset? CheckoutsPausedAt { get; private set; }

    public DateTimeOffset? LastTestedAt { get; private set; }

    public ConnectionTestStatus? LastTestStatus { get; private set; }

    public string? LastTestMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <param name="key">The connection's key.</param>
    /// <param name="provider">The adapter's key.</param>
    /// <param name="name">The display name.</param>
    /// <param name="environment">The provider's environment.</param>
    /// <param name="encryptCredentials">Encrypts the credentials for the new connection's ID.</param>
    /// <param name="settings">The validated settings.</param>
    /// <param name="isEnabled">Whether the connection takes payments.</param>
    /// <param name="now">The time.</param>
    public static Result<ProviderConnection> Create(
        string key,
        string provider,
        string name,
        ConnectionEnvironment environment,
        Func<Guid, byte[]> encryptCredentials,
        IReadOnlyDictionary<string, string> settings,
        bool isEnabled,
        DateTimeOffset now)
    {
        // "routing" would be shadowed by the routing endpoint, /provider-connections/routing.
        if (!CatalogKey.IsValid(key) || key == "routing")
        {
            return ConnectionErrors.InvalidKey;
        }

        if (!Text.FitsRequired(name, NameMaxLength))
        {
            return ConnectionErrors.InvalidName;
        }

        var id = Guid.CreateVersion7(now);
        return new ProviderConnection
        {
            Id = id,
            Key = key,
            Provider = provider,
            Name = name.Trim(),
            Environment = environment,
            Credentials = encryptCredentials(id),
            Settings = settings,
            IsEnabled = isEnabled,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public Result Update(string name, IReadOnlyDictionary<string, string> settings, bool isEnabled, DateTimeOffset now)
    {
        if (!Text.FitsRequired(name, NameMaxLength))
        {
            return ConnectionErrors.InvalidName;
        }

        Name = name.Trim();
        Settings = settings;
        IsEnabled = isEnabled;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Stops or resumes new checkout payments on the connection (CHK-22); saved cards keep being charged.</summary>
    public void PauseCheckouts(bool paused, DateTimeOffset now)
    {
        if (paused == CheckoutsPausedAt is not null)
        {
            return;
        }

        CheckoutsPausedAt = paused ? now : null;
        UpdatedAt = now;
    }

    public void ReplaceCredentials(Func<Guid, byte[]> encryptCredentials, DateTimeOffset now)
    {
        Credentials = encryptCredentials(Id);
        UpdatedAt = now;
    }

    public void RecordTest(ConnectionTestStatus status, string? message, DateTimeOffset now)
    {
        LastTestedAt = now;
        LastTestStatus = status;
        var text = Text.Normalize(message);
        LastTestMessage = text is { Length: > TestMessageMaxLength } ? text[..TestMessageMaxLength] : text;
    }
}

/// <summary>Which connection serves a currency for customer-present payments (PAY-4); one per currency.</summary>
public sealed class ConnectionCurrency
{
    private ConnectionCurrency()
    {
    }

    public string Currency { get; private init; } = string.Empty;

    public Guid ConnectionId { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static ConnectionCurrency Create(string currency, Guid connectionId, DateTimeOffset now) =>
        new() { Currency = currency, ConnectionId = connectionId, CreatedAt = now };
}

/// <summary>A provider's own customer for one of Tahsil's customers, such as Stripe's <c>cus_…</c>, kept per connection.</summary>
public sealed class ProviderCustomer
{
    public const int ReferenceMaxLength = 200;

    private ProviderCustomer()
    {
    }

    public Guid CustomerId { get; private init; }

    public Guid ConnectionId { get; private init; }

    public string Reference { get; private set; } = string.Empty;

    /// <summary>Non-secret adapter data.</summary>
    public IReadOnlyDictionary<string, string> Data { get; private set; } = new Dictionary<string, string>();

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ProviderCustomer Create(Guid customerId, Guid connectionId, string reference, DateTimeOffset now) => new()
    {
        CustomerId = customerId,
        ConnectionId = connectionId,
        Reference = reference,
        CreatedAt = now,
        UpdatedAt = now,
    };
}
