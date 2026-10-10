namespace Tahsil.Admin.Components.Common;

/// <summary>The answers a simulator card can be set to give (SIM-8), as the test API names them.</summary>
public static class CardBehaviors
{
    public static readonly IReadOnlyList<string> All =
    [
        "succeed",
        "card_declined",
        "insufficient_funds",
        "expired_card",
        "authentication_required",
        "fraud_suspected",
        "processing_error",
        "pending",
        "timeout_after_charge",
        "timeout_before_charge",
    ];
}
