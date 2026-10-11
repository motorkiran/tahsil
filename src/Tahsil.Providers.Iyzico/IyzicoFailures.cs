using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Iyzico;

/// <summary>iyzico's error groups and codes as Tahsil's failure codes (§6): the group first, then the code.</summary>
internal static class IyzicoFailures
{
    public static FailureCode Map(string? group, string? code, bool customerPresent)
    {
        var authentication = customerPresent ? FailureCode.AuthenticationFailed : FailureCode.AuthenticationRequired;
        var byGroup = group switch
        {
            "NOT_SUFFICIENT_FUNDS" => FailureCode.InsufficientFunds,
            "DO_NOT_HONOUR" or "INVALID_TRANSACTION" or "NOT_PERMITTED_TO_CARDHOLDER" or "RESTRICTED_BY_LAW" or "CARD_NOT_PERMITTED" or "REFER_TO_CARD_ISSUER"
                or "BLOCKED_CARD" or "DECLINED" or "RESTRICTED_CARD" or "REQUEST_BLOCKED_BY_BANK" or "INVALID_AMOUNT" => FailureCode.CardDeclined,
            "NOT_PERMITTED_TO_FOREIGN_CARD" => FailureCode.CardNotSupported,
            "EXPIRED_CARD" or "INVALID_EXPIRE_YEAR_MONTH" => FailureCode.ExpiredCard,
            "INVALID_CARD_NUMBER" or "INVALID_CVC2" or "CVC2_MAX_ATTEMPT" or "INVALID_CARD_TYPE" or "BIN_NOT_FOUND" => FailureCode.InvalidCard,
            "PICKUP_CARD" or "LOST_CARD" or "STOLEN_CARD" => FailureCode.LostOrStolen,
            "FRAUD_SUSPECT" => FailureCode.FraudSuspected,
            "EXCEEDS_WITHDRAWAL_AMOUNT_LIMIT" or "EXCEEDED_TRANSACTION_AMOUNT" => FailureCode.LimitExceeded,
            "DEBIT_CARDS_REQUIRES_3DS" or "INVALID_CAVV" or "INVALID_ECI" => authentication,
            "NOT_PERMITTED_TO_TERMINAL" => FailureCode.MerchantNotPermitted,
            "COMMUNICATION_OR_SYSTEM_ERROR" or "NO_SUCH_ISSUER" or "REQUEST_TIMEOUT" or "ISSUER_OR_SWITCH_INOPERATIVE" => FailureCode.ProviderUnavailable,
            "UNKNOWN" or "INVALID_MERCHANT_OR_SP" => FailureCode.ProviderError,
            _ => (FailureCode?)null,
        };

        return byGroup ?? code switch
        {
            "10051" => FailureCode.InsufficientFunds,
            "10005" or "10012" or "10057" or "10093" or "10201" or "10207" or "10209" or "10220" or "10225" or "10230" or "10232" => FailureCode.CardDeclined,
            "10221" or "5184" or "5075" => FailureCode.CardNotSupported,
            "10054" or "10229" => FailureCode.ExpiredCard,
            "10215" or "10084" or "10212" or "10233" or "10213" or "3005" or "3006" or "5111" => FailureCode.InvalidCard,
            "10041" or "10043" => FailureCode.LostOrStolen,
            "10034" or "6000" or "6001" or "5242" => FailureCode.FraudSuspected,
            "10224" or "10248" => FailureCode.LimitExceeded,
            "10217" or "5010" or "5072" or "5167" or "10210" or "10211" => authentication,
            "5269" or "5124" or "5326" or "5125" => FailureCode.Abandoned,
            "3007" or "10058" => FailureCode.MerchantNotPermitted,
            "10214" or "10216" or "10219" or "10228" or "50000" => FailureCode.ProviderUnavailable,
            "1" or "2" or "5210" or "9000" or "5081" or "10202" or "10204" or "10236" or "10208" or "5070" or "5071" or "5074" or "5083" or "5090" or "5091"
                or "5114" or "5153" or "5155" or "5156" or "5185" => FailureCode.ProviderError,
            { } number when int.TryParse(number, out var value) && (value is >= 1000 and <= 1009 or >= 5000 and <= 5062) => FailureCode.InvalidRequest,
            _ => FailureCode.ProviderError,
        };
    }
}
