using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Domain.Checkout;

namespace Tahsil.Application.Catalog;

/// <summary>
/// The records that use prices (DOM-3): invoice lines, pending invoice items, subscription items, and the items of
/// open checkout sessions, which would fail once their prices were gone.
/// </summary>
public sealed class BillingPriceUsage(ITahsilDbContext context) : IPriceUsage
{
    public async Task<bool> AnyPriceUsedAsync(Guid productId, CancellationToken cancellationToken)
    {
        var prices = context.Prices.Where(price => price.ProductId == productId).Select(price => price.Id);
        if (await context.Invoices.SelectMany(invoice => invoice.Lines).AnyAsync(line => line.PriceId != null && prices.Contains(line.PriceId.Value), cancellationToken)
            || await context.PendingInvoiceItems.AnyAsync(item => item.PriceId != null && prices.Contains(item.PriceId.Value), cancellationToken)
            || await context.SubscriptionItems.AnyAsync(item => prices.Contains(item.PriceId), cancellationToken))
        {
            return true;
        }

        var priceIds = await prices.ToListAsync(cancellationToken);
        var sold = await context.CheckoutSessions.Where(session => session.Status == CheckoutStatus.Open).Select(session => session.Items).ToListAsync(cancellationToken);
        return sold.Any(items => items.Any(item => priceIds.Contains(item.PriceId)));
    }
}
