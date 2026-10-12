namespace Tahsil.Application.Checkout;

/// <summary>From the <c>Tahsil:Checkout</c> section: the page's consents and pay button (CHK §8).</summary>
public sealed class CheckoutPageOptions
{
    public const string SectionName = "Tahsil:Checkout";

    public ConsentTextOptions ImmediateStartConsent { get; set; } = new() { Enabled = true };

    /// <summary>Payment attempts per session (CHK-21).</summary>
    public int MaxPaymentAttempts { get; set; } = 5;

    /// <summary>Attempts per network (an IPv4 address or an IPv6 /64) per hour, across sessions (CHK-21).</summary>
    public int MaxAttemptsPerAddressPerHour { get; set; } = 10;

    /// <summary>Failed attempts per customer per day (CHK-21).</summary>
    public int MaxFailedAttemptsPerCustomerPerDay { get; set; } = 10;

    /// <summary>The pay button's text per locale and mode, such as <c>PayButtonText:tr:payment</c>; <c>{amount}</c> is the total.</summary>
    public Dictionary<string, Dictionary<string, string>> PayButtonText { get; set; } = [];
}

public sealed class ConsentTextOptions
{
    public bool Enabled { get; set; }

    /// <summary>The checkbox text per locale; Tahsil's own when omitted.</summary>
    public Dictionary<string, string> Text { get; set; } = [];
}

/// <summary>
/// The texts of the page that customers agree to. They are recorded exactly as shown (CHK-16), so they live here, not
/// in the page's translations.
/// </summary>
public static class CheckoutTexts
{
    /// <summary>The consent to start the service at once, which ends the right of withdrawal (CHK-12).</summary>
    public static string ImmediateStart(CheckoutPageOptions options, string locale) =>
        options.ImmediateStartConsent.Text.GetValueOrDefault(locale) ?? (locale == "tr"
            ? "Hizmetin hemen başlamasını istiyorum; hizmet başladığında cayma hakkımı kaybedeceğimi kabul ediyorum."
            : "I want the service to start at once, and I acknowledge that I lose my right of withdrawal when it does.");

    /// <summary>The consent to save the card for future charges (CHK-13), for a setup session.</summary>
    public static string CardStorage(string merchant, string locale) => locale == "tr"
        ? $"Kartımın {merchant} tarafından kaydedilmesini ve sonraki ödemelerimde kullanılmasını kabul ediyorum. Kayıtlı kartımı istediğim zaman kaldırabilirim."
        : $"I agree that {merchant} saves my card and charges it for my future payments. I can remove the saved card at any time.";

    /// <summary>
    /// The consent to save the card for a subscription's renewals (CHK-13): the amount, how often, the date of the first
    /// automatic charge, and that the subscription can be canceled at any time.
    /// </summary>
    public static string SubscriptionCardStorage(string plan, string amount, string recurrence, string firstCharge, string locale) => locale == "tr"
        ? $"Kartımın kaydedilmesini ve {plan} aboneliğim için {recurrence} {amount} tahsil edilmesini kabul ediyorum. İlk otomatik tahsilat {firstCharge}. Aboneliğimi istediğim zaman iptal edebilirim."
        : $"I agree that my card is saved and charged {amount} {recurrence} for my {plan} subscription. The first automatic charge is on {firstCharge}. I can cancel my subscription at any time.";

    /// <summary>The pay button names the obligation (CHK-17).</summary>
    public static string PayButton(CheckoutPageOptions options, string locale, string mode, string amount)
    {
        if (options.PayButtonText.GetValueOrDefault(locale)?.GetValueOrDefault(mode) is { } configured)
        {
            return configured.Replace("{amount}", amount, StringComparison.Ordinal);
        }

        return (mode, locale) switch
        {
            ("setup", "tr") => "Kartımı kaydet",
            ("setup", _) => "Save my card",
            ("subscription", "tr") => $"{amount} öde ve abone ol",
            ("subscription", _) => $"Pay {amount} and subscribe",
            ("trial_with_card", "tr") => "Kartımı kaydet ve denemeyi başlat",
            ("trial_with_card", _) => "Save my card and start the trial",
            ("trial", "tr") => "Denemeyi başlat",
            ("trial", _) => "Start the trial",
            (_, "tr") => $"{amount} öde",
            _ => $"Pay {amount}",
        };
    }
}
