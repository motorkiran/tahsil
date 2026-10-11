using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Domain.Catalog;

namespace Tahsil.Application.Checkout;

/// <summary>
/// The merchant's identity (CHK-30): shown in the footer of hosted pages, used by legal documents, and printed on
/// invoices as the seller.
/// </summary>
public sealed class MerchantOptions
{
    public const string SectionName = "Tahsil:Merchant";

    /// <summary>The name customers know, such as <c>Acme</c>.</summary>
    public string? TradeName { get; set; }

    /// <summary>The registered name, such as <c>Acme Yazılım A.Ş.</c>.</summary>
    public string? LegalName { get; set; }

    public string? MersisNumber { get; set; }

    public string? TaxOffice { get; set; }

    public string? TaxNumber { get; set; }

    public string? Address { get; set; }

    public string? KepAddress { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public Uri? Website { get; set; }

    public Uri? LogoUrl { get; set; }

    /// <summary>The name to show: the trade name, or the legal name.</summary>
    public string? DisplayName => string.IsNullOrWhiteSpace(TradeName) ? LegalName : TradeName;
}

/// <summary>From the <c>Tahsil:Checkout</c> configuration section.</summary>
public sealed class CheckoutOptions
{
    public const string SectionName = "Tahsil:Checkout";

    /// <summary>The legal documents of the checkout (CHK-26); the starter pre-information form and distance sales contract when none are configured.</summary>
    public List<LegalDocumentOptions> LegalDocuments { get; set; } = [];

    public IReadOnlyList<LegalDocumentOptions> Documents => LegalDocuments.Count == 0 ? LegalDocumentOptions.Starters : LegalDocuments;
}

/// <summary>
/// A legal document of the checkout (CHK-26): a Liquid template Tahsil renders with the session's data, or a URL shown
/// as a link (whose content Tahsil cannot record). Changing a template's content needs a new version (CHK-29).
/// </summary>
public sealed class LegalDocumentOptions
{
    /// <summary>The prefix of the templates Tahsil ships (CHK-28), such as <c>starter:pre-information</c>.</summary>
    public const string StarterPrefix = "starter:";

    public static readonly IReadOnlyList<string> Types = ["pre_information", "distance_sales_contract", "subscription_contract", "privacy_notice", "custom"];

    public static readonly IReadOnlyList<string> AllModes = ["payment", "subscription", "setup"];

    public static readonly IReadOnlyList<string> AllCustomerTypes = ["individual", "company"];

    /// <summary>The starter documents of a fresh installation, to be reviewed by the merchant's lawyer.</summary>
    public static readonly IReadOnlyList<LegalDocumentOptions> Starters =
    [
        new()
        {
            Key = "pre-information",
            Type = "pre_information",
            Version = "2026-10",
            Title = new() { ["tr"] = "Ön Bilgilendirme Formu", ["en"] = "Pre-information Form" },
            Template = new() { ["tr"] = $"{StarterPrefix}pre-information", ["en"] = $"{StarterPrefix}pre-information" },
            Modes = ["payment", "subscription"],
            CustomerTypes = ["individual"],
            RequiresAcceptance = true,
        },
        new()
        {
            Key = "distance-sales",
            Type = "distance_sales_contract",
            Version = "2026-10",
            Title = new() { ["tr"] = "Mesafeli Satış Sözleşmesi", ["en"] = "Distance Sales Contract" },
            Template = new() { ["tr"] = $"{StarterPrefix}distance-sales", ["en"] = $"{StarterPrefix}distance-sales" },
            Modes = ["payment", "subscription"],
            CustomerTypes = ["individual"],
            RequiresAcceptance = true,
        },
    ];

    public string Key { get; set; } = string.Empty;

    /// <summary><c>pre_information</c>, <c>distance_sales_contract</c>, <c>subscription_contract</c>, <c>privacy_notice</c> or <c>custom</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Changes whenever the content does, so earlier acceptances stay provable (CHK-29).</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The title per locale.</summary>
    public Dictionary<string, string> Title { get; set; } = [];

    /// <summary>A Liquid template per locale: a file path, or a starter such as <c>starter:pre-information</c>.</summary>
    public Dictionary<string, string>? Template { get; set; }

    /// <summary>Instead of a template, a page per locale that the checkout links to.</summary>
    public Dictionary<string, string>? Url { get; set; }

    /// <summary>The session modes that show it; <c>payment</c> and <c>subscription</c> when empty.</summary>
    public List<string> Modes { get; set; } = [];

