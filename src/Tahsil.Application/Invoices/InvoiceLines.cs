using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Catalog;
using Tahsil.Application.Common;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Common;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;
using InvoiceLineRequest = Tahsil.Contracts.InvoiceLineRequest;

namespace Tahsil.Application.Invoices;

/// <summary>
/// Turns requested lines and pending items into invoice lines (INV-21): one-time prices in the invoice's currency, or
/// free-form lines with a description and an amount. Descriptions are written once, in the customer's language (INV-2).
/// </summary>
public sealed class InvoiceLines(ITahsilDbContext context)
{
    public async Task<Result<List<InvoiceLineDraft>>> FromRequestsAsync(
        IReadOnlyList<InvoiceLineRequest> requests, string currency, string locale, CancellationToken cancellationToken)
    {
        var lines = new List<InvoiceLineDraft>();
        for (var index = 0; index < requests.Count; index++)
        {
            var line = await FromRequestAsync(requests[index], $"lines[{index}]", currency, locale, cancellationToken);
            if (line.IsFailure)
            {
                return line.Error;
            }

            lines.Add(line.Value);
        }

        return lines;
    }

    /// <summary>The lines of pending items; the caller checked that they are pending and in the invoice's currency.</summary>
    public async Task<List<InvoiceLineDraft>> FromPendingItemsAsync(IReadOnlyList<PendingInvoiceItem> items, string locale, CancellationToken cancellationToken)
    {
        var lines = new List<InvoiceLineDraft>();
        foreach (var item in items)
        {
            var price = item.PriceId is { } priceId ? await context.Prices.AsNoTracking().SingleAsync(row => row.Id == priceId, cancellationToken) : null;
            var taxRateId = price?.TaxRateId ?? item.TaxRateId;
            var taxRate = taxRateId is { } rateId ? await context.TaxRates.AsNoTracking().SingleAsync(rate => rate.Id == rateId, cancellationToken) : null;
            lines.Add(new InvoiceLineDraft(
                item.IsProration ? InvoiceLineType.Proration : InvoiceLineType.Item,
                item.Description ?? await DescribeAsync(price!, item.Quantity, locale, cancellationToken),
                item.Quantity,
                price is null ? MoneyMath.Multiply(item.UnitAmount!.Value, item.Quantity) : PriceAmounts.For(price, item.Quantity),
                UnitAmountDecimal: price is null ? item.UnitAmount : UnitAmountOf(price),
                PriceId: price?.Id,
                PendingItemId: item.Id,
                TaxRateId: taxRate?.Id,
                Tax: taxRate is null ? null : new TaxRateSnapshot(taxRate.Percentage, taxRate.IsInclusive),
                IsDiscountable: item.IsDiscountable,
                PeriodStart: item.PeriodStart,
                PeriodEnd: item.PeriodEnd));
        }

        return lines;
    }

    /// <summary>The line of a one-time price, with its tax rate, described in the customer's language unless a description is given.</summary>
    public async Task<InvoiceLineDraft> FromPriceAsync(Price price, decimal quantity, string? description, string locale, CancellationToken cancellationToken)
    {
        var taxRate = price.TaxRateId is { } rateId ? await context.TaxRates.AsNoTracking().SingleAsync(rate => rate.Id == rateId, cancellationToken) : null;
        return new InvoiceLineDraft(
            InvoiceLineType.OneTime,
            description ?? await DescribeAsync(price, quantity, locale, cancellationToken),
            quantity,
            PriceAmounts.For(price, quantity),
            UnitAmountDecimal: UnitAmountOf(price),
            PriceId: price.Id,
            TaxRateId: taxRate?.Id,
            Tax: taxRate is null ? null : new TaxRateSnapshot(taxRate.Percentage, taxRate.IsInclusive));
    }

    /// <summary>A one-time price that can be sold in the currency, by ID or lookup key.</summary>
    public async Task<Result<Price>> SellablePriceAsync(string idOrKey, string currency, string field, CancellationToken cancellationToken)
    {
        if (await context.FindPriceAsync(idOrKey, cancellationToken) is not { } price)
        {
            return InvoiceErrors.InvalidLine(field, "No price has this ID or lookup key.");
        }

        if (price.Type != PriceType.OneTime)
        {
            return InvoiceErrors.InvalidLine(field, "Invoices and invoice items take one-time prices; recurring prices are billed by subscriptions.");
        }

        if (price.Currency != currency)
        {
            return InvoiceErrors.InvalidLine(field, $"The price is in {price.Currency}, not {currency}.");
        }

        var product = await context.Products.AsNoTracking().SingleAsync(row => row.Id == price.ProductId, cancellationToken);
        return price.IsArchived || product.IsArchived ? InvoiceErrors.InvalidLine(field, "The price or its product is archived.") : price;
    }

