using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Domain.Checkout;
using Tahsil.Domain.Customers;
using Tahsil.Providers.Abstractions;
using Tahsil.Server.Ui;

namespace Tahsil.Server.Pages.Checkout;

/// <summary>
/// The checkout page (CHK §2): the summary, the customer's details, the legal documents and consents, the final
/// summary and the pay button; submitting it starts the payment and shows the provider's fields.
/// </summary>
[RunsScripts]
public sealed class IndexModel(
    CheckoutPages pages,
    IOptions<CheckoutPageOptions> pageOptions,
    IOptions<MerchantOptions> merchant,
    IOptions<Application.Invoices.BillingOptions> billing,
    Microsoft.Extensions.Localization.IStringLocalizer<SharedResource> localizer) : HostedPageModel, IContentSecurityPolicySources
{
    public CheckoutView View { get; private set; } = null!;

    public string Token { get; private set; } = string.Empty;

    /// <summary>The provider's fields, once the form is submitted.</summary>
    public Presentation.Embed? Embed { get; private set; }

    /// <summary>The provider's direct card form, shown alone on a response without scripts (ADP-13).</summary>
    public Presentation.DirectForm? CardForm { get; private set; }

    /// <summary>Why the last attempt failed, or what the form lacks.</summary>
    public string? ErrorMessage { get; private set; }

    [BindProperty]
    public CheckoutForm Form { get; set; } = new();

    /// <summary>The promotion code the customer enters (CHK-9).</summary>
    [BindProperty]
    public string? PromotionCode { get; set; }

    public MerchantOptions Merchant => merchant.Value;

    public CultureInfo Culture => Formatting.CultureFor(View.Locale);

    public bool IsSetup => View.Session.Mode == CheckoutMode.Setup;

    /// <summary>For subscription sessions: the subscription, as the session creates it.</summary>
    public CheckoutSubscription? Subscription => View.Session.Subscription;

    public bool OffersImmediateStart => !IsSetup && pageOptions.Value.ImmediateStartConsent.Enabled;

    public string ImmediateStartText => CheckoutTexts.ImmediateStart(pageOptions.Value, View.Locale);

    /// <summary>Whether the page asks to save the card for future charges, in a box of its own (CHK-13).</summary>
    public bool SavesCard => CheckoutPages.SavesCard(View.Session);

    public string CardStorageText => pages.CardStorageText(View);

    public string PayButtonText => CheckoutTexts.PayButton(pageOptions.Value, View.Locale, ButtonMode, Money(View.Session.Quote.AmountDue));

    public string Money(long amount) => Formatting.Money(
        amount, View.Session.Currency, Domain.Money.Currency.TryGet(View.Session.Currency, out var currency) ? currency.Exponent : 2, Culture);

    public string Date(DateTimeOffset instant) => Formatting.Date(instant, billing.Value.Zone, Culture);

    private string ButtonMode => Subscription switch
    {
        null => IsSetup ? "setup" : "payment",
        { HasTrial: false } => "subscription",
        { CollectsCardForTrial: true } => "trial_with_card",
        _ => "trial",
    };

    public async Task<IActionResult> OnGetAsync(string token, string? failed, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(token, cancellationToken))
        {
            return NotFound();
        }

        if (View.Session.Status == CheckoutStatus.Complete)
        {
            return Redirect(View.Session.SuccessUrlFor());
        }

        Prefill();
        if (failed is not null)
        {
            ErrorMessage = FailureMessage(failed);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(token, cancellationToken) || View.Session.Status != CheckoutStatus.Open)
        {
            return NotFound();
        }

        var started = await pages.SubmitAsync(token, new CheckoutSubmission(
            Form.Email,
            Form.Name,
            Form.Company ? CustomerType.Company : CustomerType.Individual,
            Form.TaxId,
            Form.TaxOffice,
            string.IsNullOrWhiteSpace(Form.AddressLine1) ? null : new CustomerAddress(Form.AddressLine1, null, null, Form.City, Form.PostalCode, Form.Country ?? "TR"),
            Form.Phone,
            Form.Documents.ToHashSet(StringComparer.Ordinal),
            Form.ImmediateStart,
            Form.CardStorage,
            View.Locale,
            HttpContext.Connection.RemoteIpAddress,
            Request.Headers.UserAgent.ToString()), cancellationToken);
        if (started.IsFailure)
        {
            ErrorMessage = started.Error.Code switch
            {
                "checkout.details_required" => localizer["Fill in the required details."],
                "checkout.consent_required" => localizer["Tick the required boxes to continue."],
                "checkout.not_open" => localizer["This checkout is no longer open."],
                "checkout.attempts_exceeded" => localizer["Too many payment attempts. Please try again later."],
                "checkout.paused" => localizer["Payments are paused for now. Please try again later."],
                "checkout.discount_removed" => localizer["The discount does not apply to you and was removed. Check the total and continue."],
                _ => started.Error.Field is null ? localizer["The payment could not be started. Please try again."] : started.Error.Message,
            };
            Response.StatusCode = started.Error.Code == "checkout.attempts_exceeded" ? StatusCodes.Status429TooManyRequests : StatusCodes.Status400BadRequest;
            if (started.Error.Code == CheckoutPageErrors.DiscountRemoved.Code)
            {
                // The quote changed with the discount; the page shows the new total.
                await LoadAsync(token, cancellationToken);
            }

            return Page();
        }

        if (started.Value.CompletedUrl is { } completed)
        {
            // A trial without a card needs no payment; the session completed with the form (CHK-25).
            return Redirect(completed);
        }

        switch (started.Value.Payment!.Presentation)
        {
            case Presentation.Redirect redirect:
                return Redirect(redirect.Url.AbsoluteUri);
            case Presentation.Embed embed:
                Embed = embed;
                return Page();
            case Presentation.DirectForm form:
                CardForm = form;
                return Page();
            default:
                throw new NotSupportedException("The provider presents the payment in a way the page does not know.");
        }
    }

    /// <summary>The customer enters a promotion code (CHK-9); the quote and the summary follow.</summary>
    public async Task<IActionResult> OnPostCodeAsync(string token, CancellationToken cancellationToken) =>
        await DiscountAsync(token, await pages.ApplyPromotionCodeAsync(token, PromotionCode, HttpContext.Connection.RemoteIpAddress, cancellationToken), cancellationToken);

    public async Task<IActionResult> OnPostRemoveCodeAsync(string token, CancellationToken cancellationToken) =>
        await DiscountAsync(token, await pages.RemovePromotionCodeAsync(token, cancellationToken), cancellationToken);

    private async Task<IActionResult> DiscountAsync(string token, Domain.Common.Result changed, CancellationToken cancellationToken)
    {
        if (changed.IsSuccess)
        {
            return RedirectToPage(new { token });
        }

        if (changed.Error.Code == CheckoutErrors.NotOpen.Code || !await LoadAsync(token, cancellationToken) || View.Session.Status != CheckoutStatus.Open)
        {
            // Also for a session that takes no codes from customers.
            return NotFound();
        }

        Prefill();
        var limited = changed.Error.Code == CheckoutPageErrors.PromotionCodeRateLimited.Code;
        ErrorMessage = limited ? localizer["Too many codes were tried. Please try again later."] : localizer["This code cannot be used."];
        Response.StatusCode = limited ? StatusCodes.Status429TooManyRequests : StatusCodes.Status400BadRequest;
        return Page();
    }

    /// <summary>The page's policy, extended by the routed provider's sources (FND-42).</summary>
    public CspSources GetCspSources() => View is null
        ? new CspSources()
        : new CspSources
        {
            Scripts = View.ContentSecurity.Scripts,
            Frames = View.ContentSecurity.Frames,
            Connections = View.ContentSecurity.Connections,
            Images = View.ContentSecurity.Images,
            FormActions = View.ContentSecurity.FormActions,
            ForbidScripts = CardForm is not null,
        };

    /// <summary>A document of the page as the box shows it, with the details known so far.</summary>
    public Task<string?> DocumentAsync(string key, CancellationToken cancellationToken) => pages.RenderDocumentAsync(
        View.Session, key, View.Locale, new LegalDocumentCustomer(Form.Name, Form.Email, Form.Company ? CustomerType.Company : CustomerType.Individual, null, null, null, null), cancellationToken);

    private async Task<bool> LoadAsync(string token, CancellationToken cancellationToken)
    {
        Token = token;
        if (await pages.FindAsync(token, cancellationToken) is not { } session || await pages.ViewAsync(session, Locale, cancellationToken) is not { } view)
        {
            return false;
        }

        View = view;
        return true;
    }

    private void Prefill()
    {
        if (View.Customer is { } customer)
        {
            Form = new CheckoutForm
            {
                Email = customer.Email,
                Name = customer.Name,
                Company = customer.Type == CustomerType.Company,
                TaxOffice = customer.TaxOffice,
                Phone = customer.Phone,
                AddressLine1 = customer.Address?.Line1,
                City = customer.Address?.City,
                PostalCode = customer.Address?.PostalCode,
                Country = customer.Address?.Country,
            };
        }
        else if (View.Session.CustomerDetails is { } details)
        {
            Form = new CheckoutForm
            {
                Email = details.Email,
                Name = details.Name,
                Company = details.Type == CustomerType.Company,
                Phone = details.Phone,
                AddressLine1 = details.Address?.Line1,
                City = details.Address?.City,
                PostalCode = details.Address?.PostalCode,
                Country = details.Address?.Country,
            };
        }
    }

    private string FailureMessage(string code)
    {
        if (code == CheckoutPageErrors.CardNotSaved.Code)
        {
            return localizer["The card was not saved. Please try again."];
        }

        var failure = Enum.GetValues<FailureCode>().FirstOrDefault(candidate => Application.Payments.FailureMessages.Name(candidate) == code, FailureCode.ProviderError);
        return View.Locale == "tr" ? Application.Payments.FailureMessages.Turkish(failure) : Application.Payments.FailureMessages.English(failure);
    }
}

/// <summary>The form of the checkout page.</summary>
public sealed class CheckoutForm
{
    public string? Email { get; set; }

    public string? Name { get; set; }

    public bool Company { get; set; }

    public string? TaxId { get; set; }

    public string? TaxOffice { get; set; }

    public string? AddressLine1 { get; set; }

    public string? City { get; set; }

    public string? PostalCode { get; set; }

    public string? Country { get; set; }

    public string? Phone { get; set; }

    /// <summary>The keys of the documents whose boxes are ticked.</summary>
    public List<string> Documents { get; set; } = [];

    public bool ImmediateStart { get; set; }

    public bool CardStorage { get; set; }
}
