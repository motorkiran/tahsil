using System.Text.Json;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Application.Payments;

/// <summary>What each failure code tells the customer, in English and Turkish (PAY-26).</summary>
public static class FailureMessages
{
    private const string GenericEnglish = "The payment could not be completed. Please try again later.";
    private const string GenericTurkish = "Ödeme tamamlanamadı. Lütfen daha sonra tekrar deneyin.";

    public static string Name(FailureCode code) => JsonNamingPolicy.SnakeCaseLower.ConvertName(code.ToString());

    /// <summary>The customer message for a recorded failure code, in the customer's language.</summary>
    public static string For(string? code, string locale)
    {
        var known = Enum.GetValues<FailureCode>().Cast<FailureCode?>().FirstOrDefault(candidate => Name(candidate!.Value) == code);
        var turkish = locale.StartsWith("tr", StringComparison.OrdinalIgnoreCase);
        return known is { } failure ? turkish ? Turkish(failure) : English(failure) : turkish ? GenericTurkish : GenericEnglish;
    }

    public static string Category(FailureCode code) => JsonNamingPolicy.SnakeCaseLower.ConvertName(FailureCodes.CategoryOf(code).ToString());

    public static string English(FailureCode code) => code switch
    {
        FailureCode.InsufficientFunds => "Your card has insufficient funds or limit.",
        FailureCode.CardDeclined => "Your card was declined by your bank.",
        FailureCode.LimitExceeded => "Your card's transaction limit was exceeded.",
        FailureCode.ProcessingError => "Your bank could not process the payment. Please try again.",
        FailureCode.Abandoned => "The payment was not completed.",
        FailureCode.ExpiredCard => "Your card has expired.",
        FailureCode.InvalidCard => "Your card details are not valid.",
        FailureCode.LostOrStolen or FailureCode.FraudSuspected => "Your card was declined.",
        FailureCode.CardNotSupported => "This card cannot be used for this payment.",
        FailureCode.PaymentMethodMissing => "No card is saved for this payment.",
        FailureCode.AuthorizationRevoked => "Your bank reports that you stopped payments to us.",
        FailureCode.AuthenticationRequired => "Your bank asks you to confirm this payment.",
        FailureCode.AuthenticationFailed => "The payment could not be verified with your bank.",
        _ => GenericEnglish,
    };

    public static string Turkish(FailureCode code) => code switch
    {
        FailureCode.InsufficientFunds => "Kartınızın bakiyesi veya limiti yetersiz.",
        FailureCode.CardDeclined => "Kartınız bankanız tarafından reddedildi.",
        FailureCode.LimitExceeded => "Kartınızın işlem limiti aşıldı.",
        FailureCode.ProcessingError => "Bankanız ödemeyi işleyemedi. Lütfen tekrar deneyin.",
        FailureCode.Abandoned => "Ödeme tamamlanmadı.",
        FailureCode.ExpiredCard => "Kartınızın süresi dolmuş.",
        FailureCode.InvalidCard => "Kart bilgileriniz geçersiz.",
        FailureCode.LostOrStolen or FailureCode.FraudSuspected => "Kartınız reddedildi.",
        FailureCode.CardNotSupported => "Bu kart bu ödeme için kullanılamıyor.",
        FailureCode.PaymentMethodMissing => "Bu ödeme için kayıtlı kart yok.",
        FailureCode.AuthorizationRevoked => "Bankanız bu işyerine yapılan ödemeleri durdurduğunuzu bildirdi.",
        FailureCode.AuthenticationRequired => "Bankanız bu ödemeyi onaylamanızı istiyor.",
        FailureCode.AuthenticationFailed => "Ödeme bankanızla doğrulanamadı.",
        _ => GenericTurkish,
    };
}
