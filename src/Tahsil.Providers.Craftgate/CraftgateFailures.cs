using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Craftgate;

/// <summary>Craftgate's error groups as Tahsil's failure codes (§6), on Craftgate's own guidance to map by group.</summary>
internal static class CraftgateFailures
{
    public static FailureCode Map(string? group, string? code) => group switch
    {
        "NOT_SUFFICIENT_FUNDS" => FailureCode.InsufficientFunds,
        "DECLINED" or "DO_NOT_HONOUR" or "REFER_TO_CARD_ISSUER" or "NOT_PERMITTED_TO_CARDHOLDER" or "CARD_NOT_PERMITTED" or "RESTRICTED_CARD" or "RESTRICTED_BY_LAW"
            or "BLOCKED_CARD" or "INVALID_TRANSACTION" or "INVALID_AMOUNT" => FailureCode.CardDeclined,
        "NOT_PERMITTED_TO_FOREIGN_CARD" => FailureCode.CardNotSupported,
        "EXPIRED_CARD" or "INVALID_EXPIRE_YEAR_MONTH" => FailureCode.ExpiredCard,
        "INVALID_CARD_NUMBER" or "BIN_NOT_FOUND" or "NO_SUCH_ISSUER" or "INVALID_CARD_TYPE" or "INVALID_CVC2" or "INVALID_CVC2_LENGTH" => FailureCode.InvalidCard,
        "LOST_CARD" or "STOLEN_CARD" or "PICKUP_CARD" => FailureCode.LostOrStolen,
        "FRAUD_SUSPECT" or "FRAUD_CHECK_BLOCK" => FailureCode.FraudSuspected,
        "EXCEEDS_WITHDRAWAL_AMOUNT_LIMIT" or "CVC2_MAX_ATTEMPT" => FailureCode.LimitExceeded,
        "DEBIT_CARDS_REQUIRES_3DS" => FailureCode.AuthenticationRequired,
        "THREEDS_VERIFICATION_ERROR" or "CARDHOLDER_NOT_VERIFIED" or "INVALID_CAVV" or "INVALID_ECI" => FailureCode.AuthenticationFailed,
        "CARDHOLDER_GAVE_UP" or "USER_ABORT" => FailureCode.Abandoned,

        // The POS settings of CRG-1: payments without CVC or 3D Secure are not allowed.
        "CVC_REQUIRED" or "NOT_PERMITTED_TO_TERMINAL" => FailureCode.MerchantNotPermitted,
        "INVALID_MERCHANT_OR_SP" or "INVALID_IP" or "REQUEST_BLOCKED_BY_BANK" or "THREEDS_INIT_ERROR" or "UNKNOWN" => FailureCode.ProviderError,
        "REQUEST_TIMEOUT" or "NO_RESPONSE" or "COMMUNICATION_OR_SYSTEM_ERROR" or "ISSUER_OR_SWITCH_INOPERATIVE" => FailureCode.ProviderUnavailable,
        _ => code switch
        {
            "15" => FailureCode.ProviderError,
            "16" => FailureCode.ProviderUnavailable,
            _ => FailureCode.InvalidRequest,
        },
    };
}