    /// <summary>A tax rate that new lines can use, by ID or key.</summary>
    public async Task<Result<TaxRate>> UsableTaxRateAsync(string idOrKey, string field, CancellationToken cancellationToken) =>
        await context.FindTaxRateAsync(idOrKey, cancellationToken) switch
        {
            null => InvoiceErrors.InvalidLine(field, "No tax rate has this ID or key."),
            { IsArchived: true } => InvoiceErrors.InvalidLine(field, "The tax rate is archived."),
            var rate => rate,
        };

    public static Result<decimal> Quantity(decimal? quantity, bool whole, string field)
    {
        var value = quantity ?? 1;
        return value <= 0 || decimal.Round(value, whole ? 0 : 8) != value
            ? InvoiceErrors.InvalidLine(field, whole ? "The quantity of a price is a positive whole number." : "The quantity is positive, with at most eight decimals.")
            : value;
    }

    private async Task<Result<InvoiceLineDraft>> FromRequestAsync(InvoiceLineRequest request, string path, string currency, string locale, CancellationToken cancellationToken)
    {
        if (request.Price is { } priceKey)
        {
            if (request.UnitAmount is not null || request.TaxRate is not null)
            {
                return InvoiceErrors.InvalidLine($"{path}.unitAmount", "A line with a price takes its amount and tax rate from the price.");
            }

            var price = await SellablePriceAsync(priceKey, currency, $"{path}.price", cancellationToken);
            if (price.IsFailure)
            {
                return price.Error;
            }

            var quantity = Quantity(request.Quantity, whole: true, $"{path}.quantity");
            if (quantity.IsFailure)
            {
                return quantity.Error;
            }

            if (!Text.FitsOptional(request.Description, InvoiceLine.DescriptionMaxLength))
            {
                return InvoiceErrors.InvalidLine($"{path}.description", "The description is at most 500 characters.");
            }

            return await FromPriceAsync(price.Value, quantity.Value, Text.Normalize(request.Description), locale, cancellationToken);
        }

        if (!Text.FitsRequired(request.Description, InvoiceLine.DescriptionMaxLength) || request.UnitAmount is null)
        {
            return InvoiceErrors.InvalidLine($"{path}.description", "A line without a price needs a description of at most 500 characters and a unit amount.");
        }

        var amountQuantity = Quantity(request.Quantity, whole: false, $"{path}.quantity");
        if (amountQuantity.IsFailure)
        {
            return amountQuantity.Error;
        }

        TaxRate? rate = null;
        if (request.TaxRate is { } taxRateKey)
        {
            var found = await UsableTaxRateAsync(taxRateKey, $"{path}.taxRate", cancellationToken);
            if (found.IsFailure)
            {
                return found.Error;
            }

            rate = found.Value;
        }

        return new InvoiceLineDraft(
            InvoiceLineType.Item,
            request.Description!,
            amountQuantity.Value,
            MoneyMath.Multiply(request.UnitAmount.Value, amountQuantity.Value),
            UnitAmountDecimal: request.UnitAmount,
            TaxRateId: rate?.Id,
            Tax: rate is null ? null : new TaxRateSnapshot(rate.Percentage, rate.IsInclusive));
    }

    /// <summary>A one-time line's description, as <c>1,000 credits × 2</c> or <c>1.000 kredi × 2</c> (INV-2).</summary>
    private async Task<string> DescribeAsync(Price price, decimal quantity, string locale, CancellationToken cancellationToken)
    {
        var product = await context.Products.AsNoTracking().SingleAsync(row => row.Id == price.ProductId, cancellationToken);
        return Describe(product, price, quantity, locale);
    }

    /// <summary>A price's line description, as <c>Pro (Monthly) × 5</c> or <c>Pro (Aylık) × 5</c> (INV-2).</summary>
    internal static string Describe(Product product, Price price, decimal quantity, string locale)
    {
        var culture = CultureInfo.GetCultureInfo(locale.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "tr-TR" : "en-US");
        return $"{PriceDisplay.Name(product.Name, price.Name)} × {quantity.ToString("#,0.########", culture)}";
    }

    internal static decimal? UnitAmountOf(Price price) => price.BillingScheme == BillingScheme.PerUnit ? price.UnitAmountDecimal ?? price.UnitAmount : null;
}
