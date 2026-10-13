namespace Tahsil.Providers.Abstractions;

/// <summary>What a provider is and can do; the engine decides every flow from it, never from the provider's name (ADP-1).</summary>
public sealed record ProviderDescriptor
{
    /// <summary>The stable key of the provider, such as <c>iyzico</c>.</summary>
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Secret fields, write-only for operators.</summary>
    public required IReadOnlyList<FieldDefinition> Credentials { get; init; }

    /// <summary>Non-secret fields.</summary>
    public required IReadOnlyList<FieldDefinition> Settings { get; init; }

    /// <summary>ISO 4217 codes the provider charges in.</summary>
    public required IReadOnlySet<string> Currencies { get; init; }

    /// <summary>The least amount the provider charges, per currency, in minor units.</summary>
    public required IReadOnlyDictionary<string, long> MinimumAmounts { get; init; }

    public required ProviderCapabilities Capabilities { get; init; }

    public required CustomerDataRequirements CustomerData { get; init; }

    /// <summary>The sources the hosted page needs for the provider's fields, per environment.</summary>
    public required IReadOnlyDictionary<ProviderEnvironment, ContentSecuritySources> ContentSecurity { get; init; }

    /// <summary>The environments a connection may use, such as a sandbox and production.</summary>
    public IReadOnlySet<ProviderEnvironment> Environments { get; init; } = new HashSet<ProviderEnvironment> { ProviderEnvironment.Sandbox, ProviderEnvironment.Production };
}

public enum ProviderEnvironment
{
    Sandbox,
    Production,
}

public enum FieldType
{
    Text,
    Secret,
    Number,
    Boolean,
    Choice,
    Url,
}

/// <summary>A credential or setting an operator enters for a connection.</summary>
public sealed record FieldDefinition
{
    /// <summary>The key in the connection's credentials or settings, such as <c>apiKey</c>.</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }

    public FieldType Type { get; init; } = FieldType.Text;

    public bool Required { get; init; } = true;

    public string? Description { get; init; }

    /// <summary>The values of a <see cref="FieldType.Choice"/> field.</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    public string? Default { get; init; }

    /// <summary>A regular expression a text value must match.</summary>
    public string? Pattern { get; init; }
}

public enum CardEntry
{
    /// <summary>To the provider's own page.</summary>
    Redirect,

    /// <summary>The provider's script or iframe inside Tahsil's page.</summary>
    Embedded,

    /// <summary>Card fields on Tahsil's page posting straight to the provider (PCI DSS SAQ A-EP; opt-in, ADP-13).</summary>
    DirectForm,
}

public enum CardSaving
{
    None,
    DuringPayment,

    /// <summary>The provider's page asks the customer whether to save the card.</summary>
    DuringPaymentWhenCustomerAgrees,
}

public enum CardSetup
{
    /// <summary>Saves a card without charging it.</summary>
    Native,

    /// <summary>A small charge that is voided or refunded at once (ADP-15).</summary>
    VerificationCharge,

    None,
}

public enum RefundSupport
{
    Full,
    Partial,

    /// <summary>Full refunds (or a void) on the day of the payment, partial ones from the next day.</summary>
    PartialAfterSettlementDay,
}

public enum VoidSupport
{
    None,
    SameDayFullCancel,
}

public enum ReferenceSearch
{
    Any,
    SuccessfulOnly,
    None,
}

public enum NotificationSupport
{
    /// <summary>Notifications carry a signature the adapter verifies.</summary>
    Verifiable,

    /// <summary>Notifications are hints the engine verifies by asking the provider.</summary>
    HintsOnly,

    None,
}

/// <summary>What a provider can do (ADP-1).</summary>
public sealed record ProviderCapabilities
{
    public required IReadOnlySet<CardEntry> Entry { get; init; }

    public required CardSaving SavesCards { get; init; }

    public required CardSetup Setup { get; init; }

    public required bool ChargesSavedCards { get; init; }

    /// <summary>Whether charging saved cards needs a permission the merchant asks the provider for.</summary>
    public bool ChargingSavedCardsRequiresMerchantPermission { get; init; }

    public required RefundSupport Refunds { get; init; }

    public bool AsynchronousRefunds { get; init; }

    public required VoidSupport Voids { get; init; }

    public required ReferenceSearch FindByReference { get; init; }

    public required NotificationSupport Notifications { get; init; }

    /// <summary>Whether the provider itself rejects or replays a repeated reference.</summary>
    public bool IdempotentCharges { get; init; }

    /// <summary>How many charges may run at once on one saved card; <see langword="null"/> for no limit.</summary>
    public int? ConcurrentChargesPerCard { get; init; }

    public bool RequiresStaticIp { get; init; }
}

public enum CustomerField
{
    Name,
    Email,
    Phone,
    IdentityNumber,
    Address,
    City,
    Country,
    IpAddress,
}

/// <summary>A customer field a provider needs, and what the adapter sends when the customer has none.</summary>
/// <param name="Field">The field.</param>
/// <param name="Fallback">The documented fallback, or <see langword="null"/> when the field must be collected.</param>
public sealed record CustomerFieldRequirement(CustomerField Field, string? Fallback);

/// <summary>The customer data a provider needs, with the customer present and for merchant-initiated charges.</summary>
public sealed record CustomerDataRequirements(IReadOnlyList<CustomerFieldRequirement> CustomerPresent, IReadOnlyList<CustomerFieldRequirement> MerchantInitiated)
{
    public static CustomerDataRequirements None { get; } = new([], []);
}

/// <summary>Sources a hosted page adds to its content security policy for the provider's fields.</summary>
public sealed record ContentSecuritySources
{
    public static ContentSecuritySources None { get; } = new();

    public IReadOnlyList<string> Scripts { get; init; } = [];

    public IReadOnlyList<string> Frames { get; init; } = [];

    public IReadOnlyList<string> Connections { get; init; } = [];

    public IReadOnlyList<string> Images { get; init; } = [];

    public IReadOnlyList<string> FormActions { get; init; } = [];

    /// <summary>Origins whose frames load Tahsil's return pages, such as a provider that sends the customer back inside its iframe.</summary>
    public IReadOnlyList<string> ReturnFrameAncestors { get; init; } = [];
}
