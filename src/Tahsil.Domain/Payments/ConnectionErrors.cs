using Tahsil.Domain.Common;

namespace Tahsil.Domain.Payments;

public static class ConnectionErrors
{
    public static readonly Error NotFound = Error.NotFound("connection.not_found", "The provider connection does not exist.");
    public static readonly Error KeyTaken = Error.Conflict("connection.key_taken", "Another provider connection has this key.");
    public static readonly Error InUse = Error.Conflict("connection.in_use", "The connection has payment methods or payments; disable it instead.");
    public static readonly Error ModeChangeWithCards = Error.Conflict(
        "connection.mode_change_with_cards", "The connection has saved cards, which belong to its card entry mode; allowDirectForm cannot change while it has them.");
    public static readonly Error UnknownProvider = Error.Validation("connection.unknown_provider", "No such provider; GET /providers lists them.", "provider");
    public static readonly Error InvalidKey = Error.Validation(
        "connection.invalid", "A key starts with a lowercase letter and has up to 64 lowercase letters, digits, '_' or '-'.", "key");
    public static readonly Error InvalidName = Error.Validation("connection.invalid", "A name is required and is at most 100 characters.", "name");
    public static readonly Error UnsupportedEnvironment = Error.Validation("connection.invalid", "The provider has no such environment.", "environment");
    public static readonly Error NotAllowedInMode = Error.Validation(
        "connection.not_allowed_in_mode",
        "Test mode takes sandbox connections and the simulator; Live mode takes production connections only.",
        "environment");

    public static Error InvalidCredential(string field, string message) => Error.Validation("connection.invalid_credentials", message, $"credentials.{field}");

    public static Error InvalidSetting(string field, string message) => Error.Validation("connection.invalid", message, $"settings.{field}");

    public static Error CurrencyNotSupported(string currency) =>
        Error.Validation("connection.currency_not_supported", $"The connection's provider does not charge in {currency}.", currency);

    public static Error Disabled(string currency) =>
        Error.Validation("connection.disabled", "A disabled connection cannot serve a currency; enable it first.", currency);

    public static Error UnknownCurrency(string currency) =>
        Error.Validation("currency.unsupported", $"'{currency}' is not a supported ISO 4217 code.", currency);

    public static Error UnknownConnection(string currency) =>
        Error.Validation("connection.not_found", "No provider connection has this key.", currency);
}
