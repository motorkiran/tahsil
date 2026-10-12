using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Checkout;
using Tahsil.Domain.Customers;

namespace Tahsil.Application.Checkout;

/// <summary>Renders legal documents from their Liquid templates.</summary>
public interface ILegalDocumentRenderer
{
    /// <summary>The document as HTML in the locale, with the template data of CHK-27.</summary>
    Task<string> RenderAsync(LegalDocumentOptions document, string locale, IReadOnlyDictionary<string, object?> model, CancellationToken cancellationToken);

    /// <summary>The template's source in the locale, or <see langword="null"/> for a document given as a URL.</summary>
    string? SourceOf(LegalDocumentOptions document, string locale);
}

/// <summary>The buyer as legal documents print them, from the details submitted on the page.</summary>
public sealed record LegalDocumentCustomer(string? Name, string? Email, CustomerType Type, string? TaxId, string? TaxOffice, CustomerAddress? Address, string? Phone);

/// <summary>The template data of legal documents (CHK-27): merchant, customer, items with tax, totals, terms and date.</summary>
public sealed class LegalDocumentModels(IOptions<MerchantOptions> merchant, IOptions<BillingOptions> billing, BillingClock clock)
{
    public IReadOnlyDictionary<string, object?> For(CheckoutSession session, string locale, LegalDocumentCustomer customer)
    {
        var culture = Formatting.CultureFor(locale);
        var exponent = Domain.Money.Currency.TryGet(session.Currency, out var currency) ? currency.Exponent : 2;
        string Money(long amount) => Formatting.Money(amount, session.Currency, exponent, culture);
        var seller = merchant.Value;

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["merchant"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = seller.DisplayName,
                ["legal_name"] = seller.LegalName,
                ["trade_name"] = seller.TradeName,
                ["mersis_number"] = seller.MersisNumber,
                ["tax_office"] = seller.TaxOffice,
                ["tax_number"] = seller.TaxNumber,
                ["address"] = seller.Address,
                ["kep_address"] = seller.KepAddress,
                ["phone"] = seller.Phone,
                ["email"] = seller.Email,
                ["website"] = seller.Website?.AbsoluteUri,
            },
            ["customer"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = customer.Name,
                ["email"] = customer.Email,
                ["is_company"] = customer.Type == CustomerType.Company,
                ["tax_id"] = customer.TaxId,
                ["tax_office"] = customer.TaxOffice,
                ["address"] = customer.Address is { } address
                    ? string.Join(", ", new[] { address.Line1, address.Line2, address.District, address.City, address.PostalCode, address.Country }.Where(part => !string.IsNullOrWhiteSpace(part)))
                    : null,
                ["phone"] = customer.Phone,
            },
            ["items"] = session.Quote.Lines.Select(line => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["description"] = line.Description,
                ["quantity"] = line.Quantity.ToString("0.########", culture),
                ["amount"] = Money(line.Total),
                ["tax"] = Money(line.TaxAmount),
                ["tax_rate"] = line.TaxPercentage?.ToString("0.####", culture),
                ["tax_included"] = line.TaxInclusive == true,
            }).ToList(),
            ["totals"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["subtotal"] = Money(session.Quote.Subtotal),
                ["discount"] = session.Quote.DiscountTotal != 0 ? Money(session.Quote.DiscountTotal) : null,
                ["tax"] = Money(session.Quote.TaxTotal),
                ["total"] = Money(session.Quote.Total),
                ["amount_due"] = Money(session.Quote.AmountDue),
                ["currency"] = session.Currency,
            },
            ["mode"] = session.Mode.ToString().ToLowerInvariant(),
            ["subscription"] = session.Subscription is { } subscription && session.Quote.Recurring is { } recurring
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["recurrence"] = Catalog.PriceDisplay.Recurrence(recurring.Interval.Map<Contracts.PriceInterval>(), recurring.IntervalCount, locale),
                    ["amount"] = Money(recurring.Period.Total),
                    ["trial_days"] = subscription.TrialDays > 0 ? subscription.TrialDays : null,
                    ["trial_end"] = subscription.TrialEnd is { } trialEnd ? Formatting.Date(trialEnd, billing.Value.Zone, culture) : null,
                    ["card_for_trial"] = subscription.CollectsCardForTrial,
                    ["invoiced"] = subscription.CollectionMethod == Domain.Invoices.CollectionMethod.SendInvoice,
                }
                : null,
            ["date"] = Formatting.Date(clock.GetUtcNow(), billing.Value.Zone, culture),
            ["session_id"] = session.Id.ToString(),
        };
    }
}

/// <summary>
/// Keeps legal documents honest across deployments (CHK-29): the content of each template version is registered when
/// the database is prepared, and a changed template under a known version stops startup.
/// </summary>
public sealed class LegalDocumentVersions(ITahsilDbContext context, IOptions<CheckoutOptions> options, ILegalDocumentRenderer renderer, BillingClock clock)
{
    /// <exception cref="InvalidOperationException">A template changed without a new version.</exception>
    public async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        foreach (var document in options.Value.Documents)
        {
            foreach (var locale in ServerOptions.SupportedLocales)
            {
                if (renderer.SourceOf(document, locale) is not { } source)
                {
                    continue;
                }

                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
                var known = await context.LegalDocumentVersions.AsNoTracking()
                    .SingleOrDefaultAsync(version => version.Key == document.Key && version.Version == document.Version && version.Locale == locale, cancellationToken);
                if (known is null)
                {
                    context.LegalDocumentVersions.Add(LegalDocumentVersion.Register(document.Key, document.Version, locale, hash, now));
                }
                else if (!known.ContentHash.AsSpan().SequenceEqual(hash))
                {
                    throw new InvalidOperationException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The legal document '{document.Key}' ({locale}) changed under version '{document.Version}', which customers may have accepted. Give it a new version (CHK-29)."));
                }
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
