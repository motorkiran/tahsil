using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tahsil.Contracts;

/// <summary>
/// What an installation sells and how it is wired, declared so it can live in version control and be applied the same
/// way to every environment. Items are matched by key; applying creates what is missing and updates what differs, and
/// never deletes: retire an item with <c>isArchived</c>, or <c>enabled: false</c> for connections and endpoints.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProvisioningDocument
{
    public const string SchemaUrl = "https://raw.githubusercontent.com/motorkiran/tahsil/main/docs/schemas/provisioning.schema.json";

    /// <summary>The JSON Schema editors use for completion and validation.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public IReadOnlyList<ProvisionedTaxRate>? TaxRates { get; init; }

    public IReadOnlyList<ProvisionedMeter>? Meters { get; init; }

    public IReadOnlyList<ProvisionedProduct>? Products { get; init; }

    public IReadOnlyList<ProvisionedConnection>? ProviderConnections { get; init; }

    public IReadOnlyList<ProvisionedWebhookEndpoint>? WebhookEndpoints { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProvisionedTaxRate
{
    [Required]
    public required string Key { get; init; }

    [Required]
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Fixed once created.</summary>
    public required decimal Percentage { get; init; }

    /// <summary>Fixed once created.</summary>
    public required bool Inclusive { get; init; }

    public string? Country { get; init; }

    public bool IsArchived { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProvisionedMeter
{
    [Required]
    public required string Key { get; init; }

    [Required]
    public required string Name { get; init; }

    /// <summary>Fixed once created.</summary>
    public required MeterAggregation Aggregation { get; init; }

    /// <summary>Fixed once created.</summary>
    public string? UniqueProperty { get; init; }

    public bool IsArchived { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProvisionedProduct
{
    [Required]
    public required string Key { get; init; }

    [Required]
    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? KimlikPlan { get; init; }

    public bool IsArchived { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public IReadOnlyList<ProvisionedPrice>? Prices { get; init; }
}

/// <summary>
/// A price of the product. Only the name, trial, archive flag and metadata may change; a new amount is a new price with a
/// new key.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProvisionedPrice
{
    [Required]
    public required string Key { get; init; }

    public string? Name { get; init; }

    [Required]
    public required string Currency { get; init; }

    public required PriceType Type { get; init; }

    public PriceInterval? Interval { get; init; }

    public int? IntervalCount { get; init; }

    public UsageType? UsageType { get; init; }

    public BillingScheme BillingScheme { get; init; } = BillingScheme.PerUnit;

    public long? UnitAmount { get; init; }

    public decimal? UnitAmountDecimal { get; init; }

    public int? PackageSize { get; init; }

    public PackageRounding? PackageRounding { get; init; }

    public TiersMode? TiersMode { get; init; }

    public IReadOnlyList<PriceTier>? Tiers { get; init; }

    /// <summary>The meter's key, for metered prices.</summary>
    public string? Meter { get; init; }

    /// <summary>The tax rate's key.</summary>
    public string? TaxRate { get; init; }

    public int? TrialDays { get; init; }

    public bool IsArchived { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// A payment provider connection and the currencies it serves. In a startup file, credentials are configuration
/// references such as <c>${Iyzico:ApiKey}</c>, so secrets never live in version control.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProvisionedConnection
{
    [Required]
    public required string Key { get; init; }

    /// <summary>Fixed once created.</summary>
    [Required]
    public required string Provider { get; init; }

    [Required]
    public required string Name { get; init; }

    /// <summary>Fixed once created.</summary>
    public required ConnectionEnvironment Environment { get; init; }

    /// <summary>The currencies routed to this connection; currencies not listed keep their routing.</summary>
    public IReadOnlyList<string>? Currencies { get; init; }

    /// <summary>The provider's credential fields; omitted ones keep their values.</summary>
    public IReadOnlyDictionary<string, string>? Credentials { get; init; }

    /// <summary>The provider's setting fields, as strings, numbers or booleans; omitted ones keep their values.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Settings { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>A webhook endpoint. In a startup file, its secret is a configuration reference such as <c>${Tahsil:AppWebhookSecret}</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProvisionedWebhookEndpoint
{
    [Required]
    public required string Key { get; init; }

    [Required]
    public required string Url { get; init; }

    public string? Description { get; init; }

    /// <summary>The event types it receives; none means all.</summary>
    public IReadOnlyList<string>? EventTypes { get; init; }

    /// <summary>The signing secret; a new one replaces the current secret, which keeps signing for the rotation overlap.</summary>
    public string? Secret { get; init; }

    public bool Enabled { get; init; } = true;
}

public enum ProvisioningItemResult
{
    Created,
    Updated,
    Unchanged,
}

public sealed record ProvisionedItem(string Path, string Key, ProvisioningItemResult Result);

/// <summary>What applying a document did, or would do in a dry run.</summary>
public sealed record ProvisioningResult(int Created, int Updated, int Unchanged, IReadOnlyList<ProvisionedItem> Items);
