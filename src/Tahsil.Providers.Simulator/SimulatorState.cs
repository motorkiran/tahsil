using System.Text.Json;
using System.Text.Json.Serialization;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Simulator;

internal enum SimulatedPaymentStatus
{
    AwaitingCard,
    AwaitingAuthentication,
    Succeeded,
    Failed,
    Pending,
    Abandoned,
}

internal sealed record PaymentState
{
    public required SimulatedPaymentStatus Status { get; init; }

    public required long Amount { get; init; }

    public required string Currency { get; init; }

    public required int Exponent { get; init; }

    public PaymentPurpose Purpose { get; init; } = PaymentPurpose.Charge;

    public bool SaveCard { get; init; }

    /// <summary>Whether the card page asks the customer before saving the card.</summary>
    public bool AskToSave { get; init; }

    public string? ReturnUrl { get; init; }

    public string? CancelUrl { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The test card: a public test number, never a real one (SIM-4).</summary>
    public string? Card { get; init; }

    public int? ExpMonth { get; init; }

    public int? ExpYear { get; init; }

    public FailureCode? FailureCode { get; init; }

    /// <summary>Whether the customer pressed Cancel, which sends them to the cancel URL.</summary>
    public bool Cancelled { get; init; }

    public ThreeDSecureResult ThreeDSecure { get; init; }

    public string? SavedCardToken { get; init; }

    public DateTimeOffset? SucceededAt { get; init; }

    /// <summary>What succeeded and pending refunds took back.</summary>
    public long Refunded { get; init; }
}

internal sealed record RefundState
{
    public required RefundStatus Status { get; init; }

    public required RefundKind Kind { get; init; }

    public required long Amount { get; init; }

    public required string PaymentReference { get; init; }

    public DateTimeOffset? SettlesAt { get; init; }

    public string? FailureCode { get; init; }
}

internal sealed record CardState
{
    public required string Card { get; init; }

    public int ExpMonth { get; init; }

    public int ExpYear { get; init; }

    public bool Removed { get; init; }

    /// <summary>A behavior set through the test API (SIM-8), in place of the test card's.</summary>
    public string? Behavior { get; init; }

    /// <summary>Saved-card charges that succeeded.</summary>
    public int Charges { get; init; }
}

internal static class SimulatorJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write<T>(T state) => JsonSerializer.Serialize(state, Options);

    public static T Read<T>(SimulatorEntry entry) =>
        JsonSerializer.Deserialize<T>(entry.State, Options) ?? throw new InvalidDataException($"The simulator record {entry.Id} has no state.");
}
