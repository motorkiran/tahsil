namespace Tahsil.Providers.Abstractions;

/// <summary>Why a payment failed, normalized across providers (PAY-26).</summary>
public enum FailureCode
{
    InsufficientFunds,
    CardDeclined,
    LimitExceeded,
    ProcessingError,
    Abandoned,
    ExpiredCard,
    InvalidCard,
    LostOrStolen,
    FraudSuspected,
    CardNotSupported,
    PaymentMethodMissing,
    AuthorizationRevoked,
    AuthenticationRequired,
    AuthenticationFailed,
    MerchantNotPermitted,
    InvalidRequest,
    ProviderError,
    ProviderUnavailable,
    NotSubmitted,
}

/// <summary>What a failure asks for: another try, another card, the customer's confirmation, or the merchant's attention.</summary>
public enum FailureCategory
{
    Retry,
    NewPaymentMethod,
    Authenticate,

    /// <summary>The merchant's or provider's problem, never blamed on the customer.</summary>
    Merchant,
}

public static class FailureCodes
{
    public static FailureCategory CategoryOf(FailureCode code) => code switch
    {
        FailureCode.InsufficientFunds or FailureCode.CardDeclined or FailureCode.LimitExceeded or FailureCode.ProcessingError or FailureCode.Abandoned
            => FailureCategory.Retry,
        FailureCode.ExpiredCard or FailureCode.InvalidCard or FailureCode.LostOrStolen or FailureCode.FraudSuspected or FailureCode.CardNotSupported
            or FailureCode.PaymentMethodMissing or FailureCode.AuthorizationRevoked
            => FailureCategory.NewPaymentMethod,
        FailureCode.AuthenticationRequired or FailureCode.AuthenticationFailed => FailureCategory.Authenticate,
        _ => FailureCategory.Merchant,
    };
}
