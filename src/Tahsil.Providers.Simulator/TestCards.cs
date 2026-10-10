using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Simulator;

public enum CardBehaviorKind
{
    Succeed,
    Fail,

    /// <summary>The charge is made, but the answer never arrives: the adapter says <c>Unknown</c>.</summary>
    TimeoutAfterCharge,

    /// <summary>The charge never reaches the simulated provider, and the adapter says <c>Unknown</c>.</summary>
    TimeoutBeforeCharge,
    Pending,
}

/// <summary>How a card answers a charge: succeed, fail with a code, time out or stay pending.</summary>
public readonly record struct CardBehavior(CardBehaviorKind Kind, FailureCode? FailureCode = null)
{
    public static CardBehavior Succeed { get; } = new(CardBehaviorKind.Succeed);

    public static CardBehavior Fail(FailureCode code) => new(CardBehaviorKind.Fail, code);

    /// <summary>
    /// Reads a behavior as the test API names it (SIM-8): <c>succeed</c>, <c>timeout_after_charge</c>,
    /// <c>timeout_before_charge</c>, <c>pending</c>, or a failure code such as <c>insufficient_funds</c>.
    /// </summary>
    public static CardBehavior? Parse(string? value) => value switch
    {
        "succeed" => Succeed,
        "timeout_after_charge" => new(CardBehaviorKind.TimeoutAfterCharge),
        "timeout_before_charge" => new(CardBehaviorKind.TimeoutBeforeCharge),
        "pending" => new(CardBehaviorKind.Pending),
        _ => Enum.GetValues<FailureCode>().Cast<FailureCode?>().FirstOrDefault(code => SnakeCase(code!.Value) == value) is { } code ? Fail(code) : null,
    };

    public override string ToString() => Kind switch
    {
        CardBehaviorKind.Fail => SnakeCase(FailureCode!.Value),
        CardBehaviorKind.TimeoutAfterCharge => "timeout_after_charge",
        CardBehaviorKind.TimeoutBeforeCharge => "timeout_before_charge",
        CardBehaviorKind.Pending => "pending",
        _ => "succeed",
    };

    private static string SnakeCase(FailureCode code) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(code.ToString());
}

public enum RefundBehavior
{
    Succeed,

    /// <summary>Pending until the billing clock passes one hour after the refund.</summary>
    PendingForAnHour,
    Fail,
}

/// <summary>A test card of SIM §3: how it answers with the customer present and when charged later.</summary>
/// <param name="Number">The card number, digits only.</param>
/// <param name="Brand">The card scheme.</param>
/// <param name="Funding"><c>credit</c> or <c>debit</c>.</param>
/// <param name="Present">The answer with the customer present.</param>
/// <param name="Saved">The answer when charged later as a saved card.</param>
/// <param name="ThreeDSecure">Whether the bank asks the customer to approve the payment.</param>
/// <param name="Refunds">How refunds of its payments behave.</param>
/// <param name="FailsAfterCharges">Saved-card charges that succeed before it fails with <c>insufficient_funds</c>.</param>
public sealed record TestCard(
    string Number,
    string Brand,
    string Funding,
    CardBehavior Present,
    CardBehavior Saved,
    bool ThreeDSecure = false,
    RefundBehavior Refunds = RefundBehavior.Succeed,
    int? FailsAfterCharges = null)
{
    public string Last4 => Number[^4..];

    public string Bin => Number[..6];
}

/// <summary>The only card numbers the simulator accepts (SIM-4).</summary>
public static class TestCards
{
    public static IReadOnlyList<TestCard> All { get; } =
    [
        new("4242424242424242", "visa", "credit", CardBehavior.Succeed, CardBehavior.Succeed),
        new("4000000000003220", "visa", "credit", CardBehavior.Succeed, CardBehavior.Succeed, ThreeDSecure: true),
        new("4000002500003155", "visa", "credit", CardBehavior.Succeed, CardBehavior.Fail(FailureCode.AuthenticationRequired), ThreeDSecure: true),
        new("4000000000000341", "visa", "credit", CardBehavior.Succeed, CardBehavior.Fail(FailureCode.CardDeclined)),
        new("4000000000009995", "visa", "credit", CardBehavior.Fail(FailureCode.InsufficientFunds), CardBehavior.Fail(FailureCode.InsufficientFunds)),
        new("4000000000000002", "visa", "credit", CardBehavior.Fail(FailureCode.CardDeclined), CardBehavior.Fail(FailureCode.CardDeclined)),
        new("4000000000000069", "visa", "credit", CardBehavior.Fail(FailureCode.ExpiredCard), CardBehavior.Fail(FailureCode.ExpiredCard)),
        new("4000000000009987", "visa", "credit", CardBehavior.Fail(FailureCode.LostOrStolen), CardBehavior.Fail(FailureCode.LostOrStolen)),
        new("4000000000000259", "visa", "credit", CardBehavior.Succeed, CardBehavior.Fail(FailureCode.FraudSuspected)),
        new("4000000000000119", "visa", "credit", CardBehavior.Fail(FailureCode.ProcessingError), CardBehavior.Fail(FailureCode.ProcessingError)),
        new("4000000000007726", "visa", "credit", CardBehavior.Succeed, CardBehavior.Succeed, Refunds: RefundBehavior.PendingForAnHour),
        new("4000000000005126", "visa", "credit", CardBehavior.Succeed, CardBehavior.Succeed, Refunds: RefundBehavior.Fail),
        new("4000000000001000", "visa", "credit", CardBehavior.Succeed, new(CardBehaviorKind.TimeoutAfterCharge)),
        new("4000000000001018", "visa", "credit", CardBehavior.Succeed, new(CardBehaviorKind.TimeoutBeforeCharge)),
        new("4000000000001026", "visa", "credit", CardBehavior.Succeed, CardBehavior.Succeed, FailsAfterCharges: 3),
        new("9792000000000001", "troy", "debit", CardBehavior.Succeed, CardBehavior.Succeed),
    ];

    /// <summary>The test card with this number, ignoring spaces and dashes; <see langword="null"/> for any other number.</summary>
    public static TestCard? Find(string? number)
    {
        if (number is null)
        {
            return null;
        }

        var digits = string.Concat(number.Where(character => character is not (' ' or '-')));
        return All.FirstOrDefault(card => card.Number == digits);
    }
}