    /// <summary>The customer types it applies to; both when empty.</summary>
    public List<string> CustomerTypes { get; set; } = [];

    /// <summary>Whether the customer must tick its checkbox to pay; a notice such as the privacy notice is only linked.</summary>
    public bool RequiresAcceptance { get; set; }

    /// <summary>The checkbox text per locale; a text for the document's type when omitted.</summary>
    public Dictionary<string, string>? CheckboxText { get; set; }

    public bool IsStarter => Template?.Values.Any(path => path.StartsWith(StarterPrefix, StringComparison.Ordinal)) == true;

    public bool AppliesTo(string mode) => (Modes.Count == 0 ? ["payment", "subscription"] : Modes).Contains(mode);

    /// <summary>A per-locale value in the locale, or in another locale when the document has none for it.</summary>
    public static string? InLocale(IReadOnlyDictionary<string, string>? values, string locale) =>
        values is null ? null : values.GetValueOrDefault(locale) ?? ServerOptions.SupportedLocales.Select(values.GetValueOrDefault).FirstOrDefault(value => value is not null);

    /// <summary>The text of the document's checkbox, exactly as shown and recorded (CHK-11, CHK-16).</summary>
    public string CheckboxTextIn(string locale)
    {
        if (InLocale(CheckboxText, locale) is { } configured)
        {
            return configured;
        }

        var title = InLocale(Title, locale) ?? Key;
        return (Type, locale) switch
        {
            ("pre_information", "tr") => $"{title}'nu okudum.",
            ("pre_information", _) => $"I have read the {title}.",
            ("distance_sales_contract" or "subscription_contract", "tr") => $"{title}'ni kabul ediyorum.",
            ("distance_sales_contract" or "subscription_contract", _) => $"I accept the {title}.",
            (_, "tr") => $"{title} metnini okudum ve kabul ediyorum.",
            _ => $"I have read and accept the {title}.",
        };
    }
}

/// <summary>Checks the legal documents, and the merchant details the starter templates print (CHK-26, CHK-30).</summary>
internal sealed class CheckoutOptionsValidation(IOptions<MerchantOptions> merchant) : IValidateOptions<CheckoutOptions>
{
    public ValidateOptionsResult Validate(string? name, CheckoutOptions options)
    {
        var failures = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, document) in options.LegalDocuments.Index())
        {
            var path = $"{CheckoutOptions.SectionName}:LegalDocuments:{index}";
            if (!CatalogKey.IsValid(document.Key) || !keys.Add(document.Key))
            {
                failures.Add($"{path}:Key must be a unique key of lowercase letters, digits, '_' or '-'.");
            }

            if (!LegalDocumentOptions.Types.Contains(document.Type))
            {
                failures.Add($"{path}:Type must be one of: {string.Join(", ", LegalDocumentOptions.Types)}.");
            }

            if (document.Version is not { Length: > 0 and <= 32 })
            {
                failures.Add($"{path}:Version is required, at most 32 characters.");
            }

            if (document.Title.Count == 0 || document.Title.Keys.Any(locale => !ServerOptions.SupportedLocales.Contains(locale)))
            {
                failures.Add($"{path}:Title needs a title per locale ({string.Join(", ", ServerOptions.SupportedLocales)}).");
            }

            if ((document.Template is { Count: > 0 }) == (document.Url is { Count: > 0 }))
            {
                failures.Add($"{path} needs either a Template or a Url per locale.");
            }

            if (document.Url?.Values.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) == true)
            {
                failures.Add($"{path}:Url must be absolute HTTPS URLs.");
            }

            if (document.Modes.Any(mode => !LegalDocumentOptions.AllModes.Contains(mode)) || document.CustomerTypes.Any(type => !LegalDocumentOptions.AllCustomerTypes.Contains(type)))
            {
                failures.Add($"{path}: Modes are payment, subscription or setup; CustomerTypes are individual or company.");
            }
        }

        if (options.Documents.Any(document => document.IsStarter))
        {
            var missing = new (string Name, string? Value)[]
            {
                ("LegalName", merchant.Value.LegalName), ("Address", merchant.Value.Address), ("Phone", merchant.Value.Phone), ("Email", merchant.Value.Email),
            }.Where(field => string.IsNullOrWhiteSpace(field.Value)).Select(field => $"{MerchantOptions.SectionName}:{field.Name}").ToList();
            if (missing.Count > 0)
            {
                failures.Add($"The starter legal documents print the merchant's identity; set {string.Join(", ", missing)}.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
