using System.Net;
using Stripe;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Stripe;

/// <summary>Stripe's errors as Tahsil's failure codes (STR §6): the decline code first, then the error code and type.</summary>
internal static class StripeFailures
{
    public static FailureCode Map(StripeError? error, HttpStatusCode status)
    {
        if (status == HttpStatusCode.TooManyRequests || (int)status >= 500)
        {
            return FailureCode.ProviderUnavailable;
        }

        if (error is null)
        {
            return status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? FailureCode.InvalidRequest : FailureCode.ProviderError;
        }

        return Decline(error.DeclineCode) ?? Code(error.Code) ?? error.Type switch
        {
            "card_error" => FailureCode.CardDeclined,
            "invalid_request_error" or "idempotency_error" => FailureCode.InvalidRequest,
            "api_error" => FailureCode.ProviderError,
            _ => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? FailureCode.InvalidRequest : FailureCode.ProviderError,
        };
    }

    /// <summary>A PaymentIntent's or SetupIntent's last error, as Stripe keeps it on the object.</summary>
    public static FailureCode Map(string? declineCode, string? code, string? type) =>
        Decline(declineCode) ?? Code(code) ?? (type == "card_error" ? FailureCode.CardDeclined : FailureCode.ProviderError);

    /// <summary>Whether a failed call may have charged: Stripe or the network could not say.</summary>
    public static bool IsUncertain(FailureCode code) => code is FailureCode.ProviderUnavailable;

    private static FailureCode? Decline(string? code) => code switch
    {
        "insufficient_funds" => FailureCode.InsufficientFunds,
        "card_velocity_exceeded" or "withdrawal_count_limit_exceeded" => FailureCode.LimitExceeded,
        "expired_card" => FailureCode.ExpiredCard,
        "incorrect_number" or "invalid_number" or "incorrect_cvc" or "invalid_cvc" or "invalid_expiry_month" or "invalid_expiry_year" or "invalid_account"
            or "new_account_information_available" => FailureCode.InvalidCard,
        "lost_card" or "stolen_card" or "pickup_card" or "restricted_card" => FailureCode.LostOrStolen,
        "fraudulent" or "merchant_blacklist" => FailureCode.FraudSuspected,
        "card_not_supported" or "currency_not_supported" => FailureCode.CardNotSupported,
        "authentication_required" or "authentication_not_handled" => FailureCode.AuthenticationRequired,
        "revocation_of_authorization" or "revocation_of_all_authorizations" or "stop_payment_order" => FailureCode.AuthorizationRevoked,
        "generic_decline" or "do_not_honor" or "call_issuer" or "do_not_try_again" or "no_action_taken" or "not_permitted" or "security_violation"
            or "service_not_allowed" or "transaction_not_allowed" or "try_again_later" => FailureCode.CardDeclined,
        "processing_error" or "reenter_transaction" => FailureCode.ProcessingError,
        "issuer_not_available" => FailureCode.ProviderUnavailable,
        _ => null,
    };

    private static FailureCode? Code(string? code) => code switch
    {
        "card_decline_rate_limit_exceeded" => FailureCode.LimitExceeded,
        "expired_card" or "expired_payment_method" => FailureCode.ExpiredCard,
        "incorrect_number" or "invalid_number" or "incorrect_cvc" or "invalid_cvc" or "invalid_expiry_month" or "invalid_expiry_year" => FailureCode.InvalidCard,
        "authentication_required" or "payment_intent_action_required" => FailureCode.AuthenticationRequired,
        "payment_intent_authentication_failure" or "setup_intent_authentication_failure" => FailureCode.AuthenticationFailed,
        "processing_error" => FailureCode.ProcessingError,
        "payment_method_provider_timeout" or "rate_limit" or "lock_timeout" or "idempotency_key_in_use" => FailureCode.ProviderUnavailable,
        "livemode_mismatch" or "testmode_charges_only" or "idempotency_error" => FailureCode.InvalidRequest,
        _ => null,
    };
}
