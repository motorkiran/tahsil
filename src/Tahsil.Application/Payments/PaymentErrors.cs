using Tahsil.Domain.Common;

namespace Tahsil.Application.Payments;

public static class PaymentErrors
{
    public static readonly Error NotFound = Error.NotFound("payment.not_found", "The payment does not exist.");
    public static readonly Error CurrencyNotRouted = Error.Validation(
        "payment.currency_not_routed", "No enabled provider connection takes payments in this currency; set one with PUT /provider-connections/routing.", "currency");
}
