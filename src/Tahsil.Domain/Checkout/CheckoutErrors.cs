using Tahsil.Domain.Common;

namespace Tahsil.Domain.Checkout;

public static class CheckoutErrors
{
    public static readonly Error NotFound = Error.NotFound("checkout.not_found", "The checkout session does not exist.");

    public static readonly Error NotOpen = Error.Conflict("checkout.not_open", "The checkout session is already complete or expired.");

    public static readonly Error InvalidLifetime = Error.Validation(
        "checkout.invalid", "expiresIn is an ISO 8601 duration from PT30M to PT24H.", "expiresIn");

    public static readonly Error AttemptsExceeded = Error.RateLimited("checkout.attempts_exceeded", "Too many payment attempts; try again later.");

    public static readonly Error Paused = Error.Conflict("checkout.paused", "New checkouts are paused on the connection that serves the currency.");

    public static readonly Error CurrencyNotRouted = Error.Validation(
        "checkout.currency_not_routed", "No enabled provider connection serves the session's currency; route it first.", "currency");

    public static Error InvalidItems(string field, string message) => Error.Validation("checkout.invalid_items", message, field);

    public static Error InvalidUrl(string field) => Error.Validation(
        "checkout.invalid_url", "The URL must be an absolute HTTPS URL without credentials or a fragment; plain HTTP is accepted for localhost in Test mode only.", field);

    public static Error NotSupportedByConnection(string capability) => Error.Validation(
        "checkout.not_supported_by_connection", $"The connection that serves the currency cannot {capability}.", "mode");

    public static Error Invalid(string field, string message) => Error.Validation("checkout.invalid", message, field);
}
